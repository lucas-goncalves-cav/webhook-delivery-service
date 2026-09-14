using Microsoft.EntityFrameworkCore;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Domain.Common;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Infrastructure.Persistence;

public sealed class EndpointRepository : IEndpointRepository
{
    private readonly WebhookDbContext _context;

    public EndpointRepository(WebhookDbContext context)
    {
        _context = context;
    }

    public Task<WebhookEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Endpoints.FirstOrDefaultAsync(endpoint => endpoint.Id == id, cancellationToken);

    public async Task<PagedResult<WebhookEndpoint>> SearchAsync(
        bool? active,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Endpoints.AsNoTracking().AsQueryable();

        if (active.HasValue)
        {
            query = query.Where(endpoint => endpoint.Active == active.Value);
        }

        var totalItems = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(endpoint => endpoint.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<WebhookEndpoint>(items, totalItems, page, pageSize);
    }

    /// <summary>
    /// Subscription matching supports wildcards, so the filtering happens in
    /// memory over active endpoints rather than in SQL. That is fine at the
    /// scale where endpoints number in the thousands; past that, the matching
    /// would move to a normalized subscriptions table with an index.
    /// </summary>
    public async Task<IReadOnlyCollection<WebhookEndpoint>> GetActiveSubscribersAsync(
        string eventType,
        CancellationToken cancellationToken = default)
    {
        var active = await _context.Endpoints
            .Where(endpoint => endpoint.Active)
            .ToListAsync(cancellationToken);

        return active
            .Where(endpoint => endpoint.IsSubscribedTo(eventType))
            .ToList();
    }

    public async Task AddAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken = default) =>
        await _context.Endpoints.AddAsync(endpoint, cancellationToken);

    public void Update(WebhookEndpoint endpoint) => _context.Endpoints.Update(endpoint);
}

public sealed class EventRepository : IEventRepository
{
    private readonly WebhookDbContext _context;

    public EventRepository(WebhookDbContext context)
    {
        _context = context;
    }

    public Task<WebhookEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Events.FirstOrDefaultAsync(webhookEvent => webhookEvent.Id == id, cancellationToken);

    public Task<WebhookEvent?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default) =>
        _context.Events.FirstOrDefaultAsync(webhookEvent => webhookEvent.IdempotencyKey == key, cancellationToken);

    public async Task AddAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default) =>
        await _context.Events.AddAsync(webhookEvent, cancellationToken);
}

public sealed class DeliveryRepository : IDeliveryRepository
{
    private readonly WebhookDbContext _context;

    public DeliveryRepository(WebhookDbContext context)
    {
        _context = context;
    }

    public Task<WebhookDeliveryRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Deliveries
            .Include(delivery => delivery.Attempts)
            .FirstOrDefaultAsync(delivery => delivery.Id == id, cancellationToken);

    public async Task<PagedResult<WebhookDeliveryRecord>> SearchAsync(
        DeliveryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Deliveries
            .Include(delivery => delivery.Attempts)
            .AsNoTracking()
            .AsQueryable();

        if (filter.EndpointId.HasValue)
        {
            query = query.Where(delivery => delivery.EndpointId == filter.EndpointId.Value);
        }

        if (filter.EventId.HasValue)
        {
            query = query.Where(delivery => delivery.EventId == filter.EventId.Value);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(delivery => delivery.Status == filter.Status.Value);
        }

        if (filter.From.HasValue)
        {
            query = query.Where(delivery => delivery.CreatedAt >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(delivery => delivery.CreatedAt <= filter.To.Value);
        }

        var totalItems = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(delivery => delivery.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<WebhookDeliveryRecord>(items, totalItems, filter.Page, filter.PageSize);
    }

    /// <summary>
    /// Claims a batch of due deliveries for this worker.
    ///
    /// The locking hints are the important part. Two workers polling the same
    /// table would otherwise both read the same rows and deliver the same
    /// webhook twice.
    ///
    ///   UPDLOCK   takes an update lock immediately, so a second reader waits
    ///             instead of reading the same row and racing to update it
    ///   READPAST  skips rows another worker already locked, instead of
    ///             blocking behind them
    ///   ROWLOCK   keeps the lock granularity at the row, so one worker does
    ///             not lock a whole page out from under the others
    ///
    /// Together they let N workers poll the same queue and each get a disjoint
    /// batch. This is SQL Server specific; PostgreSQL spells it
    /// FOR UPDATE SKIP LOCKED.
    /// </summary>
    public async Task<IReadOnlyCollection<WebhookDeliveryRecord>> ClaimDueAsync(
        int batchSize,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (!_context.Database.IsSqlServer())
        {
            // The in memory and SQLite providers used by tests do not support
            // locking hints, so fall back to a plain query there.
            return await _context.Deliveries
                .Include(delivery => delivery.Event)
                .Include(delivery => delivery.Endpoint)
                .Include(delivery => delivery.Attempts)
                .Where(delivery =>
                    (delivery.Status == DeliveryStatus.Pending || delivery.Status == DeliveryStatus.Scheduled) &&
                    delivery.NextAttemptAt <= now)
                .OrderBy(delivery => delivery.NextAttemptAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
        }

        var ids = await _context.Database
            .SqlQuery<Guid>($"""
                SELECT TOP ({batchSize}) Id
                FROM Deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status IN ('Pending', 'Scheduled')
                  AND NextAttemptAt <= {now}
                ORDER BY NextAttemptAt
                """)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        return await _context.Deliveries
            .Include(delivery => delivery.Event)
            .Include(delivery => delivery.Endpoint)
            .Include(delivery => delivery.Attempts)
            .Where(delivery => ids.Contains(delivery.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task AddRangeAsync(
        IEnumerable<WebhookDeliveryRecord> deliveries,
        CancellationToken cancellationToken = default) =>
        await _context.Deliveries.AddRangeAsync(deliveries, cancellationToken);

    public void Update(WebhookDeliveryRecord delivery) => _context.Deliveries.Update(delivery);
}
