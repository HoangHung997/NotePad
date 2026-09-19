using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Capabilities;
using H2AgentLab.Context;
using H2AgentLab.Extensions;
using H2AgentLab.Prompting;
using H2AgentLab.Runtime;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Acceptance;

public static class MbMinimumBootableAgentAcceptanceTests
{
    private sealed record AcceptanceResult(
        string Id,
        string Requirement,
        string Evidence,
        bool Passed,
        string? Failure);

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-100 acceptance directory.");
        Directory.CreateDirectory(root);

        var results = new List<AcceptanceResult>();
        var suiteRuns = new Dictionary<string, Task<int>>(StringComparer.Ordinal);

        async Task RequireSuite(
            string key,
            Func<Task<int>> run)
        {
            if (!suiteRuns.TryGetValue(key, out var task))
            {
                task = run();
                suiteRuns[key] = task;
            }

            var exitCode = await task.ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Acceptance evidence suite '{key}' returned exit code {exitCode}.");
        }

        async Task Case(
            string id,
            string requirement,
            string evidence,
            Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                results.Add(new AcceptanceResult(
                    id,
                    requirement,
                    evidence,
                    Passed: true,
                    Failure: null));
                Console.WriteLine($"PASS {id} {requirement}");
            }
            catch (Exception ex)
            {
                results.Add(new AcceptanceResult(
                    id,
                    requirement,
                    evidence,
                    Passed: false,
                    Failure: ex.GetType().Name + ": " + ex.Message));
                Console.WriteLine(
                    $"FAIL {id} {requirement}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        await Case(
            "MBA-01",
            "direct response",
            "AgentRuntime direct-response fixture",
            DirectResponseAsync);

        await Case(
            "MBA-02",
            "multi-tool task",
            "MB-10 runtime suite: tool_search -> selected tool -> final",
            () => RequireSuite(
                "runtime-core",
                () => MbAgentRuntimeTests.Run(
                    Path.Combine(root, "suite-runtime-core"))));

        await Case(
            "MBA-03",
            "dynamic tool_search schema loading",
            "MB-32 deferred tool loading suite",
            () => RequireSuite(
                "deferred-tools",
                () => MbDeferredToolLoadingTests.Run(
                    Path.Combine(root, "suite-deferred-tools"))));

        await Case(
            "MBA-04",
            "tool error -> recovery",
            "AgentRuntime unknown-tool -> tool_search -> selected tool -> final fixture",
            ToolErrorRecoveryAsync);

        await Case(
            "MBA-05",
            "mutation -> verification PASS",
            "MB-42 real runtime verification suite",
            () => RequireSuite(
                "verification",
                () => MbVerificationRuntimeTests.Run(
                    Path.Combine(root, "suite-verification"))));

        await Case(
            "MBA-06",
            "mutation -> verification FAIL -> repair -> PASS",
            "MB-10 runtime verifier-repair continuation",
            () => RequireSuite(
                "runtime-core",
                () => MbAgentRuntimeTests.Run(
                    Path.Combine(root, "suite-runtime-core"))));

        await Case(
            "MBA-07",
            "long context",
            "MB-20 10/100/1000-turn bounded context suite",
            () => RequireSuite(
                "bounded-context",
                () => MbContextRuntimeTests.Run(
                    Path.Combine(root, "suite-bounded-context"))));

        await Case(
            "MBA-08",
            "cancellation",
            "MB-10 provider-neutral cancellation fixture",
            () => RequireSuite(
                "runtime-core",
                () => MbAgentRuntimeTests.Run(
                    Path.Combine(root, "suite-runtime-core"))));

        await Case(
            "MBA-09",
            "provider switch",
            "MB-12 provider-neutral transport/runtime suite",
            () => RequireSuite(
                "provider-switch",
                () => MbProviderRuntimeTests.Run(
                    Path.Combine(root, "suite-provider-switch"))));

        await Case(
            "MBA-10",
            "plugin tool registration",
            "MB-60 extension registration and AgentRuntime tool-use suite",
            () => RequireSuite(
                "extension-registration",
                () => MbExtensionRegistrationTests.Run(
                    Path.Combine(root, "suite-extension-registration"))));

        await Case(
            "MBA-11",
            "progressive skill loading",
            "MB-52 metadata -> SKILL.md -> selected resource suite",
            () => RequireSuite(
                "progressive-skills",
                () => MbProgressiveSkillLoadingTests.Run(
                    Path.Combine(root, "suite-progressive-skills"))));

        await Case(
            "MBA-12",
            "install-and-continue",
            "MB-82 one-request discover/install/refresh/use/verify suite",
            () => RequireSuite(
                "install-continue",
                () => MbEndToEndInstallContinueTests.Run(
                    Path.Combine(root, "suite-install-continue"))));

        var passed = results.Count(x => x.Passed);
        var failed = results.Count - passed;
        var textLines = results.Select(x =>
            (x.Passed ? "PASS " : "FAIL ")
            + x.Id + " " + x.Requirement
            + " | evidence=" + x.Evidence
            + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        textLines.Add($"RESULT: {passed} passed, {failed} failed.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-minimum-bootable-agent-acceptance.txt"),
            textLines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-minimum-bootable-agent-acceptance.json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    total = results.Count,
                    passed,
                    failed,
                    cases = results
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine($"RESULT: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task DirectResponseAsync()
    {
        var registry = new ToolRegistry();
        await using var runtime = new AgentRuntime(
            new DirectResponseTransport(),
            new AgentContextManager(),
            registry);

        var result = await runtime.RunAsync(
            Request("Answer directly without tools."),
            CancellationToken.None).ConfigureAwait(false);

        if (result.FinalText != "direct-ok"
            || result.ToolRounds != 0
            || result.ToolCalls != 0
            || result.LoadedToolSchemas.Count != 0)
        {
            throw new InvalidOperationException(
                "Direct response did not finish without executing/loading a detailed tool.");
        }
    }

    private static async Task ToolErrorRecoveryAsync()
    {
        var registry = new ToolRegistry();
        registry.Register(ReadTool());
        var transport = new ToolErrorRecoveryTransport();

        await using var runtime = new AgentRuntime(
            transport,
            new AgentContextManager(),
            registry);

        var result = await runtime.RunAsync(
            Request("Recover from one failed tool proposal and read fixture evidence."),
            CancellationToken.None).ConfigureAwait(false);

        if (result.FinalText != "recovered-ok"
            || result.ToolRounds != 3
            || result.ToolCalls != 3
            || !result.LoadedToolSchemas.Contains(
                "fixture.read",
                StringComparer.Ordinal)
            || !transport.SawUnknownToolError
            || !transport.SawReadResult)
        {
            throw new InvalidOperationException(
                "AgentRuntime did not recover from the tool error through deferred discovery and a corrected tool call.");
        }
    }

    private static AgentRuntimeRequest Request(string userInput)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "mb100:fixture",
            null,
            null,
            ["do not mutate"],
            ["return deterministic acceptance result"],
            [],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

        return new AgentRuntimeRequest(
            contract,
            userInput,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-100 deterministic acceptance fixture"),
            PromptCacheKey: "mb100-fixture",
            MaxToolRounds: 8,
            MaxRepairRounds: 2);
    }

    private static ToolDescriptor ReadTool()
    {
        const string name = "fixture.read";
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description = "Read deterministic MB-100 fixture evidence.",
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
            new ToolNamespace(
                "fixture",
                "Deterministic Minimum Bootable Agent acceptance tools."),
            "Read deterministic MB-100 fixture evidence.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema: schema,
            executor: new DelegatingToolExecutor(
                "mb100-fixture",
                (call, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        JsonSerializer.Serialize(new
                        {
                            value = "fixture-recovered"
                        }));
                }),
            provenance: new ToolProvenance(
                "mb100-fixture",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: null,
            serializationKey: "fixture");
    }

    private sealed class DirectResponseTransport : IAgentTransport
    {
        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Tools.Select(x => x.Name)
                .SequenceEqual([DeferredToolDiscovery.SearchToolName]))
            {
                throw new InvalidOperationException(
                    "Direct response initial surface was not bounded to tool_search.");
            }

            yield return AgentTransportEvent.TextDeltaEvent("direct-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb100-direct",
                "stop");
        }

        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Direct response unexpectedly requested a continuation.");

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class ToolErrorRecoveryTransport : IAgentTransport
    {
        private int _continuations;

        public bool SawUnknownToolError { get; private set; }
        public bool SawReadResult { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "bad-1",
                "invented.tool",
                "{}"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb100-error-1",
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
                var error = request.ToolResults.Single();
                SawUnknownToolError = error.IsError
                    && error.ToolCallId == "bad-1"
                    && error.Content.Contains(
                        "unknown_tool",
                        StringComparison.Ordinal);
                if (!SawUnknownToolError)
                    throw new InvalidOperationException(
                        "Unknown tool failure was not returned as a typed tool error.");

                yield return AgentTransportEvent.Tool(new(
                    "search-1",
                    DeferredToolDiscovery.SearchToolName,
                    JsonSerializer.Serialize(new
                    {
                        query = "fixture.read",
                        max_results = 1
                    })));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb100-error-2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.read")
                    throw new InvalidOperationException(
                        "Recovery tool_search did not load fixture.read.");

                yield return AgentTransportEvent.Tool(new(
                    "read-1",
                    "fixture.read",
                    "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb100-error-3",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 3)
            {
                var read = request.ToolResults.Single();
                SawReadResult = !read.IsError
                    && read.ToolCallId == "read-1"
                    && read.ToolName == "fixture.read"
                    && read.Content.Contains(
                        "fixture-recovered",
                        StringComparison.Ordinal);
                if (!SawReadResult)
                    throw new InvalidOperationException(
                        "Corrected recovery tool result was not observed.");

                yield return AgentTransportEvent.TextDeltaEvent("recovered-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb100-error-4",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-100 recovery continuation count.");
        }

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
