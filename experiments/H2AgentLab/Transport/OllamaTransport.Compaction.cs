using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace H2AgentLab.Transport;

public sealed partial class OllamaTransport
{
    public AgentRequestBudgetReceipt PreviewContextRebase(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, CancellationToken cancellationToken)
    {
        EnsureUsable(); cancellationToken.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        AgentContextRebase.Validate(_taskId, _turnId, _started, _pendingCalls, context, acknowledged);
        if (context.AllowParallelToolCalls && !Capabilities.ParallelToolCalls)
            throw new InvalidOperationException("context-rebase-parallel-capability");
        var tools = SnapshotTools(context.Tools);
        var input = new JsonArray();
        foreach (var message in context.Messages) input.Add(ToOllamaMessage(message));
        return _requestBudget.Preview(BuildContextPayload(input, tools),
            _taskId, _turnId, "OllamaTransport", cancellationToken);
    }

    public async IAsyncEnumerable<AgentTransportEvent> RebaseContextAsync(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, string expectedBodySha256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentContextRebase.RequireFingerprint(PreviewContextRebase(context, acknowledged, cancellationToken), expectedBodySha256);
        // The host has durably checkpointed the entire completed batch before this call.
        // This is a new context segment, not resubmission of any tool action.
        _pendingCalls = []; _tools.Clear();
        _started = false;
        await foreach (var item in StartAsync(context, cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }
}
