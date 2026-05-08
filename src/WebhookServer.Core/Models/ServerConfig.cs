namespace WebhookServer.Core.Models;

public sealed class ServerConfig
{
    public int HttpPort { get; set; } = 8080;
    public HttpsBinding? HttpsBinding { get; set; }

    /// <summary>
    /// IP addresses Kestrel binds to. Empty = listen on all interfaces (default).
    /// Non-empty = listen only on the named addresses.
    /// </summary>
    public List<string> BindAddresses { get; set; } = new();

    /// <summary>
    /// Hostname or IP that the GUI uses when constructing webhook URLs to display.
    /// Null = "localhost". Has no effect on what Kestrel actually accepts.
    /// </summary>
    public string? DisplayHost { get; set; }

    /// <summary>
    /// IPs/CIDRs allowed to set X-Forwarded-For. Empty = forwarded headers are ignored
    /// and the direct connection IP is always used.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();

    public int LogRetentionDays { get; set; } = 14;

    public List<EndpointConfig> Endpoints { get; set; } = new();
}
