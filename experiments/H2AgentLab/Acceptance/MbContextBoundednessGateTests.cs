using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Acceptance;

public static class MbContextBoundednessGateTests
{
    private sealed record Measurement(
        string Case,
        int HistoricalTurns,
        int ActiveContextCharacters,
        int RequestBytes,
        long CandidateContextCharacters,
        int SessionEvents);

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-102 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var measurements = new List<Measurement>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
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

        await Test("MB-102 normal UI keeps 10 100 1000 turn requests bounded and non-linear", async () =>
        {
            var ten = await RunUiAsync(root, 10, null).ConfigureAwait(false);
            var hundred = await RunUiAsync(root, 100, null).ConfigureAwait(false);
            var thousand = await RunUiAsync(root, 1000, null).ConfigureAwait(false);

            foreach (var sample in new[] { ten, hundred, thousand })
            {
                var requestBytes = RequestBytes(sample.Request);
                measurements.Add(new Measurement(
                    "history-" + sample.HistoricalTurns,
                    sample.HistoricalTurns,
                    sample.Snapshot.ActiveContextCharacters,
                    requestBytes,
                    sample.Snapshot.Diagnostics.CandidateContextCharacters,
                    sample.Session.Events.Count));

                Check(sample.Snapshot.State == AgentTaskState.Completed,
                    "Normal UI bounded-context fixture did not complete.");
                Check(sample.Snapshot.ActiveContextCharacters <= new AgentContextBudget().MaxTotalCharacters,
                    "Active context exceeded AgentContextBudget.");
                Check(sample.Request.Messages.Sum(x => x.Content.Length) <= 30_000,
                    "Provider request message envelope exceeded bounded expectation.");
                Check(requestBytes <= 40_000,
                    "Serialized provider request exceeded bounded byte envelope.");
                Check(sample.Request.Messages.Any(x =>
                        x.Role == AgentTransportMessageRole.User
                        && x.Content == CurrentTask),
                    "Current task was lost from provider request.");
            }

            var combinedThousand = string.Join(
                "\n",
                thousand.Request.Messages.Select(x => x.Content));
            Check(combinedThousand.Contains("HISTORY-0999", StringComparison.Ordinal),
                "Latest historical turn was not retained at 1000 turns.");
            Check(!combinedThousand.Contains("HISTORY-0000", StringComparison.Ordinal),
                "Oldest raw historical turn leaked into 1000-turn prompt.");

            var tenBytes = RequestBytes(ten.Request);
            var hundredBytes = RequestBytes(hundred.Request);
            var thousandBytes = RequestBytes(thousand.Request);
            Check(hundredBytes <= tenBytes + 2_500,
                "Request bytes grew materially from 10 to 100 turns.");
            Check(thousandBytes <= hundredBytes + 2_500,
                "Request bytes grew materially from 100 to 1000 turns.");
            Check(thousand.Snapshot.Diagnostics.CandidateContextCharacters
                    > hundred.Snapshot.Diagnostics.CandidateContextCharacters
                && hundred.Snapshot.Diagnostics.CandidateContextCharacters
                    > ten.Snapshot.Diagnostics.CandidateContextCharacters,
                "Candidate history did not grow while active request stayed bounded.");
        });

