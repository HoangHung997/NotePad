using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

public static class MbRetireV1ToolRegistryAdapterTests
{
    private static readonly string[] ExpectedTools =
    [
        "click_control",
        "check_word",
        "find_files",
        "inspect_artifact",
        "inspect_window",
        "list_files",
        "list_skills",
        "open_file",
        "publish_artifact",
        "read_file",
        "read_run",
        "read_skill",
        "read_tool_output",
        "run_python",
        "search_files",
        "type_control",
        "update_plan",
        "view_artifact",
        "word_paragraphs",
        "write_text"
    ];

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-92 test directory.");
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

        await Test("MB-92 canonical registry replaces adapter metadata surface", () =>
        {
            var executor = FixtureExecutor("mb92-all");
            var registry = new ToolRegistry();
            NormalRuntimeToolRegistry.Populate(registry, executor);

            var names = registry.Tools
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Check(names.SequenceEqual(ExpectedTools.OrderBy(x => x, StringComparer.Ordinal)),
                "Canonical registry tool surface changed while retiring the v1 adapter.");
            Check(registry.Tools.All(x => x.Executor.ExecutorId == executor.ExecutorId),
                "Canonical descriptor population did not preserve the supplied executor.");

            var python = Get(registry, "run_python");
            Check(python.SchemaVersion == "v2"
                  && python.Access == AgentToolAccess.Mutating
                  && python.Risk == AgentToolRisk.Medium
                  && python.ResourceScope?.ScopeId == "python.sandbox"
                  && python.Preference?.InteractionFidelity == ToolInteractionFidelity.EscapeHatch
                  && python.Preference.ExplicitRequestOnly,
                "run_python canonical metadata regressed.");

            var read = Get(registry, "read_file");
            Check(read.CanProvideVerificationEvidence
                  && read.Preference?.InteractionFidelity == ToolInteractionFidelity.Structured,
                "read_file canonical evidence/preference metadata regressed.");

            var inspectWindow = Get(registry, "inspect_window");
            Check(inspectWindow.CanProvideVerificationEvidence
                  && inspectWindow.Preference?.InteractionFidelity == ToolInteractionFidelity.Accessibility,
                "inspect_window canonical evidence/preference metadata regressed.");

            var write = Get(registry, "write_text");
            var publish = Get(registry, "publish_artifact");
            Check(write.ResourceScope?.ScopeId == "workspace.write"
                  && publish.ResourceScope?.ScopeId == "workspace.write",
                "Canonical alternate workspace mutations no longer share one host scope.");
            return Task.CompletedTask;
        });

        await Test("MB-92 first-party namespace reuse needs no legacy adapter", () =>
        {
            var executor = FixtureExecutor("mb92-python");
            var registry = new ToolRegistry();
            NormalRuntimeToolRegistry.PopulateNamespace(
                registry,
                "python",
                executor,
                new ToolProvenance(
                    "firstparty.python-sandbox",
                    "1.2.3",
                    "windows-appcontainer",
                    "2.0.0"));

            var python = registry.GetNamespace("python");
            Check(python.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(new[]
                    {
                        "inspect_artifact",
                        "publish_artifact",
                        "read_run",
                        "run_python",
                        "view_artifact"
                    }),
                "Canonical Python namespace surface changed during adapter retirement.");
            Check(python.All(x =>
                    x.Executor.ExecutorId == executor.ExecutorId
                    && x.Provenance?.ProviderId == "firstparty.python-sandbox"
                    && x.Provenance.ProviderVersion == "1.2.3"),
                "Canonical namespace registration did not preserve extension executor/provenance.");
            Check(registry.Tools.All(x => x.Namespace.Name == "python"),
                "PopulateNamespace leaked unrelated normal-runtime tools.");
            return Task.CompletedTask;
        });

        await Test("MB-92 repository has no CSharp dependency on retired adapter", () =>
        {
            var repo = FindRepoRoot();
            var adapterPath = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Tools",
                "V1ToolRegistryAdapter.cs");
            Check(!File.Exists(adapterPath),
                "Retired V1ToolRegistryAdapter.cs still exists.");

            var self = Path.GetFullPath(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Runtime",
                "MbRetireV1ToolRegistryAdapterTests.cs"));

            foreach (var path in Directory.EnumerateFiles(
                         Path.Combine(repo, "experiments", "H2AgentLab"),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(path).Equals(self, StringComparison.OrdinalIgnoreCase))
                    continue;
                var source = File.ReadAllText(path);
                foreach (var forbidden in new[]
                {
                    "V1ToolRegistryAdapter.Populate",
                    "V1ToolRegistryAdapter.Create",
                    "new V1AgentToolsExecutor",
                    "V1AgentToolsExecutor("
                })
                    Check(!source.Contains(forbidden, StringComparison.Ordinal),
                        "C# source still depends on retired adapter symbol '" + forbidden + "': " + path);
            }

            var pythonCard = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "FirstPartyExtensions",
                "Python",
                "PythonSandboxFirstPartyExtension.cs"));
            Check(pythonCard.Contains(
                    "NormalRuntimeToolRegistry.PopulateNamespace",
                    StringComparison.Ordinal),
                "Python first-party card is not using the canonical normal-runtime descriptor source.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-retire-v1-tool-registry-adapter-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolDescriptor Get(ToolRegistry registry, string name)
        => registry.TryGet(name, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException("Missing canonical descriptor: " + name);

    private static IAgentToolExecutor FixtureExecutor(string id)
        => new DelegatingToolExecutor(
            id,
            (call, ct) => ValueTask.FromResult("{}"));

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
