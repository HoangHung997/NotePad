using System.Security.Cryptography;
using System.Text;

namespace H2AgentLab.Tools;

public sealed record SkillReadResult(
    string SkillName,
    string ResourcePath,
    string Sha256,
    string Version,
    string? Content,
    bool Unchanged);

/// <summary>
/// Per-task progressive skill reader. SkillCatalog remains the v1 source of validation and content;
/// this wrapper remembers source stamps and content hashes so unchanged guidance is not resent to
/// the model on every tool turn.
/// </summary>
public sealed class DeferredSkillSession
{
    private readonly global::H2AgentLab.SkillCatalog _catalog;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public DeferredSkillSession(global::H2AgentLab.SkillCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public global::H2AgentLab.SkillCatalog Catalog => _catalog;

    public object Discover(string query)
        => _catalog.Discover(query ?? "");

    public SkillReadResult Read(string skillName, string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);

        var name = skillName.Trim();
        var relative = resourcePath.Trim().Replace('\\', '/');
        var key = name + "\n" + relative;
        var stamp = ResolveStamp(name, relative);

        if (_cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
        {
            return new SkillReadResult(
                name,
                relative,
                cached.Sha256,
                cached.Version,
                Content: null,
                Unchanged: true);
        }

        var content = _catalog.Read(name, relative);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var version = "sha256:" + hash[..16];
        _cache[key] = new CacheEntry(stamp, hash, version);

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

    private SourceStamp ResolveStamp(string skillName, string resourcePath)
    {
        string path;
        if (resourcePath == "references/runtime.md")
        {
            path = Path.Combine(AppContext.BaseDirectory, "runtime-guide.md");
        }
        else
        {
            var skill = _catalog.Skills.SingleOrDefault(
                x => string.Equals(x.Name, skillName, StringComparison.Ordinal))
                ?? throw new global::H2AgentLab.AgentFaultException(
                    "skill_not_found",
                    "Unknown skill. Use list_skills. Available names: "
                    + string.Join(", ", _catalog.Skills.Select(x => x.Name)));

            var scope = new global::H2AgentLab.SafeWorkspace(skill.Directory);
            path = scope.Resolve(resourcePath);
        }

        var info = new FileInfo(path);
        if (!info.Exists)
            return new SourceStamp(-1, -1);
        return new SourceStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private sealed record CacheEntry(SourceStamp Stamp, string Sha256, string Version);
    private readonly record struct SourceStamp(long Length, long LastWriteTicks);
}
