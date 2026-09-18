using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using H2AgentLab.Plugins;

namespace H2AgentLab.Skills;

public enum SkillSourceKind
{
    BuiltIn = 0,
    Plugin = 1,
    RemoteMetadata = 2
}

public sealed record SkillIdentity(
    SkillSourceKind SourceKind,
    string SourceId,
    string? PluginId,
    string? PluginVersion,
    string SkillId,
    string Sha256);

public sealed record SkillSummary(
    SkillIdentity Identity,
    string Name,
    string Description,
    string Availability,
    string Trust);

public sealed record SkillContent(
    SkillSummary Summary,
    string EntryPoint,
    IReadOnlyList<string> AvailableResources);

public sealed record SkillResourceContent(
    SkillIdentity Identity,
    string RelativePath,
    string Sha256,
    string Content);

public interface ISkillSource
{
    string SourceId { get; }
    SkillSourceKind SourceKind { get; }

    IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20);
    SkillContent Read(SkillIdentity identity);
    SkillResourceContent ReadResource(SkillIdentity identity, string relativePath);
}

public sealed class BuiltInSkillSource : ISkillSource
{
    private readonly global::H2AgentLab.LabSkill[] _skills;

    public BuiltInSkillSource(global::H2AgentLab.SkillCatalog catalog, string sourceId = "built-in")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _skills = catalog.Skills.ToArray();
        SourceId = NormalizeId(sourceId);
    }

    public BuiltInSkillSource(string root, string sourceId = "built-in")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _skills = ScanMetadataOnly(Path.GetFullPath(root));
        SourceId = NormalizeId(sourceId);
    }

    public string SourceId { get; }
    public SkillSourceKind SourceKind => SkillSourceKind.BuiltIn;

    public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
    {
        ValidateMax(maxResults);
        return Rank(
            _skills.Select(skill =>
            {
                var entry = Path.Combine(skill.Directory, "SKILL.md");
                var hash = File.Exists(entry) ? HashFile(entry) : "";
                return new SkillSummary(
                    new SkillIdentity(
                        SourceKind,
                        SourceId,
                        null,
                        null,
                        skill.Name,
                        hash),
                    skill.Name,
                    skill.Description,
                    "installed",
                    "built-in");
            }),
            query,
            maxResults);
    }

    public SkillContent Read(SkillIdentity identity)
    {
        ValidateIdentity(identity);
        var skill = _skills.SingleOrDefault(x => x.Name == identity.SkillId)
            ?? throw new KeyNotFoundException($"Built-in skill '{identity.SkillId}' is unavailable.");
        var entry = Path.Combine(skill.Directory, "SKILL.md");
        var content = ReadBounded(entry, 80_000);
        var current = HashText(content);
        if (!string.Equals(current, identity.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Built-in skill changed after selection; resolve capability again.");
        var resources = ListResources(skill.Directory);
        return new SkillContent(
            new SkillSummary(identity, skill.Name, skill.Description, "installed", "built-in"),
            content,
            resources);
    }

    public SkillResourceContent ReadResource(SkillIdentity identity, string relativePath)
    {
        ValidateIdentity(identity);
        var skill = _skills.SingleOrDefault(x => x.Name == identity.SkillId)
            ?? throw new KeyNotFoundException($"Built-in skill '{identity.SkillId}' is unavailable.");
        var relative = ValidateProgressiveResource(relativePath);
        var scope = new global::H2AgentLab.SafeWorkspace(skill.Directory);
        var path = scope.Resolve(relative);
        var content = ReadBounded(path, 80_000);
        return new SkillResourceContent(identity, relative, HashText(content), content);
    }

    private static global::H2AgentLab.LabSkill[] ScanMetadataOnly(string root)
    {
        if (!Directory.Exists(root))
            return [];

        var skills = new List<global::H2AgentLab.LabSkill>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
        {
            var path = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(path))
                continue;
            var info = new FileInfo(path);
            if (info.Length > 80_000)
                throw new IOException("Built-in skill exceeds 80 KB: " + directory);

            var header = ReadFrontmatter(path);
            string Field(string name)
            {
                var match = Regex.Match(
                    header,
                    "(?m)^" + Regex.Escape(name) + @":\s*(.+)$",
                    RegexOptions.CultureInvariant);
                return match.Success
                    ? match.Groups[1].Value.Trim().Trim((char)34, (char)39)
                    : "";
            }

            var name = Field("name");
            var description = Field("description");
            if (!Regex.IsMatch(name, "^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)
                || description.Length is < 5 or > 1_500
                || skills.Any(x => x.Name == name))
                throw new IOException("Invalid or duplicate built-in skill metadata: " + directory);

            skills.Add(new global::H2AgentLab.LabSkill(name, description, directory));
        }

        return skills.ToArray();
    }

    private static string ReadFrontmatter(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4 * 1024,
            leaveOpen: false);

        var builder = new StringBuilder();
        var lines = 0;
        while (!reader.EndOfStream && builder.Length <= 16_000 && lines++ < 200)
        {
            var line = reader.ReadLine() ?? "";
            builder.Append(line).Append((char)10);
            if (lines > 1 && line.Trim() == "---")
                break;
        }
        var text = builder.ToString();
        if (!text.StartsWith("---", StringComparison.Ordinal)
            || text.IndexOf(((char)10).ToString() + "---", 3, StringComparison.Ordinal) < 0)
            throw new IOException("Built-in skill frontmatter is invalid: " + path);
        return text;
    }

    private void ValidateIdentity(SkillIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.SourceKind != SourceKind || identity.SourceId != SourceId)
            throw new InvalidOperationException("Skill identity belongs to a different source.");
    }

    private static string[] ListResources(string directory)
    {
        var scope = new global::H2AgentLab.SafeWorkspace(directory);
        return scope.Files()
            .Select(x => x.Replace((char)92, '/'))
            .Where(IsProgressiveResource)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(200)
            .ToArray();
    }

    internal static IReadOnlyList<SkillSummary> Rank(
        IEnumerable<SkillSummary> skills,
        string query,
        int maxResults)
    {
        query ??= "";
        var terms = Tokens(query).Distinct(StringComparer.Ordinal).ToArray();
        var scored = skills.Select(skill => new
        {
            Skill = skill,
            Score = Score(skill, terms, query)
        });

        if (terms.Length > 0)
            scored = scored.Where(x => x.Score > 0);

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Skill)
            .ToArray();
    }

    private static double Score(
        SkillSummary skill,
        IReadOnlyList<string> terms,
        string rawQuery)
    {
        if (terms.Count == 0) return 1;
        var nameTerms = Tokens(skill.Name).ToHashSet(StringComparer.Ordinal);
        var descriptionTerms = Tokens(skill.Description).ToHashSet(StringComparer.Ordinal);
        var score = 0d;
        foreach (var term in terms)
        {
            if (descriptionTerms.Contains(term)) score += 3;
            if (nameTerms.Contains(term)) score += 2;
            if (skill.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        }

        if (skill.Description.Contains(rawQuery, StringComparison.OrdinalIgnoreCase))
            score += 8;
        return score;
    }

    internal static IEnumerable<string> Tokens(string value)
    {
        foreach (Match match in Regex.Matches(
            (value ?? "").ToLowerInvariant().Replace('_', ' ').Replace('-', ' '),
            @"[p{L}p{N}]+",
            RegexOptions.CultureInvariant))
        {
            if (match.Value.Length > 1)
                yield return match.Value;
        }
    }

    internal static string HashFile(string path)
        => global::H2AgentLab.SafeWorkspace.Hash(File.ReadAllBytes(path)).ToLowerInvariant();

    internal static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string ReadBounded(string path, int maxBytes)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Skill resource is missing.", path);
        var info = new FileInfo(path);
        if (info.Length > maxBytes) throw new IOException("Skill resource exceeds safety limit.");
        return File.ReadAllText(path);
    }

    internal static string ValidateProgressiveResource(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var relative = relativePath.Trim().Replace((char)92, '/');
        if (!IsProgressiveResource(relative))
            throw new UnauthorizedAccessException(
                "Only selected skill references/scripts/assets/agents resources may be loaded progressively.");
        return relative;
    }

    private static bool IsProgressiveResource(string relative)
        => relative.StartsWith("references/", StringComparison.Ordinal)
            || relative.StartsWith("scripts/", StringComparison.Ordinal)
            || relative.StartsWith("assets/", StringComparison.Ordinal)
            || relative.StartsWith("agents/", StringComparison.Ordinal);

    private static string NormalizeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Skill source ID is invalid.", nameof(value));
        return normalized;
    }

    private static void ValidateMax(int maxResults)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
    }
}

