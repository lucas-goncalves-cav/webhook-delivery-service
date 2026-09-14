using FluentAssertions;
using WebhookDelivery.Domain.Deliveries;

namespace WebhookDelivery.UnitTests.Domain;

public class WebhookSignatureTests
{
    private const string Secret = "whsec_test_secret_value_for_unit_tests";
    private const string Payload = """{"event":"order.created","data":{"orderId":123}}""";

    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateProducesAHeaderWithATimestampAndASignature()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);

        header.Should().StartWith($"t={Now.ToUnixTimeSeconds()},v1=");
        header.Split("v1=")[1].Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void AFreshSignatureVerifies()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);

        WebhookSignature.Verify(Payload, Secret, header, Now).Should().BeTrue();
    }

    [Fact]
    public void TheSameInputAlwaysProducesTheSameSignature()
    {
        var first = WebhookSignature.Create(Payload, Secret, Now);
        var second = WebhookSignature.Create(Payload, Secret, Now);

        first.Should().Be(second);
    }

    [Fact]
    public void ATamperedPayloadFailsVerification()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var tampered = Payload.Replace("123", "999");

        WebhookSignature.Verify(tampered, Secret, header, Now).Should().BeFalse();
    }

    [Fact]
    public void TheWrongSecretFailsVerification()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);

        WebhookSignature.Verify(Payload, "whsec_a_different_secret", header, Now).Should().BeFalse();
    }

    /// <summary>
    /// The reason the timestamp is signed rather than just sent alongside:
    /// a captured request stops being replayable once it falls outside the
    /// tolerance window.
    /// </summary>
    [Fact]
    public void AStaleSignatureIsRejected()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var muchLater = Now.AddMinutes(10);

        WebhookSignature.Verify(Payload, Secret, header, muchLater).Should().BeFalse();
    }

    [Fact]
    public void ASignatureInsideTheToleranceWindowIsAccepted()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var slightlyLater = Now.AddMinutes(4);

        WebhookSignature.Verify(Payload, Secret, header, slightlyLater).Should().BeTrue();
    }

    /// <summary>
    /// A timestamp far in the future is as suspicious as one far in the past,
    /// and clock skew between the sender and receiver should not open a hole.
    /// </summary>
    [Fact]
    public void ASignatureTimestampedInTheFutureIsRejected()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now.AddMinutes(10));

        WebhookSignature.Verify(Payload, Secret, header, Now).Should().BeFalse();
    }

    [Fact]
    public void ACustomToleranceIsHonoured()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var later = Now.AddMinutes(30);

        WebhookSignature.Verify(Payload, Secret, header, later, TimeSpan.FromHours(1)).Should().BeTrue();
        WebhookSignature.Verify(Payload, Secret, header, later, TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v1=abc")]                      // missing timestamp
    [InlineData("t=1736947200")]                // missing signature
    [InlineData("t=notanumber,v1=abc")]
    public void AMalformedHeaderIsRejectedRatherThanThrowing(string header)
    {
        var act = () => WebhookSignature.Verify(Payload, Secret, header, Now);

        act.Should().NotThrow();
        WebhookSignature.Verify(Payload, Secret, header, Now).Should().BeFalse();
    }

    [Fact]
    public void ExtraHeaderPartsAreIgnoredSoTheSchemeCanBeExtended()
    {
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var withFutureVersion = $"{header},v2=somethingelse";

        WebhookSignature.Verify(Payload, Secret, withFutureVersion, Now).Should().BeTrue();
    }

    /// <summary>
    /// Documents the exact bytes that get signed, so a customer implementing
    /// verification in another language has something to check against.
    /// </summary>
    [Fact]
    public void TheSignedValueIsTimestampDotPayload()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var header = WebhookSignature.Create(Payload, Secret, Now);
        var actualSignature = header.Split("v1=")[1];

        var expected = Convert.ToHexString(
                System.Security.Cryptography.HMACSHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Secret),
                    System.Text.Encoding.UTF8.GetBytes($"{timestamp}.{Payload}")))
            .ToLowerInvariant();

        actualSignature.Should().Be(expected);
    }
}
