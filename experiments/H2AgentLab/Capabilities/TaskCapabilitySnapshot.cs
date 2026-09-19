using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public sealed record TaskModelPin(
    string ProviderId,
    string ProviderVersion,
    string ModelId);

public sealed record TaskPolicyPin(
    string PolicyId,
    string Version);

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

/// <summary>
/// Explicit task-local usage/selection. Nothing enters a capability snapshot merely because it is
/// installed on the machine. Model/provider identity and important host policy versions are pinned
/// alongside only the tools/providers/plugins/skills the task actually used or selected.
/// </summary>
public sealed record TaskCapabilitySelection(
    TaskModelPin? Model,
    IReadOnlyList<string> UsedToolNames,
    IReadOnlyList<string> UsedProviderIds,
    IReadOnlyList<string> UsedPluginIds,
    IReadOnlyList<SkillSummary> SelectedSkills,
    IReadOnlyList<TaskPolicyPin> Policies);

public sealed record TaskCapabilitySnapshot(
    Guid TaskId,
    long Revision,
    long RegistryVersion,
    IReadOnlyList<TaskProviderPin> Providers,
    IReadOnlyList<TaskPluginPin> Plugins,
    IReadOnlyList<TaskToolPin> Tools,
    IReadOnlyList<TaskSkillPin> Skills,
    DateTime CapturedUtc,
    string Reason)
{
    /// <summary>
    /// RegistryVersion is diagnostic provenance only. It is never a correctness pin; unrelated
    /// registry changes are allowed while a task is active.
    /// </summary>
    public TaskModelPin? Model { get; init; }

    public IReadOnlyList<TaskPolicyPin> Policies { get; init; }
        = Array.Empty<TaskPolicyPin>();
}

public sealed record TaskCapabilityEvidence(
    Guid TaskId,
    long Revision,
    long RegistryVersion,
    IReadOnlyList<string> ProviderVersions,
    IReadOnlyList<string> PluginVersions,
    IReadOnlyList<string> ToolVersions,
    IReadOnlyList<string> SkillHashes)
{
    public string? ModelIdentity { get; init; }

    public IReadOnlyList<string> PolicyVersions { get; init; }
        = Array.Empty<string>();
}

