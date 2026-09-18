using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public static class MbAgentRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB runtime test directory.");
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

        await Test("MB-10 real runtime performs tool_search then loaded tool then final in one task", async () =>
        {
            var registry = new ToolRegistry();
            registry.Register(ReadTool());
            await using var runtime = new AgentRuntime(
                new SearchReadFinalTransport(),
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request(
                    ReadOnlyContract(),
                    "Read fixture evidence.",
                    requireVerification: false),
                CancellationToken.None);

            Check(result.FinalText == "Verified fixture answer",
                "Runtime final answer mismatch.");
            Check(result.ToolRounds == 2 && result.ToolCalls == 2,
                "Runtime did not perform both discovery and selected-tool rounds.");
            Check(result.LoadedToolSchemas.Contains("fixture.read", StringComparer.Ordinal),
                "Deferred selected tool schema was not retained in the same task.");
            Check(result.Usage.InputTokens == 30
                && result.Usage.OutputTokens == 6,
                "Runtime did not aggregate provider-neutral usage.");
        });

        await Test("MB-10 verifier failure becomes bounded repair continuation and can pass on a later tool round", async () =>
        {
            var registry = new ToolRegistry();
            registry.Register(WriteTool());
            var verifier = new RepairVerifier();
            await using var runtime = new AgentRuntime(
                new RepairTransport(),
                new AgentContextManager(),
                registry,
                verifier: verifier);

            var result = await runtime.RunAsync(
                Request(
                    MutatingContract(),
                    "Set fixture value correctly.",
                    requireVerification: true),
                CancellationToken.None);

            Check(result.FinalText == "Mutation verified",
                "Runtime did not continue to verified final after repair.");
            Check(result.ToolRounds == 3
                && result.RepairRounds == 1
                && result.VerificationHistory.Count == 2
                && !result.VerificationHistory[0].Passed
                && result.VerificationHistory[1].Passed,
                "Runtime verification/repair loop history is wrong.");
            Check(verifier.WriteVerifications == 2,
                "Verifier did not observe both mutation attempts.");
        });

        await Test("MB-10 cancellation reaches provider-neutral transport and executes no latent tool", async () =>
        {
            var registry = new ToolRegistry();
            var executed = 0;
            registry.Register(ReadTool((_, _) =>
            {
                Interlocked.Increment(ref executed);
                return ValueTask.FromResult("{}");
            }));

            var transport = new CancellingTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

            try
            {
                _ = await runtime.RunAsync(
                    Request(
                        ReadOnlyContract(),
                        "Wait for cancelled provider.",
                        requireVerification: false),
                    cancel.Token);
                throw new InvalidOperationException("Cancelled runtime unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }

            Check(transport.Cancelled,
                "Runtime cancellation did not call IAgentTransport.Cancel().");
            Check(executed == 0,
                "A latent tool executed after transport cancellation.");
        });

        await Test("MB-10 AgentRuntime source has no AgentRunner compatibility dependency", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Runtime", "AgentRuntime.cs"));
            Check(!source.Contains("AgentRunner", StringComparison.Ordinal),
                "Real AgentRuntime depends on legacy AgentRunner.");
            Check(source.Contains("IAgentTransport", StringComparison.Ordinal)
                && source.Contains("AgentContextManager", StringComparison.Ordinal)
                && source.Contains("AgentPromptLayout", StringComparison.Ordinal)
                && source.Contains("DeferredToolDiscovery", StringComparison.Ordinal)
                && source.Contains("ToolExecutionScheduler", StringComparison.Ordinal),
                "AgentRuntime is missing one of the required V2 core dependencies.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(
        AgentTaskContract contract,
        string userInput,
        bool requireVerification)
        => new(
            contract,
            userInput,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                "CORE TOOLS"),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "fixture-state"),
            PromptCacheKey: "mb10-fixture",
            MaxToolRounds: 8,
            MaxRepairRounds: 2);

    private static AgentTaskContract ReadOnlyContract()
        => new(
            Guid.NewGuid(),
            "Read fixture evidence.",
            "fixture",
            null,
            null,
            ["do not mutate"],
            ["return verified fixture answer"],
            [],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

    private static AgentTaskContract MutatingContract()
        => new(
            Guid.NewGuid(),
            "Set fixture value correctly.",
            "fixture:resource-1",
            null,
            ["set fixture value"],
            ["preserve unrelated state"],
            ["return only after verifier passes"],
            [
                new AgentAcceptanceCriterion(
                    "fixture.correct",
                    "Fixture value is correct.")
            ],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(
                requireVerification: true,
                requiredVerifierIds: ["fixture-verifier"]));

    private static ToolDescriptor ReadTool(
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>>? execute = null)
        => Tool(
            "fixture.read",
            "Read fixture evidence.",
            AgentToolAccess.ReadOnly,
            AgentToolRisk.Low,
            supportsParallel: true,
            execute ?? ((call, ct) => ValueTask.FromResult("{"value":"fixture-value"}")),
            resourceScope: null);

    private static ToolDescriptor WriteTool()
        => Tool(
            "fixture.write",
            "Set the fixture value.",
            AgentToolAccess.Mutating,
            AgentToolRisk.Medium,
            supportsParallel: false,
            (call, ct) =>
            {
                var value = call.Arguments.TryGetProperty("value", out var node)
                    ? node.GetString() ?? ""
                    : "";
                return ValueTask.FromResult(
                    JsonSerializer.Serialize(new
                    {
                        ok = true,
                        value
                    }));
            },
            new ToolResourceScope("fixture:resource-1", "fixture:resource-1"));

    private static ToolDescriptor Tool(
        string name,
        string description,
        AgentToolAccess access,
        AgentToolRisk risk,
        bool supportsParallel,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute,
        ToolResourceScope? resourceScope)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters = new
                {
                    type = "object",
                    properties = name.EndsWith(".write", StringComparison.Ordinal)
                        ? new Dictionary<string, object>
                        {
                            ["value"] = new
                            {
                                type = "string"
                            }
                        }
                        : new Dictionary<string, object>(),
                    additionalProperties = false
                }
            }
        });

        return new ToolDescriptor(
            name,
            new ToolNamespace("fixture", "Deterministic MB-10 fixture tools."),
            description,
            risk,
            access,
            supportsParallel,
            "v1",
            schema,
            new DelegatingToolExecutor("mb10-fixture", execute),
            provenance: new ToolProvenance(
                "mb10-fixture",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: resourceScope,
            serializationKey: "fixture");
    }

    private sealed class SearchReadFinalTransport : IAgentTransport
    {
        private int _continuations;

        public AgentTransportCapabilities Capabilities { get; }
            = AgentTransportCapabilities.OpenAiResponsesWebSocket;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = request.Tools.Select(x => x.Name).ToArray();
            if (!names.SequenceEqual(new[] { DeferredToolDiscovery.SearchToolName }))
                throw new InvalidOperationException(
                    "Initial runtime surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "search-1",
                DeferredToolDiscovery.SearchToolName,
                "{"query":"fixture read evidence"}"));
            yield return AgentTransportEvent.Meter(new(InputTokens: 10, OutputTokens: 1));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("r1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                if (request.ToolResults.Single().ToolCallId != "search-1"
                    || request.NewlyLoadedTools?.Single().Name != "fixture.read")
                    throw new InvalidOperationException(
                        "Deferred tool_search continuation lost result/schema identity.");

                yield return AgentTransportEvent.Tool(new(
                    "read-1",
                    "fixture.read",
                    "{}"));
                yield return AgentTransportEvent.Meter(new(InputTokens: 8, OutputTokens: 2));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("r2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                if (result.ToolCallId != "read-1"
                    || result.ToolName != "fixture.read"
                    || !result.Content.Contains("fixture-value", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Tool result identity/content was not preserved.");

                yield return AgentTransportEvent.TextDeltaEvent("Verified fixture answer");
                yield return AgentTransportEvent.Meter(new(InputTokens: 12, OutputTokens: 3));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("r3", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected continuation count.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RepairTransport : IAgentTransport
    {
        private int _continuations;

        public AgentTransportCapabilities Capabilities { get; }
            = AgentTransportCapabilities.OpenAiResponsesWebSocket;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "search-write",
                DeferredToolDiscovery.SearchToolName,
                "{"query":"set fixture value"}"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("w1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.write")
                    throw new InvalidOperationException("fixture.write schema was not loaded.");
                yield return AgentTransportEvent.Tool(new(
                    "write-bad",
                    "fixture.write",
                    "{"value":"bad"}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("w2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var failed = request.ToolResults.Single();
                if (!failed.IsError
                    || !failed.Content.Contains("[HOST VERIFICATION FAILED]", StringComparison.Ordinal)
                    || !failed.Content.Contains("fixture.correct", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Verifier failure did not become bounded repair continuation.");

                yield return AgentTransportEvent.Tool(new(
                    "write-good",
                    "fixture.write",
                    "{"value":"good"}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("w3", "tool_calls");
                yield break;
            }

            if (_continuations == 3)
            {
                var passed = request.ToolResults.Single();
                if (passed.IsError)
                    throw new InvalidOperationException(
                        "Verified corrective result remained marked as error.");
                yield return AgentTransportEvent.TextDeltaEvent("Mutation verified");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("w4", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected repair continuation count.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RepairVerifier : IAgentRuntimeVerifier
    {
        public int WriteVerifications { get; private set; }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Calls.Any(x => x.Name == "fixture.write"))
                return Task.FromResult<VerificationReport?>(null);

            WriteVerifications++;
            if (WriteVerifications == 1)
            {
                return Task.FromResult<VerificationReport?>(new VerificationReport(
                    "fixture-verifier",
                    [
                        new VerificationCriterionResult(
                            "fixture.correct",
                            VerificationCriterionStatus.Failed,
                            ["evidence:fixture:bad"],
                            new VerificationFailure(
                                "fixture.correct",
                                "Fixture value is still incorrect.",
                                ["evidence:fixture:bad"]))
                    ]));
            }

            return Task.FromResult<VerificationReport?>(new VerificationReport(
                "fixture-verifier",
                [
                    new VerificationCriterionResult(
                        "fixture.correct",
                        VerificationCriterionStatus.Passed,
                        ["evidence:fixture:good"])
                ]));
        }
    }

    private sealed class CancellingTransport : IAgentTransport
    {
        public bool Cancelled { get; private set; }

        public AgentTransportCapabilities Capabilities { get; }
            = AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            yield return AgentTransportEvent.Complete("never", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            yield return AgentTransportEvent.Complete("never", "stop");
        }

        public void Cancel() => Cancelled = true;
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
