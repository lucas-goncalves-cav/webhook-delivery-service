using Microsoft.AspNetCore.Mvc;
using WebhookDelivery.Application.Common;
using WebhookDelivery.Application.Endpoints;

namespace WebhookDelivery.Api.Endpoints;

public static class EndpointRoutes
{
    public static IEndpointRouteBuilder MapEndpointRoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/endpoints")
            .WithTags("Endpoints");

        group.MapPost("/", async (
            CreateEndpointRequest request,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(request, cancellationToken);

            return result.IsSuccess
                ? Results.Created($"/api/endpoints/{result.Value.Endpoint.Id}", result.Value)
                : result.ToProblem();
        })
        .WithName("CreateEndpoint")
        .WithSummary("Registers a destination and returns its signing secret")
        .WithDescription(
            "The secret is returned only here and when it is rotated. Store it: " +
            "it is needed to verify the signature of every webhook sent to this endpoint.")
        .Produces<EndpointWithSecretResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/", async (
            [FromQuery] bool? active,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SearchAsync(
                active,
                page <= 0 ? 1 : page,
                pageSize is <= 0 or > 100 ? 20 : pageSize,
                cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("ListEndpoints")
        .WithSummary("Lists registered endpoints")
        .Produces<PagedResponse<EndpointResponse>>();

        group.MapGet("/{id:guid}", async (
            Guid id,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetByIdAsync(id, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("GetEndpoint")
        .WithSummary("Gets one endpoint")
        .Produces<EndpointResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateEndpointRequest request,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(id, request, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("UpdateEndpoint")
        .WithSummary("Updates an endpoint's name, URL or subscriptions")
        .Produces<EndpointResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/activate", async (
            Guid id,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SetActiveAsync(id, active: true, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("ActivateEndpoint")
        .WithSummary("Re enables an endpoint and resets its failure counter")
        .Produces<EndpointResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/deactivate", async (
            Guid id,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SetActiveAsync(id, active: false, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("DeactivateEndpoint")
        .WithSummary("Stops delivering to an endpoint")
        .Produces<EndpointResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/rotate-secret", async (
            Guid id,
            IEndpointService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RotateSecretAsync(id, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("RotateEndpointSecret")
        .WithSummary("Issues a new signing secret")
        .WithDescription(
            "The previous secret stops working immediately. Deliveries already in flight " +
            "are signed with whichever secret is current when the attempt is made.")
        .Produces<EndpointWithSecretResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
