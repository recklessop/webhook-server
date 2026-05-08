namespace WebhookServer.Core.Models;

public sealed class RunAsConfig
{
    public RunAsMode Mode { get; set; } = RunAsMode.Service;

    /// <summary>
    /// "DOMAIN\user" or "user@upn" or just "user" (local). Required when
    /// <see cref="Mode"/> is <see cref="RunAsMode.SpecificUser"/>.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>DPAPI-protected password for SpecificUser mode.</summary>
    public ProtectedString? Password { get; set; }

    /// <summary>
    /// When true, load the user's profile (HKCU + AppData) before running.
    /// Slower; only needed for hooks that read user-scope settings.
    /// </summary>
    public bool LoadProfile { get; set; }
}
