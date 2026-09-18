namespace H2AgentLab.Metrics;

public sealed class AgentRunTelemetry
{
    public AgentRunTelemetry(
        Guid? taskId = null,
        Guid? turnId = null,
        AgentVersionIdentifiers? versions = null)
    {
        Trace = new AgentTrace(taskId, turnId, versions: versions);
        Metrics = new AgentMetrics();
    }

    public AgentTrace Trace { get; }
    public AgentMetrics Metrics { get; }
}
