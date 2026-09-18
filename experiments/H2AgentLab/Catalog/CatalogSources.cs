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

public interface ICatalogSource
{
    string SourceId { get; }
    CatalogSourceKind Kind { get; }
    int Priority { get; }
    PluginTrustState TrustState { get; }
    bool Enabled { get; }

    Task<CatalogSnapshot> FetchMetadataAsync(CancellationToken cancellationToken);
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
    private readonly Dictionary<string, CatalogSnapshot> _cache = new(StringComparer.Ordinal);

    public void Register(ICatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_sources.Any(x => x.SourceId == source.SourceId))
            throw new InvalidOperationException($"Catalog source '{source.SourceId}' is already registered.");
        _sources.Add(source);
    }

    public IReadOnlyList<string> SourceIds
        => _sources.Select(x => x.SourceId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public CatalogMergeResult CachedView()
        => Merge(_cache.Values);

    public bool HasFreshMetadata(TimeSpan maxAge, DateTime utcNow)
    {
        utcNow = utcNow.ToUniversalTime();
        return _sources.Where(x => x.Enabled).All(source =>
            _cache.TryGetValue(source.SourceId, out var snapshot)
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
                var snapshot = await source.FetchMetadataAsync(cancellationToken).ConfigureAwait(false);
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
                if (_cache.TryGetValue(source.SourceId, out var cached))
                    _cache[source.SourceId] = cached with { Stale = true };
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

    private static CatalogMergeResult Merge(IEnumerable<CatalogSnapshot> snapshots)
    {
        var enabled = snapshots
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .ToArray();
        var conflicts = new List<CatalogConflict>();
        var output = new List<AvailableCapabilityRecord>();

        foreach (var group in enabled
            .SelectMany(snapshot => snapshot.Entries.Select(entry => (Snapshot: snapshot, Entry: entry)))
            .GroupBy(x => x.Entry.PluginId + "@" + x.Entry.PluginVersion, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var publisherGroups = items
                .GroupBy(x => x.Entry.Publisher, StringComparer.Ordinal)
                .ToArray();
            var hashGroups = items
                .GroupBy(x => x.Entry.ArchiveSha256, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (publisherGroups.Length > 1)
            {
                conflicts.Add(new CatalogConflict(
                    items[0].Entry.PluginId,
                    items[0].Entry.PluginVersion,
                    "same plugin/version has different publishers",
                    items.Select(x => x.Snapshot.SourceId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
                continue;
            }

            if (hashGroups.Length > 1)
            {
                conflicts.Add(new CatalogConflict(
                    items[0].Entry.PluginId,
                    items[0].Entry.PluginVersion,
                    "same plugin/version has different package hashes",
                    items.Select(x => x.Snapshot.SourceId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
                continue;
            }

            var selected = items
                .OrderByDescending(x => x.Snapshot.Priority)
                .ThenByDescending(x => TrustRank(x.Snapshot.TrustState))
                .ThenBy(x => x.Snapshot.SourceId, StringComparer.Ordinal)
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
                .ThenByDescending(x => Version.Parse(x.PluginVersion))
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
            throw new InvalidDataException("Catalog snapshot identity does not match registered source.");

        foreach (var entry in snapshot.Entries)
        {
            if (entry.SourceId != source.SourceId
                || entry.SourcePriority != source.Priority)
                throw new InvalidDataException("Catalog entry source provenance is inconsistent.");
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

    public void Replace(IReadOnlyList<AvailableCapabilityRecord> entries)
        => _entries = entries;

    public Task<CatalogSnapshot> FetchMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnFetch) throw new IOException("Fixture catalog source unavailable.");
        return Task.FromResult(new CatalogSnapshot(
            SourceId,
            Kind,
            Priority,
            TrustState,
            DateTime.UtcNow,
            Stale,
            _entries));
    }
}
