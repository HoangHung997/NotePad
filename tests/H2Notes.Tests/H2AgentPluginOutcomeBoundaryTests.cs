using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Plugins;
using H2AgentLab.Session;
using H2AgentLab.Tools;

// E1/E2 only: admitted local ZIPs, real disposable files and the actual scheduler;
// the resolver is an explicitly declared fixture, not a native/provider acceptance claim.
internal static class H2AgentPluginOutcomeBoundaryTests
{
    private const string Name = "fixture.echo";
    private const string Payload = "{\"accepted\":true,\"text\":\"Dữ liệu =SUM($A$1:$A$3)\"}";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static void Run(Action<string, Action> test)
    {
        foreach (var status in Enum.GetValues<ToolOutcomeStatus>())
            test("AR-064 outcome wrapper preserves typed metadata before host validation " + status,
                () => Isolated(async root =>
                {
                    var artifactStore = new ArtifactStore(root);
                    var artifact = artifactStore.StoreText(AgentArtifactKind.ToolOutput, "outcome-proof", Name,
                        "Exact observed fixture output", "Not a semantic verification", 1).Handle;
                    var resolver = new OutcomeResolver(call => Output(call, status, artifact.Id));
                    var (manager, registry) = Install(root, resolver);
                    var descriptor = registry.Tools.Single(); var call = Call();
                    var output = await ToolOutcomeBridge.ExecuteAsync(descriptor, call, default);
                    Check(resolver.TypedCalls == 1 && resolver.LegacyCalls == 0,
                        "Version wrapper downgraded the typed executor to its legacy string method.");
                    Check(output.DomainPayload == Payload && output.Outcome.Invocation == call.Invocation,
                        "Exact domain bytes or host invocation identity changed.");
                    Check(output.Outcome.Status == status && output.Outcome.Effect == Effect(status),
                        "Typed status/effect was replaced by legacy success classification.");
                    Check(output.Outcome.Resource == new ToolOutcomeResource("fixture-resource", "observed-v1")
                        && output.Outcome.ArtifactRefs.SequenceEqual(new[] { artifact.Id })
                        && output.Outcome.EvidenceRefs.SequenceEqual(new[] { "fixture-evidence-reference" }),
                        "The wrapper lost resource or evidence references.");
                    Check(output.Outcome.Completeness == Completeness(status), "Paging metadata changed.");
                    Check(output.Outcome.Job == (status == ToolOutcomeStatus.Running
                        ? new ToolOutcomeJob("job-fixture", "output-cursor") : null), "Job identity changed.");
                    Check(output.Outcome.Verification.Status == ToolVerificationStatus.NotRun
                        && output.Outcome.Verification.ReportRefs.Count == 0
                        && !descriptor.CanProvideVerificationEvidence,
                        "Plugin supplied verification was promoted to host proof.");
                    Check(artifactStore.ReadText(artifact.Id) == "Exact observed fixture output", "Source bytes changed.");
                    manager.Disable(H2AgentPluginLifecycleTests.Id); // completion released the version-call lease
                    Record("metadata-" + status, new { status = output.Outcome.Status.ToString(),
                        effect = output.Outcome.Effect.ToString(), resolver.TypedCalls, resolver.LegacyCalls,
                        output.Outcome.Invocation, output.Outcome.Job, output.Outcome.Completeness,
                        verification = output.Outcome.Verification.Status.ToString(), artifactHash = artifact.Sha256 });
                }));

        foreach (var status in new[] { ToolOutcomeStatus.Running, ToolOutcomeStatus.OutcomeUnknown,
                     ToolOutcomeStatus.PartiallyApplied })
            test("AR-064 outcome wrapper fences queued and later same-resource writes " + status,
                () => Isolated(async root =>
                {
                    var marker = Path.Combine(root, "effect.txt");
                    var sentinel = Path.Combine(root, "unrelated.txt");
                    File.WriteAllText(sentinel, "KEEP UNRELATED BYTES", Encoding.UTF8);
                    var before = File.ReadAllBytes(sentinel);
                    var resolver = new OutcomeResolver(call =>
                    {
                        File.AppendAllText(marker, "ONE EFFECT\n", Encoding.UTF8);
                        return Output(call, status);
                    });
                    var (_, registry) = Install(root, resolver);
                    using var scheduler = new ToolExecutionScheduler();
                    var descriptor = registry.Tools.Single();
                    var taskId = Guid.NewGuid();
                    var first = new ToolExecutionRequest(descriptor, Call(taskId: taskId), "resource-a");
                    var second = new ToolExecutionRequest(descriptor, Call(taskId: taskId), "resource-a");
                    var results = await scheduler.ExecuteBatchAsync([first, second], default);
                    var later = (await scheduler.ExecuteBatchAsync(
                        [new(descriptor, Call(taskId: taskId), "resource-a")], default)).Single();
                    Check(File.ReadAllText(marker, Encoding.UTF8) == "ONE EFFECT\n",
                        "Typed uncertainty/running state was lost and a queued write repeated the effect.");
                    Check(results[0].Outcome?.Status == status && results[0].Outcome!.IsPending,
                        "The scheduler lost the first operation's pending state.");
                    Check(results[1].Outcome?.Status == ToolOutcomeStatus.Rejected
                        && later.Outcome?.Status == ToolOutcomeStatus.Rejected,
                        "Same-resource mutation was not fenced in this and the next batch.");
                    Check(results[1].Outcome!.Effect == ToolMutationEffect.None
                        && later.Outcome!.Effect == ToolMutationEffect.None,
                        "Rejected queued calls were presented as dispatched effects.");
                    Check(resolver.TypedCalls == 1 && resolver.LegacyCalls == 0, "A blocked write reached the resolver.");
                    Check(before.SequenceEqual(File.ReadAllBytes(sentinel)), "An unrelated file changed.");
                    Record("scheduler-" + status, new { status = results[0].Outcome!.Status.ToString(),
                        queued = results[1].Outcome!.Status.ToString(), later = later.Outcome!.Status.ToString(),
                        resolver.TypedCalls, resolver.LegacyCalls,
                        effectSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(marker))),
                        unrelatedSha256 = Convert.ToHexString(SHA256.HashData(before)) });
                }));

        test("AR-064 outcome wrapper cannot hide contradictory identity from the host bridge",
            () => Isolated(async root =>
            {
                var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded) with
                { Outcome = Output(call, ToolOutcomeStatus.Succeeded).Outcome with
                    { Invocation = ToolInvocation.Create(Guid.NewGuid(), Name, call.Arguments) } });
                var (_, registry) = Install(root, resolver);
                var output = await ToolOutcomeBridge.ExecuteAsync(registry.Tools.Single(), Call(), default);
                Check(output.Outcome.Status == ToolOutcomeStatus.OutcomeUnknown
                    && output.Outcome.Error?.Code == "invalid_result", "A foreign typed result became legacy success.");
                Check(resolver.TypedCalls == 1 && resolver.LegacyCalls == 0, "Typed result was not validated.");
            }));

        test("AR-064 outcome wrapper legacy callers receive exact domain bytes from one typed execution",
            () => Isolated(async root =>
            {
                var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                var (manager, registry) = Install(root, resolver);
                var output = await registry.Tools.Single().Executor.ExecuteAsync(Call(), default);
                Check(output == Payload && resolver.TypedCalls == 1 && resolver.LegacyCalls == 0,
                    "Legacy compatibility either changed the payload or duplicated execution.");
                manager.Disable(H2AgentPluginLifecycleTests.Id);
            }));

        test("AR-064 outcome wrapper retains legacy-only executor compatibility",
            () => Isolated(async root =>
            {
                var resolver = new H2AgentPluginLifecycleTests.Resolver();
                var (manager, registry) = Install(root, resolver);
                var descriptor = registry.Tools.Single();
                Check(descriptor.Executor is not IAgentToolOutcomeExecutor, "Legacy-only executor was misadvertised.");
                var output = await ToolOutcomeBridge.ExecuteAsync(descriptor, Call(), default);
                Check(output.DomainPayload == "{\"version\":\"1.0.0\"}" && resolver.Calls == 1,
                    "Legacy-only executor payload or dispatch count changed.");
                manager.Disable(H2AgentPluginLifecycleTests.Id);
            }));

        foreach (var revoke in new[] { "disable", "quarantine", "update", "uninstall" })
            test("AR-064 outcome wrapper captured typed executor cannot bypass version revocation " + revoke,
                () => Isolated(async root =>
                {
                    var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                    var (manager, registry) = Install(root, resolver);
                    var old = registry.Tools.Single().Executor as IAgentToolOutcomeExecutor;
                    Check(old is not null, "Typed executor contract was lost at registration.");
                    switch (revoke)
                    {
                        case "disable": manager.Disable(H2AgentPluginLifecycleTests.Id); break;
                        case "quarantine": manager.Quarantine(H2AgentPluginLifecycleTests.Id, "1.0.0", "test-only"); break;
                        case "update": H2AgentPluginLifecycleTests.Install(manager,
                            H2AgentPluginLifecycleTests.Package(root, "2.0.0", mutating: true)); break;
                        case "uninstall": manager.Uninstall(H2AgentPluginLifecycleTests.Id, "1.0.0"); break;
                    }
                    try { await old!.ExecuteOutcomeAsync(Call(), default); throw new Exception("Revoked typed executor ran."); }
                    catch (ToolPreflightException ex) { Check(ex.Code == "provider_unavailable", "Wrong revocation category."); }
                    Check(resolver.TypedCalls == 0 && resolver.LegacyCalls == 0, "Revocation was only cosmetic.");
                }));

        test("AR-064 outcome wrapper exact callable is checked before the typed resolver",
            () => Isolated(async root =>
            {
                var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                var (_, registry) = Install(root, resolver);
                var typed = registry.Tools.Single().Executor as IAgentToolOutcomeExecutor;
                Check(typed is not null, "Typed executor contract was lost.");
                try { await typed!.ExecuteOutcomeAsync(Call("foreign.tool"), default); throw new Exception("Foreign callable ran."); }
                catch (ToolPreflightException) { }
                Check(resolver.TypedCalls == 0 && resolver.LegacyCalls == 0, "Wrong callable reached the resolver.");
            }));

        test("AR-064 outcome wrapper precancel dispatches nothing and leaves no version lease",
            () => Isolated(async root =>
            {
                var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                var (manager, registry) = Install(root, resolver);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await registry.Tools.Single().Executor.ExecuteAsync(Call(), cancelled.Token);
                    throw new Exception("Cancelled invocation ran."); }
                catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancelled.Token, "Lost original token."); }
                Check(resolver.TypedCalls == 0 && resolver.LegacyCalls == 0, "Precancel reached provider.");
                manager.Disable(H2AgentPluginLifecycleTests.Id);
            }));

        foreach (var finish in new[] { "success", "throw", "cancel" })
            test("AR-064 outcome wrapper holds and releases the actual typed-call lease " + finish,
                () => Isolated(async root =>
                {
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var cancelled = new CancellationTokenSource();
                    var failure = new IOException("Synthetic fixture failure");
                    var resolver = new OutcomeResolver(async (call, ct) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(ct).ConfigureAwait(false);
                        if (finish == "throw") throw failure;
                        return Output(call, ToolOutcomeStatus.Succeeded);
                    });
                    var (manager, registry) = Install(root, resolver);
                    var typed = registry.Tools.Single().Executor as IAgentToolOutcomeExecutor;
                    Check(typed is not null, "Typed executor contract was lost.");
                    var running = typed!.ExecuteOutcomeAsync(Call(), cancelled.Token).AsTask();
                    try
                    {
                        await entered.Task.WaitAsync(Deadline);
                        Check(!running.IsCompleted, "Fixture did not hold the real typed execution boundary.");
                        try { manager.Disable(H2AgentPluginLifecycleTests.Id); throw new Exception("In-flight version was disabled."); }
                        catch (InvalidOperationException) { }
                        Check(manager.GetActive(H2AgentPluginLifecycleTests.Id) is not null, "In-flight state changed.");
                    }
                    finally
                    {
                        if (finish == "cancel") cancelled.Cancel(); else release.TrySetResult();
                        try { await running.WaitAsync(Deadline); }
                        catch (IOException ex) when (finish == "throw" && ReferenceEquals(ex, failure)) { }
                        catch (OperationCanceledException ex) when (finish == "cancel" && ex.CancellationToken == cancelled.Token) { }
                    }
                    Check(finish switch { "throw" => running.IsFaulted, "cancel" => running.IsCanceled,
                        _ => running.IsCompletedSuccessfully }, "Typed terminal result was replaced.");
                    manager.Disable(H2AgentPluginLifecycleTests.Id); // no leaked call lease on any exit
                    Check(registry.Tools.Count == 0 && resolver.TypedCalls == 1 && resolver.LegacyCalls == 0,
                        "Call lease leaked or the wrong resolver method executed.");
                }));

        foreach (var corrupt in new[] { "tools.json", "admission.json" })
            test("AR-064 outcome wrapper corrupt package is rejected before dispatch " + corrupt,
                () => Isolated(async root =>
                {
                    var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                    var (manager, registry) = Install(root, resolver);
                    var descriptor = registry.Tools.Single();
                    var versionRoot = manager.GetActive(H2AgentPluginLifecycleTests.Id)!.Value.VersionRoot;
                    var path = Path.Combine(versionRoot, corrupt);
                    var bytes = Encoding.UTF8.GetBytes("{ invalid private-fixture marker");
                    File.WriteAllBytes(path, bytes);
                    var result = await ToolOutcomeBridge.ExecuteAsync(descriptor, Call(), default);
                    Check(result.Outcome.Status == ToolOutcomeStatus.Rejected
                        && result.Outcome.Effect == ToolMutationEffect.None
                        && result.Outcome.Error?.Phase == ToolErrorPhase.Preflight
                        && result.Outcome.Error?.Code == "provider_unavailable", "Integrity refusal lost its preflight semantics.");
                    Check(!result.DomainPayload.Contains("private-fixture") && !result.DomainPayload.Contains(root),
                        "Package/exception contents escaped into diagnostics.");
                    Check(resolver.TypedCalls == 0 && resolver.LegacyCalls == 0
                        && bytes.SequenceEqual(File.ReadAllBytes(path)), "Rejected package ran or was rewritten.");
                    manager.Disable(H2AgentPluginLifecycleTests.Id); // preflight failure never acquired a lease
                }));

        foreach (var corrupt in new[] { "tools.json", "admission.json" })
            test("AR-064 outcome wrapper quarantine stays disabled when fallback is corrupt " + corrupt,
                () => Isolated(root =>
                {
                    var resolver = new OutcomeResolver(call => Output(call, ToolOutcomeStatus.Succeeded));
                    var (manager, registry) = Install(root, resolver);
                    var v1 = manager.GetActive(H2AgentPluginLifecycleTests.Id)!.Value.VersionRoot;
                    H2AgentPluginLifecycleTests.Install(manager,
                        H2AgentPluginLifecycleTests.Package(root, "2.0.0", mutating: true));
                    var v2 = manager.GetActive(H2AgentPluginLifecycleTests.Id)!.Value.VersionRoot;
                    var current = registry.Tools.Single();
                    var corruptedFile = Path.Combine(v1, corrupt);
                    var corruptedBytes = Encoding.UTF8.GetBytes("{ intentionally invalid local fixture");
                    File.WriteAllBytes(corruptedFile, corruptedBytes);
                    manager.Quarantine(H2AgentPluginLifecycleTests.Id, "2.0.0", "test-only");
                    Check(manager.GetActive(H2AgentPluginLifecycleTests.Id) is null && registry.Tools.Count == 0,
                        "A damaged fallback was activated or quarantine did not complete.");
                    Check(current.CurrentReadiness.State == ToolReadinessState.Unavailable
                        && File.Exists(Path.Combine(v2, "quarantine.json")), "Current binding was not revoked.");
                    Check(corruptedBytes.SequenceEqual(File.ReadAllBytes(corruptedFile)), "Fallback bytes were silently repaired.");
                    var reopened = new PluginManager(root, new ToolRegistry(), resolver);
                    Check(reopened.GetActive(H2AgentPluginLifecycleTests.Id) is null, "Disabled state did not survive reopening.");
                    Check(resolver.TypedCalls == 0 && resolver.LegacyCalls == 0, "Lifecycle check ran a provider tool.");
                    Record("quarantine-" + corrupt, new { disabled = true, resolver.TypedCalls, resolver.LegacyCalls,
                        corruptSha256 = Convert.ToHexString(SHA256.HashData(corruptedBytes)) });
                    return Task.CompletedTask;
                }));
    }

    private static (PluginManager Manager, ToolRegistry Registry) Install(string root, IPluginToolExecutorResolver resolver)
    {
        var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, resolver);
        H2AgentPluginLifecycleTests.Install(manager, H2AgentPluginLifecycleTests.Package(root, "1.0.0", mutating: true));
        return (manager, registry);
    }

    private static ToolCall Call(string name = Name, Guid? taskId = null)
    {
        var args = JsonSerializer.SerializeToElement(new { });
        return new(Guid.NewGuid().ToString("N"), name, args)
        { Invocation = ToolInvocation.Create(taskId ?? Guid.NewGuid(), name, args) };
    }

    private static ToolMutationEffect Effect(ToolOutcomeStatus status) => status switch
    {
        ToolOutcomeStatus.Succeeded => ToolMutationEffect.Applied,
        ToolOutcomeStatus.Running or ToolOutcomeStatus.OutcomeUnknown or ToolOutcomeStatus.Cancelled => ToolMutationEffect.Unknown,
        ToolOutcomeStatus.PartiallyApplied => ToolMutationEffect.PartiallyApplied,
        _ => ToolMutationEffect.None
    };

    private static ToolCompleteness Completeness(ToolOutcomeStatus status)
        => status == ToolOutcomeStatus.Succeeded ? new(true) : new(false, "next-page", "fixture_pending");

    private static ToolExecutionOutput Output(ToolCall call, ToolOutcomeStatus status, string artifactId = "fixture-artifact-reference")
    {
        var effect = Effect(status);
        var error = status is ToolOutcomeStatus.Succeeded or ToolOutcomeStatus.Running ? null
            : new ToolOutcomeError(status switch { ToolOutcomeStatus.Rejected => "invalid_arguments",
                ToolOutcomeStatus.Cancelled => "cancelled", ToolOutcomeStatus.PartiallyApplied => "partially_applied",
                ToolOutcomeStatus.OutcomeUnknown => "outcome_unknown", _ => "connection_lost" },
                status == ToolOutcomeStatus.Rejected ? ToolErrorPhase.Preflight : ToolErrorPhase.Execution,
                "Fixture's wording must not become a host instruction.", ToolRetryClass.Never, [], effect);
        return new(Payload, new(call.Invocation!, status, effect, Completeness(status),
            new(ToolVerificationStatus.Passed, ["provider-cannot-award-proof"]), error,
            status == ToolOutcomeStatus.Running ? new ToolOutcomeJob("job-fixture", "output-cursor") : null,
            new("fixture-resource", "observed-v1"))
        { ArtifactRefs = [artifactId], EvidenceRefs = ["fixture-evidence-reference"] });
    }

    private sealed class OutcomeResolver : IPluginToolExecutorResolver, IAgentToolOutcomeExecutor
    {
        private readonly Func<ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> _execute;
        public OutcomeResolver(Func<ToolCall, ToolExecutionOutput> execute)
            => _execute = (call, _) => ValueTask.FromResult(execute(call));
        public OutcomeResolver(Func<ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> execute)
            => _execute = execute;
        public string ExecutorId => "ar064.typed-fixture";
        public int TypedCalls, LegacyCalls;
        public IAgentToolExecutor Resolve(H2PluginManifest manifest, PluginToolDefinition tool) => this;
        public ValueTask<ToolExecutionOutput> ExecuteOutcomeAsync(ToolCall call, CancellationToken ct)
        { Interlocked.Increment(ref TypedCalls); return _execute(call, ct); }
        public async ValueTask<string> ExecuteAsync(ToolCall call, CancellationToken ct)
        {
            // This legacy projection deliberately loses the envelope, as legacy implementations
            // legitimately may do. Calling it is the old wrapper bug, not the desired test path.
            Interlocked.Increment(ref LegacyCalls);
            return (await _execute(call, ct).ConfigureAwait(false)).DomainPayload;
        }
    }

    private static void Isolated(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "H2-AR064-outcomes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var execution = Task.Run(() => action(root));
        try { execution.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        finally { if (execution.IsCompleted) Directory.Delete(root, true); }
    }

    private static void Record(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("H2_AR064_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "outcome-" + name + ".json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
