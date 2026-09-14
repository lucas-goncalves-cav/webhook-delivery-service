namespace WebhookDelivery.Domain.Common;

public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int TotalItems, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    public bool HasNext => Page < TotalPages;
}
