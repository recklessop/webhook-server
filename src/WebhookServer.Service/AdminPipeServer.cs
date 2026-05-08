using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebhookServer.Core.Ipc;
using WebhookServer.Core.Models;
using WebhookServer.Core.Storage;

namespace WebhookServer.Service;

[SupportedOSPlatform("windows")]
internal sealed class AdminPipeServer : BackgroundService
{
    private readonly ServiceState _state;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AdminPipeServer> _logger;

    public AdminPipeServer(ServiceState state, IHostApplicationLifetime lifetime, ILogger<AdminPipeServer> logger)
    {
        _state = state;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Admin pipe server listening on \\\\.\\pipe\\{Pipe}", PipeSecurityFactory.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(
                    PipeSecurityFactory.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    PipeSecurityFactory.Create());

                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleClientAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Admin pipe accept loop error");
                try { await Task.Delay(500, stoppingToken).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = PipeFraming.CreateReader(pipe);

        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            AdminRequest? request;
            try { request = await PipeFraming.ReadAsync<AdminRequest>(reader, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Admin pipe read error");
                break;
            }
            if (request is null) break;

            AdminResponse response;
            try
            {
                response = await DispatchAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Admin op {Op} failed", request.Op);
                response = AdminResponse.Failure(ex.Message);
            }

            try { await PipeFraming.WriteAsync(pipe, response, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Admin pipe write error");
                break;
            }
        }
    }

    private async Task<AdminResponse> DispatchAsync(AdminRequest request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case AdminOps.Ping:
                return AdminResponse.Success(new { pong = true, at = DateTimeOffset.UtcNow });

            case AdminOps.GetStatus:
            {
                var snap = _state.Snapshot();
                return AdminResponse.Success(new StatusInfo
                {
                    Running = true,
                    HttpPort = snap.HttpPort,
                    HttpsPort = snap.HttpsBinding?.Port,
                    DisplayHost = snap.DisplayHost,
                    StartedAt = _state.StartedAt,
                    EndpointCount = snap.Endpoints.Count,
                });
            }

            case AdminOps.GetConfig:
            {
                var snap = SafeSnapshotForWire(_state.Snapshot());
                return AdminResponse.Success(snap);
            }

            case AdminOps.UpdateConfig:
            {
                var incoming = DeserializeData<ServerConfig>(request) ?? throw new ArgumentException("missing config payload");
                MergeWithExistingSecrets(incoming, _state.Snapshot());
                await _state.ReplaceAsync(incoming, ct).ConfigureAwait(false);
                _logger.LogInformation("Server config replaced ({Count} endpoints)", incoming.Endpoints.Count);
                return AdminResponse.Success(SafeSnapshotForWire(_state.Snapshot()));
            }

            case AdminOps.ListEndpoints:
                return AdminResponse.Success(SafeSnapshotForWire(_state.Snapshot()).Endpoints);

            case AdminOps.CreateEndpoint:
            {
                var ep = DeserializeData<EndpointConfig>(request) ?? throw new ArgumentException("missing endpoint");
                if (ep.Id == Guid.Empty) ep.Id = Guid.NewGuid();
                var next = CloneSnapshotForEdit();
                if (next.Endpoints.Any(e => string.Equals(e.Slug, ep.Slug, StringComparison.Ordinal)))
                    return AdminResponse.Failure($"slug '{ep.Slug}' already exists");
                next.Endpoints.Add(ep);
                await _state.ReplaceAsync(next, ct).ConfigureAwait(false);
                _logger.LogInformation("Endpoint created: {Slug} ({Id})", ep.Slug, ep.Id);
                return AdminResponse.Success(ep);
            }

            case AdminOps.UpdateEndpoint:
            {
                var ep = DeserializeData<EndpointConfig>(request) ?? throw new ArgumentException("missing endpoint");
                var next = CloneSnapshotForEdit();
                var idx = next.Endpoints.FindIndex(e => e.Id == ep.Id);
                if (idx < 0) return AdminResponse.Failure("endpoint not found");
                MergeEndpointSecrets(ep, next.Endpoints[idx]);
                next.Endpoints[idx] = ep;
                await _state.ReplaceAsync(next, ct).ConfigureAwait(false);
                _logger.LogInformation("Endpoint updated: {Slug} ({Id})", ep.Slug, ep.Id);
                return AdminResponse.Success(ep);
            }

            case AdminOps.DeleteEndpoint:
            {
                var args = DeserializeData<DeleteEndpointArgs>(request) ?? throw new ArgumentException("missing id");
                var next = CloneSnapshotForEdit();
                var removed = next.Endpoints.RemoveAll(e => e.Id == args.Id);
                if (removed == 0) return AdminResponse.Failure("endpoint not found");
                await _state.ReplaceAsync(next, ct).ConfigureAwait(false);
                _logger.LogInformation("Endpoint deleted: {Id}", args.Id);
                return AdminResponse.Success();
            }

            case AdminOps.EnableEndpoint:
            case AdminOps.DisableEndpoint:
            {
                var args = DeserializeData<EndpointToggle>(request) ?? throw new ArgumentException("missing id");
                var next = CloneSnapshotForEdit();
                var ep = next.Endpoints.FirstOrDefault(e => e.Id == args.Id);
                if (ep is null) return AdminResponse.Failure("endpoint not found");
                var newState = request.Op == AdminOps.EnableEndpoint;
                ep.Enabled = newState;
                await _state.ReplaceAsync(next, ct).ConfigureAwait(false);
                _logger.LogInformation("Endpoint {Slug} {State}", ep.Slug, newState ? "enabled" : "disabled");
                return AdminResponse.Success(ep);
            }

            case AdminOps.BindHttps:
            {
                var binding = DeserializeData<HttpsBinding>(request);
                var next = CloneSnapshotForEdit();
                next.HttpsBinding = binding;
                await _state.ReplaceAsync(next, ct).ConfigureAwait(false);
                _logger.LogInformation("HTTPS binding {Action}",
                    binding is null || binding.Kind == HttpsBindingKind.None ? "cleared" : $"set ({binding.Kind} on port {binding.Port})");
                return AdminResponse.Success();
            }

            case AdminOps.RestartListener:
                _logger.LogInformation("Restart requested via admin pipe");
                _lifetime.StopApplication();
                return AdminResponse.Success();

            case AdminOps.TailLogs:
            {
                var args = DeserializeData<TailLogsArgs>(request) ?? new TailLogsArgs();
                var lines = ReadTailLines(args.LinesToBacklog);
                return AdminResponse.Success(new { lines });
            }

            case AdminOps.ListBackups:
            {
                var entries = ListBackups();
                return AdminResponse.Success(new { backups = entries });
            }

            case AdminOps.RestoreBackup:
            {
                var args = DeserializeData<RestoreBackupArgs>(request) ?? throw new ArgumentException("missing fileName");
                var restored = await RestoreBackupAsync(args.FileName, ct).ConfigureAwait(false);
                _logger.LogInformation("Restored config from backup {File}", args.FileName);
                return AdminResponse.Success(SafeSnapshotForWire(restored));
            }

            case AdminOps.ImportConfig:
            {
                var incoming = DeserializeData<ServerConfig>(request) ?? throw new ArgumentException("missing config payload");
                MergeWithExistingSecrets(incoming, _state.Snapshot());
                await _state.ReplaceAsync(incoming, ct).ConfigureAwait(false);
                _logger.LogInformation("Config imported ({Count} endpoints)", incoming.Endpoints.Count);
                return AdminResponse.Success(SafeSnapshotForWire(_state.Snapshot()));
            }

            case AdminOps.CreateCheckpoint:
            {
                var entry = CreateCheckpoint("manual");
                _logger.LogInformation("Manual checkpoint created: {File}", entry.FileName);
                return AdminResponse.Success(entry);
            }

            default:
                return AdminResponse.Failure($"unknown op '{request.Op}'");
        }
    }

