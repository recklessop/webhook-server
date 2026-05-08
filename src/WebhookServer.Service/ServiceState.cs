using System.Runtime.Versioning;
using WebhookServer.Core.Auth;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;

namespace WebhookServer.Service;

/// <summary>
/// In-memory authoritative copy of the current <see cref="ServerConfig"/>. Holds parsed
/// helpers (allowlists keyed by endpoint id, slug → endpoint map) and notifies subscribers
/// when the config is replaced so the listener and dispatcher can react.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceState
{
    private readonly ConfigStore _store;
    private readonly object _lock = new();
    private ServerConfig _config = new();
    private Dictionary<string, EndpointConfig> _bySlug = new(StringComparer.Ordinal);
    private Dictionary<Guid, IpAllowList> _allowLists = new();
    private IpAllowList _trustedProxies = IpAllowList.Parse(Array.Empty<string>());

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public event EventHandler? ListenerSettingsChanged;

    public ServiceState(ConfigStore store)
    {
        _store = store;
    }

    public ServerConfig Snapshot()
    {
        lock (_lock) return _config;
    }

    public bool TryGetEndpoint(string slug, out EndpointConfig endpoint)
    {
        lock (_lock)
        {
            return _bySlug.TryGetValue(slug, out endpoint!);
        }
    }

    public IpAllowList GetAllowList(Guid endpointId)
    {
        lock (_lock)
        {
            return _allowLists.TryGetValue(endpointId, out var l) ? l : IpAllowList.Parse(Array.Empty<string>());
        }
    }

    public IpAllowList GetTrustedProxies()
    {
        lock (_lock) return _trustedProxies;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
        ConfigStore.DecryptSecrets(loaded);
        Replace(loaded, listenerChanged: true);
    }

    public async Task ReplaceAsync(ServerConfig replacement, CancellationToken ct = default)
    {
        // Save to disk first; that re-encrypts secrets in place. Then publish in-memory.
        var listenerChanged = HasListenerSettingsChanged(_config, replacement);
        await _store.SaveAsync(replacement, ct).ConfigureAwait(false);

        // SaveAsync filled in Encrypted; ensure Plaintext is populated for runtime use.
        ConfigStore.DecryptSecrets(replacement);
        Replace(replacement, listenerChanged);
    }

    private void Replace(ServerConfig cfg, bool listenerChanged)
    {
        var bySlug = new Dictionary<string, EndpointConfig>(StringComparer.Ordinal);
        var allow = new Dictionary<Guid, IpAllowList>();

        foreach (var ep in cfg.Endpoints)
        {
            if (!string.IsNullOrEmpty(ep.Slug))
                bySlug[ep.Slug] = ep;
            allow[ep.Id] = IpAllowList.Parse(ep.AllowedClients);
        }

        var trusted = IpAllowList.Parse(cfg.TrustedProxies);

        lock (_lock)
        {
            _config = cfg;
            _bySlug = bySlug;
            _allowLists = allow;
            _trustedProxies = trusted;
        }

        if (listenerChanged)
            ListenerSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasListenerSettingsChanged(ServerConfig oldCfg, ServerConfig newCfg)
    {
        if (oldCfg.HttpPort != newCfg.HttpPort) return true;
        if (!oldCfg.BindAddresses.SequenceEqual(newCfg.BindAddresses, StringComparer.OrdinalIgnoreCase)) return true;
        var a = oldCfg.HttpsBinding;
        var b = newCfg.HttpsBinding;
        if ((a is null) != (b is null)) return true;
        if (a is not null && b is not null)
        {
            if (a.Kind != b.Kind || a.Port != b.Port || a.PfxPath != b.PfxPath || a.Thumbprint != b.Thumbprint)
                return true;
        }
        // DisplayHost is cosmetic; don't restart for it.
        return false;
    }
}
