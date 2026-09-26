using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace H2AgentLab.Transport;

public sealed partial class OpenAiResponsesWebSocketTransport
{
    public AgentRequestBudgetReceipt PreviewContextRebase(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, CancellationToken cancellationToken)
    {
        EnsureUsable(); cancellationToken.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (_fallback is IAgentContextRebaseTransport fallback)
            return fallback.PreviewContextRebase(context, acknowledged, cancellationToken);
        if (_fallback is not null || _connection?.IsOpen != true)
            throw new InvalidOperationException("context-rebase-connection-unavailable");
        AgentContextRebase.Validate(_taskId, _turnId, _started, _pendingCalls, context, acknowledged);
        if (context.AllowParallelToolCalls && !Capabilities.ParallelToolCalls)
            throw new InvalidOperationException("context-rebase-parallel-capability");
        var input = new JsonArray();
        foreach (var message in context.Messages) input.Add(ToResponsesMessage(message));
        return _requestBudget.Preview(BuildContextPayload(input, null, SnapshotTools(context.Tools),
            context.AllowParallelToolCalls, NormalizeCacheKey(context.PromptCacheKey)),
            _taskId, _turnId, "ResponsesWebSocket", cancellationToken);
    }

    public async IAsyncEnumerable<AgentTransportEvent> RebaseContextAsync(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, string expectedBodySha256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentContextRebase.RequireFingerprint(PreviewContextRebase(context, acknowledged, cancellationToken), expectedBodySha256);
        if (_fallback is IAgentContextRebaseTransport fallback)
        {
            await foreach (var item in fallback.RebaseContextAsync(context, acknowledged, expectedBodySha256,
                cancellationToken).WithCancellation(cancellationToken)) yield return item;
            yield break;
        }
        var input = new JsonArray();
        foreach (var message in context.Messages) input.Add(ToResponsesMessage(message));
        _tools.Clear(); foreach (var tool in SnapshotTools(context.Tools)) _tools[tool.Name] = tool;
        _allowParallelToolCalls = context.AllowParallelToolCalls;
        _promptCacheKey = NormalizeCacheKey(context.PromptCacheKey);
        _pendingCalls = []; _previousResponseId = null; _initialInput = input;
        // Reuse the currently open socket; never prewarm/reconnect/fallback to mask an uncertain send.
        await foreach (var item in SendAndReceive(BuildPayload(input, null), cancellationToken)
            .WithCancellation(cancellationToken)) yield return item;
    }
}
