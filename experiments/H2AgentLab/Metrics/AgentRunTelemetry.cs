namespace H2AgentLab.Metrics;

public sealed class AgentRunTelemetry
{
    public AgentRunTelemetry(Guid? taskId = null, Guid? turnId = null)
    {
        Trace = new AgentTrace(taskId, turnId);
        Metrics = new AgentMetrics();
    }

    public AgentTrace Trace { get; }
    public AgentMetrics Metrics { get; }
}
