using System.Security.Cryptography.X509Certificates;

namespace WebhookServer.Core.Models;

public sealed class HttpsBinding
{
    public HttpsBindingKind Kind { get; set; } = HttpsBindingKind.None;
    public int Port { get; set; } = 8443;

    /// <summary>Path to a .pfx file when Kind = PfxFile.</summary>
    public string? PfxPath { get; set; }
    public ProtectedString? PfxPassword { get; set; }

    /// <summary>Cert thumbprint when Kind = CertStoreThumbprint.</summary>
    public string? Thumbprint { get; set; }
    public StoreLocation StoreLocation { get; set; } = StoreLocation.LocalMachine;
}
