using System.Collections.Concurrent;

namespace H2AgentLab.Tools;

/// <summary>
/// Host-local mutation coordinator shared by all AgentRuntime instances created by one production
/// runtime factory. It is deliberately NOT a distributed lock or Coordinator/NAS fence.
/// Reads never acquire it; only mutating calls with an exact host resource key participate.
/// </summary>
public sealed class HostResourceMutationCoordinator
{
    private sealed class ResourceState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
        public int Uncertain;
    }

    private sealed class Lease(
        HostResourceMutationCoordinator owner,
        string key,
        ResourceState state) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            state.Gate.Release();
            owner.Release(key, state);
        }
    }

    private readonly ConcurrentDictionary<string, ResourceState> _states =
        new(StringComparer.Ordinal);

    internal async ValueTask<IDisposable> AcquireAsync(
        string resourceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        var state = _states.GetOrAdd(resourceKey, static _ => new ResourceState());
        Interlocked.Increment(ref state.Users);
        try
        {
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, resourceKey, state);
        }
        catch
        {
            Release(resourceKey, state);
            throw;
        }
    }

    internal bool IsUncertain(string resourceKey)
        => _states.TryGetValue(resourceKey, out var state)
            && Volatile.Read(ref state.Uncertain) != 0;

    internal void MarkUncertain(string resourceKey)
    {
        var state = _states.GetOrAdd(resourceKey, static _ => new ResourceState());
        Volatile.Write(ref state.Uncertain, 1);
    }

    internal void ClearUncertain(string resourceKey)
    {
        if (!_states.TryGetValue(resourceKey, out var state)) return;
        Volatile.Write(ref state.Uncertain, 0);
        TryCleanup(resourceKey, state);
    }

    internal int TrackedResourceCount => _states.Count;

    private void Release(string key, ResourceState state)
    {
        Interlocked.Decrement(ref state.Users);
        TryCleanup(key, state);
    }

    private void TryCleanup(string key, ResourceState state)
    {
        if (Volatile.Read(ref state.Users) != 0 || Volatile.Read(ref state.Uncertain) != 0) return;
        ((ICollection<KeyValuePair<string, ResourceState>>)_states)
            .Remove(new(key, state));
    }
}
