using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookDelivery.Domain.Deliveries;

/// <summary>
/// HMAC SHA256 signing, following the scheme Stripe popularised.
///
/// The signed value is <c>{timestamp}.{payload}</c>, not the payload alone.
/// Signing the payload by itself would let an attacker who captured one
/// request replay it forever, because the signature would stay valid. Binding
/// the timestamp into the signature lets the receiver reject anything older
/// than a tolerance window.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Webhook-Signature";
    public const string TimestampHeaderName = "X-Webhook-Timestamp";
    public const string EventIdHeaderName = "X-Webhook-Event-Id";
    public const string EventTypeHeaderName = "X-Webhook-Event-Type";
    public const string DeliveryIdHeaderName = "X-Webhook-Delivery-Id";
    public const string AttemptHeaderName = "X-Webhook-Attempt";

    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Produces the header value: <c>t=1736947200,v1=a1b2c3...</c>
    /// </summary>
    public static string Create(string payload, string secret, DateTimeOffset timestamp)
    {
        var unixTimestamp = timestamp.ToUnixTimeSeconds();
        var signature = ComputeHex(payload, secret, unixTimestamp);

        return $"t={unixTimestamp},v1={signature}";
    }

    /// <summary>
    /// Verifies a signature header against the payload.
    ///
    /// This is the code a customer writes on their side, so it is part of the
    /// product rather than an internal detail.
    /// </summary>
    public static bool Verify(
        string payload,
        string secret,
        string signatureHeader,
        DateTimeOffset now,
        TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        if (!TryParse(signatureHeader, out var unixTimestamp, out var providedSignature))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        var window = tolerance ?? DefaultTolerance;

        // Reject both stale requests and ones timestamped in the future.
        if (age > window || age < -window)
        {
            return false;
        }

        var expected = ComputeHex(payload, secret, unixTimestamp);

        // Fixed time comparison. A plain string equality leaks, through timing,
        // how many leading characters matched, which is enough to forge a
        // signature one byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(providedSignature));
    }

    private static bool TryParse(string header, out long timestamp, out string signature)
    {
        timestamp = 0;
        signature = string.Empty;

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator];
            var value = part[(separator + 1)..];

            switch (key)
            {
                case "t" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    timestamp = parsed;
                    break;

                case "v1":
                    signature = value;
                    break;
            }
        }

        return timestamp > 0 && signature.Length > 0;
    }

    private static string ComputeHex(string payload, string secret, long unixTimestamp)
    {
        var signedPayload = $"{unixTimestamp}.{payload}";
        var key = Encoding.UTF8.GetBytes(secret);
        var message = Encoding.UTF8.GetBytes(signedPayload);

        return Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
    }
}
