using Microsoft.Extensions.DependencyInjection;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Application.Endpoints;
using WebhookDelivery.Application.Events;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, DeliveryOptions? options = null)
    {
        services.AddSingleton(options ?? new DeliveryOptions());
        services.AddSingleton(new RetryPolicy());
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<IEndpointService, EndpointService>();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<IDeliveryService, DeliveryService>();
        services.AddScoped<DeliveryProcessor>();

        return services;
    }
}
