using WebhookDelivery.Domain.Common;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Application.Abstractions;

public interface IEndpointRepository
{
    Task<WebhookEndpoint?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<WebhookEndpoint>> SearchAsync(
        bool? active,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<WebhookEndpoint>> GetActiveSubscribersAsync(
        string eventType,
        CancellationToken cancellationToken = default);

    Task AddAsync(WebhookEndpoint endpoint, CancellationToken cancellationToken = default);

    void Update(WebhookEndpoint endpoint);
}

public interface IEventRepository
{
    Task<WebhookEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<WebhookEvent?> GetByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default);

    Task AddAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}

public interface IDeliveryRepository
{
    Task<WebhookDeliveryRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<WebhookDeliveryRecord>> SearchAsync(
        DeliveryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a batch of deliveries that are due, so several worker
    /// instances can run without delivering the same webhook twice.
    /// </summary>
    Task<IReadOnlyCollection<WebhookDeliveryRecord>> ClaimDueAsync(
        int batchSize,
        DateTime now,
        CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<WebhookDeliveryRecord> deliveries, CancellationToken cancellationToken = default);

    void Update(WebhookDeliveryRecord delivery);
}

public sealed record DeliveryFilter(
    Guid? EndpointId = null,
    Guid? EventId = null,
    DeliveryStatus? Status = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 20);

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends one HTTP request to a customer endpoint. Abstracted so the delivery
/// logic can be tested without a network.
/// </summary>
public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(
        WebhookSendRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WebhookSendRequest(
    Guid DeliveryId,
    Guid EventId,
    string EventType,
    string Url,
    string Payload,
    string Secret,
    int AttemptNumber);

public sealed record WebhookSendResult(
    int? StatusCode,
    string? ResponseBody,
    string? Error,
    TimeSpan Duration)
{
    public bool Succeeded => StatusCode is >= 200 and < 300;

    public static WebhookSendResult Ok(int statusCode, string? body, TimeSpan duration) =>
        new(statusCode, body, null, duration);

    public static WebhookSendResult HttpFailure(int statusCode, string? body, TimeSpan duration) =>
        new(statusCode, body, $"Endpoint responded with {statusCode}.", duration);

    /// <summary>
    /// A transport level failure: DNS, TLS, connection refused, timeout.
    /// There is no status code because no response ever arrived.
    /// </summary>
    public static WebhookSendResult TransportFailure(string error, TimeSpan duration) =>
        new(null, null, error, duration);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
