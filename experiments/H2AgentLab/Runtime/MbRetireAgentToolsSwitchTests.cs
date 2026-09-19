using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

public static class MbRetireAgentToolsSwitchTests
{
    private static readonly string[] ExpectedTools =
    [
        "list_skills",
        "read_skill",
        "update_plan",
        "run_python",
        "inspect_artifact",
        "read_run",
        "view_artifact",
        "publish_artifact",
        "list_files",
        "find_files",
        "read_file",
        "search_files",
        "write_text",
        "open_file",
        "word_paragraphs",
        "check_word",
        "inspect_window",
        "click_control",
        "type_control"
    ];

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-91 test directory.");
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
                lines.Add(
                    "FAIL " + name + ": "
                    + ex.GetType().Name + ": "
                    + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        await Test("MB-91 normal ToolRegistry exposes the complete callable surface with domain executors", async () =>
        {
            var workspace = Path.Combine(root, "workspace");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            var approvals = 0;
            var journal = new List<(string Kind, string Text)>();
            var host = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) =>
                {
                    approvals++;
                    return Task.FromResult(true);
                },
                (kind, text) => journal.Add((kind, text)))
            {
                ReadOnly = false
            };

            var registry = NormalRuntimeToolRegistry.Create(host);
            var names = registry.Tools
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Check(
                names.SequenceEqual(
                    ExpectedTools.OrderBy(x => x, StringComparer.Ordinal),
                    StringComparer.Ordinal),
                "Normal runtime registry lost or added legacy callable names.");

            var executors = registry.Tools
                .Select(x => x.Executor.ExecutorId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Check(executors.Length == 6,
                "Expected six domain executors, got: " + string.Join(", ", executors));
            Check(executors.All(x => x.StartsWith("normal.", StringComparison.Ordinal)),
                "Normal registry still includes a legacy giant-switch executor.");

            var write = Get(registry, "write_text");
            var writeResult = await write.Executor.ExecuteAsync(
                Call("write_text", new
                {
                    path = "note.txt",
                    text = "MB91 registry path",
                    expectedHash = ""
                }),
                CancellationToken.None);
            using (var doc = JsonDocument.Parse(writeResult))
                Check(doc.RootElement.GetProperty("sha256").GetString()?.Length == 64,
                    "Dedicated file executor did not write through the approved workspace.");

            var read = Get(registry, "read_file");
            var readResult = await read.Executor.ExecuteAsync(
                Call("read_file", new
                {
                    path = "note.txt",
                    offset = "0"
                }),
                CancellationToken.None);
            using (var doc = JsonDocument.Parse(readResult))
                Check(doc.RootElement.GetProperty("content").GetString() == "MB91 registry path",
                    "Dedicated file executor did not read its own mutation.");

            var plan = Get(registry, "update_plan");
            _ = await plan.Executor.ExecuteAsync(
                Call("update_plan", new
                {
                    plan = "MB-91 domain executor evidence"
                }),
                CancellationToken.None);

            var skills = Get(registry, "list_skills");
            var skillResult = await skills.Executor.ExecuteAsync(
                Call("list_skills", new { }),
                CancellationToken.None);
            using (var doc = JsonDocument.Parse(skillResult))
                Check(doc.RootElement.TryGetProperty("skills", out _),
                    "Canonical skill executor was not used.");

            Check(approvals == 1,
                "File mutation approval count changed during direct executor acceptance.");
            Check(journal.Any(x => x.Kind == "plan"
                    && x.Text.Contains("MB-91", StringComparison.Ordinal)),
                "Plan executor did not preserve the host journal boundary.");
        });

        await Test("MB-91 normal registry schemas are independent of AgentTools.Definitions", () =>
        {
            var workspace = Path.Combine(root, "schema-workspace");
            var state = Path.Combine(root, "schema-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);
            var host = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });

            var registry = NormalRuntimeToolRegistry.Create(host);
            foreach (var name in ExpectedTools)
            {
                var descriptor = Get(registry, name);
                var function = descriptor.CallableSchema.GetProperty("function");
                Check(function.GetProperty("name").GetString() == name,
                    "Callable schema identity mismatch for " + name + ".");
            }

            var listSkills = Get(registry, "list_skills")
                .CallableSchema
                .GetProperty("function")
                .GetProperty("parameters");
            Check(listSkills.GetProperty("required").GetArrayLength() == 0,
                "list_skills optional-query contract regressed.");
            return Task.CompletedTask;
        });

        await Test("MB-91 source guard keeps normal runtime off AgentTools Definitions and Execute", () =>
        {
            var repo = FindRepoRoot();
            var runtimeFactory = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Runtime",
                    "AgentRuntimeFactory.cs"));
            var registrySource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Tools",
                    "NormalRuntimeToolRegistry.cs"));
            var window = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "LabWindow.cs"));
            var facade = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Tasking",
                    "AgentOrchestratedRun.cs"));

            Check(runtimeFactory.Contains(
                    "NormalRuntimeToolRegistry.Create(tools)",
                    StringComparison.Ordinal),
                "AgentRuntimeFactory does not construct the authoritative normal ToolRegistry.");

            foreach (var source in new[]
            {
                runtimeFactory,
                registrySource,
                window,
                facade
            })
            {
                Check(!source.Contains(
                        "AgentTools.Definitions",
                        StringComparison.Ordinal),
                    "Normal production source references AgentTools.Definitions.");
                Check(!source.Contains(
                        "V1ToolRegistryAdapter.Create",
                        StringComparison.Ordinal),
                    "Normal production source still creates the v1 registry adapter.");
                Check(!source.Contains(
                        ".Execute(call",
                        StringComparison.Ordinal),
                    "Normal production source routes a call through the giant AgentTools.Execute switch.");
            }

            Check(!registrySource.Contains(
                    "V1AgentToolsExecutor",
                    StringComparison.Ordinal),
                "Normal ToolRegistry still uses the v1 giant-switch executor.");
            return Task.CompletedTask;
        });

        lines.Add(
            $"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-retire-agent-tools-switch-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(
            string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolDescriptor Get(ToolRegistry registry, string name)
        => registry.TryGet(name, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException(
                "Missing normal runtime descriptor: " + name);

    private static global::H2AgentLab.ToolCall Call(
        string name,
        object arguments)
        => new(
            "mb91-" + name,
            name,
            JsonSerializer.SerializeToElement(arguments));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(
            Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(
                    Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }
}
