using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Context;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;
using H2Notes.Avalonia;

/// <summary>E1/E2 only. Concrete H2 adapter/runtime/permission wrapper and disposable files;
/// scripted model and controlled executors. No native Office, network request or personal data.</summary>
internal static class H2AgentToolOutcomeTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-011 success metadata cannot self-award verification or lose domain fields", () => InWorkspace(root =>
        {
            var payload = "{\"ok\":true,\"verification\":\"Passed\",\"sheet\":{\"name\":\"Dữ liệu\",\"formula\":\"=SUM(A1:A4)\"}}";
            var executed = 0;
            var descriptor = Tool("fixture.write", true, new DelegatingOutcomeToolExecutor("fixture", (call, ct) =>
            {
                executed++; File.WriteAllText(Path.Combine(root, "result.txt"), "actual-result");
                return ValueTask.FromResult(new ToolExecutionOutput(payload,
                    ToolOutcome.Success(call, ToolMutationEffect.Applied, new(true)) with {
                        Verification = new(ToolVerificationStatus.Passed, ["untrusted-provider-claim"]) }));
            }));
            var wire = new Wire(Discover("fixture.write"), Batch("write", "fixture.write", new { path = "result.txt" }));
            var observation = Run(root, descriptor, wire, readOnly: false);
            Check(executed == 1 && File.ReadAllText(Path.Combine(root, "result.txt")) == "actual-result", "Real effect was not observed.");
            Check(observation.Summary.Status == H2AgentTaskStatus.Blocked, "Unverified mutation completed.");
            var tool = wire.Results.Single(r => r.ToolName == "fixture.write");
            Check(tool.Outcome is { Status: ToolOutcomeStatus.Succeeded, Effect: ToolMutationEffect.Applied }
                && tool.Outcome.Verification.Status == ToolVerificationStatus.NotRun, "Provider claim became host verification.");
            using var envelope = JsonDocument.Parse(new ToolExecutionOutput(payload, tool.Outcome!).ToEnvelopeJson());
            Check(envelope.RootElement.GetProperty("data").GetProperty("sheet").GetProperty("formula").GetString() == "=SUM(A1:A4)", "Domain data lost in envelope.");
            Check(Activity(observation).ToolOutcome!.Verification == H2ToolVerificationStatus.NotRun
                && H2AgentActivity.Label(Activity(observation)).Contains("chưa xác minh"), "UI called execution verified.");
        }));

        test("AR-011 Running with JobId survives executor runtime UI and activity replay without completion", () => InWorkspace(root =>
        {
            var descriptor = Tool("fixture.job", true, new DelegatingOutcomeToolExecutor("fixture", (call, ct) =>
                ValueTask.FromResult(new ToolExecutionOutput("{\"job\":\"controlled-job-1\"}",
                    ToolOutcome.Success(call, ToolMutationEffect.None, new(false, Reason: "awaiting_job")) with {
                        Status = ToolOutcomeStatus.Running, Job = new("controlled-job-1", "cursor-0") }))));
            var wire = new Wire(Discover("fixture.job"), Batch("job", "fixture.job", new { }));
            var observation = Run(root, descriptor, wire, false);
            Check(observation.Summary.Status == H2AgentTaskStatus.Blocked, "Running job was declared complete.");
            var activity = Activity(observation);
            Check(activity.ToolOutcome is { Status: H2ToolRunStatus.Running, JobId: "controlled-job-1" }, "Running job became a generic failure/success.");
            Check(H2AgentActivity.Label(activity).StartsWith("Đang chạy"), "UI lost running state.");
            Check(wire.Results.Last().Content.Contains("HOST TOOL OUTCOME"), "Model did not receive the pending operation warning.");
            using var reopened = new H2ProductionAgentAdapter(Path.Combine(root, "state"), () => new(Profile(), ""));
            var replay = reopened.ObserveTask(observation.Summary.TaskId).Progress.Single(p => p.ToolOutcome?.JobId == "controlled-job-1");
            Check(replay.ToolOutcome == activity.ToolOutcome, "Activity replay rewrote job/invocation state.");
        }));

        test("AR-011 preflight rejection records no effect and invokes no mutation", () => InWorkspace(root =>
        {
            var executed = 0;
            var descriptor = Tool("fixture.preflight", true, new DelegatingToolExecutor("fixture", (call, ct) => {
                executed++; return ValueTask.FromResult("{\"ok\":true}"); })) with { };
            descriptor = new ToolDescriptor(descriptor.Name, descriptor.Namespace, descriptor.Description,
                descriptor.Risk, descriptor.Access, false, "v1", descriptor.CallableSchema, descriptor.Executor,
                resourceScope: new("fixture-resource", "temporary"),
                preflight: call => ToolOutcomeBridge.Failure(call, null, "invalid_arguments", ToolErrorPhase.Preflight, ToolMutationEffect.None));
            var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("reject", descriptor.Name, new { })), false);
            Check(executed == 0 && !File.Exists(Path.Combine(root, "result.txt")), "Preflight performed a write.");
            Check(Activity(observation).ToolOutcome is { Status: H2ToolRunStatus.Rejected, Effect: H2ToolMutationEffect.None, ErrorPhase: "Preflight" }, "Preflight became an uncertain write.");
        }));

        test("AR-011 actual write followed by lost response fences same-batch and next-round repeats", () => InWorkspace(root =>
        {
            var executions = 0;
            ToolInvocation? dispatched = null;
            var descriptor = Tool("fixture.lost", true, new DelegatingToolExecutor("fixture", (call, ct) => {
                executions++; dispatched = call.Invocation;
                File.AppendAllText(Path.Combine(root, "effect.txt"), "ONE-WRITE\n");
                throw new IOException("sensitive-token=DO-NOT-LOG https://user:password@example.test");
            }));
            var args = new { path = "effect.txt" };
            var wire = new Wire(Discover(descriptor.Name), [Call("lost-1", descriptor.Name, args), Call("lost-2", descriptor.Name, args)],
                Batch("lost-3", descriptor.Name, args));
            var observation = Run(root, descriptor, wire, false);
            Check(executions == 1 && File.ReadAllText(Path.Combine(root, "effect.txt")) == "ONE-WRITE\n", "Uncertain write was repeated.");
            Check(observation.Summary.Status == H2AgentTaskStatus.Blocked, "Lost response allowed completion.");
            var first = wire.Results.Single(r => r.ToolCallId == "lost-1");
            Check(first.Outcome is { Status: ToolOutcomeStatus.OutcomeUnknown, Effect: ToolMutationEffect.Unknown }
                && first.Outcome.Error!.RetryClass == ToolRetryClass.ReconcileRequired, "Lost response was classified as safe retry.");
            Check(first.Outcome!.Invocation == dispatched, "Executor and runtime invocation IDs differ.");
            var attempts = wire.Results.Where(r => r.ToolName == descriptor.Name).Select(r => r.Outcome!).ToArray();
            Check(attempts.Select(o => o.Invocation.InvocationId).Distinct().Count() == 3
                && attempts.Select(o => o.Invocation.LogicalOperationId).Distinct().Count() == 1, "Attempt versus logical identity was lost.");
            var persisted = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "state"), "tool-outcomes.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
            Check(!persisted.Contains("DO-NOT-LOG") && !persisted.Contains("password") && !first.Content.Contains("sensitive-token"), "Exception body leaked into outcome diagnostics.");
            Check(observation.Progress.Any(p => p.ToolOutcome?.Status == H2ToolRunStatus.OutcomeUnknown), "UI lost uncertain effect.");
        }));

        test("AR-011 partially applied operation remains distinct from failed or verified", () => InWorkspace(root =>
        {
            var descriptor = Tool("fixture.partial", true, new DelegatingOutcomeToolExecutor("fixture", (call, ct) => {
                File.WriteAllText(Path.Combine(root, "partial.txt"), "first-half");
                return ValueTask.FromResult(ToolOutcomeBridge.Failure(call, null, "partially_applied", ToolErrorPhase.Execution,
                    ToolMutationEffect.PartiallyApplied, "{\"changed\":[\"A1\"],\"remaining\":[\"A2\"]}")); }));
            var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("partial", descriptor.Name, new { })), false);
            Check(File.ReadAllText(Path.Combine(root, "partial.txt")) == "first-half", "Partial effect fixture did not execute.");
            Check(Activity(observation).ToolOutcome is { Status: H2ToolRunStatus.PartiallyApplied, Effect: H2ToolMutationEffect.PartiallyApplied }, "Partial effect was flattened.");
            Check(observation.Summary.Status != H2AgentTaskStatus.Completed, "Partial effect completed the goal.");
        }));

        test("AR-011 paged output preserves exact cursor and typed domain payload", () => InWorkspace(root =>
        {
            const string payload = "{\"rows\":[{\"id\":7,\"formula\":\"=A1+$B$2\",\"name\":\"Đầu trang\"}],\"nativeFields\":{\"hidden\":true}}";
            var descriptor = Tool("fixture.page", false, new DelegatingOutcomeToolExecutor("fixture", (call, ct) =>
                ValueTask.FromResult(new ToolExecutionOutput(payload, ToolOutcome.Success(call, ToolMutationEffect.None,
                    new(false, "opaque-page-2", "paged"))))));
            var wire = new Wire(Discover(descriptor.Name), Batch("page", descriptor.Name, new { }));
            var observation = Run(root, descriptor, wire, true);
            var result = wire.Results.Last();
            Check(result.Content == payload, "Non-evidence domain payload was flattened or reserialized.");
            Check(result.Outcome!.Completeness == new ToolCompleteness(false, "opaque-page-2", "paged"), "Paging was represented as complete.");
            Check(Activity(observation).ToolOutcome is { Complete: false, NextCursor: "opaque-page-2" }
                && H2AgentActivity.Label(Activity(observation)).Contains("giới hạn"), "UI lost completeness.");
        }));

        foreach (var malformed in new[] { "{broken-json", "{\"ok\":\"success\"}", "{\"ok\":true,\"success\":false}" })
            test("AR-011 malformed structured payload fails closed " + malformed, () => InWorkspace(root =>
            {
                var descriptor = Tool("fixture.malformed", false, new DelegatingToolExecutor("fixture", (call, ct) => ValueTask.FromResult(malformed)));
                var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("bad", descriptor.Name, new { })), true);
                Check(Activity(observation).ToolOutcome is { ErrorCode: "invalid_result", Verification: H2ToolVerificationStatus.NotRun }, "Malformed result was accepted.");
                Check(observation.Summary.Status != H2AgentTaskStatus.Completed, "Malformed structured output completed.");
            }));

        foreach (var malformed in new[] { "{\"isError\":\"true\"}", "{\"ok\":true,\"isError\":true}" })
            test("AR-011 malformed negative control flag rejects " + malformed, () => InWorkspace(root =>
            {
                var descriptor = Tool("fixture.mcpbad", false, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult(malformed)));
                var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("badflag", descriptor.Name, new { })), true);
                Check(Activity(observation).ToolOutcome!.ErrorCode == "invalid_result"
                    && observation.Summary.Status != H2AgentTaskStatus.Completed, "Malformed MCP control flag was accepted.");
            }));

        test("AR-011 MCP tool error keeps domain blocks and cannot complete the production task", () => InWorkspace(root =>
        {
            const string payload = "{\"content\":[{\"type\":\"text\",\"text\":\"Controlled provider failure\"}],\"isError\":true}";
            var descriptor = Tool("fixture.mcperror", false, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult(payload)));
            var wire = new Wire(Discover(descriptor.Name), Batch("mcperror", descriptor.Name, new { }));
            var observation = Run(root, descriptor, wire, true);
            Check(wire.Results.Last().Content == payload && wire.Results.Last().IsError,
                "MCP failure blocks were flattened or converted to success.");
            Check(Activity(observation).ToolOutcome is { Status: H2ToolRunStatus.Failed, Effect: H2ToolMutationEffect.None }
                && observation.Summary.Status != H2AgentTaskStatus.Completed, "MCP tool error allowed false completion.");
        }));

        test("AR-011 malformed typed Running without JobId is rejected", () => InWorkspace(root =>
        {
            var descriptor = Tool("fixture.badjob", false, new DelegatingOutcomeToolExecutor("fixture", (call, ct) =>
                ValueTask.FromResult(new ToolExecutionOutput("{}", ToolOutcome.Success(call, ToolMutationEffect.None) with { Status = ToolOutcomeStatus.Running }))));
            var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("badjob", descriptor.Name, new { })), true);
            Check(Activity(observation).ToolOutcome!.ErrorCode == "invalid_result", "Running without job identity was accepted.");
        }));

        test("AR-011 unavailable registered tool is discoverable but never exposed or executed", () => InWorkspace(root =>
        {
            var invoked = 0;
            var descriptor = Tool("fixture.unavailable", false, new DelegatingToolExecutor("fixture", (call, ct) => {
                invoked++; return ValueTask.FromResult("{}"); })) with { Readiness = new(ToolReadinessState.NeedsConfiguration) };
            var wire = new Wire(Discover(descriptor.Name), Batch("unavailable", descriptor.Name, new { }));
            var observation = Run(root, descriptor, wire, true);
            using var search = JsonDocument.Parse(wire.Results.First().Content);
            var capability = search.RootElement.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("name").GetString() == descriptor.Name);
            Check(capability.GetProperty("readiness").GetString() == "NeedsConfiguration" && !capability.GetProperty("executable").GetBoolean(), "Discovery advertised unavailable executor as ready.");
            Check(invoked == 0 && !wire.Loaded.Contains(descriptor.Name), "Unready tool got an executable schema or executed.");
            Check(Activity(observation).ToolOutcome is { Status: H2ToolRunStatus.Rejected, ErrorCode: "needs_configuration", Effect: H2ToolMutationEffect.None }, "Unavailable tool became generic success/failure.");
        }));

        test("AR-011 production search explains missing search and browser backends without starting them", () => InWorkspace(root =>
        {
            var wire = new Wire(Discover("web search browser"));
            var observation = Run(root, null, wire, true);
            using var search = JsonDocument.Parse(wire.Results.Single().Content);
            var unavailable = search.RootElement.GetProperty("unavailableCapabilities");
            Check(unavailable.EnumerateArray().Any(c => c.GetProperty("name").GetString() == "web.search"), "Missing search backend was not explained.");
            Check(unavailable.EnumerateArray().Any(c => c.GetProperty("name").GetString() == "web.open_browser"), "Missing browser backend was not explained.");
            Check(!wire.Loaded.Contains("web.search") && !wire.Loaded.Contains("web.open_browser"), "Nonexistent backend got a callable schema.");
            Check(observation.Summary.Status == H2AgentTaskStatus.Completed, "Metadata-only discovery was not executable.");
        }));

        test("AR-011 permission rejection is not a dispatched mutation", () => InWorkspace(root =>
        {
            var invoked = 0;
            var descriptor = Tool("fixture.denied", true, new DelegatingToolExecutor("fixture", (call, ct) => { invoked++; return ValueTask.FromResult("{}"); }));
            var observation = Run(root, descriptor, new Wire(Discover(descriptor.Name), Batch("denied", descriptor.Name, new { })), true);
            Check(invoked == 0, "Read-only mode dispatched a write.");
            Check(Activity(observation).ToolOutcome is { Status: H2ToolRunStatus.Rejected, Effect: H2ToolMutationEffect.None, ErrorCode: "permission_denied" }, "Permission denial lost no-effect semantics.");
        }));

        test("AR-011 cancellation preserves cancellation and unknown effect without automatic retry", () => InWorkspace(root =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var descriptor = Tool("fixture.cancel", true, new DelegatingToolExecutor("fixture", async (call, ct) => {
                calls++; File.WriteAllText(Path.Combine(root, "cancel-effect.txt"), "effect-before-cancel"); entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); return "{}"; }));
            var wire = new Wire(Discover(descriptor.Name), Batch("cancel", descriptor.Name, new { }));
            using var adapter = Adapter(root, descriptor, wire);
            try
            {
                var id = adapter.StartTaskAsync(null, "Controlled cancellation fixture", Context(root, false), false).Result;
                entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); adapter.CancelTask(id);
                var done = Wait(adapter, id); var activity = Activity(adapter.ObserveTask(id));
                Check(calls == 1 && done.Status == H2AgentTaskStatus.Cancelled && wire.Cancelled, "Cancellation was swallowed or retried.");
                Check(activity.ToolOutcome is { Status: H2ToolRunStatus.Cancelled, Effect: H2ToolMutationEffect.Unknown }, "Cancellation incorrectly claimed no effect.");
            }
            finally { Drain(adapter); }
        }));

        test("AR-011 legacy denial retains its code without inventing proof of no effect", () =>
        {
            var call = new ToolCall("denial", "fixture.denial", JsonSerializer.SerializeToElement(new { }));
            var descriptor = Tool(call.Name, true, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}")));
            const string denial = "{\"success\":false,\"code\":\"denied\",\"error\":\"User declined shared fixture mutation.\"}";
            var unknown = ToolOutcomeBridge.FromLegacy(call, descriptor, denial).Outcome;
            Check(unknown.Error?.Code == "permission_denied" && unknown.Effect == ToolMutationEffect.Unknown,
                "Legacy error message replaced typed code or claimed an unproven no-effect.");
            var preflight = ToolOutcomeBridge.FromLegacy(call, descriptor, denial[..^1] + ",\"mutationApplied\":false}").Outcome;
            Check(preflight is { Status: ToolOutcomeStatus.Rejected, Effect: ToolMutationEffect.None }
                && preflight.Error?.Code == "permission_denied", "Explicit no-effect denial lost preflight semantics.");
        });

        test("AR-011 typed preflight busy stale and execution deadlines have distinct retry semantics", () =>
        {
            var call = new ToolCall("taxonomy", "fixture.taxonomy", JsonSerializer.SerializeToElement(new { }));
            call = call with { Invocation = ToolInvocation.Create(Guid.NewGuid(), call.Name, call.Arguments) };
            var descriptor = Tool(call.Name, true, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}")));
            foreach (var code in new[] { "invalid_arguments", "provider_busy", "stale_resource", "modal_blocked", "needs_configuration", "permission_denied" })
            {
                var output = ToolOutcomeBridge.FromException(call, descriptor, new ToolPreflightException(code));
                Check(output.Outcome.Status == ToolOutcomeStatus.Rejected && output.Outcome.Effect == ToolMutationEffect.None
                    && output.Outcome.Error!.Code == code, "Typed preflight category lost: " + code);
            }
            var timeout = ToolOutcomeBridge.FromException(call, descriptor, new TimeoutException("never-log-this-token"));
            Check(timeout.Outcome.Status == ToolOutcomeStatus.OutcomeUnknown && timeout.Outcome.Error!.Code == "deadline_exceeded"
                && timeout.Outcome.Error.RetryClass == ToolRetryClass.ReconcileRequired, "Mutation timeout became a safe blind retry.");
            Check(!timeout.DomainPayload.Contains("never-log-this-token"), "Unsafe exception message was exposed.");
        });

        test("AR-011 invalid metadata lists identities and enums fail closed without escaping wrapper", () =>
        {
            var descriptor = Tool("fixture.invalid", false, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}")));
            var call = new ToolCall("badmeta", descriptor.Name, JsonSerializer.SerializeToElement(new { }));
            call = call with { Invocation = ToolInvocation.Create(Guid.NewGuid(), call.Name, call.Arguments) };
            var valid = ToolOutcome.Success(call, ToolMutationEffect.None, new(true));
            foreach (var invalid in new[] {
                valid with { Invocation = null! }, valid with { ArtifactRefs = null! },
                valid with { EvidenceRefs = Enumerable.Repeat("ref", 33).ToArray() },
                valid with { Status = (ToolOutcomeStatus)999 },
                valid with { Completeness = new(true, "contradictory-cursor") },
                valid with { Status = ToolOutcomeStatus.Failed, Error = new("invalid_arguments", ToolErrorPhase.Execution,
                    "provider secret", ToolRetryClass.Never, null!, ToolMutationEffect.None) } })
            {
                var result = ToolOutcomeBridge.Validate(new("{}", invalid), call, descriptor);
                Check(result.Outcome.IsError && result.Outcome.Error!.Code == "invalid_result", "Invalid control metadata was accepted.");
            }
            var a = ToolInvocation.Create(Guid.Empty, "tool", JsonSerializer.SerializeToElement(new { z = new { b = 2, a = 1 }, x = 1 }));
            var b = ToolInvocation.Create(Guid.Empty, "tool", JsonSerializer.SerializeToElement(new { x = 1, z = new { a = 1, b = 2 } }));
            Check(a.LogicalOperationId == b.LogicalOperationId && a.InvocationId != b.InvocationId,
                "Nested JSON ordering changed logical operation identity or reused attempt identity.");
        });

        test("AR-011 advertised output bound uses existing artifact and marks projection incomplete", () => InWorkspace(root =>
        {
            var payload = JsonSerializer.Serialize(new { rows = new string('x', 5_000), complete = true });
            var original = Tool("fixture.bounded", false, new DelegatingOutcomeToolExecutor("fixture", (call, ct) =>
                ValueTask.FromResult(new ToolExecutionOutput(payload, ToolOutcome.Success(call, ToolMutationEffect.None, new(true))))));
            var descriptor = new ToolDescriptor(original.Name, original.Namespace, original.Description, original.Risk, original.Access,
                true, "v1", original.CallableSchema, original.Executor, limits: new(MaxOutputCharacters: 1_024), resultFormat: ToolResultFormat.Json);
            var wire = new Wire(Discover(descriptor.Name), Batch("bounded", descriptor.Name, new { }));
            _ = Run(root, descriptor, wire, true);
            var observed = wire.Results.Last();
            Check(observed.Content.Length <= 1_024 && observed.Outcome!.Completeness is { Complete: false, Reason: "artifact_projection" }
                && observed.Outcome.ArtifactRefs.Count == 1, "Bounded output lost its artifact or pretended complete.");
        }));

        test("AR-011 provider cached health changes discovery without reconnect and forwards host identity", () =>
        {
            var provider = new CachedProvider(); var registry = new ToolRegistry();
            new CapabilityProviderToolRegistryAdapter(registry).LoadSelectedAsync(provider, ["fixture.provider"], CancellationToken.None).GetAwaiter().GetResult();
            Check(registry.TryGet("fixture.provider", out var tool), "Provider descriptor missing.");
            var call = new ToolCall("provider", tool.Name, JsonSerializer.SerializeToElement(new { }));
            call = call with { Invocation = ToolInvocation.Create(Guid.NewGuid(), call.Name, call.Arguments) };
            var result = ToolOutcomeBridge.ExecuteAsync(tool, call, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Check(provider.Received == call.Invocation && result.Outcome.Invocation == provider.Received, "Provider identity propagation failed.");
            provider.Status = ProviderHealthStatus.Disconnected;
            var discovery = new DeferredToolDiscovery(registry);
            var batch = discovery.SearchAndLoad(tool.Name);
            Check(batch.CallableSchemas.Count == 0 && tool.CurrentReadiness.State == ToolReadinessState.Unavailable && provider.Connections == 0, "Discovery connected a provider or exposed stale readiness.");
        });
    }

    private static ToolDescriptor Tool(string name, bool write, IAgentToolExecutor executor) => new(name,
        new("fixture", "Controlled AR-011 E2 fixture"), "Controlled " + name,
        write ? AgentToolRisk.Medium : AgentToolRisk.Low, write ? AgentToolAccess.Mutating : AgentToolAccess.ReadOnly,
        !write, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new { name,
            description = "Controlled outcome test", parameters = new { type = "object", properties = new { path = new { type = "string" } } } } }),
        executor, resourceScope: new("fixture-resource", "temporary test files"), resultFormat: ToolResultFormat.Json);
    private static AgentTransportToolCall Call(string id, string name, object args) => new(id, name, JsonSerializer.Serialize(args));
    private static AgentTransportToolCall[] Batch(string id, string name, object args) => [Call(id, name, args)];
    private static AgentTransportToolCall[] Discover(string name) => Batch("discover", "tool_search", new { query = name });
    private static AiProfile Profile() => new() { Model = "scripted-no-network", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" };
    private static H2AgentTaskContext Context(string root, bool readOnly) => new(root, "Disposable AR-011 fixture",
        PermissionScope: readOnly ? null : WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
    private static H2ProductionAgentAdapter Adapter(string root, ToolDescriptor? tool, Wire wire)
        => new(Path.Combine(root, "state"), () => new(Profile(), ""), runtimeFactory: new FixtureFactory(tool, wire));
    private static H2AgentTaskObservation Run(string root, ToolDescriptor? tool, Wire wire, bool readOnly)
    {
        using var adapter = Adapter(root, tool, wire);
        try
        {
            var id = adapter.StartTaskAsync(null, "Execute only the controlled outcome fixture", Context(root, readOnly), readOnly).Result;
            _ = Wait(adapter, id); return adapter.ObserveTask(id);
        }
        finally { Drain(adapter); }
    }
    private static H2AgentProgress Activity(H2AgentTaskObservation o)
    {
        var activity = o.Progress.Last(p => p.ToolOutcome is not null && p.Message != "tool_search");
        Check(WorkAssistantActivityText.FromProgress(activity) == H2AgentActivity.Label(activity),
            "Desktop ticker disagreed with typed chat activity.");
        return activity;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Drain(H2ProductionAgentAdapter adapter) => adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id)
    {
        var until = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < until)
        {
            var summary = adapter.GetTaskSummary(id);
            if (H2AgentActivity.IsTerminal(summary.Status)) return summary;
            Thread.Sleep(10);
        }
        throw new TimeoutException("AR-011 bounded fixture did not stop.");
    }
    private static void InWorkspace(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar011-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Exception? primary = null;
        try { body(root); } catch (Exception ex) { primary = ex; throw; }
        finally { try { Directory.Delete(root, true); } catch (Exception cleanup) when (primary is not null) { primary.Data["cleanup"] = cleanup.GetType().Name; } }
    }
    private sealed class FixtureFactory(ToolDescriptor? tool, Wire wire) : IAgentRuntimeFactory
    {
        public AgentRuntime Create(AiProfile profile, string key, AgentTools tools, AgentContextManager context, AgentRunTelemetry telemetry)
        {
            var registry = NormalRuntimeToolRegistry.Create(tools);
            if (tool is not null) registry.Register(tool);
            // Exercise the ACTUAL production permission/outcome wrapper. Reflection avoids adding
            // a public testing-only registration bypass to production code.
            var session = typeof(AgentTools).GetProperty("ProductionSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;
            registry = (ToolRegistry)session.GetType().GetMethod("Configure")!.Invoke(session,
                [tools, registry, new List<IAgentRuntimeDomainVerifier>()])!;
            return new AgentRuntime(wire, context, registry, permissionPolicy: (IAgentRuntimePermissionPolicy)session,
                evidenceProjector: new(new ArtifactStore(tools.StateRoot)), hooks: new AgentRuntimeHooks(telemetry));
        }
    }
    private sealed class Wire(params AgentTransportToolCall[][] rounds) : IAgentTransport
    {
        private int _round;
        public List<AgentToolResult> Results { get; } = [];
        public HashSet<string> Loaded { get; } = [];
        public bool Cancelled { get; private set; }
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken ct = default) => Emit(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken ct = default)
        {
            Results.AddRange(request.ToolResults); foreach (var tool in request.NewlyLoadedTools ?? []) Loaded.Add(tool.Name);
            return Emit(ct);
        }
        private async IAsyncEnumerable<AgentTransportEvent> Emit([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await Task.CompletedTask;
            if (_round < rounds.Length) { foreach (var call in rounds[_round++]) yield return AgentTransportEvent.Tool(call); }
            else yield return AgentTransportEvent.TextDeltaEvent("Fixture final candidate; host must decide completion.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CachedProvider : IInvocationAwareCapabilityProvider
    {
        public ProviderHealthStatus Status { get; set; } = ProviderHealthStatus.Ready;
        public ToolInvocation? Received { get; private set; }
        public int Connections { get; private set; }
        public ProviderProvenance Provenance => new("fixture-provider", "1.0", "fixture", "none");
        public ProviderHealthState Health => new(Status, DateTime.UtcNow);
        public Task ConnectAsync(CancellationToken ct) { Connections++; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderNamespaceSummary>>([]);
        public Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderToolSummary>>([]);
        public Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            var descriptor = Tool("fixture.provider", false, new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}")));
            return Task.FromResult<IReadOnlyList<ProviderToolDefinition>>([new(new("fixture.provider", "fixture", "Cached fixture provider", AgentToolAccess.ReadOnly, AgentToolRisk.Low, true, "v1", "fixture-resource", "fixture-provider", "1.0"), descriptor.CallableSchema)]);
        }
        public Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderResourceSummary>>([]);
        public Task<string> ReadResourceAsync(string id, CancellationToken ct) => Task.FromResult("{}");
        public ValueTask<string> ExecuteToolAsync(string name, JsonElement args, CancellationToken ct) => throw new InvalidOperationException("Identity-aware path required.");
        public ValueTask<string> ExecuteToolAsync(string name, JsonElement args, ToolInvocation invocation, CancellationToken ct)
        { Received = invocation; return ValueTask.FromResult("{\"ok\":true}"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
