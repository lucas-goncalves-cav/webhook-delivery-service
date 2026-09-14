using WebhookDelivery.Application.Deliveries;

namespace WebhookDelivery.Worker;

public sealed record WorkerOptions
{
    /// <summary>How long to wait when the queue is empty.</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait after a full batch. Short, because a full batch means
    /// there is probably more waiting.
    /// </summary>
    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long to back off after an unexpected failure.</summary>
    public TimeSpan ErrorDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Polls for due deliveries and processes them in batches.
///
/// Several instances can run at once: the claim query takes row locks and
/// skips rows another worker already holds, so each instance gets a disjoint
/// batch.
/// </summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerOptions _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        WorkerOptions options,
        ILogger<DeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                // A scope per batch, so the DbContext does not accumulate
                // tracked entities across the lifetime of the process.
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();

                var result = await processor.ProcessBatchAsync(stoppingToken);

                delay = result.Claimed == 0 ? _options.IdleDelay : _options.BusyDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The worker must survive anything a batch throws. A database
                // outage should make it back off, not exit and leave the queue
                // unprocessed until someone notices.
                _logger.LogError(exception, "Batch failed. Backing off for {Delay}.", _options.ErrorDelay);

                delay = _options.ErrorDelay;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Delivery worker stopped.");
    }
}
