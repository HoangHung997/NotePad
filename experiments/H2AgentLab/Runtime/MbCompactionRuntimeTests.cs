using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public static class MbCompactionRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-21 test directory.");
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

        await Test("MB-21 pressure creates auditable checkpoint without modifying raw journal and reuses it", () =>
        {
            var caseRoot = Path.Combine(root, "checkpoint-reuse");
            var state = Path.Combine(caseRoot, "state");
            Directory.CreateDirectory(state);

            var session = BuildLongSession(140);
            session.Save(state);
            var sessionFile = Path.Combine(state, "session.json");
            var rawBefore = SHA256.HashData(File.ReadAllBytes(sessionFile));

            var manager = new AgentContextManager();
            var adapter = new LabSessionContextAdapter(manager);
            var input = adapter.BuildInput(
                session,
                taskContract: "continue long task",
                currentState: "workspace=fixture; route=ComplexAgent");
            var coordinator = new RuntimeCompactionCoordinator(state, manager);

            var first = coordinator.Prepare(session, input);
            Check(first.CreatedCheckpoint
                && !string.IsNullOrWhiteSpace(first.CheckpointId),
                "Context pressure did not create a runtime checkpoint.");
            Check(first.Snapshot.Usage.TotalCharacters <= first.Snapshot.Budget.MaxTotalCharacters,
                "Compacted active context exceeded context budget.");
            Check((first.Snapshot.RuntimeContext.WorkingState ?? "")
                .Contains(first.CheckpointId!, StringComparison.Ordinal),
                "Compacted checkpoint was not projected into active runtime context.");
            Check(!(first.Snapshot.RuntimeContext.WorkingState ?? "")
                .Contains("OLD-RAW-0000", StringComparison.Ordinal),
                "Old raw history leaked back into compacted active context.");

            var checkpointFiles = Directory.GetFiles(
                Path.Combine(state, "checkpoints"),
                "*.json");
            Check(checkpointFiles.Length == 1,
                "First runtime compaction should create exactly one checkpoint.");

            var second = coordinator.Prepare(session, input);
            Check(!second.CreatedCheckpoint
                && second.CheckpointId == first.CheckpointId,
                "Unchanged history did not reuse the existing checkpoint.");
            Check(Directory.GetFiles(Path.Combine(state, "checkpoints"), "*.json").Length == 1,
                "Repeated preparation created a duplicate checkpoint for unchanged history.");

            var rawAfter = SHA256.HashData(File.ReadAllBytes(sessionFile));
            Check(rawBefore.SequenceEqual(rawAfter),
                "Runtime compaction modified the durable raw session journal.");
            return Task.CompletedTask;
        });

        await Test("MB-21 growing history creates chained checkpoint with durable source references", () =>
        {
            var caseRoot = Path.Combine(root, "checkpoint-chain");
            var state = Path.Combine(caseRoot, "state");
            Directory.CreateDirectory(state);

            var session = BuildLongSession(100);
            var manager = new AgentContextManager();
            var adapter = new LabSessionContextAdapter(manager);
            var coordinator = new RuntimeCompactionCoordinator(state, manager);

            var first = coordinator.Prepare(
                session,
                adapter.BuildInput(session, taskContract: "long task", currentState: "state=1"));
            Check(first.CreatedCheckpoint && first.CheckpointId is not null,
                "Initial checkpoint was not created.");

            for (var i = 100; i < 180; i++)
                session.Add(i % 2 == 0 ? "user" : "assistant",
                    $"GROWING-{i:D4} " + new string('g', 600));

            var second = coordinator.Prepare(
                session,
                adapter.BuildInput(session, taskContract: "long task", currentState: "state=2"));
            Check(second.CreatedCheckpoint
                && second.CheckpointId is not null
                && second.CheckpointId != first.CheckpointId,
                "Growing history did not create a new chained checkpoint.");

            var loaded = new CompactionManager(state).Load(second.CheckpointId);
            Check(loaded.PreviousCheckpointId == first.CheckpointId,
                "New checkpoint did not retain its previous-checkpoint chain.");
            Check(loaded.Sources.Any(x =>
                    x.Kind == AgentCompactionSourceKind.Checkpoint
                    && x.SourceId == first.CheckpointId),
                "New checkpoint is missing durable reference to previous checkpoint.");
            Check(loaded.Sources.Any(x => x.Kind == AgentCompactionSourceKind.JournalEvent),
                "New checkpoint is missing durable journal source references.");
            return Task.CompletedTask;
        });

        await Test("MB-21 normal AgentOrchestratedRun automatically uses compacted context before model start", async () =>
        {
            var caseRoot = Path.Combine(root, "normal-runtime");
            var workspace = Path.Combine(caseRoot, "workspace");
            var state = Path.Combine(caseRoot, "state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            var session = BuildLongSession(220);
            session.Workspace = workspace;
            var factory = new CapturingRuntimeFactory();
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var run = new AgentOrchestratedRun(orchestrator);
            var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });

            var trace = new List<string>();
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
                "current compacted task",
                (kind, text) => trace.Add(kind + ":" + text),
                () => { },
                new AgentRunTelemetry(),
                readOnly: true,
                CancellationToken.None);

            var request = factory.LastRequest
                ?? throw new InvalidOperationException("Normal runtime request was not captured.");
            var combined = string.Join(
                ((char)10).ToString(),
                request.Messages.Select(x => x.Content));

            Check(combined.Contains("Compacted historical checkpoint", StringComparison.Ordinal),
                "Normal runtime request did not receive compacted historical checkpoint.");
            Check(combined.Contains("OLD-RAW-0219", StringComparison.Ordinal),
                "Recent historical state was lost after automatic compaction.");
            Check(!combined.Contains("OLD-RAW-0000", StringComparison.Ordinal),
                "Oldest raw history was replayed after automatic compaction.");
            Check(inspection.ProgressEvents.Any(x =>
                    x.Code == "context-compacted"
                    && x.Kind == AgentProgressEventKind.Evidence),
                "Normal runtime typed trace did not expose the compaction evidence event.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-compaction-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static global::H2AgentLab.LabSession BuildLongSession(int count)
    {
        var session = new global::H2AgentLab.LabSession();
        for (var i = 0; i < count; i++)
        {
            session.Add(
                i % 2 == 0 ? "user" : "assistant",
                $"OLD-RAW-{i:D4} " + new string((char)('a' + (i % 20)), 600));
        }
        return session;
    }

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
            yield return AgentTransportEvent.TextDeltaEvent("compaction-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("compaction", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Compaction fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
