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
            var candidates = installed.Select((x, index) => new CapabilityResolutionCandidate(
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
                candidates,
                CatalogMetadataRefreshed: false,
                PackageDownloaded: false,
                InstallationAttempted: false);
        }

        var availableCandidates = _available.Search(query, 12);
        var refreshed = false;
        if (availableCandidates.Count == 0
            || !_catalogs.HasFreshMetadata(_catalogMaxAge, DateTime.UtcNow))
        {
            var merged = await _catalogs.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _available.Rebuild(merged.Entries);
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

        var candidates = new List<CapabilityResolutionCandidate>();
        foreach (var package in availableCandidates)
        {
            var compatible = Version.TryParse(package.MinAgentVersion, out var minimum)
                && minimum <= _agentVersion;
            var allowed = compatible && _policy.AllowsMetadataCandidate(package);
            var status = !compatible
                ? CapabilityResolutionStatus.INCOMPATIBLE
                : allowed
                    ? CapabilityResolutionStatus.AVAILABLE
                    : CapabilityResolutionStatus.BLOCKED_BY_POLICY;

            var matchingSkills = package.Skills
                .Select(skill => new
                {
                    Skill = skill,
                    Score = Score(query, skill.Name, skill.Description)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Skill.SkillId, StringComparer.Ordinal)
                .Take(5)
                .ToArray();

            if (matchingSkills.Length == 0)
            {
                candidates.Add(new CapabilityResolutionCandidate(
                    status,
                    package.PluginId,
                    string.Join(" ", package.ToolSummaries),
                    package.SourceId,
                    package.PluginId,
                    package.PluginVersion,
                    null,
                    Score(query, package.PluginId, string.Join(" ", package.ToolSummaries)),
                    package));
            }
            else
            {
                foreach (var match in matchingSkills)
                {
                    candidates.Add(new CapabilityResolutionCandidate(
                        status,
                        match.Skill.SkillId,
                        match.Skill.Description,
                        package.SourceId,
                        package.PluginId,
                        package.PluginVersion,
                        match.Skill.SkillId,
                        match.Score,
                        package));
                }
            }
        }

        var ordered = candidates
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.CapabilityId, StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        var overall = ordered.Any(x => x.Status == CapabilityResolutionStatus.AVAILABLE)
            ? CapabilityResolutionStatus.AVAILABLE
            : ordered.Any(x => x.Status == CapabilityResolutionStatus.BLOCKED_BY_POLICY)
                ? CapabilityResolutionStatus.BLOCKED_BY_POLICY
                : CapabilityResolutionStatus.INCOMPATIBLE;

        return new(
            overall,
            query,
            ordered,
            refreshed,
            PackageDownloaded: false,
            InstallationAttempted: false);
    }

    private static double Score(string query, string name, string description)
    {
        var q = CapabilityRanking.SemanticTerms(query).Distinct(StringComparer.Ordinal).ToArray();
        var n = CapabilityRanking.SemanticTerms(name).ToHashSet(StringComparer.Ordinal);
        var d = CapabilityRanking.SemanticTerms(description).ToHashSet(StringComparer.Ordinal);
        var score = 0d;
        foreach (var term in q)
        {
            if (d.Contains(term)) score += 4;
            if (n.Contains(term)) score += 2;
        }
        return score;
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
