using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Application.Deliveries;
using WebhookDelivery.Application.Endpoints;
using WebhookDelivery.Application.Events;

namespace WebhookDelivery.IntegrationTests;

/// <summary>
/// Drives the whole system through its public API: register an endpoint,
/// publish an event, run the worker's batch, inspect the delivery history.
/// </summary>
public class WebhookFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public WebhookFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Sender.Reset();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // -------------------------------------------------------------------------
    // Endpoint registration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RegisteringAnEndpointReturnsItsSigningSecretOnce()
    {
        var response = await CreateEndpointAsync("https://customer-a.example.com/hooks", ["order.created"]);

        response.Secret.Should().StartWith("whsec_").And.HaveLength(70);
        response.Endpoint.Active.Should().BeTrue();
        response.Endpoint.SubscribedEvents.Should().Equal("order.created");

        // Fetching it again never exposes the secret.
        var fetched = await _client.GetFromJsonAsync<EndpointResponse>(
            $"/api/endpoints/{response.Endpoint.Id}", JsonOptions);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(response.Endpoint.Id);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/hooks")]
    public async Task AnInvalidUrlIsRejected(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/endpoints", new
        {
            name = "Bad endpoint",
            url,
            subscribedEvents = new[] { "order.created" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnEndpointWithNoSubscriptionsIsRejected()
    {
        var response = await _client.PostAsJsonAsync("/api/endpoints", new
        {
            name = "No subscriptions",
            url = "https://example.com/hooks",
            subscribedEvents = Array.Empty<string>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RotatingTheSecretIssuesADifferentOne()
    {
        var created = await CreateEndpointAsync("https://customer-rotate.example.com/hooks", ["*"]);

        var response = await _client.PostAsync($"/api/endpoints/{created.Endpoint.Id}/rotate-secret", null);
        var rotated = await response.Content.ReadFromJsonAsync<EndpointWithSecretResponse>(JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rotated!.Secret.Should().NotBe(created.Secret).And.StartWith("whsec_");
    }

    [Fact]
    public async Task GettingAnUnknownEndpointReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/endpoints/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -------------------------------------------------------------------------
    // Publishing and fan out
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishingFansOutToEverySubscribedEndpoint()
    {
        var eventType = $"order.created.{Guid.NewGuid():N}";

        await CreateEndpointAsync($"https://a-{Guid.NewGuid():N}.example.com/hooks", [eventType]);
        await CreateEndpointAsync($"https://b-{Guid.NewGuid():N}.example.com/hooks", [eventType]);
        await CreateEndpointAsync($"https://c-{Guid.NewGuid():N}.example.com/hooks", ["something.else"]);

        var published = await PublishAsync(eventType, new { orderId = 123 });

        published.DeliveriesCreated.Should().Be(2);
    }

    [Fact]
    public async Task AWildcardSubscriptionMatchesEveryEventOfAPrefix()
    {
        var prefix = $"invoice{Guid.NewGuid():N}";

        await CreateEndpointAsync($"https://wildcard-{Guid.NewGuid():N}.example.com/hooks", [$"{prefix}.*"]);

        var paid = await PublishAsync($"{prefix}.paid", new { invoiceId = 1 });
        var voided = await PublishAsync($"{prefix}.voided", new { invoiceId = 2 });

        paid.DeliveriesCreated.Should().Be(1);
        voided.DeliveriesCreated.Should().Be(1);
    }

    [Fact]
    public async Task AnEventWithNoSubscribersIsStillRecorded()
    {
        var published = await PublishAsync($"nobody.listens.{Guid.NewGuid():N}", new { value = 1 });

        published.EventId.Should().NotBeEmpty();
        published.DeliveriesCreated.Should().Be(0);
    }

    /// <summary>
    /// A producer that times out and retries must not create the event twice.
    /// </summary>
    [Fact]
    public async Task PublishingTwiceWithTheSameIdempotencyKeyCreatesOneEvent()
    {
        var eventType = $"payment.received.{Guid.NewGuid():N}";
        var key = $"idem-{Guid.NewGuid():N}";

        await CreateEndpointAsync($"https://idem-{Guid.NewGuid():N}.example.com/hooks", [eventType]);

        var first = await PublishAsync(eventType, new { paymentId = 1 }, key);
        var second = await PublishAsync(eventType, new { paymentId = 1 }, key);

        first.WasDuplicate.Should().BeFalse();
        first.DeliveriesCreated.Should().Be(1);

        second.WasDuplicate.Should().BeTrue();
        second.EventId.Should().Be(first.EventId);
        second.DeliveriesCreated.Should().Be(0);
    }

    [Fact]
    public async Task ADeactivatedEndpointReceivesNoNewDeliveries()
    {
        var eventType = $"order.shipped.{Guid.NewGuid():N}";
        var created = await CreateEndpointAsync($"https://off-{Guid.NewGuid():N}.example.com/hooks", [eventType]);

        await _client.PostAsync($"/api/endpoints/{created.Endpoint.Id}/deactivate", null);

        var published = await PublishAsync(eventType, new { orderId = 9 });

        published.DeliveriesCreated.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Delivery
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulDeliveryIsRecordedWithItsAttempt()
    {
        var eventType = $"order.paid.{Guid.NewGuid():N}";
        var url = $"https://ok-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);
        _factory.Sender.ScriptFor(url, WebhookSendResult.Ok(200, "received", TimeSpan.FromMilliseconds(15)));

        await PublishAsync(eventType, new { orderId = 42 });
        await RunWorkerBatchAsync();

        var deliveries = await ListDeliveriesAsync(endpoint.Endpoint.Id);
        var delivery = deliveries.Items.Should().ContainSingle().Subject;

        delivery.Status.Should().Be("Delivered");
        delivery.AttemptCount.Should().Be(1);
        delivery.LastStatusCode.Should().Be(200);
        delivery.Attempts.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task TheSentRequestCarriesThePayloadAndTheEndpointSecret()
    {
        var eventType = $"order.created.{Guid.NewGuid():N}";
        var url = $"https://payload-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);

        await PublishAsync(eventType, new { orderId = 777, total = 99.9 });
        await RunWorkerBatchAsync();

        var sent = _factory.Sender.Requests.Should().ContainSingle(request => request.Url == url).Subject;

        sent.EventType.Should().Be(eventType);
        sent.Secret.Should().Be(endpoint.Secret);
        sent.Payload.Should().Contain("777");
        sent.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public async Task AFailedDeliveryIsRescheduledRatherThanLost()
    {
        var eventType = $"order.failed.{Guid.NewGuid():N}";
        var url = $"https://flaky-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);
        _factory.Sender.ScriptFor(url, WebhookSendResult.HttpFailure(503, "down", TimeSpan.FromMilliseconds(30)));

        await PublishAsync(eventType, new { orderId = 1 });
        await RunWorkerBatchAsync();

        var delivery = (await ListDeliveriesAsync(endpoint.Endpoint.Id)).Items.Single();

        delivery.Status.Should().Be("Scheduled");
        delivery.NextAttemptAt.Should().NotBeNull();
        delivery.LastStatusCode.Should().Be(503);
    }

    /// <summary>
    /// A 4xx is not retried, so it reaches the dead letter queue on the first
    /// attempt instead of consuming the whole retry budget.
    /// </summary>
    [Fact]
    public async Task AClientErrorGoesStraightToTheDeadLetterQueue()
    {
        var eventType = $"order.rejected.{Guid.NewGuid():N}";
        var url = $"https://badrequest-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);
        _factory.Sender.ScriptFor(url, WebhookSendResult.HttpFailure(422, "invalid", TimeSpan.FromMilliseconds(10)));

        await PublishAsync(eventType, new { orderId = 2 });
        await RunWorkerBatchAsync();

        var delivery = (await ListDeliveriesAsync(endpoint.Endpoint.Id)).Items.Single();

        delivery.Status.Should().Be("Failed");
        delivery.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task TheDeadLetterQueueCanBeListedByStatus()
    {
        var eventType = $"order.dlq.{Guid.NewGuid():N}";
        var url = $"https://dlq-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);
        _factory.Sender.ScriptFor(url, WebhookSendResult.HttpFailure(400, "bad", TimeSpan.Zero));

        await PublishAsync(eventType, new { orderId = 3 });
        await RunWorkerBatchAsync();

        var failed = await _client.GetFromJsonAsync<PagedResponseDto<DeliveryResponse>>(
            $"/api/deliveries?endpointId={endpoint.Endpoint.Id}&status=Failed", JsonOptions);

        failed!.Items.Should().ContainSingle();
    }

    // -------------------------------------------------------------------------
    // Replay
    // -------------------------------------------------------------------------

    /// <summary>
    /// The reason a dead letter queue is worth having: the customer fixes
    /// their endpoint and asks for the events they missed.
    /// </summary>
    [Fact]
    public async Task AFailedDeliveryCanBeReplayedAndThenSucceed()
    {
        var eventType = $"order.replay.{Guid.NewGuid():N}";
        var url = $"https://replay-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);
        _factory.Sender.ScriptFor(url, WebhookSendResult.HttpFailure(400, "bad", TimeSpan.Zero));

        await PublishAsync(eventType, new { orderId = 4 });
        await RunWorkerBatchAsync();

        var failed = (await ListDeliveriesAsync(endpoint.Endpoint.Id)).Items.Single();
        failed.Status.Should().Be("Failed");

        // The customer fixes their endpoint.
        _factory.Sender.ScriptFor(url, WebhookSendResult.Ok(200, "fixed", TimeSpan.Zero));

        var replayResponse = await _client.PostAsync($"/api/deliveries/{failed.Id}/replay", null);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await RunWorkerBatchAsync();

        var replayed = (await ListDeliveriesAsync(endpoint.Endpoint.Id)).Items.Single();

        replayed.Status.Should().Be("Delivered");
        replayed.AttemptCount.Should().Be(2);
        replayed.Attempts.Should().HaveCount(2, "the failed attempt is kept for auditing");
    }

    [Fact]
    public async Task ReplayingADeliveredDeliveryIsRejected()
    {
        var eventType = $"order.noreplay.{Guid.NewGuid():N}";
        var url = $"https://noreplay-{Guid.NewGuid():N}.example.com/hooks";

        var endpoint = await CreateEndpointAsync(url, [eventType]);

        await PublishAsync(eventType, new { orderId = 5 });
        await RunWorkerBatchAsync();

        var delivered = (await ListDeliveriesAsync(endpoint.Endpoint.Id)).Items.Single();
        delivered.Status.Should().Be("Delivered");

        var response = await _client.PostAsync($"/api/deliveries/{delivered.Id}/replay", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReplayingAnUnknownDeliveryReturnsNotFound()
    {
        var response = await _client.PostAsync($"/api/deliveries/{Guid.NewGuid()}/replay", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -------------------------------------------------------------------------
    // Health
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TheLivenessProbeResponds()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<EndpointWithSecretResponse> CreateEndpointAsync(string url, string[] events)
    {
        var response = await _client.PostAsJsonAsync("/api/endpoints", new
        {
            name = $"Endpoint {Guid.NewGuid():N}"[..20],
            url,
            subscribedEvents = events
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<EndpointWithSecretResponse>(JsonOptions))!;
    }

    private async Task<PublishEventResponse> PublishAsync(string eventType, object data, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(new { eventType, data })
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<PublishEventResponse>(JsonOptions))!;
    }

    /// <summary>
    /// Runs one batch the way the worker would, without starting the worker.
    /// </summary>
    private async Task RunWorkerBatchAsync()
    {
        using var scope = _factory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();

        await processor.ProcessBatchAsync();
    }

    private async Task<PagedResponseDto<DeliveryResponse>> ListDeliveriesAsync(Guid endpointId) =>
        (await _client.GetFromJsonAsync<PagedResponseDto<DeliveryResponse>>(
            $"/api/deliveries?endpointId={endpointId}", JsonOptions))!;

    private sealed record PagedResponseDto<T>(
        IReadOnlyCollection<T> Items,
        int TotalItems,
        int Page,
        int PageSize,
        int TotalPages,
        bool HasNext);
}
