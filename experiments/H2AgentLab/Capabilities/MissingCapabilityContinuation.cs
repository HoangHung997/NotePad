using H2AgentLab.Catalog;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public sealed record MissingCapabilityContinuationResult(
    string OriginalQuery,
    CapabilityResolution InitialResolution,
    PluginInstallResult? InstallResult,
    SkillContent SelectedSkill,
    IReadOnlyList<SkillResourceContent> LoadedResources,
    TaskCapabilitySnapshot CapabilitySnapshot,
    TaskCapabilityEvidence CapabilityEvidence,
    IReadOnlyList<AgentTraceEvent> TraceEvents,
    bool UserRestatementRequired);

public sealed class MissingCapabilityContinuation
{
    private readonly CapabilityResolver _resolver;
    private readonly IPackageRetriever _retriever;
    private readonly PluginManager _plugins;
    private readonly H2AgentLab.Skills.SkillCatalog _skills;
    private readonly InstalledCapabilityIndex _installed;
    private readonly ToolRegistry _registry;
    private readonly Func<IReadOnlyList<ProviderProvenance>> _providers;

    public MissingCapabilityContinuation(
        CapabilityResolver resolver,
        IPackageRetriever retriever,
        PluginManager plugins,
        H2AgentLab.Skills.SkillCatalog skills,
        InstalledCapabilityIndex installed,
        ToolRegistry registry,
        Func<IReadOnlyList<ProviderProvenance>> providers)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<MissingCapabilityContinuationResult> ResolveInstallAndContinueAsync(
        string originalQuery,
        TaskCapabilityPinGuard pinGuard,
        PluginInstallPolicy installPolicy,
        bool userApproved,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalQuery);
        ArgumentNullException.ThrowIfNull(pinGuard);
        ArgumentNullException.ThrowIfNull(installPolicy);

        var trace = new AgentTraceEventStream();
        var resolution = await _resolver.ResolveAsync(
            originalQuery,
            cancellationToken).ConfigureAwait(false);

        if (resolution.Status == CapabilityResolutionStatus.INSTALLED)
        {
            var selected = SelectInstalledSkill(originalQuery);
            var content = _skills.Read(selected.Identity);
            var resources = Array.Empty<SkillResourceContent>();
            trace.Add(
                AgentTraceEventKind.Phase,
                "capability-installed",
                "Required capability already installed; continuing original task.");
            return new MissingCapabilityContinuationResult(
                originalQuery,
                resolution,
                null,
                content,
                resources,
                pinGuard.Current,
                TaskCapabilitySnapshotBuilder.Evidence(pinGuard.Current),
                trace.Events,
                UserRestatementRequired: false);
        }

        var candidate = resolution.Candidates.FirstOrDefault(x =>
            x.Status == CapabilityResolutionStatus.AVAILABLE
            && x.AvailablePackage is not null
            && !string.IsNullOrWhiteSpace(x.SkillId))
            ?? throw new InvalidOperationException(
                resolution.Status switch
                {
                    CapabilityResolutionStatus.BLOCKED_BY_POLICY =>
                        "Required capability exists but host install policy blocks it.",
                    CapabilityResolutionStatus.INCOMPATIBLE =>
                        "Required capability exists but is incompatible with this Agent version.",
                    _ => "No installable skill capability can satisfy the original task."
                });

        var package = candidate.AvailablePackage!;
        trace.Add(
            AgentTraceEventKind.Phase,
            "capability-missing",
            $"Capability '{candidate.SkillId}' is not installed; task execution paused for host-managed resolution.");
        trace.Add(
            AgentTraceEventKind.Phase,
            "catalog-candidate",
            $"Selected catalog metadata {package.PluginId}@{package.PluginVersion} from {package.SourceId}; package bytes not yet executed.");

        var retrieval = await _retriever.RetrieveAsync(
            new PackageRetrievalRequest(
                package.PluginId,
                package.PluginVersion,
                package.PackageLocation,
                package.ArchiveSha256,
                MaxBytes: 64L * 1024 * 1024,
                Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);
        trace.Add(
            AgentTraceEventKind.Evidence,
            "package-retrieved",
            $"Retrieved immutable package sha256:{retrieval.Sha256} to private staging.");

        var catalogEntry = new PluginCatalogEntry(
            package.PluginId,
            package.PluginId,
            package.PluginVersion,
            package.Skills.FirstOrDefault()?.Description
                ?? string.Join(" ", package.ToolSummaries),
            package.Publisher,
            package.ToolSummaries,
            package.Skills.Select(x => x.Name).ToArray(),
            package.MinAgentVersion,
            package.TrustState,
            package.ArchiveSha256,
            retrieval.StagedPath);

        var installed = _plugins.InstallFromArchive(
            retrieval.StagedPath,
            catalogEntry,
            installPolicy,
            userApproved);
        trace.Add(
            AgentTraceEventKind.Phase,
            "capability-installed",
            $"Installed and atomically activated {installed.PluginId}@{installed.Version} through PluginManager.");

        _installed.Rebuild(
            _registry,
            _skills,
            _providers());

        var selectedSkill = SelectInstalledSkill(
            originalQuery,
            expectedPluginId: package.PluginId,
            expectedVersion: package.PluginVersion);
        var skillContent = _skills.Read(selectedSkill.Identity);
        trace.Add(
            AgentTraceEventKind.Evidence,
            "skill-loaded",
            $"Loaded selected SKILL.md {selectedSkill.Identity.SkillId}@sha256:{selectedSkill.Identity.Sha256}.");

        var loadedResources = Array.Empty<SkillResourceContent>();

        var revision = TaskCapabilitySnapshotBuilder.ReviseAfterExplicitInstall(
            pinGuard.Current,
            _registry,
            _providers(),
            _plugins.ActivePlugins(),
            [selectedSkill],
            installed.PluginId);
        pinGuard.ReplaceWithExplicitRevision(revision);
        trace.Add(
            AgentTraceEventKind.Phase,
            "capability-snapshot-revised",
            $"Task capability snapshot revised to {revision.Revision}; original task continues without restatement.");

        return new MissingCapabilityContinuationResult(
            originalQuery,
            resolution,
            installed,
            skillContent,
            loadedResources,
            revision,
            TaskCapabilitySnapshotBuilder.Evidence(revision),
            trace.Events,
            UserRestatementRequired: false);
    }

    private SkillSummary SelectInstalledSkill(
        string query,
        string? expectedPluginId = null,
        string? expectedVersion = null)
    {
        var candidates = _skills.Search(query, 20)
            .Where(x => expectedPluginId is null
                || (x.Identity.PluginId == expectedPluginId
                    && x.Identity.PluginVersion == expectedVersion))
            .ToArray();
        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Installed capability index refreshed, but the selected skill is not discoverable from the original task description.");
    }


}
