using System.Security.Cryptography;
using System.Text;
using WebhookServer.Core.Auth;
using WebhookServer.Core.Models;
using Xunit;

namespace WebhookServer.Core.Tests;

public class HmacVerifierTests
{
    [Fact]
    public void Compute_matches_GitHub_style_signature()
    {
        var body = Encoding.UTF8.GetBytes("{\"x\":1}");
        var secret = "topsecret";

        var hex = HmacVerifier.Compute(body, secret, HmacAlgorithm.Sha256, HmacEncoding.Hex);

        // Cross-check against direct HMACSHA256 to ensure no encoding drift.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
        Assert.Equal(expected, hex);
    }

    [Fact]
    public void Verify_accepts_correct_signature_with_prefix()
    {
        var body = Encoding.UTF8.GetBytes("hello world");
        var secret = "shhh";
        var sig = HmacVerifier.Compute(body, secret, HmacAlgorithm.Sha256, HmacEncoding.Hex);

        var options = new HmacOptions { Secret = ProtectedString.FromPlaintext(secret) };
        var result = HmacVerifier.Verify(body, $"sha256={sig}", options);

        Assert.True(result.Success);
    }

    [Fact]
    public void Verify_rejects_wrong_signature()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var options = new HmacOptions { Secret = ProtectedString.FromPlaintext("right") };

        var sig = HmacVerifier.Compute(body, "wrong", HmacAlgorithm.Sha256, HmacEncoding.Hex);
        var result = HmacVerifier.Verify(body, $"sha256={sig}", options);

        Assert.False(result.Success);
    }

    [Fact]
    public void Verify_rejects_when_prefix_missing()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var options = new HmacOptions { Secret = ProtectedString.FromPlaintext("k") };
        var sig = HmacVerifier.Compute(body, "k", HmacAlgorithm.Sha256, HmacEncoding.Hex);

        var result = HmacVerifier.Verify(body, sig, options); // no "sha256=" prefix

        Assert.False(result.Success);
    }

    [Fact]
    public void Verify_handles_base64_encoding()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var secret = "abc";
        var sig = HmacVerifier.Compute(body, secret, HmacAlgorithm.Sha256, HmacEncoding.Base64);

        var options = new HmacOptions
        {
            Encoding = HmacEncoding.Base64,
            Prefix = "",
            Secret = ProtectedString.FromPlaintext(secret),
        };

        var result = HmacVerifier.Verify(body, sig, options);
        Assert.True(result.Success);
    }
}