public sealed class PluginSkillSource : ISkillSource
{
    private readonly PluginManager _plugins;
    private readonly PluginSkillCatalog _catalog;

    public PluginSkillSource(
        PluginManager plugins,
        PluginSkillCatalog catalog,
        string sourceId = "plugins")
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        SourceId = sourceId.Trim().ToLowerInvariant();
    }

    public string SourceId { get; }
    public SkillSourceKind SourceKind => SkillSourceKind.Plugin;

    public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        // Local installed discovery is allowed to inspect bounded frontmatter metadata. Full
        // SKILL.md content is returned only by Read after selection.
        var summaries = _catalog.Discover("")
            .Select(x => new SkillSummary(
                new SkillIdentity(
                    SourceKind,
                    SourceId,
                    x.PluginId,
                    x.PluginVersion,
                    x.SkillId,
                    x.Sha256),
                x.SkillId,
                x.Description,
                "installed",
                "plugin"));
        return BuiltInSkillSource.Rank(summaries, query, maxResults);
    }

    public SkillContent Read(SkillIdentity identity)
    {
        ValidateIdentity(identity);
        var content = _catalog.Read(identity.PluginId!, identity.SkillId);
        if (content.Summary.PluginVersion != identity.PluginVersion
            || !string.Equals(content.Summary.Sha256, identity.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Plugin skill changed after selection; resolve capability again.");

        var active = _plugins.GetActive(identity.PluginId!)
            ?? throw new KeyNotFoundException("Plugin is no longer active.");
        var skillRoot = Path.Combine(active.VersionRoot, "skills", identity.SkillId);
        return new SkillContent(
            new SkillSummary(
                identity,
                identity.SkillId,
                content.Summary.Description,
                "installed",
                "plugin"),
            content.Content,
            ListResources(skillRoot));
    }

    public SkillResourceContent ReadResource(SkillIdentity identity, string relativePath)
    {
        ValidateIdentity(identity);
        var active = _plugins.GetActive(identity.PluginId!)
            ?? throw new KeyNotFoundException("Plugin is no longer active.");
        if (active.Manifest.Version != identity.PluginVersion)
            throw new InvalidOperationException("Plugin version changed after skill selection.");

        var relative = BuiltInSkillSource.ValidateProgressiveResource(relativePath);
        var skillRoot = Path.Combine(active.VersionRoot, "skills", identity.SkillId);
        var scope = new global::H2AgentLab.SafeWorkspace(skillRoot);
        var path = scope.Resolve(relative);
        var content = BuiltInSkillSource.ReadBounded(path, 80_000);
        return new SkillResourceContent(
            identity,
            relative,
            BuiltInSkillSource.HashText(content),
            content);
    }

    private void ValidateIdentity(SkillIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.SourceKind != SourceKind
            || identity.SourceId != SourceId
            || string.IsNullOrWhiteSpace(identity.PluginId)
            || string.IsNullOrWhiteSpace(identity.PluginVersion))
            throw new InvalidOperationException("Skill identity belongs to a different plugin source.");
    }

    private static string[] ListResources(string skillRoot)
    {
        if (!Directory.Exists(skillRoot)) return [];
        var scope = new global::H2AgentLab.SafeWorkspace(skillRoot);
        return scope.Files()
            .Select(x => x.Replace((char)92, '/'))
            .Where(x => x.StartsWith("references/", StringComparison.Ordinal)
                || x.StartsWith("scripts/", StringComparison.Ordinal)
                || x.StartsWith("assets/", StringComparison.Ordinal)
                || x.StartsWith("agents/", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(200)
            .ToArray();
    }
}

public sealed class UnifiedSkillCatalog
{
    private readonly List<ISkillSource> _sources = [];

    public void Register(ISkillSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_sources.Any(x => x.SourceId == source.SourceId))
            throw new InvalidOperationException($"Skill source '{source.SourceId}' is already registered.");
        _sources.Add(source);
    }

    public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        var candidates = _sources
            .SelectMany(x => x.Search(query, maxResults))
            .ToArray();
        return BuiltInSkillSource.Rank(candidates, query, maxResults);
    }

    public SkillContent Read(SkillIdentity identity)
        => Source(identity).Read(identity);

    public SkillResourceContent ReadResource(
        SkillIdentity identity,
        string relativePath)
        => Source(identity).ReadResource(identity, relativePath);

    public IReadOnlyList<string> SourceIds
        => _sources.Select(x => x.SourceId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private ISkillSource Source(SkillIdentity identity)
        => _sources.SingleOrDefault(x =>
                x.SourceId == identity.SourceId
                && x.SourceKind == identity.SourceKind)
            ?? throw new KeyNotFoundException(
                $"Skill source '{identity.SourceId}' is not registered.");
}
