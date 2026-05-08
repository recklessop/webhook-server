namespace WebhookServer.Core.Models;

public sealed class DataPassingOptions
{
    public bool StdinJson { get; set; }
    public bool EnvVars { get; set; }
    public bool ArgTemplate { get; set; }

    /// <summary>
    /// Whitespace-separated list of template tokens; each rendered token becomes one argv entry.
    /// Only used when <see cref="ArgTemplate"/> is true.
    /// </summary>
    public string? ArgTemplateString { get; set; }
}