public static class TaskCapabilitySnapshotBuilder
{
    /// <summary>
    /// Capture only task-used/selected capabilities. The registry/provider/plugin collections are
    /// authoritative lookup sources for exact versions; they are not copied wholesale.
    /// </summary>
    public static TaskCapabilitySnapshot CaptureUsed(
        Guid taskId,
        long revision,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        TaskCapabilitySelection selection,
        string reason)
    {
        ValidateTask(taskId, revision, registry, providers, plugins, selection, reason);

        var providerArray = providers.ToArray();
        var pluginArray = plugins.ToArray();
        var providerById = providerArray
            .GroupBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var pluginById = pluginArray
            .GroupBy(x => x.Manifest.Id, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var usedToolNames = NormalizeNames(
            selection.UsedToolNames,
            nameof(selection.UsedToolNames));
        var tools = usedToolNames
            .Select(name =>
            {
                if (!registry.TryGet(name, out var descriptor))
                    throw new InvalidOperationException(
                        $"Task-selected tool '{name}' is not currently registered.");
                return ToolPin(descriptor);
            })
            .OrderBy(x => x.ToolName, StringComparer.Ordinal)
            .ToArray();

        var selectedSkills = (selection.SelectedSkills
                ?? Array.Empty<SkillSummary>())
            .GroupBy(
                x => SkillKey(x.Identity),
                StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.Identity.SourceId, StringComparer.Ordinal)
            .ThenBy(x => x.Identity.SkillId, StringComparer.Ordinal)
            .ToArray();

        var pluginIds = new HashSet<string>(
            NormalizeNames(
                selection.UsedPluginIds,
                nameof(selection.UsedPluginIds)),
            StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var pluginId = PluginIdFromProvider(tool.ProviderId);
            if (pluginId is not null)
                pluginIds.Add(pluginId);
        }

        foreach (var skill in selectedSkills)
        {
            if (!string.IsNullOrWhiteSpace(skill.Identity.PluginId))
                pluginIds.Add(skill.Identity.PluginId!);
        }

        var pluginPins = pluginIds
            .Select(id =>
            {
                if (!pluginById.TryGetValue(id, out var active))
                    throw new InvalidOperationException(
                        $"Task-selected plugin '{id}' is not currently active.");
                return new TaskPluginPin(
                    active.Manifest.Id,
                    active.Manifest.Version,
                    active.Manifest.Publisher);
            })
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ToArray();

        foreach (var skill in selectedSkills.Where(x =>
            !string.IsNullOrWhiteSpace(x.Identity.PluginId)))
        {
            var active = pluginPins.Single(x =>
                x.PluginId == skill.Identity.PluginId);
            if (!string.Equals(
                    active.PluginVersion,
                    skill.Identity.PluginVersion,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Selected skill '{skill.Identity.SkillId}' is from plugin version "
                    + $"'{skill.Identity.PluginVersion}', but active version is "
                    + $"'{active.PluginVersion}'.");
        }

        var providerIds = new HashSet<string>(
            NormalizeNames(
                selection.UsedProviderIds,
                nameof(selection.UsedProviderIds)),
            StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            if (providerById.ContainsKey(tool.ProviderId))
                providerIds.Add(tool.ProviderId);
        }

        var providerPins = providerIds
            .Select(id =>
            {
                if (!providerById.TryGetValue(id, out var provider))
                    throw new InvalidOperationException(
                        $"Task-used provider '{id}' is not currently available.");
                return new TaskProviderPin(
                    provider.ProviderId,
                    provider.ProviderVersion,
                    provider.ServerId,
                    provider.TransportKind);
            })
            .OrderBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToArray();

        var skillPins = selectedSkills
            .Select(x => new TaskSkillPin(
                x.Identity,
                x.Name,
                x.Description))
            .ToArray();

        var policies = NormalizePolicies(selection.Policies);

        return new TaskCapabilitySnapshot(
            taskId,
            revision,
            registry.Version,
            providerPins,
            pluginPins,
            tools,
            skillPins,
            DateTime.UtcNow,
            reason.Trim())
        {
            Model = NormalizeModel(selection.Model),
            Policies = policies
        };
    }

    /// <summary>
    /// Compatibility overload for legacy callers. It no longer snapshots the whole registry or all
    /// installed providers/plugins. It scopes pins to the selected plugin skills and that plugin's
    /// registered tools/provider declarations only.
    /// </summary>
    public static TaskCapabilitySnapshot Capture(
        Guid taskId,
        long revision,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        IEnumerable<SkillSummary> selectedSkills,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(selectedSkills);
        var skills = selectedSkills.ToArray();
        var pluginArray = plugins.ToArray();
        var pluginIds = skills
            .Select(x => x.Identity.PluginId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var toolNames = registry.Tools
            .Where(x =>
            {
                var pluginId = PluginIdFromProvider(
                    x.Provenance?.ProviderId);
                return pluginId is not null
                    && pluginIds.Contains(pluginId, StringComparer.Ordinal);
            })
            .Select(x => x.Name)
            .ToArray();

        var providerIds = pluginArray
            .Where(x => pluginIds.Contains(
                x.Manifest.Id,
                StringComparer.Ordinal))
            .SelectMany(x => x.Manifest.Providers)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return CaptureUsed(
            taskId,
            revision,
            registry,
            providers,
            pluginArray,
            new TaskCapabilitySelection(
                Model: null,
                UsedToolNames: toolNames,
                UsedProviderIds: providerIds,
                UsedPluginIds: pluginIds,
                SelectedSkills: skills,
                Policies: Array.Empty<TaskPolicyPin>()),
            reason);
    }

    public static TaskCapabilitySnapshot ReviseUsed(
        TaskCapabilitySnapshot previous,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        TaskCapabilitySelection selection,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return CaptureUsed(
            previous.TaskId,
            previous.Revision + 1,
            registry,
            providers,
            plugins,
            selection,
            reason);
    }

    /// <summary>
    /// Legacy compatibility helper. Ordinary runtime install flow no longer requires this method.
    /// If used, it scopes the revision to the explicitly installed plugin rather than every machine
    /// capability.
    /// </summary>
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
        ArgumentNullException.ThrowIfNull(selectedSkills);

        var pluginArray = plugins.ToArray();
        var selected = selectedSkills.ToArray();
        var pluginIds = selected
            .Select(x => x.Identity.PluginId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Append(installedPluginId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var toolNames = registry.Tools
            .Where(x =>
            {
                var pluginId = PluginIdFromProvider(
                    x.Provenance?.ProviderId);
                return pluginId is not null
                    && pluginIds.Contains(pluginId, StringComparer.Ordinal);
            })
            .Select(x => x.Name)
            .ToArray();

        var providerIds = pluginArray
            .Where(x => pluginIds.Contains(
                x.Manifest.Id,
                StringComparer.Ordinal))
            .SelectMany(x => x.Manifest.Providers)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return CaptureUsed(
            previous.TaskId,
            previous.Revision + 1,
            registry,
            providers,
            pluginArray,
            new TaskCapabilitySelection(
                previous.Model,
                toolNames,
                providerIds,
                pluginIds,
                selected,
                previous.Policies),
            "explicit capability installation: " + installedPluginId.Trim());
    }

    public static TaskCapabilityEvidence Evidence(
        TaskCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new TaskCapabilityEvidence(
            snapshot.TaskId,
            snapshot.Revision,
            snapshot.RegistryVersion,
            snapshot.Providers
                .Select(x =>
                    x.ProviderId + "@" + x.ProviderVersion)
                .ToArray(),
            snapshot.Plugins
                .Select(x =>
                    x.PluginId + "@" + x.PluginVersion)
                .ToArray(),
            snapshot.Tools
                .Select(x =>
                    x.ToolName
                    + "@tool=" + x.ToolVersion
                    + ";schema=" + x.SchemaVersion
                    + ";provider=" + x.ProviderId
                    + "@" + x.ProviderVersion)
                .ToArray(),
            snapshot.Skills
                .Select(x =>
                    x.Identity.SourceId
                    + ":" + x.Identity.SkillId
                    + "@sha256:" + x.Identity.Sha256)
                .ToArray())
        {
            ModelIdentity = snapshot.Model is null
                ? null
                : snapshot.Model.ProviderId
                    + "@" + snapshot.Model.ProviderVersion
                    + ";model=" + snapshot.Model.ModelId,
            PolicyVersions = snapshot.Policies
                .Select(x => x.PolicyId + "@" + x.Version)
                .ToArray()
        };
    }

    private static TaskToolPin ToolPin(ToolDescriptor descriptor)
        => new(
            descriptor.Name,
            descriptor.Provenance?.ToolVersion
                ?? descriptor.SchemaVersion,
            descriptor.SchemaVersion,
            descriptor.Provenance?.ProviderId
                ?? "built-in",
            descriptor.Provenance?.ProviderVersion
                ?? "built-in");

    private static string? PluginIdFromProvider(string? providerId)
        => providerId?.StartsWith(
                "plugin.",
                StringComparison.Ordinal) == true
            ? providerId["plugin.".Length..]
            : null;

    private static string SkillKey(SkillIdentity identity)
        => string.Join(
            "|",
            identity.SourceKind,
            identity.SourceId,
            identity.PluginId ?? "",
            identity.PluginVersion ?? "",
            identity.SkillId);

    private static string[] NormalizeNames(
        IEnumerable<string>? values,
        string parameterName)
    {
        if (values is null)
            return [];

        var result = values
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (result.Any(x =>
            x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException(
                "Capability identifiers must be <=256 characters and contain no control characters.",
                parameterName);
        return result;
    }

    private static TaskPolicyPin[] NormalizePolicies(
        IEnumerable<TaskPolicyPin>? policies)
        => (policies ?? Array.Empty<TaskPolicyPin>())
            .Select(x =>
            {
                ArgumentNullException.ThrowIfNull(x);
                var id = Required(
                    x.PolicyId,
                    nameof(TaskPolicyPin.PolicyId));
                var version = Required(
                    x.Version,
                    nameof(TaskPolicyPin.Version));
                return new TaskPolicyPin(id, version);
            })
            .Distinct()
            .OrderBy(x => x.PolicyId, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .ToArray();

    private static TaskModelPin? NormalizeModel(TaskModelPin? model)
        => model is null
            ? null
            : new TaskModelPin(
                Required(model.ProviderId, nameof(TaskModelPin.ProviderId)),
                Required(model.ProviderVersion, nameof(TaskModelPin.ProviderVersion)),
                Required(model.ModelId, nameof(TaskModelPin.ModelId)));

    private static string Required(
        string? value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException(
                "Capability identity value must be <=256 characters and contain no control characters.",
                parameterName);
        return normalized;
    }

    private static void ValidateTask(
        Guid taskId,
        long revision,
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        TaskCapabilitySelection selection,
        string reason)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException(
                "Task ID is empty.",
                nameof(taskId));
        if (revision < 1)
            throw new ArgumentOutOfRangeException(
                nameof(revision));
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}

public sealed class TaskCapabilityPinGuard
{
    private TaskCapabilitySnapshot _current;

    public TaskCapabilityPinGuard(
        TaskCapabilitySnapshot initial)
    {
        _current = initial
            ?? throw new ArgumentNullException(nameof(initial));
    }

    public TaskCapabilitySnapshot Current
        => _current;

    /// <summary>
    /// Compatibility guard for pinned tools/plugins. It deliberately ignores ToolRegistry.Version:
    /// unrelated registry additions/removals do not invalidate a task that never used them.
    /// </summary>
    public void EnsureStillPinned(
        ToolRegistry registry,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plugins);

        foreach (var tool in _current.Tools)
        {
            if (!registry.TryGet(tool.ToolName, out var descriptor)
                || ToolPin(descriptor) != tool)
                throw new InvalidOperationException(
                    $"Task-used tool '{tool.ToolName}' changed during the task.");
        }

        var activePlugins = plugins
            .GroupBy(
                x => x.Manifest.Id,
                StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.First().Manifest,
                StringComparer.Ordinal);

        foreach (var plugin in _current.Plugins)
        {
            if (!activePlugins.TryGetValue(
                    plugin.PluginId,
                    out var active)
                || !string.Equals(
                    active.Version,
                    plugin.PluginVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    active.Publisher,
                    plugin.Publisher,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Task-used plugin '{plugin.PluginId}' changed during the task.");
        }
    }

    public void EnsureStillPinned(
        ToolRegistry registry,
        IEnumerable<ProviderProvenance> providers,
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> plugins,
        IEnumerable<SkillSummary> skills,
        TaskModelPin? currentModel,
        IEnumerable<TaskPolicyPin>? currentPolicies = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(skills);

        EnsureStillPinned(registry, plugins);

        var currentProviders = providers
            .GroupBy(
                x => x.ProviderId,
                StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.First(),
                StringComparer.Ordinal);
        foreach (var provider in _current.Providers)
        {
            if (!currentProviders.TryGetValue(
                    provider.ProviderId,
                    out var current)
                || current.ProviderVersion != provider.ProviderVersion
                || current.ServerId != provider.ServerId
                || current.TransportKind != provider.TransportKind)
                throw new InvalidOperationException(
                    $"Task-used provider '{provider.ProviderId}' changed during the task.");
        }

        var currentSkills = skills.ToArray();
        foreach (var skill in _current.Skills)
        {
            var current = currentSkills.SingleOrDefault(x =>
                SkillKey(x.Identity) == SkillKey(skill.Identity));
            if (current is null
                || !string.Equals(
                    current.Identity.Sha256,
                    skill.Identity.Sha256,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Task-selected skill '{skill.Identity.SkillId}' changed during the task.");
        }

        if (_current.Model is not null)
        {
            var normalized = NormalizeModel(currentModel);
            if (normalized != _current.Model)
                throw new InvalidOperationException(
                    "Selected model/provider identity changed during the task.");
        }

        if (_current.Policies.Count > 0)
        {
            var policies = NormalizePolicies(currentPolicies)
                .ToHashSet();
            foreach (var policy in _current.Policies)
            {
                if (!policies.Contains(policy))
                    throw new InvalidOperationException(
                        $"Task policy '{policy.PolicyId}@{policy.Version}' changed during the task.");
            }
        }
    }

    public void ReplaceWithExplicitRevision(
        TaskCapabilitySnapshot revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.TaskId != _current.TaskId
            || revision.Revision != _current.Revision + 1)
            throw new InvalidOperationException(
                "Capability snapshot revision is not the next revision for this task.");
        _current = revision;
    }

    private static TaskToolPin ToolPin(
        ToolDescriptor descriptor)
        => new(
            descriptor.Name,
            descriptor.Provenance?.ToolVersion
                ?? descriptor.SchemaVersion,
            descriptor.SchemaVersion,
            descriptor.Provenance?.ProviderId
                ?? "built-in",
            descriptor.Provenance?.ProviderVersion
                ?? "built-in");

    private static string SkillKey(
        SkillIdentity identity)
        => string.Join(
            "|",
            identity.SourceKind,
            identity.SourceId,
            identity.PluginId ?? "",
            identity.PluginVersion ?? "",
            identity.SkillId);

    private static TaskModelPin? NormalizeModel(
        TaskModelPin? model)
        => model is null
            ? null
            : new TaskModelPin(
                model.ProviderId.Trim(),
                model.ProviderVersion.Trim(),
                model.ModelId.Trim());

    private static TaskPolicyPin[] NormalizePolicies(
        IEnumerable<TaskPolicyPin>? policies)
        => (policies ?? Array.Empty<TaskPolicyPin>())
            .Select(x => new TaskPolicyPin(
                x.PolicyId.Trim(),
                x.Version.Trim()))
            .Distinct()
            .ToArray();
}
