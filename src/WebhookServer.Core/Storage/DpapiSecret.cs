using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WebhookServer.Core.Storage;

/// <summary>
/// DPAPI helpers using <see cref="DataProtectionScope.LocalMachine"/> so the same machine
/// (regardless of which Windows account the service runs under) can decrypt config secrets.
/// Wire format is plain base64 of the protected blob — caller wraps in JSON.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiSecret
{
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(blob);
    }

    public static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        var blob = Convert.FromBase64String(base64);
        var bytes = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }
}
