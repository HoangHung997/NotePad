using System.Text.Json;
using H2AgentLab.Capabilities;
using H2Notes.Core;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter : IH2AgentExtensionLifecycle
{
    private readonly H2AgentExtensionLifecycleHost _extensionLifecycle;

    public IReadOnlyList<H2AgentPluginState> GetPlugins()
        => _extensionLifecycle.GetPlugins();
    public H2AgentPluginState InstallPlugin(H2AgentPluginPackage package)
        => _extensionLifecycle.Install(package);
    public H2AgentPluginState EnablePlugin(string pluginId, string version)
        => _extensionLifecycle.Enable(pluginId, version);
    public void DisablePlugin(string pluginId)
        => _extensionLifecycle.Disable(pluginId);
    public H2AgentPluginState SelfTestPlugin(string pluginId, string version)
        => _extensionLifecycle.SelfTest(pluginId, version);
    public H2AgentPluginState RollbackPlugin(string pluginId)
        => _extensionLifecycle.Rollback(pluginId);
    public void QuarantinePlugin(string pluginId, string version, string reason)
        => _extensionLifecycle.Quarantine(pluginId, version, reason);
    public void UninstallPlugin(string pluginId, string version)
        => _extensionLifecycle.Uninstall(pluginId, version);

    private void ObserveCapabilitySnapshot(LiveTask live, TaskCapabilitySnapshot snapshot)
    {
        var projected = TaskCapabilitySnapshotBuilder.Evidence(snapshot);
        var evidence = new H2AgentEvidence(
            "capability:" + live.TaskId.ToString("N") + ":" + snapshot.Revision,
            "capability-pin",
            null,
            Bound(JsonSerializer.Serialize(projected), 2_000),
            Provenance: "H2AgentLab.Capabilities.TaskCapabilitySnapshot");
        lock (live.Gate)
        {
            live.Evidence.RemoveAll(x => x.EvidenceId == evidence.EvidenceId);
            live.Evidence.Add(evidence);
        }
        _archive.RecordVerification(live.TaskId, new
        {
            kind = "capability-pin",
            snapshot.TaskId,
            snapshot.Revision,
            snapshot.RegistryVersion,
            evidence = projected
        });
        _archive.Upsert(live.Snapshot());
    }
}
