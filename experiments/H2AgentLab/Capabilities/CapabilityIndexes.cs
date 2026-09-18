using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public enum CapabilityKind
{
    Tool = 0,
    Skill = 1,
    Provider = 2
}

public sealed record InstalledCapabilityRecord(
    CapabilityKind Kind,
    string CapabilityId,
    string Description,
    string SourceId,
    string? PluginId,
    string? PluginVersion,
    string? ProviderId,
    string? ToolVersion,
    string? SchemaVersion,
    string? Sha256,
    string Availability);

public sealed record AvailableSkillMetadata(
    string SkillId,
    string Name,
    string Description);

public sealed record AvailableCapabilityRecord(
    string PluginId,
    string PluginVersion,
    string Publisher,
    PluginTrustState TrustState,
    string MinAgentVersion,
    string ArchiveSha256,
    string PackageLocation,
    IReadOnlyList<string> ToolSummaries,
    IReadOnlyList<AvailableSkillMetadata> Skills,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Permissions,
    string SourceId,
    int SourcePriority,
    DateTime MetadataFetchedUtc,
    bool MetadataStale);

public sealed class InstalledCapabilityIndex
{
    private InstalledCapabilityRecord[] _records = [];

    public IReadOnlyList<InstalledCapabilityRecord> Records => _records;

    public void Rebuild(
        ToolRegistry registry,
        SkillCatalog skills,
        IEnumerable<ProviderProvenance>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(skills);

        var records = new List<InstalledCapabilityRecord>();
        foreach (var tool in registry.Tools)
        {
            records.Add(new InstalledCapabilityRecord(
                CapabilityKind.Tool,
                tool.Name,
                tool.Description,
                tool.Provenance?.ProviderId ?? "built-in",
                tool.Provenance?.ProviderId?.StartsWith("plugin.", StringComparison.Ordinal) == true
                    ? tool.Provenance.ProviderId["plugin.".Length..]
                    : null,
                tool.Provenance?.ProviderVersion,
                tool.Provenance?.ProviderId,
                tool.Provenance?.ToolVersion,
                tool.SchemaVersion,
                null,
                "installed"));
        }

        foreach (var skill in skills.Search("", 100))
        {
            records.Add(new InstalledCapabilityRecord(
                CapabilityKind.Skill,
                skill.Identity.SkillId,
                skill.Description,
                skill.Identity.SourceId,
                skill.Identity.PluginId,
                skill.Identity.PluginVersion,
                null,
                null,
                null,
                skill.Identity.Sha256,
                skill.Availability));
        }

        foreach (var provider in providers ?? Array.Empty<ProviderProvenance>())
        {
            records.Add(new InstalledCapabilityRecord(
                CapabilityKind.Provider,
                provider.ProviderId,
                $"Provider {provider.ProviderId} via {provider.TransportKind}.",
                provider.ProviderId,
                null,
                provider.ProviderVersion,
                provider.ProviderId,
                null,
                null,
                null,
                "installed"));
        }

        _records = records
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.CapabilityId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<InstalledCapabilityRecord> Search(
        string query,
        int maxResults = 20)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        return CapabilityRanking.RankInstalled(_records, query, maxResults);
    }
}

public sealed class AvailableCapabilityIndex
{
    private AvailableCapabilityRecord[] _records = [];

    public IReadOnlyList<AvailableCapabilityRecord> Records => _records;

    public void Rebuild(IEnumerable<AvailableCapabilityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records
            .Select(ValidateCompact)
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ThenByDescending(x => Version.Parse(x.PluginVersion))
            .ThenByDescending(x => x.SourcePriority)
            .ToArray();
    }

    public IReadOnlyList<AvailableCapabilityRecord> Search(
        string query,
        int maxResults = 20)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        return CapabilityRanking.RankAvailable(_records, query, maxResults);
    }

    private static AvailableCapabilityRecord ValidateCompact(AvailableCapabilityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ToolSummaries.Any(x => x.Length > 1_000)
            || record.Skills.Any(x => x.Description.Length > 1_500)
            || record.Skills.Any(x => x.Description.Contains("# ", StringComparison.Ordinal)
                && x.Description.Length > 500))
            throw new InvalidDataException("Available capability index contains oversized/full content instead of compact metadata.");
        if (record.PackageLocation.Length > 2_048)
            throw new InvalidDataException("Available package location is too long.");
        return record;
    }
}

internal static class CapabilityRanking
{
    public static IReadOnlyList<InstalledCapabilityRecord> RankInstalled(
        IEnumerable<InstalledCapabilityRecord> records,
        string query,
        int maxResults)
        => records
            .Select(x => new { Record = x, Score = Score(query, x.CapabilityId, x.Description) })
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Score >= MinimumScore(query))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Record.CapabilityId, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Record)
            .ToArray();

    public static IReadOnlyList<AvailableCapabilityRecord> RankAvailable(
        IEnumerable<AvailableCapabilityRecord> records,
        string query,
        int maxResults)
        => records
            .Select(record => new
            {
                Record = record,
                Score = AvailableScore(query, record)
            })
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Score >= MinimumScore(query))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Record.SourcePriority)
            .ThenByDescending(x => Version.Parse(x.Record.PluginVersion))
            .ThenBy(x => x.Record.PluginId, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Record)
            .ToArray();

    private static double MinimumScore(string query)
    {
        var concepts = H2AgentLab.Skills.BuiltInSkillSource.Tokens(query)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return concepts >= 4 ? 6d : 1d;
    }

    private static double AvailableScore(string query, AvailableCapabilityRecord record)
    {
        var score = Score(query, record.PluginId, string.Join(" ", record.ToolSummaries));
        foreach (var skill in record.Skills)
            score += Score(query, skill.Name, skill.Description) * 2;
        foreach (var provider in record.Providers)
            score += Score(query, provider, provider);
        return score;
    }

    private static double Score(string query, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        var queryTerms = SemanticTerms(query).Distinct(StringComparer.Ordinal).ToArray();
        var nameTerms = SemanticTerms(name).ToHashSet(StringComparer.Ordinal);
        var descriptionTerms = SemanticTerms(description).ToHashSet(StringComparer.Ordinal);
        var score = 0d;
        foreach (var term in queryTerms)
        {
            if (descriptionTerms.Contains(term)) score += 3;
            if (nameTerms.Contains(term)) score += 2;
        }
        return score;
    }

    internal static IEnumerable<string> SemanticTerms(string value)
    {
        foreach (var term in H2AgentLab.Skills.BuiltInSkillSource.Tokens(value))
        {
            yield return term;
            foreach (var synonym in Synonyms(term))
                yield return synonym;
        }
    }

    private static IEnumerable<string> Synonyms(string term)
        => term switch
        {
            "audit" => ["inspect", "check", "review"],
            "inspect" => ["audit", "check", "review"],
            "check" => ["audit", "inspect", "review"],
            "dynamic" => ["parameter", "action", "visibility"],
            "parameters" => ["parameter", "dynamic"],
            "actions" => ["action", "dynamic"],
            "blocks" => ["block", "autocad", "cad"],
            "autocad" => ["cad", "block"],
            "legal" => ["law", "regulation"],
            _ => Array.Empty<string>()
        };
}
