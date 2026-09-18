using H2AgentLab.Catalog;
using H2AgentLab.Plugins;

namespace H2AgentLab.Capabilities;

public static class MbAvailableCapabilityProjectionTests
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

        await Test("MB-72 available index is a derived live view over catalog cache", async () =>
        {
            var source = new InMemoryCatalogSource(
                "mb72-source",
                50,
                PluginTrustState.TrustedOfficial,
                [
                    Package(
                        "fixture.alpha",
                        "General writing helper.")
                ]);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);

            var available = new AvailableCapabilityIndex();
            available.Bind(() => catalogs.CachedView().Entries);

            Check(available.Records.Count == 0,
                "Derived view invented remote metadata before catalog refresh.");

            _ = await catalogs.RefreshAsync(CancellationToken.None);
            Check(available.Search("writing helper", 10)
                    .Single().PluginId == "fixture.alpha",
                "Derived view did not project refreshed catalog metadata.");

            source.Replace(
            [
                Package(
                    "fixture.beta",
                    "Spreadsheet formulas workbook consistency checks.")
            ]);
            _ = await catalogs.RefreshAsync(CancellationToken.None);

            Check(available.Search("spreadsheet formulas workbook", 10)
                    .Single().PluginId == "fixture.beta"
                && available.Search("writing helper", 10).Count == 0,
                "Available index required a manual rebuild or retained stale authoritative state.");
        });

        await Test("MB-72 current-scale remote search does not depend on a small inventory cap", () =>
        {
            var records = Enumerable.Range(0, 600)
                .Select(i => Package(
                    "fixture.package." + i.ToString("000"),
                    i == 587
                        ? "Inspect deterministic target workbook formulas recalculation evidence."
                        : "Generic unrelated metadata package number " + i))
                .ToArray();

            var available = new AvailableCapabilityIndex();
            available.Rebuild(records);

            var result = available.Search(
                "target workbook formulas recalculation evidence",
                5);

            Check(result.Count > 0
                && result[0].PluginId == "fixture.package.587",
                "Remote metadata search missed a relevant package beyond the first 100/typical small subset.");
            Check(available.Records.Count == 600,
                "Compatibility snapshot truncated remote metadata inventory.");
            return Task.CompletedTask;
        });

        await Test("MB-72 search strategy seam allows future semantic extension without changing index storage", () =>
        {
            var strategy = new FixtureSearchStrategy();
            var available = new AvailableCapabilityIndex(strategy);
            available.Rebuild(
            [
                Package("fixture.one", "First compact package."),
                Package("fixture.two", "Second compact package.")
            ]);

            var result = available.Search("semantic-fixture", 1);

            Check(strategy.Calls == 1
                && strategy.LastRecordCount == 2
                && result.Single().PluginId == "fixture.two",
                "AvailableCapabilityIndex did not delegate search through its replaceable strategy seam.");
            return Task.CompletedTask;
        });

        await Test("MB-72 source guard keeps catalog search compact optional and vector-free", () =>
        {
            var repo = FindRepoRoot();
            var indexSource = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Capabilities",
                "CapabilityIndexes.cs"));
            var resolverSource = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Capabilities",
                "CapabilityResolver.cs"));

            Check(indexSource.Contains(
                    "IAvailableCapabilitySearchStrategy",
                    StringComparison.Ordinal)
                && indexSource.Contains(
                    "Func<IReadOnlyList<AvailableCapabilityRecord>>",
                    StringComparison.Ordinal),
                "Available index lacks derived-view/search-strategy extension seams.");
            Check(!indexSource.Contains(
                    "private AvailableCapabilityRecord[] _records",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "_available.Rebuild(",
                    StringComparison.Ordinal),
                "Available index still owns duplicated authoritative state or requires resolver rebuilds.");

            var lower = (indexSource + "\n" + resolverSource).ToLowerInvariant();
            foreach (var forbidden in new[]
            {
                "vectordb",
                "vector database",
                "embeddingmodel",
                "embedding client",
                "faiss",
                "qdrant",
                "pinecone",
                "weaviate"
            })
                Check(!lower.Contains(forbidden, StringComparison.Ordinal),
                    "Core available-capability path added premature semantic/vector infrastructure: " + forbidden);

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-available-capability-projection-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AvailableCapabilityRecord Package(
        string pluginId,
        string description)
        => new(
            pluginId,
            "1.0.0",
            "MB-72 Fixture Publisher",
            PluginTrustState.TrustedOfficial,
            "2.0.0",
            new string('b', 64),
            "/fixture/" + pluginId + ".h2pkg",
            [description],
            Array.Empty<AvailableSkillMetadata>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "mb72-source",
            50,
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
        public int Calls { get; private set; }
        public int LastRecordCount { get; private set; }

        public IReadOnlyList<AvailableCapabilityRecord> Search(
            IEnumerable<AvailableCapabilityRecord> records,
            string query,
            int maxResults)
        {
            Calls++;
            var snapshot = records.ToArray();
            LastRecordCount = snapshot.Length;
            return snapshot
                .Where(x => x.PluginId == "fixture.two")
                .Take(maxResults)
                .ToArray();
        }
    }
}
