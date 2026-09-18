namespace H2AgentLab;

/// <summary>
/// Stable, non-secret version labels that identify the host-owned policy and tool surface used for
/// a v2 turn. These labels are metadata for cache identity and trace reproducibility; they are not
/// inserted into the model prompt. Bump the corresponding identifier whenever its cacheable contract
/// changes in a way that can affect model behavior.
/// </summary>
public sealed record AgentVersionIdentifiers
{
    public const int MaxIdentifierCharacters = 64;

    public AgentVersionIdentifiers(string agentPolicyVersion, string safetyPolicyVersion, string toolsetVersion)
    {
        AgentPolicyVersion = Validate(agentPolicyVersion, nameof(agentPolicyVersion));
        SafetyPolicyVersion = Validate(safetyPolicyVersion, nameof(safetyPolicyVersion));
        ToolsetVersion = Validate(toolsetVersion, nameof(toolsetVersion));
    }

    public string AgentPolicyVersion { get; }
    public string SafetyPolicyVersion { get; }
    public string ToolsetVersion { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        value = value.Trim();
        if (value.Length > MaxIdentifierCharacters
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/')))
            throw new ArgumentException("Agent version identifiers must be short ASCII labels using letters, digits, '.', '-', '_' or '/'.", parameterName);
        return value;
    }
}

/// <summary>
/// Current v2 host contracts. Phase 05 may replace the bootstrap toolset label with the registry
/// version, but callers can already carry the same typed metadata through prompt/cache/trace paths.
/// </summary>
public static class AgentVersions
{
    public const string AgentPolicy = "agent-policy-v2.0.0";
    public const string SafetyPolicy = "safety-policy-v2.0.0";
    public const string Toolset = "toolset-bootstrap-v1.0.0";

    public static AgentVersionIdentifiers Current { get; } = new(AgentPolicy, SafetyPolicy, Toolset);
}
