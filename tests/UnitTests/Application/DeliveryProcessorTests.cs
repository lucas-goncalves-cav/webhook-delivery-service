using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Domain.Deliveries;
using WebhookDelivery.Domain.Endpoints;
using WebhookDelivery.Domain.Events;
using WebhookDelivery.UnitTests.Fakes;

namespace WebhookDelivery.UnitTests.Application;

public class DeliveryProcessorTests
{
    private sealed record Harness(
        DeliveryProcessor Processor,
        InMemoryStore Store,
        ScriptedSender Sender,
        FixedClock Clock,
        WebhookEndpoint Endpoint,
        WebhookDeliveryRecord Delivery);

    private static Harness Build(ScriptedSender? sender = null, DeliveryOptions? options = null)
    {
        var store = new InMemoryStore();
        var clock = new FixedClock();
        var actualSender = sender ?? new ScriptedSender();

        var endpoint = new WebhookEndpoint(
            "Customer API",
            new Uri("https://customer.example.com/webhooks"),
            ["order.created"]);

        var webhookEvent = new WebhookEvent("order.created", """{"orderId":123}""");
        var delivery = new WebhookDeliveryRecord(webhookEvent, endpoint, clock.UtcNow);

        store.Endpoints.Add(endpoint);
        store.Events.Add(webhookEvent);
        store.Deliveries.Add(delivery);

        var processor = new DeliveryProcessor(
            store,
            store,
            store,
            actualSender,
            store,
            new RetryPolicy(jitterFactor: 0),
            clock,
            options ?? new DeliveryOptions(),
            NullLogger<DeliveryProcessor>.Instance);

        return new Harness(processor, store, actualSender, clock, endpoint, delivery);
    }

