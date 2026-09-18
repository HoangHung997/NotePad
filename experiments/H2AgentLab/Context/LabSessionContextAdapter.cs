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

    public AgentContextInput BuildInput(
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
                $"session:{session.Id:N}:event:{index}",
                role,
                journalEvent.Text,
                index,
                1));
        }

        var summaries = toolSummaries ?? DeriveToolSummaries(session);
        return new AgentContextInput(
            TaskContract: taskContract,
            CurrentState: currentState,
            RecentTurns: turns,
            ToolSummaries: summaries,
            CompactedHistory: compactedHistory);
    }

    public AgentContextSnapshot Build(
        LabSession session,
        string? taskContract = null,
        string? currentState = null,
        IReadOnlyList<AgentContextToolSummary>? toolSummaries = null,
        string? compactedHistory = null)
        => _manager.Build(BuildInput(
            session,
            taskContract,
            currentState,
            toolSummaries,
            compactedHistory));

    private static IReadOnlyList<AgentContextToolSummary> DeriveToolSummaries(LabSession session)
    {
        var summaries = new List<AgentContextToolSummary>();
        for (var index = 0; index < session.Events.Count; index++)
        {
            var journalEvent = session.Events[index];
            if (string.IsNullOrWhiteSpace(journalEvent.Text)
                || string.IsNullOrWhiteSpace(journalEvent.Kind)
                || !journalEvent.Kind.StartsWith("tool", StringComparison.OrdinalIgnoreCase))
                continue;

            summaries.Add(new AgentContextToolSummary(
                $"session:{session.Id:N}:tool:{index}",
                journalEvent.Kind.Trim(),
                journalEvent.Text,
                index,
                1));
        }
        return summaries;
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
