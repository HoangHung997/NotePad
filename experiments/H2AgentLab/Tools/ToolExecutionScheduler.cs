using System.Collections.Concurrent;

namespace H2AgentLab.Tools;

public sealed record ToolExecutionRequest(
    ToolDescriptor Descriptor,
    global::H2AgentLab.ToolCall Call,
    string? ResourceKey = null)
{
    public Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<ToolExecutionOutput?>>? BeforeDispatchAsync { get; init; }
    public Action<global::H2AgentLab.ToolCall>? BeforeExecute { get; init; }
    public Action<global::H2AgentLab.ToolCall, ToolExecutionOutput>? AfterExecute { get; init; }
}

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
    private readonly HostResourceMutationCoordinator _mutations;
    private bool _disposed;
    private readonly ConcurrentDictionary<Guid, (string Resource, string JobId)> _runningJobs = new();

    public ToolExecutionScheduler(HostResourceMutationCoordinator? mutationCoordinator = null)
        => _mutations = mutationCoordinator ?? new HostResourceMutationCoordinator();

    // Called only after a bound host observation, never by model text or a provider's JSON.
    internal void ObserveCompletedJob(ToolOutcome outcome)
    {
        if (outcome.Status != ToolOutcomeStatus.Succeeded || outcome.NeedsReconciliation || outcome.Job is null) return;
        if (_runningJobs.TryGetValue(outcome.Invocation.InvocationId, out var owned)
            && owned.JobId == outcome.Job.JobId
            && _runningJobs.TryRemove(outcome.Invocation.InvocationId, out _))
            _mutations.ClearUncertain(owned.Resource);
    }

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

        IDisposable? resourceLease = null;
        var serialTaken = false;
        try
        {
            if (!request.Descriptor.SupportsParallel)
            {
                await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                serialTaken = true;
            }

            if (request.Descriptor.IsMutating)
                resourceLease = await _mutations.AcquireAsync(resourceKey!, cancellationToken).ConfigureAwait(false);

            var call = request.Call with { Invocation = ToolInvocation.Bind(request.Call) };
            ToolExecutionOutput output;
            try
            {
                if (request.Descriptor.IsMutating && _mutations.IsUncertain(resourceKey!))
                    output = ToolOutcomeBridge.Failure(call, request.Descriptor, "outcome_unknown", ToolErrorPhase.Preflight, ToolMutationEffect.None);
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output = request.BeforeDispatchAsync is null
                        ? null
                        : await request.BeforeDispatchAsync(call, cancellationToken).ConfigureAwait(false);
                    if (output is null)
                    {
                        request.BeforeExecute?.Invoke(call); // Durable intent/dispatch, still under the resource gate.
                        cancellationToken.ThrowIfCancellationRequested();
                        output = await ToolOutcomeBridge.ExecuteAsync(request.Descriptor, call, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (ToolInvocationCancelledException cancelled)
            {
                // Provider-local cancellation need not cancel the batch token. Fence the resource
                // before releasing its gate, so queued writes cannot repeat an uncertain effect.
                if (request.Descriptor.IsMutating && cancelled.Observed.Outcome.IsPending)
                    _mutations.MarkUncertain(resourceKey!);
                request.AfterExecute?.Invoke(call, cancelled.Observed);
                throw;
            }
            if (request.Descriptor.IsMutating && output.Outcome.IsPending)
            {
                _mutations.MarkUncertain(resourceKey!);
                if (output.Outcome.Status == ToolOutcomeStatus.Running && output.Outcome.Job is not null)
                    _runningJobs[call.Invocation!.InvocationId] = (resourceKey!, output.Outcome.Job.JobId);
            }
            try { request.AfterExecute?.Invoke(call, output); }
            catch
            {
                _runningJobs.TryRemove(call.Invocation!.InvocationId, out _); // Lost durable receipt is not a releasable running-job fence.
                if (request.Descriptor.IsMutating) _mutations.MarkUncertain(resourceKey!);
                throw;
            }
            results[index] = new ToolExecutionResult(index, request.Descriptor.Name, output.DomainPayload)
            { Outcome = output.Outcome };
        }
        finally
        {
            resourceLease?.Dispose();
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
    }
}
