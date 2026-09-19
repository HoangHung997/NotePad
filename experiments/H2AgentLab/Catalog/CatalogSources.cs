using System.Text.Json;
using H2AgentLab.Capabilities;
using H2AgentLab.Plugins;

namespace H2AgentLab.Catalog;

public enum CatalogSourceKind
{
    LocalFolder = 0,
    HttpIndex = 1,
    GitHub = 2,
    Nas = 3,
    Private = 4,
    Test = 5
}

public sealed record CatalogSnapshot(
    string SourceId,
    CatalogSourceKind Kind,
    int Priority,
    PluginTrustState TrustState,
    DateTime FetchedUtc,
    bool Stale,
    IReadOnlyList<AvailableCapabilityRecord> Entries);

public sealed record CatalogPackageLocation(
    string SourceId,
    string PluginId,
    string PluginVersion,
    string Location,
    string ArchiveSha256);

/// <summary>
/// Minimal external package catalog seam. A source owns compact metadata search/list and resolves
/// an exact selected package location. It never retrieves, executes, verifies or installs bytes.
/// </summary>
public interface ICatalogSource
{
    string SourceId { get; }
    CatalogSourceKind Kind { get; }
    int Priority { get; }
    PluginTrustState TrustState { get; }
    bool Enabled { get; }

    Task<CatalogSnapshot> FetchMetadataAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AvailableCapabilityRecord>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);

    Task<CatalogPackageLocation> ResolvePackageLocationAsync(
        string pluginId,
        string pluginVersion,
        CancellationToken cancellationToken);
}

public sealed record CatalogConflict(
    string PluginId,
    string PluginVersion,
    string Reason,
    IReadOnlyList<string> SourceIds);

public sealed record CatalogMergeResult(
    IReadOnlyList<AvailableCapabilityRecord> Entries,
    IReadOnlyList<CatalogConflict> Conflicts,
    IReadOnlyList<string> UnavailableSources,
    DateTime MergedUtc);

public sealed class CatalogSourceManager
{
    private readonly List<ICatalogSource> _sources = [];
    private readonly Dictionary<string, CatalogSnapshot> _cache =
        new(StringComparer.Ordinal);

