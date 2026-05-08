namespace WebhookServer.Core.Auth;

public readonly record struct AuthResult(bool Success, string? Reason)
{
    public static AuthResult Ok() => new(true, null);
    public static AuthResult Fail(string reason) => new(false, reason);
}
