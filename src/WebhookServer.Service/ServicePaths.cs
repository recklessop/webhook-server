namespace WebhookServer.Service;

/// <summary>
/// Standard locations for runtime files (config + logs). Centralised so they're easy
/// to override in tests and inspect in one place.
/// </summary>
public static class ServicePaths
{
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("WEBHOOKSERVER_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebhookServer");

    public static string ConfigPath => Path.Combine(DataRoot, "config.json");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string LogFileTemplate => Path.Combine(LogsDir, "webhook-.log");
}
