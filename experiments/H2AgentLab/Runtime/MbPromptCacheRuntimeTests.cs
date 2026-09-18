using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public static class MbPromptCacheRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-22 test directory.");
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

        await Test("MB-22 normal orchestrated requests send a stable cache key across dynamic task and session state", async () =>
        {
            var factory = new CapturingRuntimeFactory();
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var profile = Profile();

            async Task RunOne(string name, string prompt, string history)
            {
                var caseRoot = Path.Combine(root, name);
                var workspace = Path.Combine(caseRoot, "workspace");
                var state = Path.Combine(caseRoot, "state");
                Directory.CreateDirectory(workspace);
                Directory.CreateDirectory(state);

                var session = new global::H2AgentLab.LabSession
                {
                    Workspace = workspace
                };
                session.Add("user", history);
                var tools = new global::H2AgentLab.AgentTools(
                    new global::H2AgentLab.SafeWorkspace(workspace),
                    state,
                    (_, _) => Task.FromResult(true),
                    (_, _) => { });

                _ = await new AgentOrchestratedRun(orchestrator).RunAsync(
                    profile,
                    "",
                    session,
                    tools,
                    prompt,
                    (_, _) => { },
                    () => { },
                    new AgentRunTelemetry(),
                    readOnly: true,
                    CancellationToken.None);
            }

            await RunOne("first", "task A at TIME_A", "SESSION_A_HISTORY");
            await RunOne("second", "task B at TIME_B", "SESSION_B_HISTORY");

            Check(factory.Requests.Count == 2,
                "Normal orchestrated fixture did not capture two model start requests.");
            var first = factory.Requests[0];
            var second = factory.Requests[1];
            Check(!string.IsNullOrWhiteSpace(first.PromptCacheKey)
                && first.PromptCacheKey == second.PromptCacheKey,
                "Dynamic task/session/workspace input contaminated the real prompt cache key.");
            Check(!first.Messages.SequenceEqual(second.Messages),
                "Dynamic prompt suffix unexpectedly became identical across different tasks.");
        });

        await Test("MB-22 stable tool namespace metadata participates in real runtime cache identity", async () =>
        {
            var scope = AgentPromptCacheIdentityBuilder.Scope(Profile());
            var files = await RunDirectAsync(
                RegistryWithNamespace("files", "Structured file capabilities."),
                scope,
                new AgentContextInput(CurrentState: "state=A"),
                "question A");
            var office = await RunDirectAsync(
                RegistryWithNamespace("office", "Structured Office capabilities."),
                scope,
                new AgentContextInput(CurrentState: "state=B"),
                "question B");

            Check(files.Start.PromptCacheKey is not null
                && office.Start.PromptCacheKey is not null
                && files.Start.PromptCacheKey != office.Start.PromptCacheKey,
                "Changing stable namespace metadata failed to invalidate runtime cache identity.");
            Check(files.Result.PromptCacheIdentity?.Key == files.Start.PromptCacheKey
                && office.Result.PromptCacheIdentity?.Key == office.Start.PromptCacheKey,
                "Runtime result did not preserve the cache identity actually sent to transport.");
        });

        await Test("MB-22 configured stable skill hash invalidates only its stable cache identity", async () =>
        {
            var scope = AgentPromptCacheIdentityBuilder.Scope(Profile());
            var hashA = new string('a', 64);
            var hashB = new string('b', 64);

            var first = await RunDirectAsync(
                new ToolRegistry(),
                scope,
                new AgentContextInput(
                    TaskContract: "task-one",
                    CurrentState: "SESSION_A TIME_A"),
                "user A",
                [new AgentStableSkillHash("cad-integrity", hashA)]);
            var dynamicOnly = await RunDirectAsync(
                new ToolRegistry(),
                scope,
                new AgentContextInput(
                    TaskContract: "task-two",
                    CurrentState: "SESSION_B TIME_B"),
                "user B",
                [new AgentStableSkillHash("cad-integrity", hashA)]);
            var changedSkill = await RunDirectAsync(
                new ToolRegistry(),
                scope,
                new AgentContextInput(
                    TaskContract: "task-three",
                    CurrentState: "SESSION_C TIME_C"),
                "user C",
                [new AgentStableSkillHash("cad-integrity", hashB)]);

            Check(first.Start.PromptCacheKey == dynamicOnly.Start.PromptCacheKey,
                "Dynamic runtime state invalidated a stable skill-aware cache identity.");
            Check(first.Start.PromptCacheKey != changedSkill.Start.PromptCacheKey,
                "Configured stable skill hash change failed to invalidate cache identity.");
            Check(changedSkill.Result.PromptCacheIdentity?.StableSkillHashes.Single().Sha256Hex == hashB,
                "Runtime cache evidence did not record the configured skill hash.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-prompt-cache-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AiProfile Profile()
        => new()
        {
            Protocol = AiProtocol.OpenAiResponses,
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-5"
        };

    private static ToolRegistry RegistryWithNamespace(
        string namespaceName,
        string namespaceDescription)
    {
        var registry = new ToolRegistry();
        var toolName = namespaceName + ".fixture";
        registry.Register(new ToolDescriptor(
            toolName,
            new ToolNamespace(namespaceName, namespaceDescription),
            "Fixture read-only capability.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = toolName,
                    description = "Fixture read-only capability.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor(
                "mb22",
                (call, ct) => ValueTask.FromResult("{}"))));
        return registry;
    }

    private static async Task<DirectRun> RunDirectAsync(
        ToolRegistry registry,
        AgentPromptCacheScope scope,
        AgentContextInput context,
        string userInput,
        IReadOnlyList<AgentStableSkillHash>? skills = null)
    {
        AgentTransportStartRequest? captured = null;
        await using var runtime = new AgentRuntime(
            new CapturingTransport(request => captured = request),
            new AgentContextManager(),
            registry);

        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "fixture",
            null,
            null,
            ["preserve"],
            ["answer"],
            [new AgentAcceptanceCriterion("final", "Return final answer.")],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

        var request = new AgentRuntimeRequest(
            contract,
            userInput,
            StablePrefix(),
            context,
            PromptCacheKey: null,
            MaxToolRounds: 4,
            MaxRepairRounds: 0,
            PromptCacheScope: scope,
            StableSkillHashes: skills);

        var result = await runtime.RunAsync(request, CancellationToken.None);
        return new DirectRun(
            captured ?? throw new InvalidOperationException("Transport start was not captured."),
            result);
    }

    private static AgentPromptStablePrefix StablePrefix()
        => new(
            AgentVersions.Current,
            "BASE STABLE POLICY",
            "SECURITY STABLE POLICY",
            "MODEL STABLE POLICY",
            "");

    private sealed record DirectRun(
        AgentTransportStartRequest Start,
        AgentRuntimeResult Result);

    private sealed class CapturingRuntimeFactory : IAgentRuntimeFactory
    {
        public List<AgentTransportStartRequest> Requests { get; } = [];

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            global::H2AgentLab.AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
            => new(
                new CapturingTransport(Requests.Add),
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
            yield return AgentTransportEvent.TextDeltaEvent("cache-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("cache", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("MB-22 fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
