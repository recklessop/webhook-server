using System.Text;
using System.Text.Json;

namespace WebhookServer.Core.Ipc;

/// <summary>
/// Line-delimited JSON over a stream. One JSON object per line, terminated by '\n'.
/// </summary>
public static class PipeFraming
{
    public static async Task WriteAsync<T>(Stream stream, T payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, AdminProtocol.JsonOptions);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null) return default;
        if (string.IsNullOrWhiteSpace(line)) return default;
        return JsonSerializer.Deserialize<T>(line, AdminProtocol.JsonOptions);
    }

    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
}