        await Test("MB-102 repeated large tool outputs stay durable outside prompt as artifact handles", async () =>
        {
            var caseRoot = Path.Combine(root, "large-tool-artifacts");
            var workspace = Path.Combine(caseRoot, "workspace");
            var stateRoot = Path.Combine(caseRoot, "state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(stateRoot);

            var session = new global::H2AgentLab.LabSession { Workspace = workspace };
            for (var i = 0; i < 120; i++)
            {
                session.Add(
                    i % 2 == 0 ? "user" : "assistant",
                    $"HISTORY-LARGE-{i:D4} " + new string((char)('a' + i % 20), 500));
            }

            var store = new ArtifactStore(stateRoot);
            var projector = new AgentRuntimeEvidenceProjector(store);
            var descriptor = LargeOutputDescriptor();
            var handles = new List<AgentArtifactHandle>();
            long rawBytes = 0;

            for (var i = 0; i < 16; i++)
            {
                var raw = $"RAW-LARGE-{i:D2}-BEGIN\n"
                    + new string((char)('A' + i % 20), 24_000)
                    + $"\nRAW-LARGE-{i:D2}-END";
                rawBytes += Encoding.UTF8.GetByteCount(raw);

                var projection = projector.Project(
                    descriptor,
                    "mb102-large-" + i,
                    i,
                    raw)
                    ?? throw new InvalidOperationException(
                        "Large tool output was not projected to durable evidence.");

                var handle = store.LoadHandle(projection.Evidence.ReferenceId);
                handles.Add(handle);
                Check(store.ReadText(handle.Id) == raw,
                    "Durable ArtifactStore did not preserve exact raw large tool output.");
                Check(projection.ModelContent.Length <= ArtifactStore.MaxContextHandleCharacters,
                    "Large tool model projection exceeded ArtifactStore bounded handle size.");
                Check(!projection.ModelContent.Contains(
                        $"RAW-LARGE-{i:D2}-END",
                        StringComparison.Ordinal),
                    "Large raw tool tail leaked into model projection.");

                session.Add("tool-result", projection.ModelContent);
            }

            var sample = await RunUiAsync(
                root,
                0,
                new SeededSession(caseRoot, stateRoot, session))
                .ConfigureAwait(false);
            var combined = string.Join(
                "\n",
                sample.Request.Messages.Select(x => x.Content));
            var requestBytes = RequestBytes(sample.Request);

            measurements.Add(new Measurement(
                "large-tool-artifacts",
                120,
                sample.Snapshot.ActiveContextCharacters,
                requestBytes,
                sample.Snapshot.Diagnostics.CandidateContextCharacters,
                sample.Session.Events.Count));

            Check(sample.Snapshot.ActiveContextCharacters <= new AgentContextBudget().MaxTotalCharacters,
                "Repeated artifact projections exceeded active context budget.");
            Check(requestBytes <= 40_000,
                "Repeated artifact projections caused unbounded provider request bytes.");
            Check(rawBytes > 300_000,
                "Large-output fixture did not create enough raw evidence pressure.");
            Check(requestBytes * 8L < rawBytes,
                "Provider request is too large relative to retained raw evidence.");
            Check(!combined.Contains("RAW-LARGE-15-END", StringComparison.Ordinal),
                "Raw large tool output leaked into active provider request.");
            Check(combined.Contains(handles[^1].Id, StringComparison.Ordinal),
                "Latest durable artifact handle was not available in bounded tool context.");
            Check(handles.All(x => File.Exists(Path.Combine(
                    stateRoot,
                    "artifacts",
                    "context",
                    x.Id + ".txt"))),
                "One or more raw artifact payloads were not retained outside the prompt.");
        });

        await Test("MB-102 repository guard keeps bounded context compaction and evidence outside prompt wired to normal UI", () =>
        {
            var repo = FindRepoRoot();
            var facade = Read(repo, "Tasking", "AgentOrchestratedRun.cs");
            var adapter = Read(repo, "Context", "LabSessionContextAdapter.cs");
            var runtime = Read(repo, "Runtime", "AgentRuntime.cs");
            var factory = Read(repo, "Runtime", "AgentRuntimeFactory.cs");
            var context = Read(repo, "Context", "AgentContextManager.cs");

            Check(facade.Contains("LabSessionContextAdapter", StringComparison.Ordinal)
                  && facade.Contains("RuntimeCompactionCoordinator", StringComparison.Ordinal)
                  && !facade.Contains("labSession.Context(", StringComparison.Ordinal),
                "Normal UI facade does not use bounded V2 context/compaction exclusively.");
            Check(!adapter.Contains(".Context()", StringComparison.Ordinal)
                  && adapter.Contains("DeriveToolSummaries", StringComparison.Ordinal),
                "Session adapter replays legacy context instead of bounded turns/tool summaries.");
            Check(runtime.Contains("_contextManager.Build(request.Context)", StringComparison.Ordinal),
                "AgentRuntime does not rebuild bounded request context.");
            Check(factory.Contains("AgentRuntimeEvidenceProjector", StringComparison.Ordinal)
                  && factory.Contains("new ArtifactStore(tools.StateRoot)", StringComparison.Ordinal),
                "Normal runtime does not retain raw evidence in ArtifactStore.");
            Check(context.Contains("MaxTotalCharacters { get; init; } = 24_000", StringComparison.Ordinal)
                  && context.Contains("MaxRecentTurns { get; init; } = 8", StringComparison.Ordinal)
                  && context.Contains("MaxToolSummaries { get; init; } = 8", StringComparison.Ordinal),
                "Canonical context hard bounds changed without updating MB-102 gate.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-context-boundedness-gate-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-context-boundedness-gate-measurements.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    budget = new AgentContextBudget(),
                    measurements,
                    passed = lines.Count(x => x.StartsWith("PASS ", StringComparison.Ordinal)),
                    failed
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        foreach (var item in measurements)
        {
            Console.WriteLine(
                $"MEASURE {item.Case} turns={item.HistoricalTurns} "
                + $"activeChars={item.ActiveContextCharacters} requestBytes={item.RequestBytes} "
                + $"candidateChars={item.CandidateContextCharacters} sessionEvents={item.SessionEvents}");
        }

        return failed == 0 ? 0 : 1;
    }

    private const string CurrentTask = "Return the deterministic MB-102 bounded-context answer.";

    private static async Task<UiSample> RunUiAsync(
        string root,
        int historicalTurns,
        SeededSession? seeded)
    {
        var caseRoot = seeded?.CaseRoot
            ?? Path.Combine(root, "history-" + historicalTurns + "-" + Guid.NewGuid().ToString("N"));
        var workspace = seeded?.Session.Workspace
            ?? Path.Combine(caseRoot, "workspace");
        var stateRoot = seeded?.StateRoot
            ?? Path.Combine(caseRoot, "state");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(stateRoot);

        var session = seeded?.Session
            ?? new global::H2AgentLab.LabSession { Workspace = workspace };
        if (seeded is null)
        {
            for (var i = 0; i < historicalTurns; i++)
            {
                session.Add(
                    i % 2 == 0 ? "user" : "assistant",
                    $"HISTORY-{i:D4} " + new string((char)('a' + i % 20), 500));
            }
        }

        var factory = new CapturingRuntimeFactory();
        var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
        var facade = new AgentOrchestratedRun(orchestrator);
        using var tools = new global::H2AgentLab.AgentTools(
            new global::H2AgentLab.SafeWorkspace(workspace),
            stateRoot,
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            },
            (_, _) => { })
        {
            ReadOnly = true
        };

        var snapshot = await facade.RunAsync(
            Profile(),
            "",
            session,
            tools,
            CurrentTask,
            (_, _) => { },
            () => { },
            new AgentRunTelemetry(),
            readOnly: true,
            CancellationToken.None).ConfigureAwait(false);

        return new UiSample(
            historicalTurns,
            snapshot,
            factory.LastRequest
                ?? throw new InvalidOperationException(
                    "MB-102 transport start request was not captured."),
            session);
    }

    private static int RequestBytes(AgentTransportStartRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages = request.Messages.Select(x => new
            {
                role = x.Role.ToString(),
                x.Content
            }),
            tools = request.Tools.Select(x => new
            {
                x.Name,
                x.Description,
                x.Parameters,
                x.SupportsParallelExecution
            }),
            request.PromptCacheKey
        }).Length;

    private static AiProfile Profile()
        => new()
        {
            Protocol = AiProtocol.Ollama,
            BaseUrl = "http://localhost:11434",
            Model = "mb102-fixture"
        };

    private static ToolDescriptor LargeOutputDescriptor()
    {
        const string name = "fixture.large_read";
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description = "Return deterministic large MB-102 fixture output.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            }
        });

        return new ToolDescriptor(
            name,
            new ToolNamespace("fixture", "MB-102 bounded context fixture."),
            "Return deterministic large MB-102 fixture output.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema: schema,
            executor: new DelegatingToolExecutor(
                "mb102-large",
                (call, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult("{}");
                }),
            provenance: new ToolProvenance(
                "mb102-fixture",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: null,
            serializationKey: "fixture",
            canProvideVerificationEvidence: true);
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
            yield return AgentTransportEvent.TextDeltaEvent("mb102-bounded-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb102", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException(
                "MB-102 direct bounded-context fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record UiSample(
        int HistoricalTurns,
        AgentInspectionSnapshot Snapshot,
        AgentTransportStartRequest Request,
        global::H2AgentLab.LabSession Session);

    private sealed record SeededSession(
        string CaseRoot,
        string StateRoot,
        global::H2AgentLab.LabSession Session);

    private static string Read(string repoRoot, params string[] relative)
        => File.ReadAllText(
            Path.Combine(
                new[] { repoRoot, "experiments", "H2AgentLab" }
                    .Concat(relative)
                    .ToArray()));

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
