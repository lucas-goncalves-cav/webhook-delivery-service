using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Common;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.Application.Deliveries;

public sealed record DeliveryAttemptResponse(
    int AttemptNumber,
    int? StatusCode,
    string? Error,
    int DurationMs,
    DateTime AttemptedAt,
    bool Succeeded);

public sealed record DeliveryResponse(
    Guid Id,
    Guid EventId,
    Guid EndpointId,
    string Url,
    string Status,
    int AttemptCount,
    DateTime? NextAttemptAt,
    DateTime? DeliveredAt,
    DateTime? FailedAt,
    int? LastStatusCode,
    string? LastError,
    DateTime CreatedAt,
    IReadOnlyCollection<DeliveryAttemptResponse> Attempts);

public interface IDeliveryService
{
    Task<Result<DeliveryResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<DeliveryResponse>>> SearchAsync(
        DeliveryFilter filter,
        CancellationToken cancellationToken = default);

    Task<Result<DeliveryResponse>> ReplayAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class DeliveryService : IDeliveryService
{
    private readonly IDeliveryRepository _deliveries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public DeliveryService(IDeliveryRepository deliveries, IUnitOfWork unitOfWork, IClock clock)
    {
        _deliveries = deliveries;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result<DeliveryResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var delivery = await _deliveries.GetByIdAsync(id, cancellationToken);

        return delivery is null
            ? Result.Failure<DeliveryResponse>(Error.NotFound($"Delivery {id} was not found."))
            : Result.Success(Map(delivery));
    }

    public async Task<Result<PagedResponse<DeliveryResponse>>> SearchAsync(
        DeliveryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var result = await _deliveries.SearchAsync(filter, cancellationToken);

        return Result.Success(new PagedResponse<DeliveryResponse>(
            result.Items.Select(Map).ToList(),
            result.TotalItems,
            result.Page,
            result.PageSize,
            result.TotalPages,
            result.HasNext));
    }

    /// <summary>
    /// Puts a dead lettered delivery back in the queue, which is the whole
    /// reason the dead letter queue is worth having: the customer fixes their
    /// endpoint and asks for the events they missed.
    /// </summary>
    public async Task<Result<DeliveryResponse>> ReplayAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var delivery = await _deliveries.GetByIdAsync(id, cancellationToken);

        if (delivery is null)
        {
            return Result.Failure<DeliveryResponse>(Error.NotFound($"Delivery {id} was not found."));
        }

        if (delivery.Status is not (DeliveryStatus.Failed or DeliveryStatus.Cancelled))
        {
            return Result.Failure<DeliveryResponse>(Error.Conflict(
                $"Only failed or cancelled deliveries can be replayed. Delivery {id} is {delivery.Status}."));
        }

        delivery.Replay(_clock.UtcNow);

        _deliveries.Update(delivery);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(delivery));
    }

    private static DeliveryResponse Map(WebhookDeliveryRecord delivery) => new(
        delivery.Id,
        delivery.EventId,
        delivery.EndpointId,
        delivery.Url,
        delivery.Status.ToString(),
        delivery.AttemptCount,
        delivery.NextAttemptAt,
        delivery.DeliveredAt,
        delivery.FailedAt,
        delivery.LastStatusCode,
        delivery.LastError,
        delivery.CreatedAt,
        delivery.Attempts
            .OrderBy(attempt => attempt.AttemptNumber)
            .Select(attempt => new DeliveryAttemptResponse(
                attempt.AttemptNumber,
                attempt.StatusCode,
                attempt.Error,
                attempt.DurationMs,
                attempt.AttemptedAt,
                attempt.Succeeded))
            .ToList());
}
