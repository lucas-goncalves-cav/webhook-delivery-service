using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WebhookDelivery.Domain.Common;

namespace WebhookDelivery.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException exception)
        {
            _logger.LogWarning(exception, "Domain rule violated on {Path}", context.Request.Path);

            await WriteProblemAsync(context, HttpStatusCode.BadRequest, "Business rule violation", exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled exception on {Path}", context.Request.Path);

            var detail = _environment.IsDevelopment()
                ? exception.ToString()
                : "An unexpected error occurred while processing the request.";

            await WriteProblemAsync(context, HttpStatusCode.InternalServerError, "Unexpected error", detail);
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        HttpStatusCode status,
        string title,
        string detail)
    {
        var problem = new ProblemDetails
        {
            Status = (int)status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        context.Response.StatusCode = problem.Status.Value;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }
}
