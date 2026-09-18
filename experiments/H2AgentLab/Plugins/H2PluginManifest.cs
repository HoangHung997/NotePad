using System.Text.Json;
using System.Text.RegularExpressions;

namespace H2AgentLab.Plugins;

public sealed record H2PluginManifest(
    string Id,
    string Name,
    string Version,
    string MinAgentVersion,
    string Publisher,
    string PackageHash,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string>? NativeHelpers = null,
    IReadOnlyList<string>? LifecycleHooks = null,
    string? SelfTestFile = null)
{
    private static readonly Regex IdPattern = new(
        "^[a-z0-9][a-z0-9._-]{2,127}$",
        RegexOptions.CultureInvariant);

    public static H2PluginManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 128_000)
            throw new InvalidDataException("Plugin manifest exceeds 128 KB.");

        H2PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<H2PluginManifest>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
                ?? throw new InvalidDataException("Plugin manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Plugin manifest JSON is invalid.", ex);
        }

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (!IdPattern.IsMatch(Id ?? ""))
            throw new InvalidDataException("Plugin ID is invalid.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 160)
            throw new InvalidDataException("Plugin name is invalid.");
        if (!System.Version.TryParse(Version, out _)
            || !System.Version.TryParse(MinAgentVersion, out _))
            throw new InvalidDataException("Plugin version/minAgentVersion is invalid.");
        if (string.IsNullOrWhiteSpace(Publisher) || Publisher.Length > 160)
            throw new InvalidDataException("Plugin publisher is invalid.");
        ValidateHash(PackageHash, "Plugin packageHash");
        ValidateIds(Capabilities, "capability", 512);
        ValidateIds(Skills, "skill", 256);
        ValidateIds(Providers, "provider", 128);
        ValidateIds(Permissions, "permission", 256);

        foreach (var helper in NativeHelpers ?? Array.Empty<string>())
            ValidateRelativePath(helper, "native helper");
        foreach (var hook in LifecycleHooks ?? Array.Empty<string>())
            ValidateId(hook, "lifecycle hook", 128);
        if (!string.IsNullOrWhiteSpace(SelfTestFile))
            ValidateRelativePath(SelfTestFile!, "self-test file");
    }

    public static void ValidateHash(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || value.Length != 71
            || value[7..].Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException(label + " must be sha256:<64 hex>.");
    }

    public static void ValidateRelativePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains(':')
            || value.Replace((char)92, '/').Split('/').Any(x => x == "..")
            || value.Any(char.IsControl))
            throw new InvalidDataException($"Plugin {label} path is unsafe: {value}");
    }

    private static void ValidateIds(
        IReadOnlyList<string>? values,
        string label,
        int max)
    {
        foreach (var value in values ?? Array.Empty<string>())
            ValidateId(value, label, max);

        if ((values ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).Count()
            != (values ?? Array.Empty<string>()).Count)
            throw new InvalidDataException($"Plugin {label} list contains duplicates.");
    }

    private static void ValidateId(
        string value,
        string label,
        int max)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > max
            || value.Any(char.IsControl))
            throw new InvalidDataException($"Plugin {label} identifier is invalid.");
    }
}

public enum PluginTrustState
{
    Unknown = 0,
    TrustedOfficial = 1,
    OrganizationApproved = 2,
    LocalDeveloper = 3,
    Untrusted = 4
}

public sealed record PluginCatalogEntry(
    string Id,
    string Name,
    string Version,
    string Summary,
    string Publisher,
    IReadOnlyList<string> CapabilityKeywords,
    IReadOnlyList<string> SkillKeywords,
    string MinAgentVersion,
    PluginTrustState TrustState,
    string ArchiveSha256,
    string DownloadLocation);

public sealed class PluginCatalog
{
    private readonly List<PluginCatalogEntry> _entries = [];

    public void Add(PluginCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        H2PluginManifest.ValidateHash(entry.ArchiveSha256, "Catalog archive hash");
        if (string.IsNullOrWhiteSpace(entry.DownloadLocation)
            || entry.DownloadLocation.Length > 2_048)
            throw new ArgumentException("Plugin download location is invalid.", nameof(entry));
        if (_entries.Any(x => x.Id == entry.Id && x.Version == entry.Version))
            throw new InvalidOperationException(
                $"Catalog already contains {entry.Id}@{entry.Version}.");
        _entries.Add(entry);
    }

    public IReadOnlyList<PluginCatalogEntry> Search(
        string query,
        int maxResults = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var terms = query.ToLowerInvariant()
            .Split(
                [' ', '.', '-', '_', '/', ':'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return _entries
            .Select(entry => new
            {
                Entry = entry,
                Score = Score(entry, terms)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => System.Version.Parse(x.Entry.Version))
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(x => x.Entry)
            .ToArray();
    }

    public IReadOnlyList<PluginCatalogEntry> All
        => _entries
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ThenByDescending(x => System.Version.Parse(x.Version))
            .ToArray();

    private static int Score(
        PluginCatalogEntry entry,
        IReadOnlyList<string> terms)
    {
        var haystack = string.Join(
            " ",
            new[]
            {
                entry.Id,
                entry.Name,
                entry.Summary,
                entry.Publisher
            }
            .Concat(entry.CapabilityKeywords)
            .Concat(entry.SkillKeywords))
            .ToLowerInvariant();

        return terms.Sum(term =>
            haystack.Contains(term, StringComparison.Ordinal)
                ? 10
                : 0);
    }
}

public sealed record PluginCapabilityIndexEntry(
    string PluginId,
    string PluginVersion,
    string Publisher,
    string Kind,
    string Id,
    string Sha256);

public sealed class PluginCapabilityIndex
{
    private readonly List<PluginCapabilityIndexEntry> _entries = [];

    public IReadOnlyList<PluginCapabilityIndexEntry> Entries
        => _entries
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();

    public void Rebuild(
        IEnumerable<(H2PluginManifest Manifest, string VersionRoot)> active)
    {
        _entries.Clear();
        foreach (var (manifest, versionRoot) in active)
        {
            foreach (var capability in manifest.Capabilities)
            {
                _entries.Add(new PluginCapabilityIndexEntry(
                    manifest.Id,
                    manifest.Version,
                    manifest.Publisher,
                    "tool",
                    capability,
                    ""));
            }

            foreach (var skill in manifest.Skills)
            {
                var skillPath = Path.Combine(
                    versionRoot,
                    "skills",
                    skill,
                    "SKILL.md");
                var sha = File.Exists(skillPath)
                    ? global::H2AgentLab.SafeWorkspace.Hash(
                        File.ReadAllBytes(skillPath)).ToLowerInvariant()
                    : "";
                _entries.Add(new PluginCapabilityIndexEntry(
                    manifest.Id,
                    manifest.Version,
                    manifest.Publisher,
                    "skill",
                    skill,
                    sha));
            }
        }
    }

    public IReadOnlyList<PluginCapabilityIndexEntry> Search(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var term = query.Trim();
        return Entries
            .Where(x =>
                x.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.PluginId.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToArray();
    }
}
