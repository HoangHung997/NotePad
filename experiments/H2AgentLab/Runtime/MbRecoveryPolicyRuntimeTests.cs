using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbRecoveryPolicyRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new AR-070 recovery-policy test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("AR-070 typed policy preserves live identity alternate-backend and uncertain-write boundaries", () =>
        {
            var live = ToolRecoveryPolicy.For("live_resource_required", ToolMutationEffect.None);
            Check(live.RetryClass == ToolRetryClass.Reobserve
                && live.RequiresChangedEvidence
                && live.PreserveTargetIdentity
                && !live.AllowsAlternateBackend
                && live.RecoveryCandidates.Contains("reacquire_same_live_resource", StringComparer.Ordinal),
                "Live-resource recovery policy can change source semantics.");

            var unavailable = ToolRecoveryPolicy.For("provider_unavailable", ToolMutationEffect.None);
            Check(unavailable.RetryClass == ToolRetryClass.Configure
                && unavailable.AllowsAlternateBackend
                && unavailable.RecoveryCandidates.Contains("check_provider_health", StringComparer.Ordinal)
                && unavailable.RecoveryCandidates.Contains("discover_semantically_equivalent_backend", StringComparer.Ordinal),
                "Missing-provider recovery lacks typed health/alternate choices.");

            var busy = ToolRecoveryPolicy.For("provider_busy", ToolMutationEffect.None);
            Check(busy.RetryClass == ToolRetryClass.WaitThenReobserve
                && busy.MinimumBackoffMilliseconds > 0
                && busy.RequiresChangedEvidence,
                "Busy-provider policy allows unbounded blind retry.");

            var uncertain = ToolRecoveryPolicy.For("connection_lost", ToolMutationEffect.Unknown);
            Check(uncertain.RetryClass == ToolRetryClass.ReconcileRequired
                && uncertain.RequiresReconciliation
                && !uncertain.AllowsAlternateBackend
                && uncertain.RecoveryCandidates.Contains("inspect_effects_without_repeating_write", StringComparer.Ordinal),
                "Unknown mutation effect escaped reconciliation.");
            return Task.CompletedTask;
        });

        await Test("AR-070 provider directive exposes bounded health and equivalent alternate tools without executing them", () =>
        {
            var registry = new ToolRegistry();
            Register(registry, "fixture.primary", (_, _) => ValueTask.FromResult("{}"),
                new ToolPreferenceMetadata("fixture-doc", ToolInteractionFidelity.Structured),
                new ToolReadiness(ToolReadinessState.Busy, "provider_busy"));
            Register(registry, "fixture.alternate", (_, _) => ValueTask.FromResult("{}"),
                new ToolPreferenceMetadata("fixture-doc", ToolInteractionFidelity.Structured),
                new ToolReadiness(ToolReadinessState.Ready));

            registry.TryGet("fixture.primary", out var descriptor);
            var call = Call("failed", "fixture.primary", new { resource_id = "doc-1" });
            var outcome = ToolOutcomeBridge.Failure(call, descriptor, "provider_unavailable",
                ToolErrorPhase.Preflight, ToolMutationEffect.None).Outcome;
            var directive = AgentRecoveryPolicy.Describe(
                registry, descriptor, outcome, "provider_unavailable", ["fixture.alternate"]);

            Check(directive.ProviderReadiness == ToolReadinessState.Busy
                && directive.ProviderReadinessReason == "provider_busy",
                "Provider readiness was not carried into bounded recovery context.");
            Check(directive.AlternateToolCandidates.SequenceEqual(["fixture.alternate"])
                && directive.SafeChoices.Contains("alternate_tool:fixture.alternate", StringComparer.Ordinal),
                "Equivalent alternate tool was not exposed as a bounded suggestion.");
            Check(directive.Plan.PreserveTargetIdentity && directive.Plan.RequiresChangedEvidence,
                "Alternate suggestion lost target/evidence constraints.");
            return Task.CompletedTask;
        });

        await Test("AR-070 unchanged provider-busy retry is blocked before a second executor call", async () =>
        {
            var executions = 0;
            var registry = new ToolRegistry();
            Register(registry, "fixture.fetch", (_, _) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult(JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "provider_busy",
                    recoveryTools = new[] { "fixture.inspect" }
                }));
            });

            var transport = new NoProgressTransport();
            await using var runtime = new AgentRuntime(transport, new AgentContextManager(), registry);
            try
            {
                _ = await runtime.RunAsync(Request("Read the exact fixture resource."), CancellationToken.None);
                throw new InvalidOperationException("No-progress retry unexpectedly completed.");
            }
            catch (AgentVerificationRequiredException)
            {
                // Expected: original provider_busy remains unresolved.
            }

            Check(executions == 1, $"Blind identical retry reached executor {executions} times.");
            Check(transport.SawRecoveryState
                && transport.SawNoProgressBlock,
                "Model continuation did not receive typed recovery state/no-progress rejection.");
        });

        await Test("AR-070 corrected arguments retry the same target without a new user prompt", async () =>
        {
            var executions = 0;
            var registry = new ToolRegistry();
            Register(registry, "fixture.fetch", (call, _) =>
            {
                Interlocked.Increment(ref executions);
                if (call.Arguments.GetProperty("value").GetString() == "bad")
                    throw new ArgumentException("fixture input rejected");
                return ValueTask.FromResult(JsonSerializer.Serialize(new { ok = true, resource_id = "doc-1", value = "good" }));
            });

            await using var runtime = new AgentRuntime(
                new CorrectInputTransport(),
                new AgentContextManager(),
                registry);
            var result = await runtime.RunAsync(Request("Read the fixture using corrected arguments."), CancellationToken.None);
            Check(result.FinalText == "corrected-input-complete" && executions == 2,
                "CorrectInput recovery did not execute one changed-argument retry.");
        });

        await Test("AR-070 same-provider reobserve unlocks one bounded transient retry and preserves target", async () =>
        {
            var fetchExecutions = 0;
            var inspectExecutions = 0;
            var registry = new ToolRegistry();
            Register(registry, "fixture.fetch", (_, _) =>
            {
                var attempt = Interlocked.Increment(ref fetchExecutions);
                return ValueTask.FromResult(attempt == 1
                    ? JsonSerializer.Serialize(new { ok = false, error = "provider_busy", recoveryTools = new[] { "fixture.inspect" } })
                    : JsonSerializer.Serialize(new { ok = true, resource_id = "doc-1", value = "READY" }));
            });
            Register(registry, "fixture.inspect", (_, _) =>
            {
                Interlocked.Increment(ref inspectExecutions);
                return ValueTask.FromResult(JsonSerializer.Serialize(new { ok = true, resource_id = "doc-1", state = "ready" }));
            });

            var transport = new ReobserveTransport();
            await using var runtime = new AgentRuntime(transport, new AgentContextManager(), registry);
            var result = await runtime.RunAsync(Request("Read the exact fixture after provider recovery."), CancellationToken.None);

            Check(result.FinalText == "reobserve-retry-complete"
                && fetchExecutions == 2
                && inspectExecutions == 1
                && transport.SawBusyRecovery
                && transport.SawInspectResult,
                "Reobserve did not unlock exactly one same-target transient retry.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(Path.Combine(root, "ar070-recovery-policy-tests.txt"), lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string goal)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            goal,
            "fixture:ar070",
            null,
            ["AR-070 recovery fixture"],
            ["preserve exact resource identity"],
            ["do not invent success"],
            [],
            AgentTaskRiskClass.Low,
            new AgentVerificationPolicy(requireVerification: false));
        return new(
            contract,
            goal,
            new AgentPromptStablePrefix(AgentVersions.Current, "BASE POLICY", "SECURITY POLICY", "MODEL POLICY", ""),
            new AgentContextInput(TaskContract: contract.UserGoal, CurrentState: "AR-070 recovery policy fixture"),
            PromptCacheKey: "ar070",
            MaxToolRounds: 12,
            MaxRepairRounds: 2);
    }

    private static void Register(
        ToolRegistry registry,
        string name,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute,
        ToolPreferenceMetadata? preference = null,
        ToolReadiness? readiness = null)
    {
        registry.Register(new ToolDescriptor(
            name,
            new ToolNamespace("fixture", "AR-070 recovery fixture."),
            "Inspect the same exact fixture resource.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: false,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description = "Inspect one exact fixture resource.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            resource_id = new { type = "string" },
                            value = new { type = "string" },
                            state_token = new { type = "string" }
                        },
                        required = new[] { "resource_id" },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor(name.Replace('.', '-'), execute),
            provenance: new ToolProvenance("fixture-provider", "1.0.0", "fixture-server", "1.0.0"),
            resourceScope: new ToolResourceScope("fixture:ar070", "fixture:ar070"),
            serializationKey: "ar070-fixture",
            canProvideVerificationEvidence: false,
            preference: preference,
            readiness: readiness));
    }

    private static global::H2AgentLab.ToolCall Call(string id, string name, object arguments)
        => new(id, name, JsonSerializer.SerializeToElement(arguments))
        { Invocation = ToolInvocation.Create(Guid.Parse("11111111-1111-1111-1111-111111111111"), name, JsonSerializer.SerializeToElement(arguments)) };

    private static AgentTransportToolCall Search(string id, string query)
        => new(id, DeferredToolDiscovery.SearchToolName, JsonSerializer.Serialize(new { query }));

    private static AgentTransportToolCall Tool(string id, string name, object arguments)
        => new(id, name, JsonSerializer.Serialize(arguments));

    private sealed class NoProgressTransport : IAgentTransport
    {
        private int _continuations;
        public bool SawRecoveryState { get; private set; }
        public bool SawNoProgressBlock { get; private set; }
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(Search("search-fetch", "fixture fetch"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("ar070", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;
            if (_continuations == 1)
            {
                yield return AgentTransportEvent.Tool(Tool("fetch-1", "fixture.fetch", new { resource_id = "doc-1", value = "same" }));
                yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                yield break;
            }
            if (_continuations == 2)
            {
                SawRecoveryState = request.SupplementalUserMessages?.Any(message =>
                    message.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)
                    && message.Contains("\"requiresChangedEvidence\":true", StringComparison.Ordinal)
                    && message.Contains("check_provider_health", StringComparison.Ordinal)) == true;
                yield return AgentTransportEvent.Tool(Tool("fetch-2", "fixture.fetch", new { resource_id = "doc-1", value = "same" }));
                yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                yield break;
            }
            if (_continuations == 3)
            {
                SawNoProgressBlock = request.ToolResults.Single().Outcome?.Error?.Code == "recovery_no_progress";
                yield return AgentTransportEvent.TextDeltaEvent(
                    "Không thể tiếp tục vì provider vẫn bận và chưa có bằng chứng trạng thái mới; cần reobserve cùng tài nguyên trước khi thử lại.");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("ar070", "stop");
                yield break;
            }
            throw new InvalidOperationException("Unexpected no-progress continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CorrectInputTransport : IAgentTransport
    {
        private int _continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(Search("search-fetch", "fixture fetch"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("ar070", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;
            if (_continuations == 1)
            {
                yield return AgentTransportEvent.Tool(Tool("bad", "fixture.fetch", new { resource_id = "doc-1", value = "bad" }));
                yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                yield break;
            }
            if (_continuations == 2)
            {
                if (request.ToolResults.Single().Outcome?.Error?.RetryClass != ToolRetryClass.CorrectInput)
                    throw new InvalidOperationException("Invalid arguments lost CorrectInput classification.");
                yield return AgentTransportEvent.Tool(Tool("good", "fixture.fetch", new { resource_id = "doc-1", value = "good" }));
                yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                yield break;
            }
            if (_continuations == 3)
            {
                if (request.ToolResults.Single().IsError)
                    throw new InvalidOperationException("Corrected retry still failed.");
                yield return AgentTransportEvent.TextDeltaEvent("corrected-input-complete");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("ar070", "stop");
                yield break;
            }
            throw new InvalidOperationException("Unexpected corrected-input continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReobserveTransport : IAgentTransport
    {
        private int _continuations;
        public bool SawBusyRecovery { get; private set; }
        public bool SawInspectResult { get; private set; }
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(Search("search-fetch", "fixture fetch"));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("ar070", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;
            switch (_continuations)
            {
                case 1:
                    yield return AgentTransportEvent.Tool(Tool("fetch-1", "fixture.fetch", new { resource_id = "doc-1", value = "same" }));
                    yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                    yield break;
                case 2:
                    SawBusyRecovery = request.ToolResults.Single().Outcome?.Error?.RetryClass == ToolRetryClass.WaitThenReobserve
                        && request.SupplementalUserMessages?.Any(message =>
                            message.Contains("tool:fixture.inspect", StringComparison.Ordinal)
                            && message.Contains("\"minimumBackoffMilliseconds\":150", StringComparison.Ordinal)) == true;
                    yield return AgentTransportEvent.Tool(Search("search-inspect", "fixture inspect"));
                    yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                    yield break;
                case 3:
                    yield return AgentTransportEvent.Tool(Tool("inspect", "fixture.inspect", new { resource_id = "doc-1" }));
                    yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                    yield break;
                case 4:
                    SawInspectResult = !request.ToolResults.Single().IsError;
                    yield return AgentTransportEvent.Tool(Tool("fetch-2", "fixture.fetch", new { resource_id = "doc-1", value = "same" }));
                    yield return AgentTransportEvent.Complete("ar070", "tool_calls");
                    yield break;
                case 5:
                    if (request.ToolResults.Single().IsError)
                        throw new InvalidOperationException("Retry after reobserve was blocked or failed.");
                    yield return AgentTransportEvent.TextDeltaEvent("reobserve-retry-complete");
                    await Task.Yield();
                    yield return AgentTransportEvent.Complete("ar070", "stop");
                    yield break;
                default:
                    throw new InvalidOperationException("Unexpected reobserve continuation.");
            }
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