    /// <summary>
    /// Snapshot the current config.json into the backups folder. Used both by the
    /// "Take checkpoint now" GUI action and by the midnight scheduler.
    /// </summary>
    public static BackupEntry CreateCheckpoint(string reason)
    {
        var configPath = ServicePaths.ConfigPath;
        if (!File.Exists(configPath))
            throw new FileNotFoundException("no config.json exists yet to snapshot");

        var dir = Path.Combine(ServicePaths.DataRoot, "backups");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var dest = Path.Combine(dir, $"config-{stamp}.json");
        // If we somehow snapshot twice in the same second, append a suffix.
        if (File.Exists(dest))
            dest = Path.Combine(dir, $"config-{stamp}-{reason}.json");

        File.Copy(configPath, dest);
        var info = new FileInfo(dest);
        return new BackupEntry
        {
            FileName = info.Name,
            SavedAt = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
        };
    }

    private static List<BackupEntry> ListBackups()
    {
        var dir = Path.Combine(ServicePaths.DataRoot, "backups");
        if (!Directory.Exists(dir)) return new List<BackupEntry>();
        return new DirectoryInfo(dir).GetFiles("config-*.json")
            .OrderByDescending(f => f.Name)
            .Take(50)
            .Select(f => new BackupEntry
            {
                FileName = f.Name,
                SavedAt = f.LastWriteTimeUtc,
                SizeBytes = f.Length,
            })
            .ToList();
    }

