using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Infrastructure.Http;
using WebhookDelivery.Infrastructure.Persistence;

namespace WebhookDelivery.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not configured.");

        services.AddDbContext<WebhookDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<WebhookDbContext>());
        services.AddScoped<IEndpointRepository, EndpointRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IDeliveryRepository, DeliveryRepository>();

        var senderOptions = configuration.GetSection("Delivery:Sender").Get<SenderOptions>() ?? new SenderOptions();
        services.AddSingleton(senderOptions);

        services
            .AddHttpClient<IWebhookSender, HttpWebhookSender>(HttpWebhookSender.HttpClientName, client =>
            {
                // The per request timeout is enforced by the sender's own
                // cancellation token, so this one is a generous outer bound.
                client.Timeout = senderOptions.Timeout.Add(TimeSpan.FromSeconds(5));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A customer endpoint that redirects is not followed. Following
                // redirects on an outbound webhook is an SSRF vector, since the
                // destination could redirect us at an internal address.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });

        return services;
    }
}
