using System.Runtime.CompilerServices;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public static class MbOrchestratorRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-11 test directory.");
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

        await Test("MB-11 normal AgentOrchestratedRun executes through the AgentRuntime-only production path", async () =>
        {
            var factory = new FixtureRuntimeFactory("Runtime final");
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);

            var workspace = Path.Combine(root, "read-only-workspace");
            var state = Path.Combine(root, "read-only-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);
            var session = new global::H2AgentLab.LabSession { Workspace = workspace };
            var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });
            var output = new List<(string Kind, string Text)>();
            var telemetry = new AgentRunTelemetry();

            var run = new AgentOrchestratedRun(orchestrator);
            var result = await run.RunAsync(
                new AiProfile
                {
                    Protocol = AiProtocol.Ollama,
                    BaseUrl = "http://localhost:11434",
                    Model = "fixture"
                },
                "",
                session,
                tools,
                "Give a direct fixture answer.",
                (kind, text) => output.Add((kind, text)),
                () => { },
                telemetry,
                readOnly: true,
                CancellationToken.None);

            Check(factory.CreateCount == 1,
                "Normal run did not construct exactly one AgentRuntime.");
            Check(result.State == AgentTaskState.Completed,
                "Read-only AgentRuntime task did not reach host Completed state.");
            Check(output.Any(x => x.Kind == "final" && x.Text == "Runtime final"),
                "AgentRuntime final text did not reach the UI facade.");
            Check(session.Events.Any(x => x.Kind == "user" && x.Text.Contains("fixture", StringComparison.OrdinalIgnoreCase))
                && session.Events.Any(x => x.Kind == "assistant" && x.Text == "Runtime final"),
                "Normal runtime did not persist user/assistant journal events.");
        });

        await Test("MB-11 mutating normal runtime remains blocked without deterministic verifier evidence", async () =>
        {
            var factory = new FixtureRuntimeFactory("Proposed mutation final");
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var workspace = Path.Combine(root, "mutation-workspace");
            var state = Path.Combine(root, "mutation-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);
            var session = new global::H2AgentLab.LabSession { Workspace = workspace };
            var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });

            var run = new AgentOrchestratedRun(orchestrator);
            var result = await run.RunAsync(
                new AiProfile
                {
                    Protocol = AiProtocol.Ollama,
                    BaseUrl = "http://localhost:11434",
                    Model = "fixture"
                },
                "",
                session,
                tools,
                "Change the fixture.",
                (_, _) => { },
                () => { },
                new AgentRunTelemetry(),
                readOnly: false,
                CancellationToken.None);

            Check(result.State == AgentTaskState.Blocked,
                "Mutating runtime was marked complete without verifier evidence.");
        });

        await Test("MB-11 source guard prevents normal UI facade from returning to AgentRunner", () =>
        {
            var repo = FindRepoRoot();
            var facade = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));
            var window = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "LabWindow.cs"));
            var orchestrator = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestrator.cs"));

            Check(facade.Contains("RunRuntimeAsync", StringComparison.Ordinal)
                && facade.Contains("CreateRuntime", StringComparison.Ordinal),
                "AgentOrchestratedRun is not wired to AgentRuntime.");
            Check(!facade.Contains("AgentRunner", StringComparison.Ordinal)
                && !facade.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "Normal AgentOrchestratedRun source references legacy execution.");
            Check(!window.Contains("AgentRunner", StringComparison.Ordinal)
                && !window.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "LabWindow normal path references legacy execution.");
            Check(!orchestrator.Contains("AgentRunner", StringComparison.Ordinal)
                && !orchestrator.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "Production AgentOrchestrator still exposes a legacy runner seam.");
            Check(orchestrator.Contains("RunRuntimeAsync", StringComparison.Ordinal),
                "AgentOrchestrator does not coordinate AgentRuntime lifecycle.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-orchestrator-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private sealed class FixtureRuntimeFactory : IAgentRuntimeFactory
    {
        private readonly string _final;

        public FixtureRuntimeFactory(string final)
        {
            _final = final;
        }

        public int CreateCount { get; private set; }

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            global::H2AgentLab.AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
        {
            CreateCount++;
            return new AgentRuntime(
                new DirectFinalTransport(_final),
                contextManager,
                new ToolRegistry());
        }
    }

    private sealed class DirectFinalTransport : IAgentTransport
    {
        private readonly string _final;

        public DirectFinalTransport(string final)
        {
            _final = final;
        }

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Count == 0)
                throw new InvalidOperationException("Runtime start request has no prompt messages.");
            yield return AgentTransportEvent.Started("mb11");
            yield return AgentTransportEvent.TextDeltaEvent(_final);
            yield return AgentTransportEvent.Meter(new(
                InputTokens: 11,
                OutputTokens: 3,
                TotalTokens: 14));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb11", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException("Direct final fixture should not continue.");
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
