using WebhookDelivery.Domain.Common;

namespace WebhookDelivery.Domain.Events;

/// <summary>
/// Something that happened, recorded once, then fanned out to every endpoint
/// subscribed to its type.
/// </summary>
public class WebhookEvent : BaseEntity
{
    private WebhookEvent()
    {
    }

    public WebhookEvent(string eventType, string payload, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new DomainException("Event type is required.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new DomainException("Event payload is required.");
        }

        EventType = eventType.Trim();
        Payload = payload;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        OccurredAt = DateTime.UtcNow;
    }

    /// <summary>Dotted name such as <c>order.created</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>The JSON document delivered to subscribers, verbatim.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>
    /// Optional caller supplied key. Publishing twice with the same key returns
    /// the original event instead of creating a duplicate, which matters when
    /// the producer retries after a timeout.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    public DateTime OccurredAt { get; private set; }
}
