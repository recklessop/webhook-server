using System.Security.Cryptography;
using System.Text;

namespace WebhookServer.Core.Auth;

public static class BearerVerifier
{
    private const string Prefix = "Bearer ";

    /// <summary>
    /// Compares the value of an Authorization header against an expected secret in fixed time.
    /// </summary>
    public static AuthResult Verify(string? authorizationHeader, string expectedSecret)
    {
        if (string.IsNullOrEmpty(expectedSecret))
            return AuthResult.Fail("server secret not configured");

        if (string.IsNullOrEmpty(authorizationHeader))
            return AuthResult.Fail("missing Authorization header");

        if (!authorizationHeader.StartsWith(Prefix, StringComparison.Ordinal))
            return AuthResult.Fail("Authorization header is not a Bearer token");

        var presented = authorizationHeader.AsSpan(Prefix.Length).Trim();
        var presentedBytes = Encoding.UTF8.GetBytes(presented.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSecret);

        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes)
            ? AuthResult.Ok()
            : AuthResult.Fail("bearer token mismatch");
    }
}
