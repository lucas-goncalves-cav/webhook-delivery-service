namespace WebhookDelivery.Domain.Deliveries;

/// <summary>
/// Decides whether a failed delivery is retried, and when.
///
/// The schedule is exponential with jitter:
///
///   attempt 1 failed -> retry in ~1 minute
///   attempt 2 failed -> retry in ~5 minutes
///   attempt 3 failed -> retry in ~15 minutes
///   attempt 4 failed -> retry in ~1 hour
///   attempt 5 failed -> retry in ~6 hours
///   attempt 6 failed -> dead letter
///
/// Jitter matters more than it looks. Without it, an endpoint that goes down
/// and comes back gets every pending delivery at the same instant, which is
/// how a recovering service is knocked over again by its own backlog.
/// </summary>
public sealed class RetryPolicy
{
    private static readonly TimeSpan[] DefaultSchedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6)
    ];

    private readonly TimeSpan[] _schedule;
    private readonly double _jitterFactor;

    public RetryPolicy(TimeSpan[]? schedule = null, double jitterFactor = 0.2)
    {
        if (jitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter must be between 0 and 1.");
        }

        _schedule = schedule is { Length: > 0 } ? schedule : DefaultSchedule;
        _jitterFactor = jitterFactor;
    }

    public int MaxAttempts => _schedule.Length + 1;

    /// <summary>
    /// Returns the delay before the next attempt, or null when the policy is
    /// exhausted and the delivery should be dead lettered.
    /// </summary>
    public TimeSpan? NextDelay(int attemptCount, Random? random = null)
    {
        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount), "Attempt count starts at 1.");
        }

        if (attemptCount > _schedule.Length)
        {
            return null;
        }

        var baseDelay = _schedule[attemptCount - 1];

        return ApplyJitter(baseDelay, random ?? Random.Shared);
    }

    public DateTime? NextAttemptAt(int attemptCount, DateTime from, Random? random = null)
    {
        var delay = NextDelay(attemptCount, random);

        return delay is null ? null : from.Add(delay.Value);
    }

    /// <summary>
    /// Whether a response is worth retrying.
    ///
    /// 4xx means the request was wrong and sending it again changes nothing,
    /// so those are terminal. The exceptions are 408 and 429, which are the
    /// server saying "not now" rather than "never".
    /// </summary>
    public static bool IsRetryable(int statusCode) => statusCode switch
    {
        408 => true,                 // Request Timeout
        429 => true,                 // Too Many Requests
        >= 500 and < 600 => true,    // Server errors
        >= 400 and < 500 => false,   // Client errors, retrying will not help
        _ => false
    };

    private TimeSpan ApplyJitter(TimeSpan delay, Random random)
    {
        if (_jitterFactor == 0)
        {
            return delay;
        }

        // Full range around the base delay: a factor of 0.2 gives 80% to 120%.
        var offset = (random.NextDouble() * 2 - 1) * _jitterFactor;

        return TimeSpan.FromTicks((long)(delay.Ticks * (1 + offset)));
    }
}
