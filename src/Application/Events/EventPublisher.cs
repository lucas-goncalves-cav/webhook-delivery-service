using Microsoft.Extensions.Logging;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Common;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Application.Events;

public sealed record PublishEventRequest(string EventType, object Payload, string? IdempotencyKey = null);

public sealed record PublishEventResponse(
    Guid EventId,
    string EventType,
    int DeliveriesCreated,
    bool WasDuplicate,
    DateTime OccurredAt);

public interface IEventPublisher
{
    Task<Result<PublishEventResponse>> PublishAsync(
        PublishEventRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Records an event once, then creates one delivery per subscribed endpoint.
///
/// The fan out happens here rather than in the worker so that a published
/// event is durable immediately: if the process dies a millisecond later, the
/// deliveries are already in the database waiting to be claimed.
/// </summary>
public sealed class EventPublisher : IEventPublisher
{
    private readonly IEventRepository _events;
    private readonly IEndpointRepository _endpoints;
    private readonly IDeliveryRepository _deliveries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<EventPublisher> _logger;

    public EventPublisher(
        IEventRepository events,
        IEndpointRepository endpoints,
        IDeliveryRepository deliveries,
        IUnitOfWork unitOfWork,
        IClock clock,
        ILogger<EventPublisher> logger)
    {
        _events = events;
        _endpoints = endpoints;
        _deliveries = deliveries;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<PublishEventResponse>> PublishAsync(
        PublishEventRequest request,
        CancellationToken cancellationToken = default)
    {
        // A producer that times out and retries must not create the event
        // twice. The idempotency key makes publishing safe to repeat.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await _events.GetByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken);

            if (existing is not null)
            {
                _logger.LogInformation(
                    "Event with idempotency key {Key} already exists as {EventId}.",
                    request.IdempotencyKey,
                    existing.Id);

                return Result.Success(new PublishEventResponse(
                    existing.Id,
                    existing.EventType,
                    DeliveriesCreated: 0,
                    WasDuplicate: true,
                    existing.OccurredAt));
            }
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(request.Payload);
        var webhookEvent = new WebhookEvent(request.EventType, payload, request.IdempotencyKey);

        await _events.AddAsync(webhookEvent, cancellationToken);

        var subscribers = await _endpoints.GetActiveSubscribersAsync(request.EventType, cancellationToken);

        var deliveries = subscribers
            .Select(endpoint => new WebhookDeliveryRecord(webhookEvent, endpoint, _clock.UtcNow))
            .ToList();

        if (deliveries.Count > 0)
        {
            await _deliveries.AddRangeAsync(deliveries, cancellationToken);
        }
        else
        {
            // Not an error. An event nobody subscribes to is still recorded,
            // so an endpoint registered later can replay it.
            _logger.LogInformation(
                "Event {EventType} published with no active subscribers.",
                request.EventType);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new PublishEventResponse(
            webhookEvent.Id,
            webhookEvent.EventType,
            deliveries.Count,
            WasDuplicate: false,
            webhookEvent.OccurredAt));
    }
}
