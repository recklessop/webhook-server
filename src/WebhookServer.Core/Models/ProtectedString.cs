using System.Text.Json.Serialization;

namespace WebhookServer.Core.Models;

/// <summary>
/// A secret value. <see cref="Encrypted"/> is the persistent (DPAPI-protected) form;
/// <see cref="Plaintext"/> is transient — the GUI sets it when submitting a new value
/// over the named pipe, and the service sets it after decrypting on load. Disk JSON
/// must never carry plaintext: <see cref="Storage.ConfigStore.SaveAsync"/> encrypts
/// then clears <see cref="Plaintext"/> before writing.
/// </summary>
public sealed class ProtectedString
{
    [JsonPropertyName("encrypted")]
    public string? Encrypted { get; set; }

    [JsonPropertyName("plaintext")]
    public string? Plaintext { get; set; }

    [JsonIgnore]
    public bool HasValue =>
        !string.IsNullOrEmpty(Encrypted) || !string.IsNullOrEmpty(Plaintext);

    public static ProtectedString FromPlaintext(string value) =>
        new() { Plaintext = value };

    public static ProtectedString FromEncrypted(string base64) =>
        new() { Encrypted = base64 };
}
