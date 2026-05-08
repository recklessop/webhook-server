using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebhookServer.Core.Storage;

/// <summary>
/// Shared JSON serialization options used for persisting <see cref="Models.ServerConfig"/>
/// and for IPC payloads. Keeps formatting and naming consistent.
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
