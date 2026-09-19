using H2AgentLab.Tools;

namespace H2AgentLab.Acceptance;

public static class MbFinalArchitectureReportTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-120 test directory.");
        Directory.CreateDirectory(root);

        var repo = FindRepoRoot();
        var reportPath = Path.Combine(repo, "docs", "H2_AGENT_FINAL_ARCHITECTURE_REPORT.md");
        var report = File.ReadAllText(reportPath);
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

        await Test("MB-120 report contains every required architecture section", () =>
        {
            foreach (var required in new[]
            {
                "## 2. Final real runtime call graph",
                "## 3. Core versus extensions",
                "## 4. Legacy removed or retained",
                "## 5. Context metrics",
                "## 6. Tool/schema metrics",
                "## 7. Correctness and completion safety",
                "## 8. Verification failure rate",
                "## 9. Model transport/provider matrix",
                "## 11. Known limitations",
                "## 12. Integration-readiness conclusion"
            })
                Check(report.Contains(required, StringComparison.Ordinal),
                    "Final architecture report is missing required section: " + required);

            foreach (var placeholder in new[] { "TBD", "TODO", "FIXME", "<fill" })
                Check(!report.Contains(placeholder, StringComparison.OrdinalIgnoreCase),
                    "Final architecture report contains unresolved placeholder: " + placeholder);
            return Task.CompletedTask;
        });

        await Test("MB-120 report freezes exact accepted CI and correctness baseline", () =>
        {
            foreach (var required in new[]
            {
                "a075c65b223ca312fc3a5a2cfe398e59824a015a",
                "35446625544",
                "59e3a72b7231f04a904f957db15d20db34d25918",
                "43cf43f842a557c0b78c9fc74ef5a7f0ad08d58cfdb2e2251248434145c5cac9",
                "336 passed, 0 failed",
                "12/12 passed",
                "14/14 = 100.00%",
                "10/10 passed",
                "boundary violations: **0**",
                "0/2 = 0%",
                "1/2 = 50%"
            })
                Check(report.Contains(required, StringComparison.OrdinalIgnoreCase),
                    "Final architecture report lost accepted baseline evidence: " + required);
            return Task.CompletedTask;
        });

        await Test("MB-120 report metrics match canonical context and tool runtime", () =>
        {
            var contextSource = File.ReadAllText(Path.Combine(
                repo, "experiments", "H2AgentLab", "Context", "AgentContextManager.cs"));
            Check(contextSource.Contains("MaxTotalCharacters { get; init; } = 24_000", StringComparison.Ordinal)
                  && contextSource.Contains("MaxRecentTurns { get; init; } = 8", StringComparison.Ordinal)
                  && contextSource.Contains("MaxToolSummaries { get; init; } = 8", StringComparison.Ordinal),
                "Canonical context bounds changed.");

            foreach (var metric in new[]
            {
                "| history-10 | 10 | 4,872 | 6,091 | 5,331 | 12 |",
                "| history-100 | 100 | 5,821 | 7,053 | 13,431 | 102 |",
                "| history-1000 | 1,000 | 5,837 | 7,069 | 13,439 | 1,002 |",
                "| large-tool-artifacts | 120 | 9,858 | 11,139 | 16,146 | 138 |"
            })
                Check(report.Contains(metric, StringComparison.Ordinal),
                    "Final report lost MB-102 measured context row: " + metric);

            var workspace = Path.Combine(root, "registry-workspace");
            var state = Path.Combine(root, "registry-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);
            using var host = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                (_, _) => { });

            var registry = NormalRuntimeToolRegistry.Create(host);
            Check(registry.Tools.Count == 19,
                "Normal built-in ToolRegistry descriptor count changed from 19.");

            var exposure = new DeferredToolDiscovery(registry).BuildInitialExposure();
            var names = exposure.CallableSchemas
                .Select(DeferredToolDiscovery.SchemaName)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            Check(names.SequenceEqual(new[] { "tool_search", "update_plan" }),
                "Normal initial schema surface is no longer tool_search + update_plan.");

            Check(report.Contains("19 callable descriptors", StringComparison.Ordinal)
                  && report.Contains("schema count is **2**", StringComparison.Ordinal)
                  && report.Contains("less than half", StringComparison.Ordinal),
                "Final report tool/schema metrics do not match canonical runtime.");
            return Task.CompletedTask;
        });

        await Test("MB-120 report matches provider matrix and legacy boundaries", () =>
        {
            var transport = File.ReadAllText(Path.Combine(
                repo, "experiments", "H2AgentLab", "Transport", "AgentTransportFactory.cs"));
            Check(transport.Contains("AiProtocol.Ollama", StringComparison.Ordinal)
                  && transport.Contains("AiProtocol.OpenAiChat", StringComparison.Ordinal)
                  && transport.Contains("AiProtocol.OpenAiResponses", StringComparison.Ordinal)
                  && transport.Contains("OpenAiResponsesWebSocketTransport", StringComparison.Ordinal)
                  && transport.Contains("AiProtocol.Gemini => throw new NotSupportedException", StringComparison.Ordinal),
                "Provider transport matrix source changed.");

            foreach (var provider in new[]
            {
                "Ollama native",
                "OpenAI-compatible Chat Completions",
                "OpenAI Responses HTTP/SSE",
                "OpenAI Responses WebSocket",
                "Gemini Agent tool transport"
            })
                Check(report.Contains(provider, StringComparison.Ordinal),
                    "Final report provider matrix is missing: " + provider);

            foreach (var removed in new[]
            {
                Path.Combine("experiments", "H2AgentLab", "Tools", "V1ToolRegistryAdapter.cs"),
                Path.Combine("experiments", "H2AgentLab", "SkillCatalog.cs"),
                Path.Combine("experiments", "H2AgentLab", "Tools", "DeferredSkillSession.cs"),
                Path.Combine("experiments", "H2AgentLab", "Computer" + "Tools.cs"),
                Path.Combine("experiments", "H2AgentLab", "Capabilities", "MissingCapabilityContinuation.cs")
            })
                Check(!File.Exists(Path.Combine(repo, removed)),
                    "Report says legacy path is retired but file still exists: " + removed);

            var facade = File.ReadAllText(Path.Combine(
                repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));
            Check(!facade.Contains("AgentRunner", StringComparison.Ordinal),
                "Normal UI facade regained AgentRunner dependency.");
            Check(File.Exists(Path.Combine(repo, "experiments", "H2AgentLab", "AgentRunner.cs")),
                "Frozen AgentRunner baseline harness unexpectedly disappeared.");

            Check(report.Contains("No H2 Notes production integration yet.", StringComparison.Ordinal)
                  && report.Contains("production/live verification failure rate", StringComparison.OrdinalIgnoreCase),
                "Final report does not state integration/telemetry limitations.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var resultPath = Path.Combine(root, "mb-final-architecture-report-tests.txt");
        await File.WriteAllLinesAsync(resultPath, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
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
