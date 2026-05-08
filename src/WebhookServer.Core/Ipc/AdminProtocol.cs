using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebhookServer.Core.Ipc;

/// <summary>
/// Operation discriminators for the named-pipe admin protocol. Request payload shape
/// is op-specific; the handler is responsible for binding <see cref="AdminRequest.Data"/>
/// to the right concrete type.
/// </summary>
public static class AdminOps
{
    public const string GetConfig = "get-config";
    public const string UpdateConfig = "update-config";
    public const string ListEndpoints = "list-endpoints";
    public const string CreateEndpoint = "create-endpoint";
    public const string UpdateEndpoint = "update-endpoint";
    public const string DeleteEndpoint = "delete-endpoint";
    public const string EnableEndpoint = "enable-endpoint";
    public const string DisableEndpoint = "disable-endpoint";
    public const string GetStatus = "get-status";
    public const string TailLogs = "tail-logs";
    public const string BindHttps = "bind-https";
    public const string RestartListener = "restart-listener";
    public const string Ping = "ping";
}

public sealed class AdminRequest
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
}

public sealed class AdminResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }

    public static AdminResponse Success(object? payload = null)
    {
        if (payload is null) return new AdminResponse { Ok = true };
        var doc = JsonSerializer.SerializeToDocument(payload, AdminProtocol.JsonOptions);
        return new AdminResponse { Ok = true, Data = doc.RootElement.Clone() };
    }

    public static AdminResponse Failure(string error) => new() { Ok = false, Error = error };
}

public sealed class StatusInfo
{
    public bool Running { get; set; }
    public int HttpPort { get; set; }
    public int? HttpsPort { get; set; }
    public string? DisplayHost { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int EndpointCount { get; set; }
}

public sealed class EndpointToggle
{
    public Guid Id { get; set; }
}

public sealed class DeleteEndpointArgs
{
    public Guid Id { get; set; }
}

public sealed class TailLogsArgs
{
    public int LinesToBacklog { get; set; } = 100;
    public bool Follow { get; set; } = true;
}

public sealed class LogLine
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = "";
}

public static class AdminProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
