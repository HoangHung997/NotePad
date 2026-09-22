using System.Collections.Concurrent;

namespace H2AgentLab.Tools;

public sealed record ToolExecutionRequest(
    ToolDescriptor Descriptor,
    global::H2AgentLab.ToolCall Call,
    string? ResourceKey = null);

public sealed record ToolExecutionResult(
    int Index,
    string ToolName,
    string Output)
{
    public ToolOutcome? Outcome { get; init; }
}

/// <summary>
/// Executes independent parallel-safe reads concurrently. A mutation must identify its resource and
/// acquires a per-resource gate, so overlapping mutations are serialized. Descriptors that opt out
/// of parallel execution additionally pass through a global serial gate.
/// </summary>
public sealed class ToolExecutionScheduler : IDisposable
{
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resourceGates =
        new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly ConcurrentDictionary<string, byte> _uncertainResources = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ToolExecutionResult>> ExecuteBatchAsync(
        IReadOnlyList<ToolExecutionRequest> requests,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) return Array.Empty<ToolExecutionResult>();

        var results = new ToolExecutionResult[requests.Count];
        var tasks = requests.Select((request, index) =>
            ExecuteOneAsync(request, index, results, cancellationToken)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task ExecuteOneAsync(
        ToolExecutionRequest request,
        int index,
        ToolExecutionResult[] results,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Descriptor);
        ArgumentNullException.ThrowIfNull(request.Call);

        if (!string.Equals(request.Descriptor.Name, request.Call.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Descriptor '{request.Descriptor.Name}' cannot execute call '{request.Call.Name}'.");

        var resourceKey = NormalizeResourceKey(request.ResourceKey);
        if (request.Descriptor.IsMutating && resourceKey is null)
            throw new InvalidOperationException(
                $"Mutating tool '{request.Descriptor.Name}' requires a resource key for serialization.");

        SemaphoreSlim? resourceGate = null;
        var serialTaken = false;
        var resourceTaken = false;
        try
        {
            if (!request.Descriptor.SupportsParallel)
            {
                await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                serialTaken = true;
            }

            if (request.Descriptor.IsMutating)
            {
                resourceGate = _resourceGates.GetOrAdd(resourceKey!, _ => new SemaphoreSlim(1, 1));
                await resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                resourceTaken = true;
            }

            var call = request.Call with { Invocation = ToolInvocation.Bind(request.Call) };
            var output = request.Descriptor.IsMutating && _uncertainResources.ContainsKey(resourceKey!)
                ? ToolOutcomeBridge.Failure(call, request.Descriptor, "outcome_unknown", ToolErrorPhase.Preflight, ToolMutationEffect.None)
                : await ToolOutcomeBridge.ExecuteAsync(request.Descriptor, call, cancellationToken).ConfigureAwait(false);
            if (request.Descriptor.IsMutating && output.Outcome.IsPending) _uncertainResources.TryAdd(resourceKey!, 0);
            results[index] = new ToolExecutionResult(index, request.Descriptor.Name, output.DomainPayload)
            { Outcome = output.Outcome };
        }
        finally
        {
            if (resourceTaken) resourceGate!.Release();
            if (serialTaken) _serialGate.Release();
        }
    }

    private static string? NormalizeResourceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 512 || normalized.Any(char.IsControl))
            throw new ArgumentException(
                "Tool resource key must be <=512 characters and contain no control characters.",
                nameof(value));
        return normalized;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serialGate.Dispose();
        foreach (var gate in _resourceGates.Values)
            gate.Dispose();
        _resourceGates.Clear();
    }
}
