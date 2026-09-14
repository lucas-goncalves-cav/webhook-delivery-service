using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;

namespace WebhookDelivery.Infrastructure.Persistence;

public sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("Endpoints");
        builder.HasKey(endpoint => endpoint.Id);

        builder.Property(endpoint => endpoint.Name).IsRequired().HasMaxLength(200);
        builder.Property(endpoint => endpoint.Url).IsRequired().HasMaxLength(2000);
        builder.Property(endpoint => endpoint.Secret).IsRequired().HasMaxLength(200);
        builder.Property(endpoint => endpoint.DisabledReason).HasMaxLength(500);

        // The subscription list is small and always read as a whole, so a JSON
        // column avoids a join table without costing anything in practice.
        builder.Property<List<string>>("_subscribedEvents")
            .HasColumnName("SubscribedEvents")
            .HasConversion(
                values => string.Join(',', values),
                value => value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (left, right) => left!.SequenceEqual(right!),
                    values => values.Aggregate(0, (hash, value) => HashCode.Combine(hash, value.GetHashCode())),
                    values => values.ToList()))
            .HasMaxLength(2000)
            .IsRequired();

        builder.Ignore(endpoint => endpoint.SubscribedEvents);

        builder.HasIndex(endpoint => endpoint.Active);
    }
}

public sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("Events");
        builder.HasKey(webhookEvent => webhookEvent.Id);

        builder.Property(webhookEvent => webhookEvent.EventType).IsRequired().HasMaxLength(200);
        builder.Property(webhookEvent => webhookEvent.Payload).IsRequired();
        builder.Property(webhookEvent => webhookEvent.IdempotencyKey).HasMaxLength(200);

        builder.HasIndex(webhookEvent => webhookEvent.EventType);
        builder.HasIndex(webhookEvent => webhookEvent.OccurredAt);

        // A filtered unique index: idempotency keys are optional, and without
        // the filter every event without one would collide on NULL.
        builder.HasIndex(webhookEvent => webhookEvent.IdempotencyKey)
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDeliveryRecord>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryRecord> builder)
    {
        builder.ToTable("Deliveries");
        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Url).IsRequired().HasMaxLength(2000);
        builder.Property(delivery => delivery.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(delivery => delivery.LastError).HasMaxLength(1000);

        builder.HasOne(delivery => delivery.Event)
            .WithMany()
            .HasForeignKey(delivery => delivery.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(delivery => delivery.Endpoint)
            .WithMany()
            .HasForeignKey(delivery => delivery.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(delivery => delivery.Attempts)
            .WithOne()
            .HasForeignKey(attempt => attempt.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(delivery => delivery.Attempts)
            .HasField("_attempts")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // The index the worker's claim query depends on. Without it, finding
        // due deliveries scans the whole table on every poll.
        builder.HasIndex(delivery => new { delivery.Status, delivery.NextAttemptAt })
            .HasDatabaseName("IX_Deliveries_Status_NextAttemptAt");

        builder.HasIndex(delivery => delivery.EndpointId);
        builder.HasIndex(delivery => delivery.EventId);
        builder.HasIndex(delivery => delivery.CreatedAt);
    }
}

public sealed class WebhookDeliveryAttemptConfiguration : IEntityTypeConfiguration<WebhookDeliveryAttemptLog>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryAttemptLog> builder)
    {
        builder.ToTable("DeliveryAttempts");
        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.ResponseBody).HasMaxLength(4000);
        builder.Property(attempt => attempt.Error).HasMaxLength(1000);

        builder.Ignore(attempt => attempt.Succeeded);

        builder.HasIndex(attempt => new { attempt.DeliveryId, attempt.AttemptNumber });
    }
}
