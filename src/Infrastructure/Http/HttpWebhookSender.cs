using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using WebhookDelivery.Application.Abstractions;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.Infrastructure.Http;

public sealed record SenderOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxResponseBodyCharacters { get; init; } = 2000;

    public string UserAgent { get; init; } = "WebhookDelivery/1.0";
}

/// <summary>
/// Sends one signed HTTP request to a customer endpoint.
///
/// Everything that can go wrong on the way out is turned into a
/// <see cref="WebhookSendResult"/> rather than an exception, because a
/// customer's DNS failing is an expected outcome of this system, not an
/// error in it.
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender
{
    public const string HttpClientName = "webhook-delivery";

    private readonly HttpClient _httpClient;
    private readonly SenderOptions _options;
    private readonly ILogger<HttpWebhookSender> _logger;

    public HttpWebhookSender(HttpClient httpClient, SenderOptions options, ILogger<HttpWebhookSender> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<WebhookSendResult> SendAsync(
        WebhookSendRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.Timeout);

        try
        {
            using var message = BuildRequest(request);
            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseContentRead,
                timeoutSource.Token);

            stopwatch.Stop();

            var body = await ReadBodyAsync(response, timeoutSource.Token);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return WebhookSendResult.Ok(statusCode, body, stopwatch.Elapsed);
            }

            _logger.LogWarning(
                "Delivery {DeliveryId} attempt {Attempt} to {Url} returned {StatusCode}.",
                request.DeliveryId,
                request.AttemptNumber,
                request.Url,
                statusCode);

            return WebhookSendResult.HttpFailure(statusCode, body, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker is shutting down. Let this propagate so the delivery
            // is left untouched and picked up by the next run.
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            return WebhookSendResult.TransportFailure(
                $"The request timed out after {_options.Timeout.TotalSeconds:F0}s.",
                stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();

            return WebhookSendResult.TransportFailure(
                $"{exception.Message}{(exception.InnerException is null ? string.Empty : $" ({exception.InnerException.Message})")}",
                stopwatch.Elapsed);
        }
    }

    private HttpRequestMessage BuildRequest(WebhookSendRequest request)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var signature = WebhookSignature.Create(request.Payload, request.Secret, timestamp);

        var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
        };

        message.Headers.UserAgent.ParseAdd(_options.UserAgent);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        message.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, signature);
        message.Headers.TryAddWithoutValidation(
            WebhookSignature.TimestampHeaderName,
            timestamp.ToUnixTimeSeconds().ToString());
        message.Headers.TryAddWithoutValidation(WebhookSignature.EventIdHeaderName, request.EventId.ToString());
        message.Headers.TryAddWithoutValidation(WebhookSignature.EventTypeHeaderName, request.EventType);
        message.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryIdHeaderName, request.DeliveryId.ToString());
        message.Headers.TryAddWithoutValidation(
            WebhookSignature.AttemptHeaderName,
            request.AttemptNumber.ToString());

        return message;
    }

    private async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return body.Length <= _options.MaxResponseBodyCharacters
                ? body
                : body[.._options.MaxResponseBodyCharacters];
        }
        catch (Exception)
        {
            // A response body that cannot be read does not change the outcome
            // of the delivery, so it is not worth failing over.
            return null;
        }
    }
}
