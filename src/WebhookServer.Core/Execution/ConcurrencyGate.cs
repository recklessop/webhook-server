using System.Collections.Concurrent;

namespace WebhookServer.Core.Execution;

/// <summary>
/// Holds one <see cref="SemaphoreSlim"/> per endpoint. When an endpoint is configured
/// with Serialize=true, the executor must acquire its semaphore before running and
/// release after — guaranteeing at-most-one concurrent run per endpoint.
/// </summary>
public sealed class ConcurrencyGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task<IDisposable> AcquireAsync(Guid endpointId, CancellationToken ct)
    {
        var sem = _gates.GetOrAdd(endpointId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    public void Forget(Guid endpointId)
    {
        if (_gates.TryRemove(endpointId, out var sem))
            sem.Dispose();
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }
}
