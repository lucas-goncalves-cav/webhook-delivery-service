using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Common;
using WebhookDelivery.Domain.Endpoints;

namespace WebhookDelivery.Application.Endpoints;

public sealed record CreateEndpointRequest(string Name, string Url, IReadOnlyCollection<string> SubscribedEvents);

public sealed record UpdateEndpointRequest(string Name, string Url, IReadOnlyCollection<string> SubscribedEvents);

public sealed record EndpointResponse(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyCollection<string> SubscribedEvents,
    bool Active,
    int ConsecutiveFailures,
    DateTime? DisabledAt,
    string? DisabledReason,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// The secret is returned only when it is created or rotated. Afterwards it
/// is write only, the same way API keys work everywhere else.
/// </summary>
public sealed record EndpointWithSecretResponse(EndpointResponse Endpoint, string Secret);

public interface IEndpointService
{
    Task<Result<EndpointWithSecretResponse>> CreateAsync(
        CreateEndpointRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EndpointResponse>> UpdateAsync(
        Guid id,
        UpdateEndpointRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<EndpointResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResponse<EndpointResponse>>> SearchAsync(
        bool? active,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<EndpointResponse>> SetActiveAsync(
        Guid id,
        bool active,
        CancellationToken cancellationToken = default);

    Task<Result<EndpointWithSecretResponse>> RotateSecretAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public sealed class EndpointService : IEndpointService
{
    private readonly IEndpointRepository _endpoints;
    private readonly IUnitOfWork _unitOfWork;

    public EndpointService(IEndpointRepository endpoints, IUnitOfWork unitOfWork)
    {
        _endpoints = endpoints;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<EndpointWithSecretResponse>> CreateAsync(
        CreateEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            return Result.Failure<EndpointWithSecretResponse>(
                Error.Validation($"'{request.Url}' is not a valid absolute URL."));
        }

        var endpoint = new WebhookEndpoint(request.Name, url, request.SubscribedEvents);

        await _endpoints.AddAsync(endpoint, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new EndpointWithSecretResponse(Map(endpoint), endpoint.Secret));
    }

    public async Task<Result<EndpointResponse>> UpdateAsync(
        Guid id,
        UpdateEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url))
        {
            return Result.Failure<EndpointResponse>(
                Error.Validation($"'{request.Url}' is not a valid absolute URL."));
        }

        var endpoint = await _endpoints.GetByIdAsync(id, cancellationToken);

        if (endpoint is null)
        {
            return Result.Failure<EndpointResponse>(Error.NotFound($"Endpoint {id} was not found."));
        }

        endpoint.Update(request.Name, url, request.SubscribedEvents);

        _endpoints.Update(endpoint);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(endpoint));
    }

    public async Task<Result<EndpointResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var endpoint = await _endpoints.GetByIdAsync(id, cancellationToken);

        return endpoint is null
            ? Result.Failure<EndpointResponse>(Error.NotFound($"Endpoint {id} was not found."))
            : Result.Success(Map(endpoint));
    }

    public async Task<Result<PagedResponse<EndpointResponse>>> SearchAsync(
        bool? active,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await _endpoints.SearchAsync(active, page, pageSize, cancellationToken);

        return Result.Success(new PagedResponse<EndpointResponse>(
            result.Items.Select(Map).ToList(),
            result.TotalItems,
            result.Page,
            result.PageSize,
            result.TotalPages,
            result.HasNext));
    }

    public async Task<Result<EndpointResponse>> SetActiveAsync(
        Guid id,
        bool active,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await _endpoints.GetByIdAsync(id, cancellationToken);

        if (endpoint is null)
        {
            return Result.Failure<EndpointResponse>(Error.NotFound($"Endpoint {id} was not found."));
        }

        if (active)
        {
            // Reactivating resets the failure counter, otherwise an endpoint
            // disabled automatically would be disabled again on its next
            // failure regardless of whether the problem was fixed.
            endpoint.Activate();
        }
        else
        {
            endpoint.Deactivate("Disabled by the customer.");
        }

        _endpoints.Update(endpoint);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(Map(endpoint));
    }

    public async Task<Result<EndpointWithSecretResponse>> RotateSecretAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await _endpoints.GetByIdAsync(id, cancellationToken);

        if (endpoint is null)
        {
            return Result.Failure<EndpointWithSecretResponse>(Error.NotFound($"Endpoint {id} was not found."));
        }

        var secret = endpoint.RotateSecret();

        _endpoints.Update(endpoint);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new EndpointWithSecretResponse(Map(endpoint), secret));
    }

    private static EndpointResponse Map(WebhookEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Name,
        endpoint.Url,
        endpoint.SubscribedEvents,
        endpoint.Active,
        endpoint.ConsecutiveFailures,
        endpoint.DisabledAt,
        endpoint.DisabledReason,
        endpoint.CreatedAt,
        endpoint.UpdatedAt);
}
