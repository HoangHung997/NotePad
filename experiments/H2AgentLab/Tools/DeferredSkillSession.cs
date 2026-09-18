using System.Security.Cryptography;
using System.Text;
using H2AgentLab.Skills;

namespace H2AgentLab.Tools;

public sealed record SkillReadResult(
    string SkillName,
    string ResourcePath,
    string Sha256,
    string Version,
    string? Content,
    bool Unchanged);

/// <summary>
/// Per-task compatibility cache over the canonical Skills.SkillCatalog. It no longer owns skill
/// discovery/parsing; it only suppresses resending unchanged selected content.
/// </summary>
public sealed class DeferredSkillSession
{
    private readonly H2AgentLab.Skills.SkillCatalog _catalog;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public DeferredSkillSession(global::H2AgentLab.SkillCatalog catalog)
        : this((catalog ?? throw new ArgumentNullException(nameof(catalog))).Canonical)
    {
    }

    public DeferredSkillSession(H2AgentLab.Skills.SkillCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public H2AgentLab.Skills.SkillCatalog Catalog => _catalog;

    public IReadOnlyList<SkillSummary> Discover(string query)
        => _catalog.Search(query ?? "", 100);

    public SkillReadResult Read(string skillName, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);

        var name = skillName.Trim();
        var relative = resourcePath.Trim().Replace('\\', '/');
        var selected = _catalog.Search("", 100)
            .SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))
            ?? throw new global::H2AgentLab.AgentFaultException(
                "skill_not_found",
                "Unknown skill. Use list_skills. Available names: "
                + string.Join(", ", _catalog.Search("", 100).Select(x => x.Name)));

        var content = relative == "SKILL.md"
            ? _catalog.Read(selected.Identity).EntryPoint
            : _catalog.ReadResource(selected.Identity, relative).Content;
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var version = "sha256:" + hash[..16];
        var key = selected.Identity.SourceId
            + "\n"
            + selected.Identity.SkillId
            + "\n"
            + relative;

        if (_cache.TryGetValue(key, out var cached)
            && string.Equals(cached.Sha256, hash, StringComparison.Ordinal))
        {
            return new SkillReadResult(
                name,
                relative,
                cached.Sha256,
                cached.Version,
                Content: null,
                Unchanged: true);
        }

        _cache[key] = new CacheEntry(hash, version);
        return new SkillReadResult(
            name,
            relative,
            hash,
            version,
            content,
            Unchanged: false);
    }

    public IReadOnlyDictionary<string, string> LoadedVersions
        => _cache.ToDictionary(
            x => x.Key.Replace('\n', ':'),
            x => x.Value.Version,
            StringComparer.Ordinal);

    private sealed record CacheEntry(string Sha256, string Version);
}
