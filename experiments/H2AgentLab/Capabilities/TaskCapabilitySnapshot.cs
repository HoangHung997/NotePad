using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public sealed record TaskProviderPin(
    string ProviderId,
    string ProviderVersion,
    string ServerId,
    string TransportKind);

public sealed record TaskPluginPin(
    string PluginId,
    string PluginVersion,
    string Publisher);

public sealed record TaskToolPin(
    string ToolName,
    string ToolVersion,
    string SchemaVersion,
    string ProviderId,
    string ProviderVersion);

public sealed record TaskSkillPin(
    SkillIdentity Identity,
    string Name,
    string Description);

public sealed record TaskCapabilitySnapshot(
    Guid TaskId,
    long Revision,
    long RegistryVersion,
    IReadOnlyList<TaskProviderPin> Providers,
    IReadOnlyList<TaskPluginPin> Plugins,
    IReadOnlyList<TaskToolPin> Tools,
    IReadOnlyList<TaskSkillPin> Skills,
    DateTime CapturedUtc,
    string Reason);

public sealed record TaskCapabilityEvidence(
    Guid TaskId,
    long Revision,
    long RegistryVersion,
    IReadOnlyList<string> ProviderVersions,
    IReadOnlyList<string> PluginVersions,
    IReadOnlyList<string> ToolVersions,
    IReadOnlyList<string> SkillHashes);

public static class TaskCapabilitySnapshotBuilder
{
    public static TaskCapabilitySnapshot Capture(
        Guid taskId,
        long revision,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        IEnumerable<SkillSummary> selectedSkills,
        string reason)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task ID is empty.", nameof(taskId));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(selectedSkills);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var providerPins = providers
            .Select(x => new TaskProviderPin(
                x.ProviderId,
                x.ProviderVersion,
                x.ServerId,
                x.TransportKind))
            .OrderBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToArray();

        var pluginPins = plugins
            .Select(x => new TaskPluginPin(
                x.Manifest.Id,
                x.Manifest.Version,
                x.Manifest.Publisher))
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ToArray();

        var toolPins = registry.Tools
            .Select(x => new TaskToolPin(
                x.Name,
                x.Provenance?.ToolVersion ?? x.SchemaVersion,
                x.SchemaVersion,
                x.Provenance?.ProviderId ?? "built-in",
                x.Provenance?.ProviderVersion ?? "built-in"))
            .OrderBy(x => x.ToolName, StringComparer.Ordinal)
            .ToArray();

        var skillPins = selectedSkills
            .Select(x => new TaskSkillPin(x.Identity, x.Name, x.Description))
            .OrderBy(x => x.Identity.SourceId, StringComparer.Ordinal)
            .ThenBy(x => x.Identity.SkillId, StringComparer.Ordinal)
            .ToArray();

        return new TaskCapabilitySnapshot(
            taskId,
            revision,
            registry.Version,
            providerPins,
            pluginPins,
            toolPins,
            skillPins,
            DateTime.UtcNow,
            reason.Trim());
    }

    public static TaskCapabilitySnapshot ReviseAfterExplicitInstall(
        TaskCapabilitySnapshot previous,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        IEnumerable<SkillSummary> selectedSkills,
        string installedPluginId)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedPluginId);
        return Capture(
            previous.TaskId,
            previous.Revision + 1,
            registry,
            providers,
            plugins,
            selectedSkills,
            "explicit capability installation: " + installedPluginId.Trim());
    }

    public static TaskCapabilityEvidence Evidence(TaskCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new TaskCapabilityEvidence(
            snapshot.TaskId,
            snapshot.Revision,
            snapshot.RegistryVersion,
            snapshot.Providers
                .Select(x => x.ProviderId + "@" + x.ProviderVersion)
                .ToArray(),
            snapshot.Plugins
                .Select(x => x.PluginId + "@" + x.PluginVersion)
                .ToArray(),
            snapshot.Tools
                .Select(x => x.ToolName + "@tool=" + x.ToolVersion + ";schema=" + x.SchemaVersion)
                .ToArray(),
            snapshot.Skills
                .Select(x => x.Identity.SourceId + ":" + x.Identity.SkillId + "@sha256:" + x.Identity.Sha256)
                .ToArray());
    }
}

public sealed class TaskCapabilityPinGuard
{
    private TaskCapabilitySnapshot _current;

    public TaskCapabilityPinGuard(TaskCapabilitySnapshot initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public TaskCapabilitySnapshot Current => _current;

    public void EnsureStillPinned(
        ToolRegistry registry,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plugins);

        if (registry.Version != _current.RegistryVersion)
            throw new InvalidOperationException(
                "ToolRegistry changed during a pinned task; explicit capability snapshot revision is required.");

        var active = plugins.ToDictionary(
            x => x.Manifest.Id,
            x => x.Manifest.Version,
            StringComparer.Ordinal);
        foreach (var plugin in _current.Plugins)
        {
            if (!active.TryGetValue(plugin.PluginId, out var version)
                || version != plugin.PluginVersion)
                throw new InvalidOperationException(
                    $"Plugin '{plugin.PluginId}' changed during a pinned task.");
        }
    }

    public void ReplaceWithExplicitRevision(TaskCapabilitySnapshot revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.TaskId != _current.TaskId
            || revision.Revision != _current.Revision + 1)
            throw new InvalidOperationException("Capability snapshot revision is not the next revision for this task.");
        _current = revision;
    }
}
