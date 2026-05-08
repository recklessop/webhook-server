namespace WebhookServer.Core.Models;

public sealed class BearerOptions
{
    public ProtectedString Secret { get; set; } = new();
}
