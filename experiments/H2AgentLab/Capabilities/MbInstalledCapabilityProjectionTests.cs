using System.Text.Json;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public static class MbInstalledCapabilityProjectionTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-71 test directory.");
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

        await Test("MB-71 installed index is a live projection over ToolRegistry and provider state", () =>
        {
            var registry = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var providers = new List<ProviderProvenance>();
            var index = new InstalledCapabilityIndex();
            index.Bind(
                registry,
                skills,
                () => providers.ToArray());

            registry.Register(Tool(
                "fixture.primary",
                "Primary deterministic capability."));
            var before = index.Search(
                "primary deterministic capability",
                10).Single();

            registry.Register(Tool(
                "fixture.unrelated",
                "Unrelated image formatting helper."));
            providers.Add(new ProviderProvenance(
                "fixture.provider",
                "1.0.0",
                "fixture-server",
                "in-process"));

            var after = index.Search(
                "primary deterministic capability",
                10).Single();
            Check(before == after,
                "Adding an unrelated registry capability changed the selected installed capability semantics.");
            Check(index.Records.Any(x =>
                    x.CapabilityId == "fixture.unrelated"
                    && x.Kind == CapabilityKind.Tool)
                && index.Records.Any(x =>
                    x.CapabilityId == "fixture.provider"
                    && x.Kind == CapabilityKind.Provider),
                "Live projection did not reflect authoritative registry/provider changes without rebuild.");
            return Task.CompletedTask;
        });

        await Test("MB-71 exact skill snapshot removes Search-empty-100 correctness cap", () =>
        {
            var source = new LargeFixtureSkillSource();
            Check(source.Search("", 100).Count == 100
                && source.Search("", 100).All(x =>
                    x.Name != "zz-needle-skill"),
                "Fixture does not reproduce the historical Search-empty-100 truncation risk.");

            var skills = new H2AgentLab.Skills.SkillCatalog();
            skills.Register(source);
            var index = new InstalledCapabilityIndex();
            index.Bind(
                new ToolRegistry(),
                skills,
                static () => Array.Empty<ProviderProvenance>());

            var result = index.Search(
                "rare needle capability",
                20);
            Check(result.Count == 1
                && result[0].Kind == CapabilityKind.Skill
                && result[0].CapabilityId == "zz-needle-skill",
                "Installed projection lost a skill beyond the historical 100-result search cap.");
            Check(index.Records.Count(x =>
                    x.Kind == CapabilityKind.Skill) == 151,
                "Installed projection did not expose the exact source metadata snapshot.");
            return Task.CompletedTask;
        });

        await Test("MB-71 unsupported snapshot sources fail closed instead of silently truncating", () =>
        {
            var skills = new H2AgentLab.Skills.SkillCatalog();
            skills.Register(new SearchOnlyFixtureSkillSource());

            try
            {
                _ = skills.SnapshotMetadata();
                throw new InvalidOperationException(
                    "Search-only source was silently accepted as an exact inventory.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains(
                    "exact installed metadata snapshots",
                    StringComparison.OrdinalIgnoreCase))
            {
            }

            return Task.CompletedTask;
        });

        await Test("MB-71 source guard removes authoritative cache and hard inventory search cap", () =>
        {
            var repo = FindRepoRoot();
            var indexSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "CapabilityIndexes.cs"));
            var skillSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Skills",
                    "UnifiedSkillCatalog.cs"));
            var continuationSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "MissingCapabilityContinuation.cs"));

            Check(!indexSource.Contains(
                    "private InstalledCapabilityRecord[] _records",
                    StringComparison.Ordinal)
                && !indexSource.Contains(
                    "skills.Search(\"\", 100)",
                    StringComparison.Ordinal)
                && indexSource.Contains(
                    "=> Project();",
                    StringComparison.Ordinal),
                "InstalledCapabilityIndex still owns cached authoritative state or hard-capped inventory discovery.");

            Check(skillSource.Contains(
                    "IInstalledSkillMetadataSource",
                    StringComparison.Ordinal)
                && skillSource.Contains(
                    "SnapshotMetadata()",
                    StringComparison.Ordinal),
                "Canonical SkillCatalog has no exact installed metadata snapshot seam.");

            Check(continuationSource.Contains(
                    "_installed.Bind(_registry, _skills, _providers);",
                    StringComparison.Ordinal)
                && !continuationSource.Contains(
                    "_installed.Rebuild(",
                    StringComparison.Ordinal),
                "Missing capability continuation still depends on rebuilding a stale installed cache after mutation.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-installed-capability-projection-tests.txt");
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
                "MB-71 live projection fixture."),
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
                "mb71-fixture",
                (call, ct) => ValueTask.FromResult("{}")),
            provenance: new ToolProvenance(
                "mb71.fixture",
                "1.0.0",
                "fixture",
                "1.0.0"));

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

    private sealed class LargeFixtureSkillSource
        : ISkillSource, IInstalledSkillMetadataSource
    {
        private readonly SkillSummary[] _all;

        public LargeFixtureSkillSource()
        {
            var items = Enumerable.Range(0, 150)
                .Select(i => Summary(
                    $"noise-{i:000}",
                    $"Generic unrelated fixture capability {i}."))
                .ToList();
            items.Add(Summary(
                "zz-needle-skill",
                "Rare needle capability for exact installed snapshot verification."));
            _all = items
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToArray();
        }

        public string SourceId => "large-fixture";
        public SkillSourceKind SourceKind => SkillSourceKind.BuiltIn;

        public IReadOnlyList<SkillSummary> Search(
            string query,
            int maxResults = 20)
        {
            if (maxResults is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            return BuiltInSkillSource.Rank(
                _all,
                query,
                maxResults);
        }

        public IReadOnlyList<SkillSummary> SnapshotMetadata()
            => _all.ToArray();

        public SkillContent Read(SkillIdentity identity)
            => throw new NotSupportedException();

        public SkillResourceContent ReadResource(
            SkillIdentity identity,
            string relativePath)
            => throw new NotSupportedException();

        private SkillSummary Summary(
            string name,
            string description)
            => new(
                new SkillIdentity(
                    SourceKind,
                    SourceId,
                    null,
                    null,
                    name,
                    new string('a', 64)),
                name,
                description,
                "installed",
                "fixture");
    }

    private sealed class SearchOnlyFixtureSkillSource : ISkillSource
    {
        public string SourceId => "search-only-fixture";
        public SkillSourceKind SourceKind => SkillSourceKind.BuiltIn;

        public IReadOnlyList<SkillSummary> Search(
            string query,
            int maxResults = 20)
            => [];

        public SkillContent Read(SkillIdentity identity)
            => throw new NotSupportedException();

        public SkillResourceContent ReadResource(
            SkillIdentity identity,
            string relativePath)
            => throw new NotSupportedException();
    }
}
