using System.Text.Json.Nodes;

namespace WebhookServer.Core.Execution;

/// <summary>
/// All data the executor needs from the inbound HTTP request.
/// </summary>
public sealed class ExecutionContext
{
    public required string RunId { get; init; }
    public required string Slug { get; init; }
    public required byte[] BodyBytes { get; init; }
    public required string BodyString { get; init; }
    public JsonNode? BodyJson { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Route { get; init; }
}
