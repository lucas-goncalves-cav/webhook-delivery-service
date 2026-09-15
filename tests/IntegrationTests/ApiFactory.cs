using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Infrastructure.Persistence;

namespace WebhookDelivery.IntegrationTests;

/// <summary>
/// Runs the real API against an in memory database, with the outbound HTTP
/// sender replaced by a recording fake so the delivery pipeline can be driven
/// end to end without a network.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"webhooks-{Guid.NewGuid()}";

    public RecordingSender Sender { get; } = new();

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ApplyMigrationsOnStartup"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<WebhookDbContext>>();
            services.RemoveAll<DbContextOptions<WebhookDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<WebhookDbContext>();

            services.AddDbContext<WebhookDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IWebhookSender>();
            services.AddSingleton<IWebhookSender>(Sender);

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    public IServiceScope CreateScope() => Services.CreateScope();
}

public sealed class TestClock : IClock
{
    private DateTime _now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Stands in for the customer's server. Responses are configurable per URL so
/// a test can make one endpoint healthy and another one broken.
/// </summary>
public sealed class RecordingSender : IWebhookSender
{
    private readonly Dictionary<string, Queue<WebhookSendResult>> _scripted = [];
    private readonly Lock _gate = new();

    public List<WebhookSendRequest> Requests { get; } = [];

    public WebhookSendResult Default { get; set; } =
        WebhookSendResult.Ok(200, "OK", TimeSpan.FromMilliseconds(20));

    public void ScriptFor(string url, params WebhookSendResult[] results)
    {
        lock (_gate)
        {
            _scripted[url] = new Queue<WebhookSendResult>(results);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Requests.Clear();
            _scripted.Clear();
            Default = WebhookSendResult.Ok(200, "OK", TimeSpan.FromMilliseconds(20));
        }
    }

    public Task<WebhookSendResult> SendAsync(
        WebhookSendRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Requests.Add(request);

            if (_scripted.TryGetValue(request.Url, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(Default);
        }
    }
}
