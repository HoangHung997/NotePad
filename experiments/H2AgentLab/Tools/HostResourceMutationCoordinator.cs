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
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
        public bool Uncertain;
        public bool Retired;
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
        ResourceState state;
        while (true)
        {
            state = _states.GetOrAdd(resourceKey, static _ => new ResourceState());
            lock (state.Sync)
            {
                // Cleanup marks a state retired before removing it from the dictionary.
                // A caller that raced with cleanup must retry against the replacement state,
                // otherwise two semaphores could protect the same resource concurrently.
                if (state.Retired) continue;
                checked { state.Users++; }
                break;
            }
        }

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
    {
        if (!_states.TryGetValue(resourceKey, out var state)) return false;
        lock (state.Sync) return !state.Retired && state.Uncertain;
    }

    internal void MarkUncertain(string resourceKey)
    {
        while (true)
        {
            var state = _states.GetOrAdd(resourceKey, static _ => new ResourceState());
            lock (state.Sync)
            {
                if (state.Retired) continue;
                state.Uncertain = true;
                return;
            }
        }
    }

    internal void ClearUncertain(string resourceKey)
    {
        if (!_states.TryGetValue(resourceKey, out var state)) return;
        lock (state.Sync)
        {
            if (state.Retired) return;
            state.Uncertain = false;
            RetireIfUnused(resourceKey, state);
        }
    }

    internal int TrackedResourceCount => _states.Count;

    private void Release(string key, ResourceState state)
    {
        lock (state.Sync)
        {
            if (state.Users <= 0)
                throw new InvalidOperationException("Mutation coordinator resource lease underflow.");
            state.Users--;
            RetireIfUnused(key, state);
        }
    }

    private void RetireIfUnused(string key, ResourceState state)
    {
        // Caller holds state.Sync. Mark retired BEFORE removal so a concurrent Acquire that
        // already read this state cannot increment it and then lose serialization.
        if (state.Retired || state.Users != 0 || state.Uncertain) return;
        state.Retired = true;
        ((ICollection<KeyValuePair<string, ResourceState>>)_states)
            .Remove(new(key, state));
    }
}
