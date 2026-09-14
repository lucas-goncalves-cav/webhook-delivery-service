namespace WebhookDelivery.Application.Common;

public sealed record PagedResponse<T>(
    IReadOnlyCollection<T> Items,
    int TotalItems,
    int Page,
    int PageSize,
    int TotalPages,
    bool HasNext);
