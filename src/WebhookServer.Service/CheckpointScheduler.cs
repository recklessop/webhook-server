using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebhookServer.Service;

/// <summary>
/// Creates a daily config checkpoint at midnight (local time). Combined with
/// the auto-on-save snapshots in ConfigStore.SaveAsync, this guarantees a
/// rollback point for every day even if the user makes no changes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CheckpointScheduler : BackgroundService
{
    private readonly ILogger<CheckpointScheduler> _logger;

    public CheckpointScheduler(ILogger<CheckpointScheduler> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily checkpoint scheduler running");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1);
            var delay = nextMidnight - now;

            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                var entry = AdminPipeServer.CreateCheckpoint("daily", "Nightly auto-checkpoint");
                _logger.LogInformation("Daily checkpoint created: {File}", entry.FileName);
            }
            catch (FileNotFoundException)
            {
                // No config.json yet (fresh install, GUI never opened) - skip silently.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Daily checkpoint creation failed");
            }
        }
    }
}