    private async Task<ServerConfig> RestoreBackupAsync(string fileName, CancellationToken ct)
    {
        // Refuse anything that tries to escape the backups directory.
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("invalid file name");
        var backupPath = Path.Combine(ServicePaths.DataRoot, "backups", fileName);
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("backup not found", fileName);

        await using var fs = File.OpenRead(backupPath);
        var cfg = await JsonSerializer.DeserializeAsync<ServerConfig>(fs, ConfigJson.Pretty, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("backup file was empty");
        await _state.ReplaceAsync(cfg, ct).ConfigureAwait(false);
        return _state.Snapshot();
    }

    private ServerConfig CloneSnapshotForEdit()
    {
        // Round-trip via JSON to avoid sharing references with the live snapshot.
        var snap = _state.Snapshot();
        var json = JsonSerializer.Serialize(snap, ConfigJson.Compact);
        return JsonSerializer.Deserialize<ServerConfig>(json, ConfigJson.Compact)!;
    }

    private static T? DeserializeData<T>(AdminRequest request)
    {
        if (request.Data is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element)
            return default;
        return element.Deserialize<T>(AdminProtocol.JsonOptions);
    }

    /// <summary>
    /// Deep-clone the snapshot for the GUI. Plaintext secrets ARE included on the
    /// wire — the admin pipe is ACL'd to SYSTEM and Administrators, so anyone able
    /// to read the wire already has full local privilege. Letting the GUI display
    /// secrets means an admin can recover a lost token without resetting it.
    /// </summary>
    private static ServerConfig SafeSnapshotForWire(ServerConfig snap)
    {
        var json = JsonSerializer.Serialize(snap, ConfigJson.Compact);
        return JsonSerializer.Deserialize<ServerConfig>(json, ConfigJson.Compact)!;
    }

    /// <summary>
    /// When the GUI sends an <see cref="EndpointConfig"/> with empty plaintext on a
    /// secret, we keep the existing encrypted blob from disk. Without this, a GUI
    /// edit that doesn't touch the secret field would erase the secret.
    /// </summary>
    private static void MergeWithExistingSecrets(ServerConfig incoming, ServerConfig existing)
    {
        var byId = existing.Endpoints.ToDictionary(e => e.Id);
        foreach (var ep in incoming.Endpoints)
        {
            if (!byId.TryGetValue(ep.Id, out var prior)) continue;
            MergeEndpointSecrets(ep, prior);
        }

        if (incoming.HttpsBinding is { } b && existing.HttpsBinding is { } prev)
            MergeProtected(b.PfxPassword, prev.PfxPassword);
    }

    private static void MergeEndpointSecrets(EndpointConfig incoming, EndpointConfig prior)
    {
        if (incoming.Bearer is { } a) MergeProtected(a.Secret, prior.Bearer?.Secret);
        if (incoming.Hmac is { } h) MergeProtected(h.Secret, prior.Hmac?.Secret);
        if (incoming.RunAs is { Password: { } runAsPwd }) MergeProtected(runAsPwd, prior.RunAs?.Password);
        if (incoming.Callback is { } cb)
        {
            if (cb.Bearer is { } cba) MergeProtected(cba.Secret, prior.Callback?.Bearer?.Secret);
            if (cb.Hmac is { } cbh) MergeProtected(cbh.Secret, prior.Callback?.Hmac?.Secret);
        }
    }

    private static void MergeProtected(ProtectedString? incoming, ProtectedString? prior)
    {
        if (incoming is null) return;
        if (!string.IsNullOrEmpty(incoming.Plaintext)) return; // GUI is supplying a new value
        if (string.IsNullOrEmpty(incoming.Encrypted) && prior is not null && !string.IsNullOrEmpty(prior.Encrypted))
            incoming.Encrypted = prior.Encrypted; // preserve previous secret
    }

    private static List<LogLine> ReadTailLines(int count)
    {
        try
        {
            var dir = ServicePaths.LogsDir;
            if (!Directory.Exists(dir)) return new List<LogLine>();
            var latest = Directory.GetFiles(dir, "webhook-*.log")
                .OrderByDescending(p => p)
                .FirstOrDefault();
            if (latest is null) return new List<LogLine>();

            using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = new LinkedList<string>();
            while (sr.ReadLine() is { } line)
            {
                lines.AddLast(line);
                if (lines.Count > count) lines.RemoveFirst();
            }
            return lines.Select(l => new LogLine
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = l,
            }).ToList();
        }
        catch
        {
            return new List<LogLine>();
        }
    }
}
