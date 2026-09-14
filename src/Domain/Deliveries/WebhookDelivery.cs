using WebhookDelivery.Domain.Common;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Domain.Deliveries;

public enum DeliveryStatus
{
    /// <summary>Waiting for its first attempt.</summary>
    Pending,

    /// <summary>An attempt failed and another is scheduled.</summary>
    Scheduled,

    /// <summary>Delivered and acknowledged with a 2xx response.</summary>
    Delivered,

    /// <summary>Every attempt failed. Moved to the dead letter queue.</summary>
    Failed,

    /// <summary>Cancelled before it could be delivered.</summary>
    Cancelled
}

/// <summary>
/// One event being delivered to one endpoint. An event subscribed to by three
/// endpoints produces three deliveries, each retried independently.
/// </summary>
public class WebhookDeliveryAttemptLog : BaseEntity
{
    private WebhookDeliveryAttemptLog()
    {
    }

    public WebhookDeliveryAttemptLog(
        Guid deliveryId,
        int attemptNumber,
        int? statusCode,
        string? responseBody,
        string? error,
        TimeSpan duration)
    {
        DeliveryId = deliveryId;
        AttemptNumber = attemptNumber;
        StatusCode = statusCode;
        ResponseBody = Truncate(responseBody, 4000);
        Error = Truncate(error, 1000);
        DurationMs = (int)duration.TotalMilliseconds;
        AttemptedAt = DateTime.UtcNow;
    }

    public Guid DeliveryId { get; private set; }

    public int AttemptNumber { get; private set; }

    public int? StatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? Error { get; private set; }

    public int DurationMs { get; private set; }

    public DateTime AttemptedAt { get; private set; }

    public bool Succeeded => StatusCode is >= 200 and < 300;

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

public class WebhookDeliveryRecord : BaseEntity
{
    private readonly List<WebhookDeliveryAttemptLog> _attempts = [];

    private WebhookDeliveryRecord()
    {
    }

    /// <summary>
    /// The current time is passed in rather than read from the clock, so the
    /// scheduling behaviour can be tested deterministically.
    /// </summary>
    public WebhookDeliveryRecord(WebhookEvent webhookEvent, WebhookEndpoint endpoint, DateTime now)
    {
        EventId = webhookEvent.Id;
        EndpointId = endpoint.Id;
        Url = endpoint.Url;
        Status = DeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        CreatedAt = now;
    }

    public Guid EventId { get; private set; }

    public Guid EndpointId { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public DeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>
    /// When the next attempt becomes eligible. The worker claims deliveries
    /// whose <see cref="NextAttemptAt"/> has passed.
    /// </summary>
    public DateTime? NextAttemptAt { get; private set; }

    public DateTime? DeliveredAt { get; private set; }

    public DateTime? FailedAt { get; private set; }

    public string? LastError { get; private set; }

    public int? LastStatusCode { get; private set; }

    public WebhookEvent? Event { get; private set; }

    public WebhookEndpoint? Endpoint { get; private set; }

    public IReadOnlyCollection<WebhookDeliveryAttemptLog> Attempts => _attempts.AsReadOnly();

    public bool IsTerminal => Status is DeliveryStatus.Delivered or DeliveryStatus.Failed or DeliveryStatus.Cancelled;

    public void RecordSuccess(int statusCode, string? responseBody, TimeSpan duration)
    {
        EnsureNotTerminal();

        AttemptCount++;
        _attempts.Add(new WebhookDeliveryAttemptLog(Id, AttemptCount, statusCode, responseBody, null, duration));

        Status = DeliveryStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        LastStatusCode = statusCode;
        LastError = null;
        NextAttemptAt = null;

        Touch();
    }

    /// <summary>
    /// Records a failed attempt. When <paramref name="nextAttemptAt"/> is null
    /// the retry policy is exhausted and the delivery is dead lettered.
    /// </summary>
    public void RecordFailure(
        int? statusCode,
        string? responseBody,
        string? error,
        TimeSpan duration,
        DateTime? nextAttemptAt)
    {
        EnsureNotTerminal();

        AttemptCount++;
        _attempts.Add(new WebhookDeliveryAttemptLog(Id, AttemptCount, statusCode, responseBody, error, duration));

        LastStatusCode = statusCode;
        LastError = error;

        if (nextAttemptAt is null)
        {
            Status = DeliveryStatus.Failed;
            FailedAt = DateTime.UtcNow;
            NextAttemptAt = null;
        }
        else
        {
            Status = DeliveryStatus.Scheduled;
            NextAttemptAt = nextAttemptAt;
        }

        Touch();
    }

    public void Cancel(string reason)
    {
        EnsureNotTerminal();

        Status = DeliveryStatus.Cancelled;
        LastError = reason;
        NextAttemptAt = null;

        Touch();
    }

    /// <summary>
    /// Puts a dead lettered delivery back in the queue. The attempt history is
    /// kept, so a replayed delivery shows every attempt including the ones that
    /// caused it to fail the first time.
    /// </summary>
    public void Replay(DateTime now)
    {
        if (Status is not (DeliveryStatus.Failed or DeliveryStatus.Cancelled))
        {
            throw new DomainException("Only failed or cancelled deliveries can be replayed.");
        }

        Status = DeliveryStatus.Pending;
        NextAttemptAt = now;
        FailedAt = null;
        LastError = null;

        Touch();
    }

    private void EnsureNotTerminal()
    {
        if (IsTerminal)
        {
            throw new DomainException($"Delivery {Id} is already {Status} and cannot be modified.");
        }
    }
}
