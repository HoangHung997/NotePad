namespace H2AgentLab.Plugins;

internal sealed record PluginSkillSummary(
    string PluginId,
    string PluginVersion,
    string SkillId,
    string Description,
    string Sha256);

internal sealed record PluginSkillContent(
    PluginSkillSummary Summary,
    string Content,
    int LoadCount);

internal sealed class PluginSkillCatalog
{
    private readonly PluginManager _plugins;
    private readonly Dictionary<string, (string Content, int LoadCount)> _cache = new(StringComparer.Ordinal);

    public PluginSkillCatalog(PluginManager plugins)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }

    public IReadOnlyList<PluginSkillSummary> Discover(string query)
    {
        query ??= "";
        var result = new List<PluginSkillSummary>();

        foreach (var (manifest, root) in _plugins.ActivePlugins())
        {
            foreach (var skillId in manifest.Skills)
            {
                var path = Path.Combine(root, "skills", skillId, "SKILL.md");
                if (!File.Exists(path))
                    continue;

                var metadata = ReadDiscoveryMetadata(path, skillId);
                if (query.Length > 0
                    && !(skillId + " " + metadata.Description + " " + manifest.Id)
                        .Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new PluginSkillSummary(
                    manifest.Id,
                    manifest.Version,
                    skillId,
                    metadata.Description,
                    metadata.Sha256));
            }
        }

        return result
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.SkillId, StringComparer.Ordinal)
            .ToArray();
    }

    public PluginSkillContent Read(
        string pluginId,
        string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var active = _plugins.GetActive(pluginId)
            ?? throw new KeyNotFoundException($"Plugin '{pluginId}' is not active.");
        var manifest = active.Manifest;
        if (!manifest.Skills.Contains(skillId, StringComparer.Ordinal))
            throw new KeyNotFoundException(
                $"Skill '{skillId}' is not declared by active plugin '{pluginId}'.");

        var path = Path.Combine(
            active.VersionRoot,
            "skills",
            skillId,
            "SKILL.md");
        if (!File.Exists(path))
            throw new FileNotFoundException("Plugin skill file is missing.", path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 80_000)
            throw new IOException($"Plugin skill '{skillId}' exceeds 80 KB.");

        var sha = global::H2AgentLab.SafeWorkspace.Hash(bytes).ToLowerInvariant();
        var summary = new PluginSkillSummary(
            manifest.Id,
            manifest.Version,
            skillId,
            ExtractDescription(System.Text.Encoding.UTF8.GetString(bytes)),
            sha);
        var key = string.Join(
            "|",
            summary.PluginId,
            summary.PluginVersion,
            summary.SkillId,
            summary.Sha256);

        if (_cache.TryGetValue(key, out var cached))
            return new PluginSkillContent(summary, cached.Content, cached.LoadCount);

        var content = System.Text.Encoding.UTF8.GetString(bytes);
        var loadCount = 1;
        _cache[key] = (content, loadCount);
        return new PluginSkillContent(summary, content, loadCount);
    }

    public void InvalidatePlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var prefix = pluginId + "|";
        foreach (var key in _cache.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _cache.Remove(key);
    }

    private static (string Description, string Sha256) ReadDiscoveryMetadata(
        string path,
        string skillId)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Plugin skill file is missing.", path);
        if (info.Length > 80_000)
            throw new IOException($"Plugin skill '{skillId}' exceeds 80 KB.");

        string header;
        using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.SequentialScan))
        using (var reader = new StreamReader(
            stream,
            new System.Text.UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4 * 1024,
            leaveOpen: false))
        {
            var builder = new System.Text.StringBuilder();
            var lineCount = 0;
            while (!reader.EndOfStream && builder.Length <= 16_000 && lineCount++ < 200)
            {
                var line = reader.ReadLine() ?? "";
                builder.Append(line).Append((char)10);
                if (lineCount > 1 && line.Trim() == "---")
                    break;
            }
            header = builder.ToString();
        }

        var description = ExtractDescription(header);
        using var hashStream = File.OpenRead(path);
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(hashStream)).ToLowerInvariant();
        return (description, hash);
    }

    private static string ExtractDescription(string content)
    {
        if (content.StartsWith("---", StringComparison.Ordinal))
        {
            var marker = ((char)10).ToString() + "---";
            var end = content.IndexOf(marker, 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var header = content[..end];
                var line = header.Split((char)10)
                    .Select(x => x.Trim())
                    .FirstOrDefault(x => x.StartsWith("description:", StringComparison.OrdinalIgnoreCase));
                if (line is not null)
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim().Trim((char)34, (char)39);
                    if (value.Length > 0)
                        return value.Length <= 1_000 ? value : value[..1_000];
                }
            }
        }

        var first = content.Split((char)10)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Length > 0 && !x.StartsWith("#", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(first)
            ? "Plugin skill"
            : first.Length <= 1_000 ? first : first[..1_000];
    }
}
