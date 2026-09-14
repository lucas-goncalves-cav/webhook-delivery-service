using FluentAssertions;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.UnitTests.Domain;

public class RetryPolicyTests
{
    /// <summary>
    /// Jitter is disabled here so the schedule itself can be asserted exactly.
    /// It gets its own tests below.
    /// </summary>
    private static RetryPolicy Deterministic() => new(jitterFactor: 0);

    [Theory]
    [InlineData(1, 1)]      // first failure, retry in 1 minute
    [InlineData(2, 5)]      // then 5
    [InlineData(3, 15)]     // then 15
    [InlineData(4, 60)]     // then 1 hour
    [InlineData(5, 360)]    // then 6 hours
    public void TheScheduleBacksOffExponentially(int attemptCount, int expectedMinutes)
    {
        var delay = Deterministic().NextDelay(attemptCount);

        delay.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void TheSixthFailureExhaustsThePolicy()
    {
        Deterministic().NextDelay(6).Should().BeNull();
    }

    [Fact]
    public void MaxAttemptsIsTheScheduleLengthPlusTheFirstTry()
    {
        Deterministic().MaxAttempts.Should().Be(6);
    }

    [Fact]
    public void AttemptCountsBelowOneAreRejected()
    {
        var act = () => Deterministic().NextDelay(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ACustomScheduleReplacesTheDefault()
    {
        var policy = new RetryPolicy([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)], jitterFactor: 0);

        policy.MaxAttempts.Should().Be(3);
        policy.NextDelay(1).Should().Be(TimeSpan.FromSeconds(10));
        policy.NextDelay(2).Should().Be(TimeSpan.FromSeconds(30));
        policy.NextDelay(3).Should().BeNull();
    }

    [Fact]
    public void NextAttemptAtAddsTheDelayToTheGivenTime()
    {
        var now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        Deterministic().NextAttemptAt(1, now).Should().Be(now.AddMinutes(1));
        Deterministic().NextAttemptAt(6, now).Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Jitter
    // -------------------------------------------------------------------------

    /// <summary>
    /// The reason jitter exists: without it, an endpoint that recovers gets
    /// every pending delivery at the same instant and falls over again.
    /// </summary>
    [Fact]
    public void JitterSpreadsRetriesInsteadOfBunchingThem()
    {
        var policy = new RetryPolicy(jitterFactor: 0.2);

        var delays = Enumerable.Range(0, 200)
            .Select(_ => policy.NextDelay(2)!.Value)
            .ToList();

        delays.Distinct().Should().HaveCountGreaterThan(100, "jittered delays must not all land on the same value");
    }

    [Fact]
    public void JitterStaysWithinTheConfiguredFactor()
    {
        var policy = new RetryPolicy(jitterFactor: 0.2);
        var baseDelay = TimeSpan.FromMinutes(5);

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var delay = policy.NextDelay(2)!.Value;

            delay.Should().BeGreaterThanOrEqualTo(baseDelay * 0.8);
            delay.Should().BeLessThanOrEqualTo(baseDelay * 1.2);
        }
    }

    [Fact]
    public void JitterIsReproducibleWithASeededRandom()
    {
        var policy = new RetryPolicy(jitterFactor: 0.2);

        var first = policy.NextDelay(2, new Random(42));
        var second = policy.NextDelay(2, new Random(42));

        first.Should().Be(second);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void AnOutOfRangeJitterFactorIsRejected(double jitterFactor)
    {
        var act = () => new RetryPolicy(jitterFactor: jitterFactor);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------
    // Which responses are worth retrying
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]   // Request Timeout
    [InlineData(429)]   // Too Many Requests
    public void ServerErrorsAndBackpressureAreRetried(int statusCode)
    {
        RetryPolicy.IsRetryable(statusCode).Should().BeTrue();
    }

    /// <summary>
    /// A 4xx means the request itself was wrong. Sending it again unchanged
    /// produces the same answer, so retrying only wastes both sides' capacity.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(422)]
    public void ClientErrorsAreNotRetried(int statusCode)
    {
        RetryPolicy.IsRetryable(statusCode).Should().BeFalse();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(301)]
    public void SuccessAndRedirectsAreNotRetried(int statusCode)
    {
        RetryPolicy.IsRetryable(statusCode).Should().BeFalse();
    }

    /// <summary>
    /// The total window a delivery gets before being dead lettered, which is
    /// the number customers actually ask about.
    /// </summary>
    [Fact]
    public void TheDefaultPolicySpansRoughlySevenHours()
    {
        var policy = Deterministic();

        var total = Enumerable.Range(1, policy.MaxAttempts - 1)
            .Select(attempt => policy.NextDelay(attempt)!.Value)
            .Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay);

        total.Should().Be(TimeSpan.FromMinutes(1 + 5 + 15 + 60 + 360));
        total.TotalHours.Should().BeApproximately(7.35, 0.01);
    }
}
