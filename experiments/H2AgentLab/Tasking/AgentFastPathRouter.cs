namespace H2AgentLab.Tasking;

public enum AgentTaskRouteClass
{
    Direct = 0,
    Retrieval = 1,
    Action = 2,
    ComplexAgent = 3
}

public enum AgentRouteSchemaMode
{
    None = 0,
    RetrievalOnly = 1,
    DeferredToolSearch = 2
}

/// <summary>
/// Host-observed routing facts. These are not model-generated permissions; mutation capability still
/// comes from the immutable task contract.
/// </summary>
public sealed record AgentTaskRoutingSignals(
    bool NeedsExternalRetrieval = false,
    bool NeedsAction = false,
    bool NeedsComplexPlanning = false);

public sealed record AgentTaskRouteDecision(
    AgentTaskRouteClass RouteClass,
    AgentRouteSchemaMode SchemaMode,
    IReadOnlyList<string> InitialToolNamespaces)
{
    public bool IsDirect => RouteClass == AgentTaskRouteClass.Direct;
}

/// <summary>
/// Cheap deterministic router used before loading heavy tool schemas. Direct requests intentionally
/// carry no tool namespaces, so Office/Desktop/Python schema material cannot enter the fast path.
/// </summary>
public sealed class AgentFastPathRouter
{
    private static readonly IReadOnlyList<string> NoNamespaces = Array.Empty<string>();
    private static readonly IReadOnlyList<string> RetrievalNamespaces =
        Array.AsReadOnly(new[] { "files" });

    public AgentTaskRouteDecision Route(
        AgentTaskContract contract,
        AgentTaskRoutingSignals? signals = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        signals ??= new AgentTaskRoutingSignals();

        if (signals.NeedsComplexPlanning)
            return new(
                AgentTaskRouteClass.ComplexAgent,
                AgentRouteSchemaMode.DeferredToolSearch,
                NoNamespaces);

        if (contract.IsMutating || signals.NeedsAction)
            return new(
                AgentTaskRouteClass.Action,
                AgentRouteSchemaMode.DeferredToolSearch,
                NoNamespaces);

        if (signals.NeedsExternalRetrieval || contract.Inputs.Count > 0)
            return new(
                AgentTaskRouteClass.Retrieval,
                AgentRouteSchemaMode.RetrievalOnly,
                RetrievalNamespaces);

        return new(
            AgentTaskRouteClass.Direct,
            AgentRouteSchemaMode.None,
            NoNamespaces);
    }
}
