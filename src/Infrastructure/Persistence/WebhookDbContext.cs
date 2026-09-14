using Microsoft.EntityFrameworkCore;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Infrastructure.Persistence;

public class WebhookDbContext : DbContext, IUnitOfWork
{
    public WebhookDbContext(DbContextOptions<WebhookDbContext> options) : base(options)
    {
    }

    public DbSet<WebhookEndpoint> Endpoints => Set<WebhookEndpoint>();

    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();

    public DbSet<WebhookDeliveryRecord> Deliveries => Set<WebhookDeliveryRecord>();

    public DbSet<WebhookDeliveryAttemptLog> DeliveryAttempts => Set<WebhookDeliveryAttemptLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WebhookDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
