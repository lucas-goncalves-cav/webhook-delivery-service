using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using WebhookDelivery.Api.Endpoints;
using WebhookDelivery.Api.Middleware;
using WebhookDelivery.Application;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Infrastructure;
using WebhookDelivery.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Webhook Delivery Service",
        Version = "v1",
        Description =
            "Registers endpoints, publishes events, and delivers them with HMAC signatures, " +
            "exponential backoff retries and a dead letter queue.",
        License = new OpenApiLicense { Name = "MIT" }
    });
});

builder.Services.AddApplication(
    builder.Configuration.GetSection("Delivery").Get<DeliveryOptions>() ?? new DeliveryOptions());

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "sqlserver",
        tags: ["ready"]);

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();

app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Webhook Delivery Service v1"));

app.MapEndpointRoutes();
app.MapEventRoutes();
app.MapDeliveryRoutes();

// Liveness answers "is this process alive", so it must not depend on the
// database. A database outage should make the service unready, not make
// the orchestrator kill and restart a perfectly healthy process.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

await ApplyMigrationsAsync(app);

app.Run();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<WebhookDbContext>();

    await context.Database.MigrateAsync();
}

public partial class Program;
