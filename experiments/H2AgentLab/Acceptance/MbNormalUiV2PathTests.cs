using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Runtime;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;

namespace H2AgentLab.Acceptance;

public static class MbNormalUiV2PathTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-101 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
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

        await Test("MB-101 normal read-only UI facade creates AgentRuntime over IAgentTransport and bounded V2 context", async () =>
        {
            var workspace = Path.Combine(root, "read-only-workspace");
            var stateRoot = Path.Combine(root, "read-only-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(stateRoot);

            var factory = UiRuntimeFactory.Direct();
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var facade = new AgentOrchestratedRun(orchestrator);
            var session = new global::H2AgentLab.LabSession { Workspace = workspace };
            for (var i = 0; i < 120; i++)
            {
                session.Add("user", "historical-user-" + i + " " + new string('u', 220));
                session.Add("assistant", "historical-assistant-" + i + " " + new string('a', 220));
            }

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

            var output = new List<(string Kind, string Text)>();
            var saves = 0;
            var snapshot = await facade.RunAsync(
                Profile(),
                "",
                session,
                tools,
                "Return the deterministic MB-101 direct UI answer.",
                (kind, text) => output.Add((kind, text)),
                () => saves++,
                new AgentRunTelemetry(),
                readOnly: true,
                CancellationToken.None).ConfigureAwait(false);

            Check(factory.CreateCount == 1
                  && ReferenceEquals(factory.ContextManagerSeen, orchestrator.ContextManager),
                "Normal UI facade did not create exactly one AgentRuntime with the orchestrator context manager.");
            Check(factory.DirectTransport is not null
                  && factory.DirectTransport.StartCount == 1
                  && factory.DirectTransport.InitialSurfaceWasDeferred,
                "Normal UI run did not enter IAgentTransport with deferred tool_search-only exposure.");
            Check(snapshot.State == AgentTaskState.Completed
                  && snapshot.ActiveContextCharacters > 0
                  && snapshot.ActiveContextCharacters < 60_000,
                "Normal UI run did not complete through bounded V2 context.");
            Check(output.Any(x => x.Kind == "status"
                    && x.Text.Contains("AgentRuntime V2", StringComparison.Ordinal))
                  && output.Any(x => x.Kind == "final"
                    && x.Text == "ui-v2-direct-ok"),
                "UI observable output did not come from the AgentRuntime V2 path.");
            Check(snapshot.ProgressEvents.Any(x => x.Code == "planned")
                  && snapshot.ProgressEvents.Any(x => x.Code == "runtime-state")
                  && snapshot.ProgressEvents.Any(x => x.Code == "runtime-final"),
                "UI progress did not expose the V2 orchestration/runtime lifecycle.");
            Check(saves >= 2,
                "Normal UI facade did not persist user/final durable state.");
        });

        await Test("MB-101 normal mutating UI facade performs deferred loading verifier repair and host-owned completion", async () =>
        {
            var workspace = Path.Combine(root, "repair-workspace");
            var stateRoot = Path.Combine(root, "repair-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(stateRoot);

            var factory = UiRuntimeFactory.Repair();
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var facade = new AgentOrchestratedRun(orchestrator);
            var session = new global::H2AgentLab.LabSession { Workspace = workspace };

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
                ReadOnly = false
            };

            var output = new List<(string Kind, string Text)>();
            var snapshot = await facade.RunAsync(
                Profile(),
                "",
                session,
                tools,
                "Set the MB-101 fixture correctly and finish only after host verification.",
                (kind, text) => output.Add((kind, text)),
                () => { },
                new AgentRunTelemetry(),
                readOnly: false,
                CancellationToken.None).ConfigureAwait(false);

            Check(factory.CreateCount == 1
                  && factory.RepairTransport is not null
                  && factory.RepairVerifier is not null,
                "Mutating UI fixture did not create the V2 repair runtime.");
            Check(factory.RepairTransport.InitialSurfaceWasDeferred
                  && factory.RepairTransport.SawLoadedWriteSchema
                  && factory.RepairTransport.SawFailedVerificationContinuation
                  && factory.RepairTransport.SawPassedCorrection,
                "Normal UI path did not perform tool_search -> loaded mutation -> verification failure -> repair -> pass.");
            Check(factory.RepairVerifier.VerificationCount == 2,
                "UI mutation was not re-verified after repair.");
            Check(snapshot.State == AgentTaskState.Completed,
                "Host-owned orchestration state did not complete after verifier PASS.");
            Check(output.Any(x => x.Kind == "final"
                    && x.Text == "ui-v2-repair-ok"),
                "Verified UI repair final text was not surfaced.");
            Check(snapshot.Criteria.Single().CriterionId
                    == AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                "Normal mutating UI contract did not retain the host-owned runtime verification criterion.");
        });

        await Test("MB-101 repository guard keeps normal UI chain fully V2 with no hidden v1 fallback", () =>
        {
            var repoRoot = FindRepoRoot();
            var window = Read(repoRoot, "LabWindow.cs");
            var facade = Read(repoRoot, "Tasking", "AgentOrchestratedRun.cs");
            var orchestrator = Read(repoRoot, "Tasking", "AgentOrchestrator.cs");
            var factory = Read(repoRoot, "Runtime", "AgentRuntimeFactory.cs");
            var runtime = Read(repoRoot, "Runtime", "AgentRuntime.cs");

            Check(window.Contains("new AgentOrchestratedRun", StringComparison.Ordinal)
                  && window.Contains(".RunAsync(", StringComparison.Ordinal)
                  && !window.Contains("AgentRunner", StringComparison.Ordinal)
                  && !window.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "LabWindow normal send path is not exclusively routed through AgentOrchestratedRun.");

            Check(facade.Contains("LabSessionContextAdapter", StringComparison.Ordinal)
                  && facade.Contains("RuntimeCompactionCoordinator", StringComparison.Ordinal)
                  && facade.Contains("AgentRuntimeRequest", StringComparison.Ordinal)
                  && facade.Contains("CreateRuntime", StringComparison.Ordinal)
                  && facade.Contains("RunRuntimeAsync", StringComparison.Ordinal)
                  && !facade.Contains("AgentRunner", StringComparison.Ordinal)
                  && !facade.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "AgentOrchestratedRun is missing bounded-context/runtime wiring or exposes a v1 fallback.");

            Check(orchestrator.Contains("IAgentRuntimeFactory", StringComparison.Ordinal)
                  && orchestrator.Contains("RunRuntimeAsync", StringComparison.Ordinal)
                  && !orchestrator.Contains("AgentRunner", StringComparison.Ordinal)
                  && !orchestrator.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "AgentOrchestrator does not exclusively coordinate AgentRuntime.");

            Check(factory.Contains("AgentTransportFactory", StringComparison.Ordinal)
                  && factory.Contains("NormalRuntimeToolRegistry.Create", StringComparison.Ordinal)
                  && factory.Contains("AgentRuntimeDomainVerifierRouter", StringComparison.Ordinal)
                  && factory.Contains("new AgentRuntime(", StringComparison.Ordinal)
                  && !factory.Contains("AgentRunner", StringComparison.Ordinal),
                "Normal runtime factory is missing provider-neutral transport/tool/verifier wiring.");

            Check(runtime.Contains("IAgentTransport", StringComparison.Ordinal)
                  && runtime.Contains("AgentContextManager", StringComparison.Ordinal)
                  && runtime.Contains("DeferredToolDiscovery", StringComparison.Ordinal)
                  && runtime.Contains("ToolExecutionScheduler", StringComparison.Ordinal)
                  && runtime.Contains("AgentRepairController", StringComparison.Ordinal)
                  && !runtime.Contains("AgentRunner", StringComparison.Ordinal),
                "AgentRuntime core is missing one of the required fully-V2 dependencies.");

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-normal-ui-v2-path-tests.txt");
        await File.WriteAllLinesAsync(report, lines).ConfigureAwait(false);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AiProfile Profile()
        => new()
        {
            Protocol = AiProtocol.Ollama,
            BaseUrl = "http://localhost:11434",
            Model = "mb101-fixture"
        };

    private static ToolDescriptor WriteTool()
    {
        const string name = "fixture.write";
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description = "Set the deterministic MB-101 fixture value.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        value = new { type = "string" }
                    },
                    required = new[] { "value" },
                    additionalProperties = false
                }
            }
        });

        return new ToolDescriptor(
            name,
            new ToolNamespace("fixture", "Deterministic MB-101 UI-path fixture."),
            "Set the deterministic MB-101 fixture value.",
            AgentToolRisk.Medium,
            AgentToolAccess.Mutating,
            supportsParallel: false,
            schemaVersion: "v2",
            callableSchema: schema,
            executor: new DelegatingToolExecutor(
                "mb101-write",
                (call, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var value = call.Arguments.GetProperty("value").GetString() ?? "";
                    return ValueTask.FromResult(JsonSerializer.Serialize(new
                    {
                        success = true,
                        value
                    }));
                }),
            provenance: new ToolProvenance(
                "mb101-fixture",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: new ToolResourceScope(
                "mb101:fixture",
                "mb101:fixture"),
            serializationKey: "mb101:fixture",
            canProvideVerificationEvidence: true);
    }

    private sealed class UiRuntimeFactory : IAgentRuntimeFactory
    {
        private readonly bool _repair;

        private UiRuntimeFactory(bool repair)
        {
            _repair = repair;
            if (repair)
            {
                RepairTransport = new UiRepairTransport();
                RepairVerifier = new UiRepairVerifier();
            }
            else
            {
                DirectTransport = new UiDirectTransport();
            }
        }

        public static UiRuntimeFactory Direct() => new(false);
        public static UiRuntimeFactory Repair() => new(true);

        public int CreateCount { get; private set; }
        public AgentContextManager? ContextManagerSeen { get; private set; }
        public UiDirectTransport? DirectTransport { get; }
        public UiRepairTransport? RepairTransport { get; }
        public UiRepairVerifier? RepairVerifier { get; }

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            global::H2AgentLab.AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
        {
            CreateCount++;
            ContextManagerSeen = contextManager;

            var registry = new ToolRegistry();
            if (_repair)
                registry.Register(WriteTool());

            return new AgentRuntime(
                _repair
                    ? RepairTransport!
                    : DirectTransport!,
                contextManager,
                registry,
                verifier: _repair ? RepairVerifier : null,
                permissionPolicy: new ScopedAgentRuntimePermissionPolicy(_ => true));
        }
    }

    private sealed class UiDirectTransport : IAgentTransport
    {
        public int StartCount { get; private set; }
        public bool InitialSurfaceWasDeferred { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            InitialSurfaceWasDeferred = request.Tools
                .Select(x => x.Name)
                .SequenceEqual([DeferredToolDiscovery.SearchToolName], StringComparer.Ordinal);
            if (!InitialSurfaceWasDeferred)
                throw new InvalidOperationException(
                    "Normal UI initial model surface was not tool_search-only.");
            if (!request.Messages.Any(x => x.Content.Contains(
                    "Return the deterministic MB-101 direct UI answer.",
                    StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "Normal UI user task did not reach IAgentTransport.");

            yield return AgentTransportEvent.TextDeltaEvent("ui-v2-direct-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb101-direct", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException(
                "Direct MB-101 UI fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UiRepairTransport : IAgentTransport
    {
        private int _continuations;

        public bool InitialSurfaceWasDeferred { get; private set; }
        public bool SawLoadedWriteSchema { get; private set; }
        public bool SawFailedVerificationContinuation { get; private set; }
        public bool SawPassedCorrection { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.OpenAiResponsesWebSocket;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitialSurfaceWasDeferred = request.Tools
                .Select(x => x.Name)
                .SequenceEqual([DeferredToolDiscovery.SearchToolName], StringComparer.Ordinal);
            if (!InitialSurfaceWasDeferred)
                throw new InvalidOperationException(
                    "Mutating UI initial model surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "mb101-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "fixture.write",
                    max_results = 1
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb101-r1",
                "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                SawLoadedWriteSchema = request.NewlyLoadedTools?.Single().Name
                    == "fixture.write";
                if (!SawLoadedWriteSchema)
                    throw new InvalidOperationException(
                        "Normal UI deferred discovery did not load fixture.write.");

                yield return AgentTransportEvent.Tool(new(
                    "mb101-write-bad",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "bad" })));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb101-r2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var failed = request.ToolResults.Single();
                SawFailedVerificationContinuation = failed.IsError
                    && failed.Content.Contains(
                        "[HOST VERIFICATION FAILED]",
                        StringComparison.Ordinal)
                    && failed.Content.Contains(
                        AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                        StringComparison.Ordinal);
                if (!SawFailedVerificationContinuation)
                    throw new InvalidOperationException(
                        "Normal UI verifier failure did not become bounded repair continuation.");

                yield return AgentTransportEvent.Tool(new(
                    "mb101-write-good",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "good" })));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb101-r3",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 3)
            {
                var passed = request.ToolResults.Single();
                SawPassedCorrection = !passed.IsError;
                if (!SawPassedCorrection)
                    throw new InvalidOperationException(
                        "Corrective UI mutation remained failed after verifier PASS.");

                yield return AgentTransportEvent.TextDeltaEvent(
                    "ui-v2-repair-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb101-r4",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-101 repair continuation count.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UiRepairVerifier : IAgentRuntimeVerifier
    {
        public int VerificationCount { get; private set; }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Calls.Any(x => x.Name == "fixture.write"))
                return Task.FromResult<VerificationReport?>(null);

            VerificationCount++;
            if (VerificationCount == 1)
            {
                return Task.FromResult<VerificationReport?>(new VerificationReport(
                    AgentRuntimeDomainVerifierRouter.VerifierId,
                    [
                        new VerificationCriterionResult(
                            AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                            VerificationCriterionStatus.Failed,
                            ["evidence:mb101:bad"],
                            new VerificationFailure(
                                AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                                "MB-101 fixture value is not correct yet.",
                                ["evidence:mb101:bad"]))
                    ]));
            }

            return Task.FromResult<VerificationReport?>(new VerificationReport(
                AgentRuntimeDomainVerifierRouter.VerifierId,
                [
                    new VerificationCriterionResult(
                        AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                        VerificationCriterionStatus.Passed,
                        ["evidence:mb101:good"])
                ]));
        }
    }

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
