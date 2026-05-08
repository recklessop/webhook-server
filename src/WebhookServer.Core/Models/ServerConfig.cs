namespace WebhookServer.Core.Models;

public sealed class ServerConfig
{
    public int HttpPort { get; set; } = 8080;
    public HttpsBinding? HttpsBinding { get; set; }

    /// <summary>
    /// IPs/CIDRs allowed to set X-Forwarded-For. Empty = forwarded headers are ignored
    /// and the direct connection IP is always used.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();

    public int LogRetentionDays { get; set; } = 14;

    public List<EndpointConfig> Endpoints { get; set; } = new();
}
