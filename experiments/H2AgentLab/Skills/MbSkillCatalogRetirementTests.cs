namespace H2AgentLab.Skills;

public static class MbSkillCatalogRetirementTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-93 test directory.");
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

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-93 canonical built-in factory owns discovery and progressive reads", () =>
        {
            var skillsRoot = Path.Combine(root, "skills");
            WriteSkill(
                skillsRoot,
                "catalog-parity",
                "Canonical catalog parity guidance.",
                "Canonical entry point.",
                "references/detail.md",
                "Canonical detail.");

            var catalog = SkillCatalog.CreateBuiltIn(skillsRoot);
            var selected = catalog.Search("catalog parity", 10).Single();
            Check(catalog.SourceIds.SequenceEqual(new[] { "built-in" }),
                "Built-in factory registered more than one catalog/source path.");
            Check(selected.Identity.Sha256.Length == 64,
                "Canonical discovery lost stable source hash.");
            Check(catalog.Read(selected.Identity).EntryPoint.Contains("Canonical entry point.", StringComparison.Ordinal)
                  && catalog.ReadResource(selected.Identity, "references/detail.md").Content.Contains("Canonical detail.", StringComparison.Ordinal),
                "Canonical progressive read parity failed.");
            return Task.CompletedTask;
        });

        await Test("MB-93 duplicate root facade deferred session and unified alias are retired", () =>
        {
            var repo = FindRepoRoot();
            Check(!File.Exists(Path.Combine(repo, "experiments", "H2AgentLab", "SkillCatalog.cs")),
                "Legacy root SkillCatalog.cs still exists.");
            Check(!File.Exists(Path.Combine(repo, "experiments", "H2AgentLab", "Tools", "DeferredSkillSession.cs")),
                "DeferredSkillSession.cs still exists.");

            var canonicalPath = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Skills",
                "UnifiedSkillCatalog.cs");
            var canonical = File.ReadAllText(canonicalPath);
            Check(canonical.Contains("public class SkillCatalog", StringComparison.Ordinal)
                  && canonical.Contains("CreateBuiltIn(", StringComparison.Ordinal)
                  && !canonical.Contains("class UnifiedSkillCatalog", StringComparison.Ordinal)
                  && !canonical.Contains("global::H2AgentLab.SkillCatalog", StringComparison.Ordinal)
                  && !canonical.Contains("global::H2AgentLab.LabSkill", StringComparison.Ordinal),
                "Canonical skill source still exposes a duplicate catalog compatibility type.");

            foreach (var path in Directory.EnumerateFiles(
                         Path.Combine(repo, "experiments", "H2AgentLab"),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(path).Equals(
                        Path.GetFullPath(Path.Combine(
                            repo,
                            "experiments",
                            "H2AgentLab",
                            "Skills",
                            "MbSkillCatalogRetirementTests.cs")),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var source = File.ReadAllText(path);
                Check(!source.Contains("DeferredSkillSession", StringComparison.Ordinal),
                    "C# source still references retired DeferredSkillSession: " + path);
                Check(!source.Contains("global::H2AgentLab.SkillCatalog", StringComparison.Ordinal),
                    "C# source still references retired root SkillCatalog: " + path);
                Check(!source.Contains("new UnifiedSkillCatalog", StringComparison.Ordinal),
                    "C# source still constructs historical UnifiedSkillCatalog: " + path);
                Check(!source.Contains(".Skills.Canonical", StringComparison.Ordinal),
                    "C# source still hops through the retired catalog facade: " + path);
            }

            return Task.CompletedTask;
        });

        await Test("MB-93 PluginSkillCatalog is internal to PluginSkillSource", () =>
        {
            var repo = FindRepoRoot();
            var pluginCatalogPath = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Plugins",
                "PluginSkillCatalog.cs");
            var pluginCatalog = File.ReadAllText(pluginCatalogPath);
            Check(pluginCatalog.Contains("internal sealed class PluginSkillCatalog", StringComparison.Ordinal)
                  && pluginCatalog.Contains("internal sealed record PluginSkillSummary", StringComparison.Ordinal)
                  && pluginCatalog.Contains("internal sealed record PluginSkillContent", StringComparison.Ordinal),
                "Plugin skill cache/catalog remains part of the public catalog surface.");

            var canonicalPath = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Skills",
                "UnifiedSkillCatalog.cs");
            var canonical = File.ReadAllText(canonicalPath);
            Check(canonical.Contains("_catalog = new PluginSkillCatalog(_plugins);", StringComparison.Ordinal),
                "PluginSkillSource no longer owns its internal plugin catalog helper.");

            foreach (var path in Directory.EnumerateFiles(
                         Path.Combine(repo, "experiments", "H2AgentLab"),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(path).Equals(
                        Path.GetFullPath(canonicalPath),
                        StringComparison.OrdinalIgnoreCase)
                    || Path.GetFullPath(path).Equals(
                        Path.GetFullPath(Path.Combine(
                            repo,
                            "experiments",
                            "H2AgentLab",
                            "Skills",
                            "MbSkillCatalogRetirementTests.cs")),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                Check(!File.ReadAllText(path).Contains("new PluginSkillCatalog(", StringComparison.Ordinal),
                    "PluginSkillCatalog escaped PluginSkillSource implementation: " + path);
            }

            return Task.CompletedTask;
        });

        await Test("MB-93 AgentTools and normal runtime share the canonical catalog", () =>
        {
            var repo = FindRepoRoot();
            var tools = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "AgentTools.cs"));
            var runtime = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Tools",
                "NormalRuntimeToolRegistry.cs"));

            Check(tools.Contains(
                    "H2AgentLab.Skills.SkillCatalog.CreateBuiltIn()",
                    StringComparison.Ordinal),
                "Legacy AgentTools does not use the canonical built-in catalog.");
            Check(runtime.Contains(
                    "new SkillRuntimeToolExecutor(host.Skills)",
                    StringComparison.Ordinal)
                  && !runtime.Contains("host.Skills.Canonical", StringComparison.Ordinal),
                "Normal runtime still depends on a facade-to-canonical hop.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-skill-catalog-retirement-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static void WriteSkill(
        string root,
        string name,
        string description,
        string body,
        string resourcePath,
        string resourceContent)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\n---\n" + body + "\n");
        var resource = Path.Combine(
            directory,
            resourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(resource)!);
        File.WriteAllText(resource, resourceContent);
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
}
