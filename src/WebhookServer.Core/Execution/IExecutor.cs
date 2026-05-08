using WebhookServer.Core.Models;

namespace WebhookServer.Core.Execution;

public interface IExecutor
{
    Task<ExecutionResult> RunAsync(EndpointConfig endpoint, ExecutionContext ctx, CancellationToken ct);
}
