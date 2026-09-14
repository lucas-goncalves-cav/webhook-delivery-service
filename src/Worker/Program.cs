using Serilog;
using WebhookDelivery.Application;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Infrastructure;
using WebhookDelivery.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) =>
    configuration.ReadFrom.Configuration(builder.Configuration));

builder.Services.AddApplication(
    builder.Configuration.GetSection("Delivery").Get<DeliveryOptions>() ?? new DeliveryOptions());

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton(
    builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions());

builder.Services.AddHostedService<DeliveryWorker>();

var host = builder.Build();

await host.RunAsync();
