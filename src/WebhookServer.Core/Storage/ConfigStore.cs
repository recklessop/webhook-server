using System.Runtime.Versioning;
using System.Text.Json;
using WebhookServer.Core.Models;

namespace WebhookServer.Core.Storage;

/// <summary>
/// Loads and saves <see cref="ServerConfig"/> JSON. Round-trips secrets through DPAPI:
/// on save, any secret that has Plaintext but no Encrypted is protected first; on load
/// (when <see cref="DecryptSecrets"/> is called) all Encrypted blobs are unprotected
/// into Plaintext for in-memory use.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConfigStore
{
    public string Path { get; }

    public ConfigStore(string path)
    {
        Path = path;
    }

    public async Task<ServerConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path))
            return new ServerConfig();

        await using var fs = File.OpenRead(Path);
        var cfg = await JsonSerializer.DeserializeAsync<ServerConfig>(fs, ConfigJson.Pretty, ct).ConfigureAwait(false);
        return cfg ?? new ServerConfig();
    }

    public async Task SaveAsync(ServerConfig config, CancellationToken ct = default)
    {
        EncryptSecrets(config);
        ClearPlaintexts(config);

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Snapshot the previous config (if any) into the backups folder before
        // overwriting. Cheap insurance against typos in the GUI.
        if (File.Exists(Path) && !string.IsNullOrEmpty(dir))
        {
            try
            {
                var backupsDir = System.IO.Path.Combine(dir, "backups");
                Directory.CreateDirectory(backupsDir);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var backupPath = System.IO.Path.Combine(backupsDir, $"config-{stamp}.json");
                if (!File.Exists(backupPath))
                {
                    File.Copy(Path, backupPath, overwrite: false);
                    var sidecar = new { description = "Before save", reason = "before-save" };
                    File.WriteAllText(
                        System.IO.Path.ChangeExtension(backupPath, ".meta.json"),
                        JsonSerializer.Serialize(sidecar, ConfigJson.Compact));
                }
                PruneBackups(backupsDir, retain: 90);
            }
            catch
            {
                // Backup is best-effort; don't fail the save if it can't write.
            }
        }

        var tmp = Path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, config, ConfigJson.Pretty, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        // Atomic replace on the same volume.
        File.Move(tmp, Path, overwrite: true);
    }

    private static void PruneBackups(string backupsDir, int retain)
    {
        var stale = new DirectoryInfo(backupsDir).GetFiles("config-*.json")
            .Where(f => !f.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Name)
            .Skip(retain);
        foreach (var f in stale)
        {
            try
            {
                f.Delete();
                var sidecar = System.IO.Path.ChangeExtension(f.FullName, ".meta.json");
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            catch { }
        }
    }

    public static void ClearPlaintexts(ServerConfig config)
    {
        foreach (var ep in config.Endpoints)
        {
            ClearOne(ep.Bearer?.Secret);
            ClearOne(ep.Hmac?.Secret);
            ClearOne(ep.RunAs?.Password);
            if (ep.Callback is { } cb)
            {
                ClearOne(cb.Bearer?.Secret);
                ClearOne(cb.Hmac?.Secret);
            }
        }
        ClearOne(config.HttpsBinding?.PfxPassword);
    }

    private static void ClearOne(ProtectedString? s)
    {
        if (s is null) return;
        s.Plaintext = null;
    }

    public static void DecryptSecrets(ServerConfig config)
    {
        foreach (var ep in config.Endpoints)
        {
            DecryptOne(ep.Bearer?.Secret);
            DecryptOne(ep.Hmac?.Secret);
            DecryptOne(ep.RunAs?.Password);
            if (ep.Callback is { } cb)
            {
                DecryptOne(cb.Bearer?.Secret);
                DecryptOne(cb.Hmac?.Secret);
            }
        }
        DecryptOne(config.HttpsBinding?.PfxPassword);
    }

    public static void EncryptSecrets(ServerConfig config)
    {
        foreach (var ep in config.Endpoints)
        {
            EncryptOne(ep.Bearer?.Secret);
            EncryptOne(ep.Hmac?.Secret);
            EncryptOne(ep.RunAs?.Password);
            if (ep.Callback is { } cb)
            {
                EncryptOne(cb.Bearer?.Secret);
                EncryptOne(cb.Hmac?.Secret);
            }
        }
        EncryptOne(config.HttpsBinding?.PfxPassword);
    }

    private static void DecryptOne(ProtectedString? s)
    {
        if (s is null) return;
        if (!string.IsNullOrEmpty(s.Plaintext)) return; // already populated
        if (string.IsNullOrEmpty(s.Encrypted)) return;
        s.Plaintext = DpapiSecret.Unprotect(s.Encrypted);
    }

    private static void EncryptOne(ProtectedString? s)
    {
        if (s is null) return;
        if (string.IsNullOrEmpty(s.Plaintext)) return;
        // Always re-encrypt when plaintext is present so secret rotation is honored.
        s.Encrypted = DpapiSecret.Protect(s.Plaintext);
    }
}
