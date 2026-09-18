using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbToolRegistryExecutionTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-31 test directory.");
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

        await Test("MB-31 selected tool executes only through ToolRegistry descriptor executor", async () =>
        {
            var executed = 0;
            var registry = new ToolRegistry();
            var descriptor = Descriptor(
                "fixture.registry_read",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                new ToolResourceScope("fixture:resource", "fixture:resource"),
                (call, ct) =>
                {
                    Interlocked.Increment(ref executed);
                    return ValueTask.FromResult(JsonSerializer.Serialize(new
                    {
                        ok = true,
                        path = "registry-executor"
                    }));
                });
            registry.Register(descriptor);

            await using var runtime = new AgentRuntime(
                new RegistryExecutionTransport(),
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Read registry fixture."),
                CancellationToken.None);

            Check(result.FinalText == "registry-path-ok",
                "Runtime did not reach final after registry executor result.");
            Check(executed == 1,
                "Registry descriptor executor did not execute exactly once.");
            Check(descriptor.Access == AgentToolAccess.ReadOnly
                && descriptor.Risk == AgentToolRisk.Low
                && descriptor.ResourceScope?.ScopeId == "fixture:resource",
                "Descriptor access/risk/scope metadata is not available at the execution boundary.");
        });

        await Test("MB-31 unknown model tool fails closed and never executes any fallback switch", async () =>
        {
            var executed = 0;
            var registry = new ToolRegistry();
            registry.Register(Descriptor(
                "fixture.known",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                null,
                (call, ct) =>
                {
                    Interlocked.Increment(ref executed);
                    return ValueTask.FromResult("{}");
                }));

            await using var runtime = new AgentRuntime(
                new UnknownToolTransport(),
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Attempt unknown tool."),
                CancellationToken.None);

            Check(result.FinalText == "unknown-tool-blocked",
                "Runtime did not continue after fail-closed unknown tool result.");
            Check(executed == 0,
                "Unknown tool call reached an unrelated/fallback executor.");
        });

        await Test("MB-31 normal runtime source resolves descriptors and never calls AgentTools.Execute directly", () =>
        {
            var repo = FindRepoRoot();
            var runtimeSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Runtime", "AgentRuntime.cs"));
            var orchestratedSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));

            Check(runtimeSource.Contains("_registry.TryGet", StringComparison.Ordinal)
                && runtimeSource.Contains("new ToolExecutionRequest", StringComparison.Ordinal)
                && runtimeSource.Contains("descriptor", StringComparison.Ordinal),
                "AgentRuntime does not resolve normal tool calls through ToolRegistry descriptors.");
            Check(!runtimeSource.Contains("AgentTools.Execute", StringComparison.Ordinal)
                && !orchestratedSource.Contains("AgentTools.Execute", StringComparison.Ordinal),
                "Normal runtime path directly references the legacy giant AgentTools.Execute switch.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-tool-registry-execution-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string userInput)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "fixture",
            null,
            null,
            ["do not mutate"],
            ["return final"],
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
                CurrentState: "fixture-state"),
            PromptCacheKey: "mb31");
    }

    private static ToolDescriptor Descriptor(
        string name,
        AgentToolAccess access,
        AgentToolRisk risk,
        ToolResourceScope? scope,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => new(
            name,
            new ToolNamespace("fixture", "MB-31 registry fixture namespace."),
            "Read secure registry metadata fixture.",
            risk,
            access,
            supportsParallel: access == AgentToolAccess.ReadOnly,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description = "Read secure registry metadata fixture.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor("mb31-fixture", execute),
            provenance: new ToolProvenance(
                "mb31-provider",
                "1.0.0",
                "mb31-server",
                "1.0.0"),
            resourceScope: scope,
            serializationKey: "mb31-fixture");

    private sealed class RegistryExecutionTransport : IAgentTransport
    {
        private int _continuations;

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "search-registry",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "secure registry metadata fixture" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb31-a", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;
            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.registry_read")
                    throw new InvalidOperationException("Selected registry schema was not loaded.");
                yield return AgentTransportEvent.Tool(new(
                    "registry-read",
                    "fixture.registry_read",
                    "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mb31-b", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                if (result.IsError
                    || !result.Content.Contains("registry-executor", StringComparison.Ordinal))
                    throw new InvalidOperationException("Registry executor result was not preserved.");
                yield return AgentTransportEvent.TextDeltaEvent("registry-path-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mb31-c", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected MB-31 continuation count.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnknownToolTransport : IAgentTransport
    {
        private bool _continued;

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "unknown-1",
                "fixture.not_registered",
                "{}"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb31-u1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_continued)
                throw new InvalidOperationException("Unexpected second unknown-tool continuation.");
            _continued = true;

            var result = request.ToolResults.Single();
            if (!result.IsError
                || !result.Content.Contains("unknown_tool", StringComparison.Ordinal)
                || result.ToolName != "fixture.not_registered")
                throw new InvalidOperationException("Unknown tool did not fail closed with typed error result.");

            yield return AgentTransportEvent.TextDeltaEvent("unknown-tool-blocked");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb31-u2", "stop");
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
