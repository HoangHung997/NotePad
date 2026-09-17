using H2AgentLab.Transport;

namespace H2AgentLab.Context;

/// <summary>
/// V2 bridge from the durable LabSession journal into bounded active context. It deliberately does
/// not call LabSession.Context(): only completed user/assistant conversation events are eligible for
/// recent-turn context, while recovery/script/unverified-draft and any future event kinds remain in
/// the raw journal until a dedicated v2 context source explicitly models them.
/// </summary>
public sealed class LabSessionContextAdapter
{
    private readonly AgentContextManager _manager;

    public LabSessionContextAdapter(AgentContextManager? manager = null)
    {
        _manager = manager ?? new AgentContextManager();
    }

    public AgentContextSnapshot Build(
        LabSession session,
        string? taskContract = null,
        string? currentState = null,
        IReadOnlyList<AgentContextToolSummary>? toolSummaries = null,
        string? compactedHistory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var turns = new List<AgentContextTurn>();
        for (var index = 0; index < session.Events.Count; index++)
        {
            var journalEvent = session.Events[index];
            if (!TryRole(journalEvent.Kind, out var role) || string.IsNullOrWhiteSpace(journalEvent.Text))
                continue;
            turns.Add(new AgentContextTurn(
                SourceId: $"session:{session.Id:N}:event:{index}",
                Role: role,
                Content: journalEvent.Text,
                Sequence: index,
                Relevance: 1));
        }

        return _manager.Build(new AgentContextInput(
            TaskContract: taskContract,
            CurrentState: currentState,
            RecentTurns: turns,
            ToolSummaries: toolSummaries,
            CompactedHistory: compactedHistory));
    }

    private static bool TryRole(string? kind, out AgentTransportMessageRole role)
    {
        if (string.Equals(kind?.Trim(), "user", StringComparison.OrdinalIgnoreCase))
        {
            role = AgentTransportMessageRole.User;
            return true;
        }
        if (string.Equals(kind?.Trim(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            role = AgentTransportMessageRole.Assistant;
            return true;
        }
        role = default;
        return false;
    }
}
