namespace H2Notes.Core;

public enum H2AgentPluginTrust
{
    Unknown = 0,
    TrustedOfficial = 1,
    OrganizationApproved = 2,
    LocalDeveloper = 3,
    Untrusted = 4
}

public sealed record H2AgentPluginPackage(
    string LocalArchivePath,
    string Id,
    string Name,
    string Version,
    string Summary,
    string Publisher,
    string MinAgentVersion,
    H2AgentPluginTrust Trust,
    string ArchiveSha256,
    bool UserApproved);

public sealed record H2AgentPluginState(
    string Id,
    string Version,
    string Publisher,
    bool Enabled,
    bool Quarantined,
    bool IntegrityValid,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Providers,
    string? ProblemCode);

/// <summary>
/// Product-facing extension lifecycle command path. H2 sees only bounded package/status data;
/// the concrete Agent bridge owns extension verification, activation and task pinning.
/// </summary>
public interface IH2AgentExtensionLifecycle
{
    IReadOnlyList<H2AgentPluginState> GetPlugins();
    H2AgentPluginState InstallPlugin(H2AgentPluginPackage package);
    H2AgentPluginState EnablePlugin(string pluginId, string version);
    void DisablePlugin(string pluginId);
    H2AgentPluginState SelfTestPlugin(string pluginId, string version);
    H2AgentPluginState RollbackPlugin(string pluginId);
    void QuarantinePlugin(string pluginId, string version, string reason);
    void UninstallPlugin(string pluginId, string version);
}
