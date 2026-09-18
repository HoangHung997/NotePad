using H2AgentLab.Transport;

namespace H2AgentLab.Prompting;

/// <summary>
/// Cacheable, host-owned prompt material. These values may depend on the selected model/profile or
/// toolset version, but must not contain current time, session journal entries or mutable workspace
/// state. Later cache-identity work can hash this prefix without inspecting runtime context.
/// </summary>
public sealed record AgentPromptStablePrefix(
    AgentVersionIdentifiers Versions,
    string BasePolicy,
    string SecurityPolicy,
    string ModelPolicy,
    string ToolNamespaceMetadata);

/// <summary>
/// Mutable per-task/per-turn context. This is deliberately a different type from the stable prefix so
/// time, journal/workspace state and task progress have one explicit place after the cache boundary.
/// </summary>
public sealed record AgentPromptRuntimeContext(
    string? TaskContract = null,
    string? WorkingState = null,
    string? LiveEnvironment = null);

/// <summary>
/// Provider-neutral prompt layout for the v2 path. Stable system messages are always emitted first,
/// then dynamic host context, then the user message. V1 keeps its frozen prompt assembly as the A/B
/// comparator until its explicit migration task.
/// </summary>
public sealed class AgentPromptLayout
{
    private readonly AgentTransportMessage[] _stablePrefix;
    private readonly AgentTransportMessage[] _dynamicSuffix;
    private readonly AgentTransportMessage[] _messages;

    private AgentPromptLayout(
        AgentVersionIdentifiers versions,
        AgentTransportMessage[] stablePrefix,
        AgentTransportMessage[] dynamicSuffix)
    {
        Versions = versions;
        _stablePrefix = stablePrefix;
        _dynamicSuffix = dynamicSuffix;
        _messages = [.. stablePrefix, .. dynamicSuffix];
    }

    /// <summary>Out-of-band version metadata used by cache identity and persisted trace evidence.</summary>
    public AgentVersionIdentifiers Versions { get; }
    public IReadOnlyList<AgentTransportMessage> StablePrefix => _stablePrefix;
    public IReadOnlyList<AgentTransportMessage> DynamicSuffix => _dynamicSuffix;
    public IReadOnlyList<AgentTransportMessage> Messages => _messages;

    /// <summary>The first message index that is not part of the cacheable stable prefix.</summary>
    public int CacheBoundaryIndex => _stablePrefix.Length;

    public static AgentPromptLayout Create(
        AgentPromptStablePrefix stable,
        AgentPromptRuntimeContext runtime,
        string userInput)
    {
        ArgumentNullException.ThrowIfNull(stable);
        ArgumentNullException.ThrowIfNull(stable.Versions);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(stable.BasePolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(stable.SecurityPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        var stableMessages = new List<AgentTransportMessage>(4);
        AddSystem(stableMessages, stable.BasePolicy);
        AddSystem(stableMessages, stable.SecurityPolicy);
        AddSystem(stableMessages, stable.ModelPolicy);
        AddSystem(stableMessages, stable.ToolNamespaceMetadata);

        var dynamicMessages = new List<AgentTransportMessage>(4);
        AddSystem(dynamicMessages, runtime.TaskContract);
        AddSystem(dynamicMessages, runtime.WorkingState);
        AddSystem(dynamicMessages, runtime.LiveEnvironment);
        dynamicMessages.Add(new(AgentTransportMessageRole.User, userInput));

        return new(stable.Versions, stableMessages.ToArray(), dynamicMessages.ToArray());
    }

    private static void AddSystem(List<AgentTransportMessage> target, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            target.Add(new(AgentTransportMessageRole.System, content));
    }
}
