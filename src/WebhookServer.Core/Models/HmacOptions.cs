namespace WebhookServer.Core.Models;

public sealed class HmacOptions
{
    public HmacAlgorithm Algorithm { get; set; } = HmacAlgorithm.Sha256;
    public string HeaderName { get; set; } = "X-Hub-Signature-256";
    public string Prefix { get; set; } = "sha256=";
    public HmacEncoding Encoding { get; set; } = HmacEncoding.Hex;
    public ProtectedString Secret { get; set; } = new();
}
