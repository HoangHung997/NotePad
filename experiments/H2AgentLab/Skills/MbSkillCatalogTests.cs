using H2AgentLab.Tools;

namespace H2AgentLab.Skills;

public static class MbSkillCatalogTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-50 test directory.");
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
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("MB-50 canonical SkillCatalog owns built-in search read and provenance", () =>
        {
            var skillsRoot = Path.Combine(root, "built-in-skills");
            WriteSkill(
                skillsRoot,
                "formula-audit",
                "Inspect spreadsheet formulas and workbook consistency.",
                "Use this skill to inspect formulas.",
                "references/checks.md",
                "Check formulas, ranges, and workbook state.");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));

            var selected = catalog.Search("spreadsheet formulas", 10).Single();
            Check(selected.Name == "formula-audit"
                && selected.Identity.SourceKind == SkillSourceKind.BuiltIn
                && selected.Identity.SourceId == "built-in"
                && selected.Identity.Sha256.Length == 64,
                "Canonical built-in search lost source/hash provenance.");

            var content = catalog.Read(selected.Identity);
            var resource = catalog.ReadResource(
                selected.Identity,
                "references/checks.md");
            Check(content.EntryPoint.Contains("inspect formulas", StringComparison.OrdinalIgnoreCase)
                && resource.Content.Contains("workbook state", StringComparison.OrdinalIgnoreCase)
                && resource.Sha256.Length == 64,
                "Canonical read APIs did not return selected entry/resource content.");
            return Task.CompletedTask;
        });

        await Test("MB-50 one catalog merges source adapters while retaining plugin provenance", () =>
        {
            var skillsRoot = Path.Combine(root, "mixed-skills");
            WriteSkill(
                skillsRoot,
                "general-writing",
                "General writing and document guidance.",
                "Built-in writing guidance.");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));
            catalog.Register(new FixturePluginSkillSource());

            var results = catalog.Search("", 10);
            Check(catalog.SourceIds.SequenceEqual(new[] { "built-in", "fixture-plugin" }),
                "Canonical catalog did not expose both registered sources.");
            var plugin = results.Single(x => x.Name == "plugin-review");
            Check(plugin.Identity.SourceKind == SkillSourceKind.Plugin
                && plugin.Identity.PluginId == "fixture.productivity"
                && plugin.Identity.PluginVersion == "2.3.0"
                && plugin.Identity.SourceId == "fixture-plugin",
                "Plugin source provenance was lost in canonical search.");

            var content = catalog.Read(plugin.Identity);
            Check(content.EntryPoint == "Plugin review instructions.",
                "Canonical read did not dispatch through the selected plugin source.");
            return Task.CompletedTask;
        });

        await Test("MB-50 canonical built-in factory preserves facade-era read parity", () =>
        {
            var skillsRoot = Path.Combine(root, "legacy-skills");
            WriteSkill(
                skillsRoot,
                "compatibility",
                "Compatibility adapter fixture guidance.",
                "Canonical compatibility body.",
                "references/detail.md",
                "Canonical detail resource.");

            var catalog = H2AgentLab.Skills.SkillCatalog.CreateBuiltIn(skillsRoot);
            Check(catalog.SourceIds.SequenceEqual(new[] { "built-in" }),
                "Canonical built-in factory did not register the built-in source.");
            var selected = catalog.SnapshotMetadata().Single();
            Check(catalog.Read(selected.Identity).EntryPoint
                    .Contains("Canonical compatibility body.", StringComparison.Ordinal)
                && catalog.ReadResource(selected.Identity, "references/detail.md").Content
                    .Contains("Canonical detail resource.", StringComparison.Ordinal),
                "Canonical catalog lost entry/resource read parity.");
            return Task.CompletedTask;
        });

        await Test("MB-50 source guard leaves one canonical catalog implementation", () =>
        {
            var repo = FindRepoRoot();
            var legacyPath = Path.Combine(repo, "experiments", "H2AgentLab", "SkillCatalog.cs");
            var deferredPath = Path.Combine(repo, "experiments", "H2AgentLab", "Tools", "DeferredSkillSession.cs");
            var canonicalSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Skills", "UnifiedSkillCatalog.cs"));
            var indexesSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Capabilities", "CapabilityIndexes.cs"));

            Check(!File.Exists(legacyPath),
                "Root legacy SkillCatalog facade still exists.");
            Check(!File.Exists(deferredPath),
                "DeferredSkillSession compatibility cache still exists.");
            Check(canonicalSource.Contains("public class SkillCatalog", StringComparison.Ordinal)
                && canonicalSource.Contains("CreateBuiltIn(", StringComparison.Ordinal)
                && !canonicalSource.Contains("class UnifiedSkillCatalog", StringComparison.Ordinal)
                && !canonicalSource.Contains("global::H2AgentLab.SkillCatalog", StringComparison.Ordinal)
                && !canonicalSource.Contains("global::H2AgentLab.LabSkill", StringComparison.Ordinal),
                "Canonical skill source still retains a duplicate facade/type path.");
            Check(indexesSource.Contains("SkillCatalog skills", StringComparison.Ordinal)
                && !indexesSource.Contains("UnifiedSkillCatalog skills", StringComparison.Ordinal),
                "Installed capability projection does not use canonical SkillCatalog.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-skill-catalog-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static void WriteSkill(
        string root,
        string name,
        string description,
        string body,
        string? resourcePath = null,
        string? resourceContent = null)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\n---\n" + body + "\n");
        if (!string.IsNullOrWhiteSpace(resourcePath))
        {
            var full = Path.Combine(
                directory,
                resourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, resourceContent ?? "");
        }
    }

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

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FixturePluginSkillSource : ISkillSource
    {
        private static readonly SkillIdentity Identity = new(
            SkillSourceKind.Plugin,
            "fixture-plugin",
            "fixture.productivity",
            "2.3.0",
            "plugin-review",
            new string('a', 64));

        public string SourceId => "fixture-plugin";
        public SkillSourceKind SourceKind => SkillSourceKind.Plugin;

        public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
        {
            if (maxResults is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            var summary = new SkillSummary(
                Identity,
                "plugin-review",
                "Review plugin-provided structured work.",
                "installed",
                "plugin");
            return BuiltInSkillSource.Rank([summary], query, maxResults);
        }

        public SkillContent Read(SkillIdentity identity)
        {
            Ensure(identity);
            return new SkillContent(
                new SkillSummary(
                    Identity,
                    "plugin-review",
                    "Review plugin-provided structured work.",
                    "installed",
                    "plugin"),
                "Plugin review instructions.",
                ["references/plugin.md"]);
        }

        public SkillResourceContent ReadResource(
            SkillIdentity identity,
            string relativePath)
        {
            Ensure(identity);
            if (relativePath != "references/plugin.md")
                throw new FileNotFoundException("Fixture plugin resource is unavailable.");
            const string content = "Plugin resource.";
            return new SkillResourceContent(
                Identity,
                relativePath,
                BuiltInSkillSource.HashText(content),
                content);
        }

        private static void Ensure(SkillIdentity identity)
        {
            if (identity != Identity)
                throw new InvalidOperationException("Fixture identity mismatch.");
        }
    }
}
