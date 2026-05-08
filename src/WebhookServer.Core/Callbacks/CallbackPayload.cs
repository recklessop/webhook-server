using System.Text.Json.Serialization;

namespace WebhookServer.Core.Callbacks;

/// <summary>
/// JSON body POSTed to a configured outbound callback URL.
/// </summary>
public sealed class CallbackPayload
{
    [JsonPropertyName("runId")] public required string RunId { get; init; }
    [JsonPropertyName("endpoint")] public required string Endpoint { get; init; }
    [JsonPropertyName("startedAt")] public required DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public required DateTimeOffset CompletedAt { get; init; }
    [JsonPropertyName("durationMs")] public required long DurationMs { get; init; }
    [JsonPropertyName("exitCode")] public required int ExitCode { get; init; }
    [JsonPropertyName("succeeded")] public required bool Succeeded { get; init; }
    [JsonPropertyName("timedOut")] public required bool TimedOut { get; init; }
    [JsonPropertyName("stdout")] public string? Stdout { get; init; }
    [JsonPropertyName("stderr")] public string? Stderr { get; init; }
    [JsonPropertyName("stdoutTruncated")] public bool StdoutTruncated { get; init; }
    [JsonPropertyName("stderrTruncated")] public bool StderrTruncated { get; init; }
}
