using System.Security.Cryptography;
using System.Text;
using WebhookServer.Core.Models;

namespace WebhookServer.Core.Auth;

public static class HmacVerifier
{
    /// <summary>
    /// Compute the signature string (encoded per <paramref name="encoding"/>, no prefix)
    /// for the given body bytes and shared secret.
    /// </summary>
    public static string Compute(
        ReadOnlySpan<byte> body,
        string secret,
        HmacAlgorithm algorithm,
        HmacEncoding encoding)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        Span<byte> hash = stackalloc byte[64]; // SHA-512 is 64 bytes max
        int written = algorithm switch
        {
            HmacAlgorithm.Sha1 => HMACSHA1.HashData(keyBytes, body, hash),
            HmacAlgorithm.Sha256 => HMACSHA256.HashData(keyBytes, body, hash),
            HmacAlgorithm.Sha512 => HMACSHA512.HashData(keyBytes, body, hash),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        var hashBytes = hash[..written];
        return encoding switch
        {
            HmacEncoding.Hex => Convert.ToHexString(hashBytes).ToLowerInvariant(),
            HmacEncoding.Base64 => Convert.ToBase64String(hashBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };
    }

    /// <summary>
    /// Verify the HMAC signature in <paramref name="presentedHeaderValue"/> against the
    /// computed signature for <paramref name="body"/>. Strips the configured prefix
    /// before comparing. Comparison is constant time.
    /// </summary>
    public static AuthResult Verify(
        ReadOnlySpan<byte> body,
        string? presentedHeaderValue,
        HmacOptions options)
    {
        if (options.Secret.Plaintext is not { Length: > 0 } secret)
            return AuthResult.Fail("HMAC secret not available");

        if (string.IsNullOrEmpty(presentedHeaderValue))
            return AuthResult.Fail($"missing {options.HeaderName} header");

        var presented = presentedHeaderValue.AsSpan().Trim();
        if (!string.IsNullOrEmpty(options.Prefix))
        {
            if (!presented.StartsWith(options.Prefix, StringComparison.OrdinalIgnoreCase))
                return AuthResult.Fail("signature prefix mismatch");
            presented = presented[options.Prefix.Length..];
        }

        var expected = Compute(body, secret, options.Algorithm, options.Encoding);

        // Encoding for hex is case-insensitive in practice; normalize to lower.
        var presentedNormalized = options.Encoding == HmacEncoding.Hex
            ? presented.ToString().ToLowerInvariant()
            : presented.ToString();

        var presentedBytes = Encoding.ASCII.GetBytes(presentedNormalized);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes)
            ? AuthResult.Ok()
            : AuthResult.Fail("HMAC signature mismatch");
    }
}
