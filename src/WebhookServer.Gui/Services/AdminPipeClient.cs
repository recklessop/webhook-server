using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebhookServer.Core.Ipc;
using WebhookServer.Core.Models;

namespace WebhookServer.Gui.Services;

/// <summary>
/// Thin client around the admin named pipe. Each call connects, sends one request,
/// reads one response, and disconnects — keeps lifecycle simple at the cost of
/// connect-per-call overhead. The service single-instance pipe queues requests so
/// concurrent calls from the GUI serialize automatically.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdminPipeClient
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<AdminResponse> InvokeAsync(string op, object? data = null, CancellationToken ct = default)
    {
        var request = new AdminRequest
        {
            Op = op,
            Data = data is null
                ? null
                : JsonSerializer.SerializeToDocument(data, AdminProtocol.JsonOptions).RootElement.Clone(),
        };

        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeSecurityFactory.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);

        await PipeFraming.WriteAsync(pipe, request, ct).ConfigureAwait(false);

        using var reader = PipeFraming.CreateReader(pipe);
        var response = await PipeFraming.ReadAsync<AdminResponse>(reader, ct).ConfigureAwait(false);
        return response ?? AdminResponse.Failure("empty response from service");
    }

    public async Task<T?> InvokeAsync<T>(string op, object? data = null, CancellationToken ct = default) where T : class
    {
        var resp = await InvokeAsync(op, data, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Data is null) return null;
        return resp.Data.Value.Deserialize<T>(AdminProtocol.JsonOptions);
    }

    public Task<AdminResponse> PingAsync(CancellationToken ct = default) =>
        InvokeAsync(AdminOps.Ping, null, ct);

    public Task<StatusInfo?> GetStatusAsync(CancellationToken ct = default) =>
        InvokeAsync<StatusInfo>(AdminOps.GetStatus, null, ct);

    public Task<ServerConfig?> GetConfigAsync(CancellationToken ct = default) =>
        InvokeAsync<ServerConfig>(AdminOps.GetConfig, null, ct);

    public Task<EndpointConfig?> CreateEndpointAsync(EndpointConfig endpoint, CancellationToken ct = default) =>
        InvokeAsync<EndpointConfig>(AdminOps.CreateEndpoint, endpoint, ct);

    public Task<EndpointConfig?> UpdateEndpointAsync(EndpointConfig endpoint, CancellationToken ct = default) =>
        InvokeAsync<EndpointConfig>(AdminOps.UpdateEndpoint, endpoint, ct);

    public Task<AdminResponse> DeleteEndpointAsync(Guid id, CancellationToken ct = default) =>
        InvokeAsync(AdminOps.DeleteEndpoint, new DeleteEndpointArgs { Id = id }, ct);

    public Task<AdminResponse> SetEndpointEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        InvokeAsync(enabled ? AdminOps.EnableEndpoint : AdminOps.DisableEndpoint, new EndpointToggle { Id = id }, ct);

    public Task<AdminResponse> BindHttpsAsync(HttpsBinding? binding, CancellationToken ct = default) =>
        InvokeAsync(AdminOps.BindHttps, binding, ct);

    public Task<AdminResponse> RestartListenerAsync(CancellationToken ct = default) =>
        InvokeAsync(AdminOps.RestartListener, null, ct);

    public async Task<List<LogLine>> TailLogsAsync(int lines, CancellationToken ct = default)
    {
        var resp = await InvokeAsync(AdminOps.TailLogs, new TailLogsArgs { LinesToBacklog = lines, Follow = false }, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Data is null) return new List<LogLine>();
        var lst = resp.Data.Value.GetProperty("lines").Deserialize<List<LogLine>>(AdminProtocol.JsonOptions);
        return lst ?? new List<LogLine>();
    }

    public async Task<List<BackupEntry>> ListBackupsAsync(CancellationToken ct = default)
    {
        var resp = await InvokeAsync(AdminOps.ListBackups, null, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Data is null) return new List<BackupEntry>();
        var lst = resp.Data.Value.GetProperty("backups").Deserialize<List<BackupEntry>>(AdminProtocol.JsonOptions);
        return lst ?? new List<BackupEntry>();
    }

    public Task<AdminResponse> RestoreBackupAsync(string fileName, CancellationToken ct = default) =>
        InvokeAsync(AdminOps.RestoreBackup, new RestoreBackupArgs { FileName = fileName }, ct);

    public Task<AdminResponse> ImportConfigAsync(ServerConfig config, CancellationToken ct = default) =>
        InvokeAsync(AdminOps.ImportConfig, config, ct);

    public Task<BackupEntry?> CreateCheckpointAsync(CancellationToken ct = default) =>
        InvokeAsync<BackupEntry>(AdminOps.CreateCheckpoint, null, ct);
}
