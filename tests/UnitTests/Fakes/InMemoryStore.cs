using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Domain.Common;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.UnitTests.Fakes;

/// <summary>
/// One in memory object playing every repository, so tests can set up a whole
/// scenario without a database and still exercise the real domain objects.
/// </summary>
public sealed class InMemoryStore : IEndpointRepository, IEventRepository, IDeliveryRepository, IUnitOfWork
{
    public List<WebhookEndpoint> Endpoints { get; } = [];

    public List<WebhookEvent> Events { get; } = [];

    public List<WebhookDeliveryRecord> Deliveries { get; } = [];

    public int SaveCount { get; private set; }

    // -------------------------------------------------------------------------
    // Endpoints
    // -------------------------------------------------------------------------

    public Task<WebhookEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Endpoints.FirstOrDefault(endpoint => endpoint.Id == id));

    public Task<PagedResult<WebhookEndpoint>> SearchAsync(
        bool? active,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = Endpoints.AsEnumerable();

        if (active.HasValue)
        {
            query = query.Where(endpoint => endpoint.Active == active.Value);
        }

        var matching = query.OrderBy(endpoint => endpoint.Name).ToList();

        var items = matching
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(new PagedResult<WebhookEndpoint>(items, matching.Count, page, pageSize));
    }

    public Task<IReadOnlyCollection<WebhookEndpoint>> GetActiveSubscribersAsync(
        string eventType,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<WebhookEndpoint> subscribers = Endpoints
            .Where(endpoint => endpoint.Active && endpoint.IsSubscribedTo(eventType))
            .ToList();

        return Task.FromResult(subscribers);
    }

    public Task AddAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        Endpoints.Add(endpoint);

        return Task.CompletedTask;
    }

    public void Update(WebhookEndpoint endpoint)
    {
        // Entities are tracked by reference here, the same way an ORM change
        // tracker behaves, so there is nothing to copy back.
    }

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    Task<WebhookEvent?> IEventRepository.GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Events.FirstOrDefault(webhookEvent => webhookEvent.Id == id));

    public Task<WebhookEvent?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Events.FirstOrDefault(webhookEvent =>
            webhookEvent.IdempotencyKey is not null &&
            webhookEvent.IdempotencyKey.Equals(key, StringComparison.Ordinal)));

    public Task AddAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(webhookEvent);

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Deliveries
    // -------------------------------------------------------------------------

    Task<WebhookDeliveryRecord?> IDeliveryRepository.GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Deliveries.FirstOrDefault(delivery => delivery.Id == id));

    public Task<PagedResult<WebhookDeliveryRecord>> SearchAsync(
        DeliveryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = Deliveries.AsEnumerable();

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

        var matching = query.OrderByDescending(delivery => delivery.CreatedAt).ToList();

        var items = matching
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToList();

        return Task.FromResult(
            new PagedResult<WebhookDeliveryRecord>(items, matching.Count, filter.Page, filter.PageSize));
    }

    public Task<IReadOnlyCollection<WebhookDeliveryRecord>> ClaimDueAsync(
        int batchSize,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<WebhookDeliveryRecord> due = Deliveries
            .Where(delivery =>
                delivery.Status is DeliveryStatus.Pending or DeliveryStatus.Scheduled &&
                delivery.NextAttemptAt <= now)
            .OrderBy(delivery => delivery.NextAttemptAt)
            .Take(batchSize)
            .ToList();

        return Task.FromResult(due);
    }

    public Task AddRangeAsync(
        IEnumerable<WebhookDeliveryRecord> deliveries,
        CancellationToken cancellationToken = default)
    {
        Deliveries.AddRange(deliveries);

        return Task.CompletedTask;
    }

    public void Update(WebhookDeliveryRecord delivery)
    {
    }

    // -------------------------------------------------------------------------
    // Unit of work
    // -------------------------------------------------------------------------

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        return Task.FromResult(0);
    }
}

public sealed class FixedClock : IClock
{
    public FixedClock(DateTime? now = null) =>
        UtcNow = now ?? new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// A sender whose responses are scripted per attempt, so a test can say
/// "fail twice, then succeed" without any HTTP.
/// </summary>
public sealed class ScriptedSender : IWebhookSender
{
    private readonly Queue<WebhookSendResult> _responses = new();
    private readonly WebhookSendResult _fallback;

    public ScriptedSender(WebhookSendResult? fallback = null) =>
        _fallback = fallback ?? WebhookSendResult.Ok(200, "OK", TimeSpan.FromMilliseconds(30));

    public List<WebhookSendRequest> Requests { get; } = [];

    public ScriptedSender Then(WebhookSendResult result)
    {
        _responses.Enqueue(result);

        return this;
    }

    public ScriptedSender ThenSucceed(int statusCode = 200) =>
        Then(WebhookSendResult.Ok(statusCode, "OK", TimeSpan.FromMilliseconds(25)));

    public ScriptedSender ThenFailWith(int statusCode) =>
        Then(WebhookSendResult.HttpFailure(statusCode, "error", TimeSpan.FromMilliseconds(40)));

    public ScriptedSender ThenTimeOut() =>
        Then(WebhookSendResult.TransportFailure("The request timed out.", TimeSpan.FromSeconds(10)));

    public Task<WebhookSendResult> SendAsync(
        WebhookSendRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : _fallback);
    }
}
