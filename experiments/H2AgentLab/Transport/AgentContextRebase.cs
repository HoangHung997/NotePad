using System.Security.Cryptography;
using System.Text;

namespace H2AgentLab.Transport;

/// <summary>Explicit same-provider context segment at a fully observed protocol boundary.
/// Not a retry, model switch, restart replay or authority to execute a tool.</summary>
public interface IAgentContextRebaseTransport
{
    AgentRequestBudgetReceipt PreviewContextRebase(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, CancellationToken cancellationToken);
    IAsyncEnumerable<AgentTransportEvent> RebaseContextAsync(AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged, string expectedBodySha256,
        CancellationToken cancellationToken = default);
}

internal static class AgentContextRebase
{
    internal static void Validate(Guid task, Guid turn, bool started,
        IReadOnlyList<AgentTransportToolCall> pending, AgentTransportStartRequest context,
        AgentTransportContinuationRequest acknowledged)
    {
        if (!started || task == Guid.Empty || turn == Guid.Empty || context.TaskId != task
            || context.TurnId != turn || acknowledged.TaskId != task || acknowledged.TurnId != turn
            || context.Messages.Count == 0)
            throw new InvalidOperationException("context-rebase-identity");
        ValidatePair(pending, acknowledged.ToolResults);
        if (pending.Count == 0 && acknowledged.SupplementalUserMessages is not { Count: > 0 })
            throw new InvalidOperationException("context-rebase-not-at-continuation");
    }

    internal static void ValidatePair(IReadOnlyList<AgentTransportToolCall> calls,
        IReadOnlyList<AgentToolResult> results)
    {
        if (calls.Count != results.Count || calls.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count
            || results.Select(x => x.ToolCallId).Distinct(StringComparer.Ordinal).Count() != results.Count
            || calls.Any(c => !results.Any(r => r.ToolCallId == c.Id && r.ToolName == c.Name)))
            throw new InvalidOperationException("context-rebase-incomplete-tool-batch");
    }

    internal static void RequireFingerprint(AgentRequestBudgetReceipt preview, string expected)
    {
        if (expected is not { Length: 64 } || !string.Equals(preview.PayloadSha256, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("context-rebase-candidate-changed");
    }
}
