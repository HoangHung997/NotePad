using H2AgentLab.Catalog;
using H2AgentLab.Plugins;

namespace H2AgentLab.Capabilities;

public static class MbAvailableCapabilitySearchTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-72 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("MB-72 available search is a live derived view over catalog metadata", async () =>
        {
            var source = new InMemoryCatalogSource(
                "mb72-live",
                50,
                PluginTrustState.TrustedOfficial,
                [
                    Record(
                        "fixture.alpha",
                        "Alpha structured document capability.",
                        "mb72-live",
                        50)
                ]);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);
            _ = await catalogs.RefreshAsync(CancellationToken.None);

            var available = new AvailableCapabilityIndex();
            available.Bind(() => catalogs.CachedView().Entries);

            Check(available.Search("alpha structured document", 10)
                    .Single().PluginId == "fixture.alpha",
                "Derived available search did not read current catalog metadata.");

            source.Replace(
            [
                Record(
                    "fixture.beta",
                    "Beta spreadsheet formula inspection capability.",
                    "mb72-live",
                    50)
            ]);
            _ = await catalogs.RefreshAsync(CancellationToken.None);

            Check(available.Search("alpha structured document", 10).Count == 0
                && available.Search("beta spreadsheet formula", 10)
                    .Single().PluginId == "fixture.beta",
                "Available search required a manual rebuild or retained stale duplicate metadata.");
        });

        await Test("MB-72 compact lexical search works beyond small historical candidate counts", async () =>
        {
            var records = Enumerable.Range(0, 500)
                .Select(i => Record(
                    "noise.plugin." + i.ToString("000"),
                    "Generic unrelated reporting helper " + i + ".",
                    "mb72-scale",
                    40))
                .ToList();
            records.Add(Record(
                "fixture.needle",
                "Rare telemetry calibration capability for deterministic needle discovery.",
                "mb72-scale",
                40));

            var source = new InMemoryCatalogSource(
                "mb72-scale",
                40,
                PluginTrustState.TrustedOfficial,
                records);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(
                new Tools.ToolRegistry(),
                new Skills.SkillCatalog());
            var available = new AvailableCapabilityIndex();
            var resolver = new CapabilityResolver(
                installed,
                available,
                catalogs,
                new PredicateCapabilityInstallPolicy(_ => true));

            var result = await resolver.ResolveAsync(
                "rare telemetry calibration needle",
                CancellationToken.None);

            Check(result.Status == CapabilityResolutionStatus.AVAILABLE
                && result.Candidates.Count > 0
                && result.Candidates[0].PluginId == "fixture.needle"
                && source.FetchCount == 1,
                "Remote compact metadata search failed at current acceptance scale.");
            Check(available.Records.Count == 501,
                "Derived available view truncated catalog metadata before search.");
        });

        await Test("MB-72 search strategy seam allows future specialized ranking without core storage redesign", () =>
        {
            var strategy = new FixtureSearchStrategy();
            var available = new AvailableCapabilityIndex(strategy);
            available.Rebuild(
            [
                Record(
                    "fixture.one",
                    "One capability.",
                    "mb72-strategy",
                    10),
                Record(
                    "fixture.two",
                    "Two capability.",
                    "mb72-strategy",
                    10)
            ]);

            var result = available.Search(
                "future-specialized-query",
                1);

            Check(strategy.CallCount == 1
                && result.Single().PluginId == "fixture.two",
                "Available search strategy seam was not used.");
        });

        await Test("MB-72 source guard keeps remote search compact and non-authoritative", () =>
        {
            var repo = FindRepoRoot();
            var indexSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "CapabilityIndexes.cs"));
            var resolverSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "CapabilityResolver.cs"));
            var catalogSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Catalog",
                    "CatalogSources.cs"));

            Check(!indexSource.Contains(
                    "private AvailableCapabilityRecord[] _records = []",
                    StringComparison.Ordinal)
                && indexSource.Contains(
                    "Func<IReadOnlyList<AvailableCapabilityRecord>> _records",
                    StringComparison.Ordinal)
                && indexSource.Contains(
                    "IAvailableCapabilitySearchStrategy",
                    StringComparison.Ordinal)
                && indexSource.Contains(
                    "LexicalAvailableCapabilitySearchStrategy",
                    StringComparison.Ordinal),
                "AvailableCapabilityIndex still owns an authoritative heavy record store or lacks a replaceable search seam.");

            Check(resolverSource.Contains(
                    "_available.Bind(() => _catalogs.CachedView().Entries);",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "_available.Rebuild(",
                    StringComparison.Ordinal),
                "CapabilityResolver still duplicates refreshed catalog metadata into AvailableCapabilityIndex.");

            Check(catalogSource.Contains(
                    "Dictionary<string, CatalogSnapshot> _cache",
                    StringComparison.Ordinal),
                "CatalogSourceManager no longer owns the remote metadata cache.");

            var lower = indexSource.ToLowerInvariant();
            foreach (var forbidden in new[]
            {
                "vector database",
                "vectordb",
                "embeddingmodel",
                "faiss",
                "qdrant",
                "pinecone"
            })
                Check(!lower.Contains(forbidden, StringComparison.Ordinal),
                    "MB-72 introduced premature vector infrastructure: " + forbidden);

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-available-capability-search-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AvailableCapabilityRecord Record(
        string pluginId,
        string description,
        string sourceId,
        int priority)
        => new(
            pluginId,
            "1.0.0",
            "MB-72 Fixture Publisher",
            PluginTrustState.TrustedOfficial,
            "2.0.0",
            new string('a', 64),
            "/fixture/" + pluginId + ".zip",
            [description],
            Array.Empty<AvailableSkillMetadata>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            sourceId,
            priority,
            DateTime.UtcNow,
            MetadataStale: false);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }

    private sealed class FixtureSearchStrategy
        : IAvailableCapabilitySearchStrategy
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<AvailableCapabilityRecord> Search(
            IEnumerable<AvailableCapabilityRecord> records,
            string query,
            int maxResults)
        {
            CallCount++;
            return records
                .Where(x => x.PluginId == "fixture.two")
                .Take(maxResults)
                .ToArray();
        }
    }
}