    public void Register(ICatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_sources.Any(x => x.SourceId == source.SourceId))
            throw new InvalidOperationException(
                $"Catalog source '{source.SourceId}' is already registered.");
        _sources.Add(source);
    }

    public IReadOnlyList<string> SourceIds
        => _sources
            .Select(x => x.SourceId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    public CatalogMergeResult CachedView()
        => Merge(_cache.Values);

    public bool HasFreshMetadata(
        TimeSpan maxAge,
        DateTime utcNow)
    {
        utcNow = utcNow.ToUniversalTime();
        return _sources
            .Where(x => x.Enabled)
            .All(source =>
                _cache.TryGetValue(
                    source.SourceId,
                    out var snapshot)
                && !snapshot.Stale
                && utcNow >= snapshot.FetchedUtc
                && utcNow - snapshot.FetchedUtc <= maxAge);
    }

    public async Task<CatalogMergeResult> RefreshAsync(
        CancellationToken cancellationToken)
    {
        var unavailable = new List<string>();
        foreach (var source in _sources
            .Where(x => x.Enabled)
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await source
                    .FetchMetadataAsync(cancellationToken)
                    .ConfigureAwait(false);
                ValidateSnapshot(source, snapshot);
                _cache[source.SourceId] = snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                unavailable.Add(source.SourceId);
                if (_cache.TryGetValue(
                        source.SourceId,
                        out var cached))
                    _cache[source.SourceId] =
                        cached with { Stale = true };
            }
        }

        var merged = Merge(_cache.Values);
        return merged with
        {
            UnavailableSources = unavailable
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray()
        };
    }

    public async Task<CatalogPackageLocation> ResolvePackageLocationAsync(
        string sourceId,
        string pluginId,
        string pluginVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginVersion);

        var source = _sources.SingleOrDefault(x =>
            string.Equals(
                x.SourceId,
                sourceId.Trim(),
                StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"Catalog source '{sourceId.Trim()}' is not registered.");
        if (!source.Enabled)
            throw new InvalidOperationException(
                $"Catalog source '{source.SourceId}' is disabled.");

        var resolved = await source.ResolvePackageLocationAsync(
            pluginId.Trim(),
            pluginVersion.Trim(),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(
                resolved.SourceId,
                source.SourceId,
                StringComparison.Ordinal)
            || !string.Equals(
                resolved.PluginId,
                pluginId.Trim(),
                StringComparison.Ordinal)
            || !string.Equals(
                resolved.PluginVersion,
                pluginVersion.Trim(),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Catalog source returned a package location for a different identity.");

        var cached = CachedView().Entries.SingleOrDefault(x =>
            x.SourceId == source.SourceId
            && x.PluginId == resolved.PluginId
            && x.PluginVersion == resolved.PluginVersion);
        if (cached is not null
            && !string.Equals(
                cached.ArchiveSha256,
                resolved.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Resolved package hash differs from cached catalog metadata.");

        return resolved;
    }

    private static CatalogMergeResult Merge(
        IEnumerable<CatalogSnapshot> snapshots)
    {
        var enabled = snapshots
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .ToArray();
        var conflicts = new List<CatalogConflict>();
        var output = new List<AvailableCapabilityRecord>();

        foreach (var group in enabled
            .SelectMany(snapshot =>
                snapshot.Entries.Select(entry =>
                    (Snapshot: snapshot, Entry: entry)))
            .GroupBy(
                x => x.Entry.PluginId
                    + "@"
                    + x.Entry.PluginVersion,
                StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var publisherGroups = items
                .GroupBy(
                    x => x.Entry.Publisher,
                    StringComparer.Ordinal)
                .ToArray();
            var hashGroups = items
                .GroupBy(
                    x => x.Entry.ArchiveSha256,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (publisherGroups.Length > 1)
            {
                conflicts.Add(new CatalogConflict(
                    items[0].Entry.PluginId,
                    items[0].Entry.PluginVersion,
                    "same plugin/version has different publishers",
                    items
                        .Select(x => x.Snapshot.SourceId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray()));
                continue;
            }

            if (hashGroups.Length > 1)
            {
                conflicts.Add(new CatalogConflict(
                    items[0].Entry.PluginId,
                    items[0].Entry.PluginVersion,
                    "same plugin/version has different package hashes",
                    items
                        .Select(x => x.Snapshot.SourceId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray()));
                continue;
            }

            var selected = items
                .OrderByDescending(x => x.Snapshot.Priority)
                .ThenByDescending(x =>
                    TrustRank(x.Snapshot.TrustState))
                .ThenBy(
                    x => x.Snapshot.SourceId,
                    StringComparer.Ordinal)
                .First();
            output.Add(selected.Entry with
            {
                SourceId = selected.Snapshot.SourceId,
                SourcePriority = selected.Snapshot.Priority,
                MetadataFetchedUtc = selected.Snapshot.FetchedUtc,
                MetadataStale = selected.Snapshot.Stale
            });
        }

        return new CatalogMergeResult(
            output
                .OrderBy(x => x.PluginId, StringComparer.Ordinal)
                .ThenByDescending(x =>
                    Version.Parse(x.PluginVersion))
                .ToArray(),
            conflicts
                .OrderBy(x => x.PluginId, StringComparer.Ordinal)
                .ThenBy(x => x.PluginVersion, StringComparer.Ordinal)
                .ToArray(),
            Array.Empty<string>(),
            DateTime.UtcNow);
    }

    private static int TrustRank(PluginTrustState state)
        => state switch
        {
            PluginTrustState.TrustedOfficial => 4,
            PluginTrustState.OrganizationApproved => 3,
            PluginTrustState.LocalDeveloper => 2,
            PluginTrustState.Unknown => 1,
            _ => 0
        };

    private static void ValidateSnapshot(
        ICatalogSource source,
        CatalogSnapshot snapshot)
    {
        if (snapshot.SourceId != source.SourceId
            || snapshot.Kind != source.Kind
            || snapshot.Priority != source.Priority
            || snapshot.TrustState != source.TrustState)
            throw new InvalidDataException(
                "Catalog snapshot identity does not match registered source.");

        foreach (var entry in snapshot.Entries)
        {
            if (entry.SourceId != source.SourceId
                || entry.SourcePriority != source.Priority)
                throw new InvalidDataException(
                    "Catalog entry source provenance is inconsistent.");
        }
    }
}

public sealed class InMemoryCatalogSource : ICatalogSource
{
    private IReadOnlyList<AvailableCapabilityRecord> _entries;

    public InMemoryCatalogSource(
        string sourceId,
        int priority,
        PluginTrustState trustState,
        IReadOnlyList<AvailableCapabilityRecord> entries,
        CatalogSourceKind kind = CatalogSourceKind.Test)
    {
        SourceId = sourceId;
        Priority = priority;
        TrustState = trustState;
        Kind = kind;
        _entries = entries;
    }

    public string SourceId { get; }
    public CatalogSourceKind Kind { get; }
    public int Priority { get; }
    public PluginTrustState TrustState { get; }
    public bool Enabled { get; set; } = true;
    public bool ThrowOnFetch { get; set; }
    public bool Stale { get; set; }
    public int FetchCount { get; private set; }

    public void Replace(
        IReadOnlyList<AvailableCapabilityRecord> entries)
        => _entries = entries;

    public Task<CatalogSnapshot> FetchMetadataAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FetchCount++;
        if (ThrowOnFetch)
            throw new IOException(
                "Fixture catalog source unavailable.");
        return Task.FromResult(new CatalogSnapshot(
            SourceId,
            Kind,
            Priority,
            TrustState,
            DateTime.UtcNow,
            Stale,
            _entries));
    }

    public async Task<IReadOnlyList<AvailableCapabilityRecord>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ValidateMaxResults(maxResults);
        var snapshot = await FetchMetadataAsync(
            cancellationToken).ConfigureAwait(false);
        var index = new AvailableCapabilityIndex();
        index.Rebuild(snapshot.Entries);
        return index.Search(query ?? "", maxResults);
    }

    public Task<CatalogPackageLocation> ResolvePackageLocationAsync(
        string pluginId,
        string pluginVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Exact(
            _entries,
            pluginId,
            pluginVersion);
        return Task.FromResult(new CatalogPackageLocation(
            SourceId,
            entry.PluginId,
            entry.PluginVersion,
            entry.PackageLocation,
            entry.ArchiveSha256));
    }

    private static AvailableCapabilityRecord Exact(
        IEnumerable<AvailableCapabilityRecord> entries,
        string pluginId,
        string pluginVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginVersion);
        return entries.SingleOrDefault(x =>
            x.PluginId == pluginId.Trim()
            && x.PluginVersion == pluginVersion.Trim())
            ?? throw new KeyNotFoundException(
                $"Catalog package '{pluginId.Trim()}@{pluginVersion.Trim()}' was not found.");
    }

    private static void ValidateMaxResults(int maxResults)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(maxResults));
    }
}

public sealed record LocalCatalogPackageMetadata(
    string PluginId,
    string PluginVersion,
    string Publisher,
    string MinAgentVersion,
    string ArchiveSha256,
    string PackagePath,
    string[]? ToolSummaries = null,
    AvailableSkillMetadata[]? Skills = null,
    string[]? Providers = null,
    string[]? Permissions = null);

/// <summary>
/// Minimal deterministic external source. The configured folder owns catalog.json plus referenced
/// package files. Metadata reads never copy/execute packages; exact path resolution remains
/// separate from IPackageRetriever and PluginManager.
/// </summary>
public sealed class LocalFolderCatalogSource : ICatalogSource
{
    private const long MaxIndexBytes = 2L * 1024 * 1024;
    private const int MaxPackages = 10_000;

    private readonly string _root;
    private readonly string _indexPath;

    public LocalFolderCatalogSource(
        string root,
        string sourceId,
        PluginTrustState trustState,
        int priority = 100,
        string indexFileName = "catalog.json")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexFileName);
        if (priority is < -10_000 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (Path.IsPathRooted(indexFileName)
            || indexFileName.Contains('/')
            || indexFileName.Contains((char)92)
            || indexFileName is "." or "..")
            throw new ArgumentException(
                "Catalog index file name must be one file inside the configured root.",
                nameof(indexFileName));

        _root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        _indexPath = Path.Combine(_root, indexFileName);
        SourceId = NormalizeId(sourceId);
        TrustState = trustState;
        Priority = priority;
    }

    public string SourceId { get; }
    public CatalogSourceKind Kind
        => CatalogSourceKind.LocalFolder;
    public int Priority { get; }
    public PluginTrustState TrustState { get; }
    public bool Enabled { get; set; } = true;
    public string Root => _root;

    public async Task<CatalogSnapshot> FetchMetadataAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enabled)
            throw new InvalidOperationException(
                $"Catalog source '{SourceId}' is disabled.");
        if (!File.Exists(_indexPath))
            throw new FileNotFoundException(
                "Configured catalog index does not exist.",
                _indexPath);

        var info = new FileInfo(_indexPath);
        if (info.Length > MaxIndexBytes)
            throw new InvalidDataException(
                "Catalog index exceeds 2 MB.");

        var json = await File.ReadAllTextAsync(
            _indexPath,
            cancellationToken).ConfigureAwait(false);
        LocalCatalogPackageMetadata[] metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<
                LocalCatalogPackageMetadata[]>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    })
                ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Catalog index JSON is invalid.",
                ex);
        }

        if (metadata.Length > MaxPackages)
            throw new InvalidDataException(
                $"Catalog index exceeds {MaxPackages} packages.");

        var fetched = DateTime.UtcNow;
        var entries = metadata
            .Select(item => ToAvailable(item, fetched))
            .ToArray();

        if (entries
            .GroupBy(
                x => x.PluginId + "@" + x.PluginVersion,
                StringComparer.Ordinal)
            .Any(x => x.Count() > 1))
            throw new InvalidDataException(
                "Catalog index contains duplicate plugin/version identities.");

        var compactCheck = new AvailableCapabilityIndex();
        compactCheck.Rebuild(entries);

        return new CatalogSnapshot(
            SourceId,
            Kind,
            Priority,
            TrustState,
            fetched,
            Stale: false,
            entries);
    }

    public async Task<IReadOnlyList<AvailableCapabilityRecord>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(maxResults));

        var snapshot = await FetchMetadataAsync(
            cancellationToken).ConfigureAwait(false);
        var index = new AvailableCapabilityIndex();
        index.Rebuild(snapshot.Entries);
        return index.Search(query ?? "", maxResults);
    }

    public async Task<CatalogPackageLocation> ResolvePackageLocationAsync(
        string pluginId,
        string pluginVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginVersion);
        var snapshot = await FetchMetadataAsync(
            cancellationToken).ConfigureAwait(false);
        var entry = snapshot.Entries.SingleOrDefault(x =>
            x.PluginId == pluginId.Trim()
            && x.PluginVersion == pluginVersion.Trim())
            ?? throw new KeyNotFoundException(
                $"Catalog package '{pluginId.Trim()}@{pluginVersion.Trim()}' was not found.");

        if (!File.Exists(entry.PackageLocation))
            throw new FileNotFoundException(
                "Selected catalog package file does not exist.",
                entry.PackageLocation);

        return new CatalogPackageLocation(
            SourceId,
            entry.PluginId,
            entry.PluginVersion,
            entry.PackageLocation,
            entry.ArchiveSha256);
    }

    private AvailableCapabilityRecord ToAvailable(
        LocalCatalogPackageMetadata metadata,
        DateTime fetchedUtc)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var pluginId = NormalizeId(metadata.PluginId);
        if (!Version.TryParse(
                metadata.PluginVersion,
                out _))
            throw new InvalidDataException(
                $"Catalog plugin '{pluginId}' has invalid version.");
        if (!Version.TryParse(
                metadata.MinAgentVersion,
                out _))
            throw new InvalidDataException(
                $"Catalog plugin '{pluginId}' has invalid minimum Agent version.");
        if (string.IsNullOrWhiteSpace(metadata.Publisher)
            || metadata.Publisher.Length > 160)
            throw new InvalidDataException(
                $"Catalog plugin '{pluginId}' has invalid publisher.");
        H2PluginManifest.ValidateHash(
            metadata.ArchiveSha256,
            "Catalog archive hash");

        var packagePath = ResolveRelativePackagePath(
            metadata.PackagePath);

        return new AvailableCapabilityRecord(
            pluginId,
            metadata.PluginVersion.Trim(),
            metadata.Publisher.Trim(),
            TrustState,
            metadata.MinAgentVersion.Trim(),
            metadata.ArchiveSha256.Trim(),
            packagePath,
            (metadata.ToolSummaries ?? [])
                .Select(x => x?.Trim() ?? "")
                .Where(x => x.Length > 0)
                .ToArray(),
            (metadata.Skills ?? [])
                .Select(x =>
                {
                    if (string.IsNullOrWhiteSpace(x.SkillId)
                        || string.IsNullOrWhiteSpace(x.Name)
                        || string.IsNullOrWhiteSpace(x.Description))
                        throw new InvalidDataException(
                            $"Catalog plugin '{pluginId}' has invalid skill metadata.");
                    return new AvailableSkillMetadata(
                        x.SkillId.Trim(),
                        x.Name.Trim(),
                        x.Description.Trim());
                })
                .ToArray(),
            (metadata.Providers ?? [])
                .Select(x => x?.Trim() ?? "")
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            (metadata.Permissions ?? [])
                .Select(x => x?.Trim() ?? "")
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceId,
            Priority,
            fetchedUtc,
            MetadataStale: false);
    }

    private string ResolveRelativePackagePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var relative = relativePath
            .Trim()
            .Replace((char)92, '/');
        if (Path.IsPathRooted(relative)
            || relative.Contains(':')
            || relative.Split('/').Any(x =>
                x is "" or "." or "..")
            || relative.Any(char.IsControl))
            throw new InvalidDataException(
                "Catalog package path must be a safe relative path inside the configured source root.");

        var full = Path.GetFullPath(
            Path.Combine(
                _root,
                relative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        if (!full.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Catalog package path escapes the configured source root.");
        return full;
    }

    private static string NormalizeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var id = value.Trim().ToLowerInvariant();
        if (id.Length > 128
            || id.Any(c =>
                !(char.IsAsciiLetterOrDigit(c)
                    || c is '.' or '-' or '_')))
            throw new InvalidDataException(
                "Catalog source/plugin identifier is invalid.");
        return id;
    }
}
