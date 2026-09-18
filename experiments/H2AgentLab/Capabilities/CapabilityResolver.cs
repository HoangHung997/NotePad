using H2AgentLab.Catalog;

namespace H2AgentLab.Capabilities;

public enum CapabilityResolutionStatus
{
    INSTALLED = 0,
    AVAILABLE = 1,
    UPDATE_AVAILABLE = 2,
    BLOCKED_BY_POLICY = 3,
    INCOMPATIBLE = 4,
    UNSUPPORTED = 5
}

public sealed record CapabilityResolutionCandidate(
    CapabilityResolutionStatus Status,
    string CapabilityId,
    string Description,
    string Source,
    string? PluginId,
    string? PluginVersion,
    string? SkillId,
    double Score,
    AvailableCapabilityRecord? AvailablePackage = null);

public sealed record CapabilityResolution(
    CapabilityResolutionStatus Status,
    string Query,
    IReadOnlyList<CapabilityResolutionCandidate> Candidates,
    bool CatalogMetadataRefreshed,
    bool PackageDownloaded,
    bool InstallationAttempted);

public interface ICapabilityInstallPolicy
{
    bool AllowsMetadataCandidate(AvailableCapabilityRecord candidate);
}

public sealed class CapabilityResolver
{
    private readonly InstalledCapabilityIndex _installed;
    private readonly AvailableCapabilityIndex _available;
    private readonly CatalogSourceManager _catalogs;
    private readonly ICapabilityInstallPolicy _policy;
    private readonly Version _agentVersion;
    private readonly TimeSpan _catalogMaxAge;

    public CapabilityResolver(
        InstalledCapabilityIndex installed,
        AvailableCapabilityIndex available,
        CatalogSourceManager catalogs,
        ICapabilityInstallPolicy policy,
        string agentVersion = "2.0.0",
        TimeSpan? catalogMaxAge = null)
    {
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _available = available ?? throw new ArgumentNullException(nameof(available));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _available.Bind(() => _catalogs.CachedView().Entries);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _agentVersion = Version.Parse(agentVersion);
        _catalogMaxAge = catalogMaxAge ?? TimeSpan.FromHours(24);
    }

    public async Task<CapabilityResolution> ResolveAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var installed = _installed.Search(query, 12);
        if (installed.Count > 0)
        {
            var installedCandidates = installed.Select((x, index) => new CapabilityResolutionCandidate(
                CapabilityResolutionStatus.INSTALLED,
                x.CapabilityId,
                x.Description,
                x.SourceId,
                x.PluginId,
                x.PluginVersion,
                x.Kind == CapabilityKind.Skill ? x.CapabilityId : null,
                Score: 100 - index)).ToArray();
            return new(
                CapabilityResolutionStatus.INSTALLED,
                query,
                installedCandidates,
                CatalogMetadataRefreshed: false,
                PackageDownloaded: false,
                InstallationAttempted: false);
        }

        return await SearchCatalogAsync(
            query,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CapabilityResolution> SearchCatalogAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var availableCandidates = _available.Search(query, 12);
        var refreshed = false;
        if (availableCandidates.Count == 0
            || !_catalogs.HasFreshMetadata(_catalogMaxAge, DateTime.UtcNow))
        {
            _ = await _catalogs.RefreshAsync(cancellationToken).ConfigureAwait(false);
            availableCandidates = _available.Search(query, 12);
            refreshed = true;
        }

        if (availableCandidates.Count == 0)
            return new(
                CapabilityResolutionStatus.UNSUPPORTED,
                query,
                [],
                refreshed,
                PackageDownloaded: false,
                InstallationAttempted: false);

        var candidates = availableCandidates
            .Select((package, index) =>
            {
                var compatible = Version.TryParse(
                        package.MinAgentVersion,
                        out var minimum)
                    && minimum <= _agentVersion;
                var allowed = compatible
                    && _policy.AllowsMetadataCandidate(package);
                var status = !compatible
                    ? CapabilityResolutionStatus.INCOMPATIBLE
                    : allowed
                        ? CapabilityResolutionStatus.AVAILABLE
                        : CapabilityResolutionStatus.BLOCKED_BY_POLICY;

                return new CapabilityResolutionCandidate(
                    status,
                    package.PluginId,
                    CompactDescription(package),
                    package.SourceId,
                    package.PluginId,
                    package.PluginVersion,
                    SkillId: null,
                    Score: 100 - index,
                    package);
            })
            .ToArray();

        var overall = candidates.Any(x =>
                x.Status == CapabilityResolutionStatus.AVAILABLE)
            ? CapabilityResolutionStatus.AVAILABLE
            : candidates.Any(x =>
                x.Status == CapabilityResolutionStatus.BLOCKED_BY_POLICY)
                ? CapabilityResolutionStatus.BLOCKED_BY_POLICY
                : CapabilityResolutionStatus.INCOMPATIBLE;

        return new(
            overall,
            query,
            candidates,
            refreshed,
            PackageDownloaded: false,
            InstallationAttempted: false);
    }

    private static string CompactDescription(
        AvailableCapabilityRecord package)
    {
        var parts = package.ToolSummaries
            .Concat(package.Skills.Select(x => x.Description))
            .Concat(package.Providers)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(12)
            .ToArray();
        return parts.Length == 0
            ? $"Extension package {package.PluginId}."
            : string.Join(" ", parts);
    }
}

public sealed class PredicateCapabilityInstallPolicy : ICapabilityInstallPolicy
{
    private readonly Func<AvailableCapabilityRecord, bool> _predicate;

    public PredicateCapabilityInstallPolicy(Func<AvailableCapabilityRecord, bool> predicate)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    }

    public bool AllowsMetadataCandidate(AvailableCapabilityRecord candidate)
        => _predicate(candidate);
}