    // -------------------------------------------------------------------------
    // The happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulDeliveryIsMarkedDeliveredAndNotRetried()
    {
        var harness = Build(new ScriptedSender().ThenSucceed(200));

        var result = await harness.Processor.ProcessBatchAsync();

        result.Should().Be(new BatchResult(1, 1, 0, 0));
        harness.Delivery.Status.Should().Be(DeliveryStatus.Delivered);
        harness.Delivery.AttemptCount.Should().Be(1);
        harness.Delivery.NextAttemptAt.Should().BeNull();
        harness.Delivery.DeliveredAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task AnyTwoHundredResponseCountsAsDelivered(int statusCode)
    {
        var harness = Build(new ScriptedSender().ThenSucceed(statusCode));

        await harness.Processor.ProcessBatchAsync();

        harness.Delivery.Status.Should().Be(DeliveryStatus.Delivered);
    }

    [Fact]
    public async Task TheRequestCarriesEverythingTheReceiverNeeds()
    {
        var harness = Build(new ScriptedSender().ThenSucceed());

        await harness.Processor.ProcessBatchAsync();

        var sent = harness.Sender.Requests.Should().ContainSingle().Subject;
        sent.Url.Should().Be("https://customer.example.com/webhooks");
        sent.EventType.Should().Be("order.created");
        sent.Payload.Should().Be("""{"orderId":123}""");
        sent.Secret.Should().StartWith("whsec_");
        sent.AttemptNumber.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Retry behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AServerErrorSchedulesARetryWithTheBackoffDelay()
    {
        var harness = Build(new ScriptedSender().ThenFailWith(503));

        var result = await harness.Processor.ProcessBatchAsync();

        result.Should().Be(new BatchResult(1, 0, 1, 0));
        harness.Delivery.Status.Should().Be(DeliveryStatus.Scheduled);
        harness.Delivery.NextAttemptAt.Should().Be(harness.Clock.UtcNow.AddMinutes(1));
        harness.Delivery.LastStatusCode.Should().Be(503);
    }

    [Fact]
    public async Task ATransportFailureIsRetriedEvenWithoutAStatusCode()
    {
        var harness = Build(new ScriptedSender().ThenTimeOut());

        await harness.Processor.ProcessBatchAsync();

        harness.Delivery.Status.Should().Be(DeliveryStatus.Scheduled);
        harness.Delivery.LastStatusCode.Should().BeNull();
        harness.Delivery.LastError.Should().Contain("timed out");
    }

    /// <summary>
    /// Walks the whole retry schedule, advancing the clock between batches the
    /// way the worker would, and asserts the delivery is dead lettered only
    /// after the policy is exhausted.
    /// </summary>
    [Fact]
    public async Task AfterTheScheduleIsExhaustedTheDeliveryIsDeadLettered()
    {
        var sender = new ScriptedSender(WebhookSendResult.HttpFailure(500, "boom", TimeSpan.FromMilliseconds(50)));
        var harness = Build(sender);

        var delays = new[] { 1, 5, 15, 60, 360 };

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            await harness.Processor.ProcessBatchAsync();

            harness.Delivery.Status.Should().Be(DeliveryStatus.Scheduled, "attempt {0} should reschedule", attempt + 1);
            harness.Clock.Advance(TimeSpan.FromMinutes(delays[attempt]));
        }

        // The sixth attempt exhausts the policy.
        var final = await harness.Processor.ProcessBatchAsync();

        final.DeadLettered.Should().Be(1);
        harness.Delivery.Status.Should().Be(DeliveryStatus.Failed);
        harness.Delivery.AttemptCount.Should().Be(6);
        harness.Delivery.NextAttemptAt.Should().BeNull();
        harness.Delivery.FailedAt.Should().NotBeNull();
        harness.Delivery.Attempts.Should().HaveCount(6);
    }

    [Fact]
    public async Task ADeliveryThatEventuallySucceedsStopsRetrying()
    {
        var sender = new ScriptedSender()
            .ThenFailWith(503)
            .ThenFailWith(502)
            .ThenSucceed(200);

        var harness = Build(sender);

        await harness.Processor.ProcessBatchAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Processor.ProcessBatchAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Processor.ProcessBatchAsync();

        harness.Delivery.Status.Should().Be(DeliveryStatus.Delivered);
        harness.Delivery.AttemptCount.Should().Be(3);
        harness.Delivery.Attempts.Count(attempt => attempt.Succeeded).Should().Be(1);
    }

    [Fact]
    public async Task ADeliveryIsNotAttemptedBeforeItsNextAttemptTime()
    {
        var harness = Build(new ScriptedSender().ThenFailWith(500));

        await harness.Processor.ProcessBatchAsync();

        // The clock has not moved, so nothing is due.
        var second = await harness.Processor.ProcessBatchAsync();

        second.Claimed.Should().Be(0);
        harness.Sender.Requests.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // Non retryable responses
    // -------------------------------------------------------------------------

    /// <summary>
    /// A 4xx means the request was wrong. Burning five more attempts on it
    /// wastes capacity on both sides, so it is dead lettered immediately.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task AClientErrorIsDeadLetteredOnTheFirstAttempt(int statusCode)
    {
        var harness = Build(new ScriptedSender().ThenFailWith(statusCode));

        var result = await harness.Processor.ProcessBatchAsync();

        result.DeadLettered.Should().Be(1);
        harness.Delivery.Status.Should().Be(DeliveryStatus.Failed);
        harness.Delivery.AttemptCount.Should().Be(1);
        harness.Delivery.LastError.Should().Contain("Not retryable");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task TimeoutAndRateLimitAreRetriedDespiteBeingFourHundreds(int statusCode)
    {
        var harness = Build(new ScriptedSender().ThenFailWith(statusCode));

        await harness.Processor.ProcessBatchAsync();

        harness.Delivery.Status.Should().Be(DeliveryStatus.Scheduled);
    }

    // -------------------------------------------------------------------------
    // Endpoint health
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulDeliveryResetsTheFailureCounter()
    {
        var sender = new ScriptedSender().ThenFailWith(500).ThenSucceed();
        var harness = Build(sender);

        await harness.Processor.ProcessBatchAsync();
        harness.Endpoint.ConsecutiveFailures.Should().Be(1);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Processor.ProcessBatchAsync();

        harness.Endpoint.ConsecutiveFailures.Should().Be(0);
    }

    /// <summary>
    /// An endpoint that has been dead for a long time stops consuming worker
    /// capacity forever.
    /// </summary>
    [Fact]
    public async Task AnEndpointIsDisabledAfterTooManyConsecutiveFailures()
    {
        var sender = new ScriptedSender(WebhookSendResult.HttpFailure(500, "boom", TimeSpan.FromMilliseconds(10)));
        var harness = Build(sender, new DeliveryOptions { DisableEndpointAfterConsecutiveFailures = 3 });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Processor.ProcessBatchAsync();
            harness.Clock.Advance(TimeSpan.FromHours(1));
        }

        harness.Endpoint.Active.Should().BeFalse();
        harness.Endpoint.DisabledReason.Should().Contain("consecutive failed deliveries");
        harness.Endpoint.DisabledAt.Should().NotBeNull();
    }

    /// <summary>
    /// Cancelled rather than failed: the dead letter queue should hold things
    /// that could not be delivered, not things that were deliberately stopped.
    /// </summary>
    [Fact]
    public async Task ADeliveryToADisabledEndpointIsCancelledWithoutBeingSent()
    {
        var harness = Build(new ScriptedSender().ThenSucceed());
        harness.Endpoint.Deactivate("Disabled by the customer.");

        await harness.Processor.ProcessBatchAsync();

        harness.Delivery.Status.Should().Be(DeliveryStatus.Cancelled);
        harness.Sender.Requests.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Batching
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyQueueDoesNoWork()
    {
        var harness = Build();
        harness.Store.Deliveries.Clear();

        var result = await harness.Processor.ProcessBatchAsync();

        result.Should().Be(new BatchResult(0, 0, 0, 0));
        harness.Store.SaveCount.Should().Be(0);
        harness.Sender.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TheBatchSizeLimitsHowManyDeliveriesAreClaimed()
    {
        var harness = Build(new ScriptedSender(), new DeliveryOptions { BatchSize = 2 });
        var webhookEvent = harness.Store.Events[0];

        for (var index = 0; index < 5; index++)
        {
            harness.Store.Deliveries.Add(new WebhookDeliveryRecord(webhookEvent, harness.Endpoint, harness.Clock.UtcNow));
        }

        var result = await harness.Processor.ProcessBatchAsync();

        result.Claimed.Should().Be(2);
    }

    [Fact]
    public async Task AllOutcomesAreCountedInOneBatch()
    {
        var store = new InMemoryStore();
        var clock = new FixedClock();

        var endpoint = new WebhookEndpoint("API", new Uri("https://example.com/hook"), ["*"]);
        var webhookEvent = new WebhookEvent("order.created", "{}");

        store.Endpoints.Add(endpoint);
        store.Events.Add(webhookEvent);

        var succeeding = new WebhookDeliveryRecord(webhookEvent, endpoint, clock.UtcNow);
        var retrying = new WebhookDeliveryRecord(webhookEvent, endpoint, clock.UtcNow);
        var deadLettering = new WebhookDeliveryRecord(webhookEvent, endpoint, clock.UtcNow);

        store.Deliveries.AddRange([succeeding, retrying, deadLettering]);

        var sender = new ScriptedSender()
            .ThenSucceed(200)
            .ThenFailWith(503)
            .ThenFailWith(400);

        var processor = new DeliveryProcessor(
            store, store, store, sender, store,
            new RetryPolicy(jitterFactor: 0), clock, new DeliveryOptions(),
            NullLogger<DeliveryProcessor>.Instance);

        var result = await processor.ProcessBatchAsync();

        result.Claimed.Should().Be(3);
        result.Delivered.Should().Be(1);
        result.Rescheduled.Should().Be(1);
        result.DeadLettered.Should().Be(1);
    }

    [Fact]
    public async Task TheBatchIsSavedOnceRatherThanPerDelivery()
    {
        var harness = Build(new ScriptedSender(), new DeliveryOptions { BatchSize = 10 });
        var webhookEvent = harness.Store.Events[0];

        for (var index = 0; index < 4; index++)
        {
            harness.Store.Deliveries.Add(new WebhookDeliveryRecord(webhookEvent, harness.Endpoint, harness.Clock.UtcNow));
        }

        await harness.Processor.ProcessBatchAsync();

        harness.Store.SaveCount.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // Attempt history
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EveryAttemptIsRecordedWithItsOutcome()
    {
        var sender = new ScriptedSender()
            .ThenFailWith(503)
            .ThenTimeOut()
            .ThenSucceed(201);

        var harness = Build(sender);

        await harness.Processor.ProcessBatchAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Processor.ProcessBatchAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Processor.ProcessBatchAsync();

        var attempts = harness.Delivery.Attempts.OrderBy(attempt => attempt.AttemptNumber).ToList();

        attempts.Should().HaveCount(3);
        attempts[0].StatusCode.Should().Be(503);
        attempts[0].Succeeded.Should().BeFalse();
        attempts[1].StatusCode.Should().BeNull();
        attempts[1].Error.Should().Contain("timed out");
        attempts[2].StatusCode.Should().Be(201);
        attempts[2].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AttemptNumbersAreSequential()
    {
        var sender = new ScriptedSender(WebhookSendResult.HttpFailure(500, null, TimeSpan.Zero));
        var harness = Build(sender);

        for (var index = 0; index < 3; index++)
        {
            await harness.Processor.ProcessBatchAsync();
            harness.Clock.Advance(TimeSpan.FromHours(1));
        }

        harness.Delivery.Attempts
            .Select(attempt => attempt.AttemptNumber)
            .Should().Equal(1, 2, 3);
    }
}
