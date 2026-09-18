using System.Text.Json;
using H2AgentLab.Plugins;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public static class MbDomainNeutralCapabilitySearchTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-70 test directory.");
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

        await Test("MB-70 core capability ranking does not expand domain synonyms", () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "fixture.one",
                "Inspect parameters actions and visibility state consistency."));
            registry.Register(Tool(
                "fixture.two",
                "Inspect checks and review workflow consistency."));
            registry.Register(Tool(
                "fixture.three",
                "Review law and regulation metadata."));
            registry.Register(Tool(
                "fixture.four",
                "Inspect CAD drawing entities and layers."));

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(
                registry,
                new H2AgentLab.Skills.SkillCatalog());

            foreach (var query in new[]
            {
                "dynamic",
                "audit",
                "legal",
                "autocad"
            })
                Check(installed.Search(query, 20).Count == 0,
                    "Generic capability core inferred a hidden domain synonym for query: "
                    + query);
            return Task.CompletedTask;
        });

        await Test("MB-70 realistic installed descriptions are selectable without hidden mappings", () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "cad.integrity",
                "Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility-state consistency."));
            registry.Register(Tool(
                "legal.current-status",
                "Review current legal law and regulation replacement or amendment status."));

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(
                registry,
                new H2AgentLab.Skills.SkillCatalog());

            var cad = installed.Search(
                "check dynamic blocks parameters actions",
                10);
            Check(cad.Count > 0
                && cad[0].CapabilityId == "cad.integrity",
                "Realistic CAD description was not selected lexically.");

            var legal = installed.Search(
                "review legal regulation amendment status",
                10);
            Check(legal.Count > 0
                && legal[0].CapabilityId == "legal.current-status",
                "Realistic legal description was not selected lexically.");
            return Task.CompletedTask;
        });

        await Test("MB-70 available package search relies on compact realistic metadata", () =>
        {
            var available = new AvailableCapabilityIndex();
            available.Rebuild(
            [
                Package(
                    "fixture.cad",
                    ["Inspect AutoCAD dynamic blocks, parameters, actions and visibility states."]),
                Package(
                    "fixture.legal",
                    ["Review current legal laws, regulations, amendments and replacement status."]),
                Package(
                    "fixture.unrelated",
                    ["General writing and note formatting helpers."])
            ]);

            var cad = available.Search(
                "dynamic blocks parameters visibility",
                10);
            Check(cad.Count > 0
                && cad[0].PluginId == "fixture.cad",
                "Available CAD package was not selected from realistic compact metadata.");

            var legal = available.Search(
                "legal regulations amendments replacement",
                10);
            Check(legal.Count > 0
                && legal[0].PluginId == "fixture.legal",
                "Available legal package was not selected from realistic compact metadata.");

            var hidden = available.Search(
                "audit",
                10);
            Check(hidden.All(x => x.PluginId != "fixture.cad"),
                "Available catalog search used a hidden audit/domain synonym mapping.");
            return Task.CompletedTask;
        });

        await Test("MB-70 source guard keeps capability core lexical and domain-neutral", () =>
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
            var combined = indexSource + "\n" + resolverSource;

            Check(indexSource.Contains(
                    "LexicalTerms",
                    StringComparison.Ordinal)
                && !combined.Contains(
                    "SemanticTerms",
                    StringComparison.Ordinal)
                && !combined.Contains(
                    "Synonyms(",
                    StringComparison.Ordinal),
                "Capability core still exposes semantic/synonym expansion helpers.");

            foreach (var forbidden in new[]
            {
                "\"audit\" =>",
                "\"dynamic\" =>",
                "\"blocks\" =>",
                "\"legal\" =>",
                "\"autocad\" =>",
                "parameter/action/visibility",
                "law/regulation",
                "block/cad"
            })
                Check(!combined.Contains(
                        forbidden,
                        StringComparison.OrdinalIgnoreCase),
                    "Capability core contains forbidden domain synonym mapping: "
                    + forbidden);

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-domain-neutral-capability-search-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolDescriptor Tool(
        string name,
        string description)
        => new(
            name,
            new ToolNamespace(
                "fixture",
                "Domain-neutral MB-70 fixture tools."),
            description,
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor(
                "mb70-fixture",
                (call, ct) => ValueTask.FromResult("{}")),
            provenance: new ToolProvenance(
                "mb70.fixture",
                "1.0.0",
                "fixture",
                "1.0.0"));

    private static AvailableCapabilityRecord Package(
        string pluginId,
        IReadOnlyList<string> toolSummaries)
        => new(
            pluginId,
            "1.0.0",
            "MB-70 Fixture Publisher",
            PluginTrustState.TrustedOfficial,
            "2.0.0",
            new string('a', 64),
            "/fixture/" + pluginId + ".zip",
            toolSummaries,
            Array.Empty<AvailableSkillMetadata>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            "mb70-fixture-catalog",
            10,
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
}
