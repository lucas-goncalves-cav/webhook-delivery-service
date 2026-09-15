using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WebhookDelivery.Application.Common;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Application.Events;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.Api.Endpoints;

public sealed record PublishEventBody(string EventType, JsonElement Data);

public static class EventRoutes
{
    public static IEndpointRouteBuilder MapEventRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events", async (
            PublishEventBody body,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            var result = await publisher.PublishAsync(
                new PublishEventRequest(body.EventType, body.Data, idempotencyKey),
                cancellationToken);

            if (result.IsFailure)
            {
                return result.ToProblem();
            }

            // A duplicate returns 200 rather than 201: nothing was created,
            // and the caller gets the original event back.
            return result.Value.WasDuplicate
                ? Results.Ok(result.Value)
                : Results.Created($"/api/events/{result.Value.EventId}", result.Value);
        })
        .WithTags("Events")
        .WithName("PublishEvent")
        .WithSummary("Publishes an event and fans it out to subscribed endpoints")
        .WithDescription(
            "Send an Idempotency-Key header to make publishing safe to retry. " +
            "A repeat with the same key returns the original event and creates no new deliveries.")
        .Produces<PublishEventResponse>(StatusCodes.Status201Created)
        .Produces<PublishEventResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}

public static class DeliveryRoutes
{
    public static IEndpointRouteBuilder MapDeliveryRoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deliveries")
            .WithTags("Deliveries");

        group.MapGet("/", async (
            [FromQuery] Guid? endpointId,
            [FromQuery] Guid? eventId,
            [FromQuery] DeliveryStatus? status,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            // Nullable so the parameters are genuinely optional. A minimal API
            // treats a non nullable value type as required and returns 400 when
            // it is missing, which would make GET /api/deliveries fail.
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            IDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            var filter = new DeliveryFilter(
                endpointId,
                eventId,
                status,
                from,
                to,
                page is null or <= 0 ? 1 : page.Value,
                pageSize is null or <= 0 or > 100 ? 20 : pageSize.Value);

            var result = await service.SearchAsync(filter, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("ListDeliveries")
        .WithSummary("Searches delivery history")
        .WithDescription("Filter by status=Failed to list the dead letter queue.")
        .Produces<PagedResponse<DeliveryResponse>>();

        group.MapGet("/{id:guid}", async (
            Guid id,
            IDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetByIdAsync(id, cancellationToken);

            return result.IsSuccess ? Results.Ok(result.Value) : result.ToProblem();
        })
        .WithName("GetDelivery")
        .WithSummary("Gets one delivery with every attempt")
        .Produces<DeliveryResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/replay", async (
            Guid id,
            IDeliveryService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.ReplayAsync(id, cancellationToken);

            return result.IsSuccess ? Results.Accepted($"/api/deliveries/{id}", result.Value) : result.ToProblem();
        })
        .WithName("ReplayDelivery")
        .WithSummary("Requeues a failed or cancelled delivery")
        .WithDescription(
            "The attempt history is kept, so a replayed delivery shows the attempts " +
            "that caused it to fail alongside the new ones.")
        .Produces<DeliveryResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
