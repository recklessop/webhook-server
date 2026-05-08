namespace WebhookServer.Core.Execution;

public sealed class ExecutionResult
{
    public required string RunId { get; init; }
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required bool TimedOut { get; init; }
    public string? LaunchError { get; init; }

    public TimeSpan Duration => CompletedAt - StartedAt;
    public bool Succeeded => !TimedOut && LaunchError is null && ExitCode == 0;
}
