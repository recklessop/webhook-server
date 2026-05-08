using Microsoft.Extensions.Hosting;
using WebhookServer.Core.Callbacks;

namespace WebhookServer.Service;

internal sealed class CallbackBackgroundService : BackgroundService
{
    private readonly CallbackDispatcher _dispatcher;

    public CallbackBackgroundService(CallbackDispatcher dispatcher) => _dispatcher = dispatcher;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _dispatcher.RunAsync(stoppingToken);
}
