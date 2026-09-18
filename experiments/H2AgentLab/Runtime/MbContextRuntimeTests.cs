using System.Runtime.CompilerServices;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public static class MbContextRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-20 test directory.");
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

        await Test("MB-20 normal runtime keeps 10 100 1000 turn histories bounded and recent", async () =>
        {
            var ten = await RunHistoryAsync(root, 10);
            var hundred = await RunHistoryAsync(root, 100);
            var thousand = await RunHistoryAsync(root, 1000);

            foreach (var sample in new[] { ten, hundred, thousand })
            {
                Check(sample.Inspection.ActiveContextCharacters <= new AgentContextBudget().MaxTotalCharacters,
                    "Normal runtime exceeded AgentContextManager total budget.");
                Check(sample.Request.Messages.Sum(x => x.Content.Length) <= 30_000,
                    "Transport start request exceeded bounded prompt envelope.");
                Check(sample.Request.Messages.Any(x =>
                        x.Content.Contains("workspace=", StringComparison.Ordinal)
                        && x.Content.Contains("route=", StringComparison.Ordinal)),
                    "Current workspace/runtime state was lost from bounded prompt.");
                Check(sample.Request.Messages.Any(x =>
                        x.Role == AgentTransportMessageRole.User
                        && x.Content == "current user task"),
                    "Current user task was lost from transport request.");
            }

            Check(thousand.Request.Messages.Any(x =>
                    x.Content.Contains("HISTORY-0999", StringComparison.Ordinal)),
                "Latest historical turn was not retained.");
            Check(!thousand.Request.Messages.Any(x =>
                    x.Content.Contains("HISTORY-0000", StringComparison.Ordinal)),
                "Oldest raw historical turn was replayed into 1000-turn prompt.");

            var hundredChars = hundred.Request.Messages.Sum(x => x.Content.Length);
            var thousandChars = thousand.Request.Messages.Sum(x => x.Content.Length);
            Check(thousandChars <= hundredChars + 1_500,
                "Prompt grew materially from 100 to 1000 turns instead of staying bounded.");
        });

        await Test("MB-20 tool journal becomes bounded tool summary rather than conversation replay", async () =>
        {
            var sample = await RunHistoryAsync(root, 80, includeLargeToolEvent: true);
            var combined = string.Join("
", sample.Request.Messages.Select(x => x.Content));

            Check(combined.Contains("TOOL-SUMMARY-LATEST", StringComparison.Ordinal),
                "Latest tool journal evidence was not projected as bounded tool context.");
            Check(!combined.Contains(new string('Z', 3_500), StringComparison.Ordinal),
                "Large raw tool output was replayed unbounded into model prompt.");

            var adapter = new LabSessionContextAdapter();
            var session = new global::H2AgentLab.LabSession();
            session.Add("tool-result", "TOOL-ONLY");
            var snapshot = adapter.Build(session);
            Check(snapshot.Usage.SelectedRecentTurns == 0
                && snapshot.Usage.SelectedToolSummaries == 1,
                "Tool journal event was incorrectly treated as a conversation turn.");
        });

        await Test("MB-20 source guard removes LabSession.Context from normal model request path", () =>
        {
            var repo = FindRepoRoot();
            var facade = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));
            var adapter = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Context", "LabSessionContextAdapter.cs"));
            var runtime = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Runtime", "AgentRuntime.cs"));

            Check(!facade.Contains("labSession.Context(", StringComparison.Ordinal)
                && !facade.Contains("_session.Context(", StringComparison.Ordinal),
                "Normal AgentOrchestratedRun still uses legacy LabSession.Context().");
            Check(!adapter.Contains(".Context()", StringComparison.Ordinal),
                "V2 session adapter calls legacy LabSession.Context().");
            Check(runtime.Contains("_contextManager.Build(request.Context)", StringComparison.Ordinal),
                "AgentRuntime does not build bounded context before model start.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-context-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task<RunSample> RunHistoryAsync(
        string root,
        int historyCount,
        bool includeLargeToolEvent = false)
    {
        var caseRoot = Path.Combine(
            root,
            "history-" + historyCount + "-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(caseRoot, "workspace");
        var state = Path.Combine(caseRoot, "state");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(state);

        var session = new global::H2AgentLab.LabSession
        {
            Workspace = workspace
        };

        for (var i = 0; i < historyCount; i++)
        {
            session.Add(
                i % 2 == 0 ? "user" : "assistant",
                $"HISTORY-{i:D4} " + new string((char)('a' + (i % 20)), 500));
        }

        if (includeLargeToolEvent)
        {
            session.Add(
                "tool-result",
                "TOOL-SUMMARY-LATEST " + new string('Z', 5_000));
        }

        var factory = new CapturingRuntimeFactory();
        var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
        var tools = new global::H2AgentLab.AgentTools(
            new global::H2AgentLab.SafeWorkspace(workspace),
            state,
            (_, _) => Task.FromResult(true),
            (_, _) => { });
        var run = new AgentOrchestratedRun(orchestrator);
        var inspection = await run.RunAsync(
            new AiProfile
            {
                Protocol = AiProtocol.Ollama,
                BaseUrl = "http://localhost:11434",
                Model = "fixture"
            },
            "",
            session,
            tools,
            "current user task",
            (_, _) => { },
            () => { },
            new AgentRunTelemetry(),
            readOnly: true,
            CancellationToken.None);

        return new RunSample(
            inspection,
            factory.LastRequest
                ?? throw new InvalidOperationException("Transport start request was not captured."));
    }

    private sealed record RunSample(
        AgentInspectionSnapshot Inspection,
        AgentTransportStartRequest Request);

    private sealed class CapturingRuntimeFactory : IAgentRuntimeFactory
    {
        public AgentTransportStartRequest? LastRequest { get; private set; }

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            global::H2AgentLab.AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
            => new(
                new CapturingTransport(request => LastRequest = request),
                contextManager,
                new ToolRegistry());
    }

    private sealed class CapturingTransport : IAgentTransport
    {
        private readonly Action<AgentTransportStartRequest> _capture;

        public CapturingTransport(Action<AgentTransportStartRequest> capture)
        {
            _capture = capture;
        }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _capture(request);
            yield return AgentTransportEvent.TextDeltaEvent("context-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("context", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Context fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
