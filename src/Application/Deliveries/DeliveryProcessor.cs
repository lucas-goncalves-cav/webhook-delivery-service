using Microsoft.Extensions.Logging;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.Application.Deliveries;

public sealed record DeliveryOptions
{
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Consecutive failures before an endpoint is disabled automatically.
    /// A customer URL that has been dead for days should stop consuming
    /// worker capacity.
    /// </summary>
    public int DisableEndpointAfterConsecutiveFailures { get; init; } = 20;
}

public sealed record BatchResult(int Claimed, int Delivered, int Rescheduled, int DeadLettered);

/// <summary>
/// Takes a batch of due deliveries, attempts each one, and applies the retry
/// policy to whatever comes back.
///
/// This is where the interesting decisions live, and all of them are testable
/// without a network because <see cref="IWebhookSender"/> is an abstraction.
/// </summary>
public sealed class DeliveryProcessor
{
    private readonly IDeliveryRepository _deliveries;
    private readonly IEndpointRepository _endpoints;
    private readonly IEventRepository _events;
    private readonly IWebhookSender _sender;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RetryPolicy _retryPolicy;
    private readonly IClock _clock;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryProcessor> _logger;

    public DeliveryProcessor(
        IDeliveryRepository deliveries,
        IEndpointRepository endpoints,
        IEventRepository events,
        IWebhookSender sender,
        IUnitOfWork unitOfWork,
        RetryPolicy retryPolicy,
        IClock clock,
        DeliveryOptions options,
        ILogger<DeliveryProcessor> logger)
    {
        _deliveries = deliveries;
        _endpoints = endpoints;
        _events = events;
        _sender = sender;
        _unitOfWork = unitOfWork;
        _retryPolicy = retryPolicy;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<BatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var due = await _deliveries.ClaimDueAsync(_options.BatchSize, now, cancellationToken);

        if (due.Count == 0)
        {
            return new BatchResult(0, 0, 0, 0);
        }

        var delivered = 0;
        var rescheduled = 0;
        var deadLettered = 0;

        foreach (var delivery in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await ProcessOneAsync(delivery, cancellationToken);

            switch (outcome)
            {
                case DeliveryStatus.Delivered:
                    delivered++;
                    break;
                case DeliveryStatus.Scheduled:
                    rescheduled++;
                    break;
                case DeliveryStatus.Failed:
                    deadLettered++;
                    break;
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Batch processed: {Claimed} claimed, {Delivered} delivered, {Rescheduled} rescheduled, {DeadLettered} dead lettered.",
            due.Count,
            delivered,
            rescheduled,
            deadLettered);

        return new BatchResult(due.Count, delivered, rescheduled, deadLettered);
    }

    private async Task<DeliveryStatus> ProcessOneAsync(
        WebhookDeliveryRecord delivery,
        CancellationToken cancellationToken)
    {
        var endpoint = delivery.Endpoint ?? await _endpoints.GetByIdAsync(delivery.EndpointId, cancellationToken);
        var webhookEvent = delivery.Event ?? await _events.GetByIdAsync(delivery.EventId, cancellationToken);

        if (endpoint is null || webhookEvent is null)
        {
            delivery.Cancel("The endpoint or event no longer exists.");
            _deliveries.Update(delivery);

            return DeliveryStatus.Cancelled;
        }

        // An endpoint disabled while this delivery was waiting should not be
        // called. Cancelling rather than failing keeps the dead letter queue
        // meaningful: it holds things that could not be delivered, not things
        // that were deliberately stopped.
        if (!endpoint.Active)
        {
            delivery.Cancel("The endpoint was disabled before this delivery was attempted.");
            _deliveries.Update(delivery);

            return DeliveryStatus.Cancelled;
        }

        var result = await _sender.SendAsync(
            new WebhookSendRequest(
                delivery.Id,
                webhookEvent.Id,
                webhookEvent.EventType,
                endpoint.Url,
                webhookEvent.Payload,
                endpoint.Secret,
                delivery.AttemptCount + 1),
            cancellationToken);

        if (result.Succeeded)
        {
            delivery.RecordSuccess(result.StatusCode!.Value, result.ResponseBody, result.Duration);
            endpoint.RecordSuccess();

            _deliveries.Update(delivery);
            _endpoints.Update(endpoint);

            return DeliveryStatus.Delivered;
        }

        return HandleFailure(delivery, endpoint, result);
    }

    private DeliveryStatus HandleFailure(
        WebhookDeliveryRecord delivery,
        Domain.Endpoints.WebhookEndpoint endpoint,
        WebhookSendResult result)
    {
        // A 4xx other than 408 and 429 means the request itself was wrong.
        // Retrying it unchanged produces the same answer, so it goes straight
        // to the dead letter queue instead of burning five more attempts.
        var retryable = result.StatusCode is null || RetryPolicy.IsRetryable(result.StatusCode.Value);

        var nextAttemptAt = retryable
            ? _retryPolicy.NextAttemptAt(delivery.AttemptCount + 1, _clock.UtcNow)
            : null;

        delivery.RecordFailure(
            result.StatusCode,
            result.ResponseBody,
            retryable ? result.Error : $"{result.Error} Not retryable, dead lettered immediately.",
            result.Duration,
            nextAttemptAt);

        var disabled = endpoint.RecordFailure(_options.DisableEndpointAfterConsecutiveFailures);

        if (disabled)
        {
            _logger.LogWarning(
                "Endpoint {EndpointId} disabled after {Failures} consecutive failures.",
                endpoint.Id,
                endpoint.ConsecutiveFailures);
        }

        _deliveries.Update(delivery);
        _endpoints.Update(endpoint);

        return nextAttemptAt is null ? DeliveryStatus.Failed : DeliveryStatus.Scheduled;
    }
}
