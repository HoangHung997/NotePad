using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Providers;
using H2AgentLab.Tools;

internal static class H2AgentProviderRevocationTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static ToolCall Call(string name) => new("ar064-revocation", name, Json("{}"));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }
    private static void Isolated(Func<Task> action)
        => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    public static void Run(Action<string, Action> test)
    {
        foreach (var alreadyLoaded in new[] { false, true })
            test("AR-064 revocation pending discovery cannot republish after removal loaded=" + alreadyLoaded,
                () => Isolated(async () =>
                {
                    var registry = new ToolRegistry();
                    await using var manager = new CapabilityProviderManager(registry);
                    var provider = new ControlledProvider(); manager.Register(provider);
                    if (alreadyLoaded)
                        await manager.LoadProviderToolsAsync(provider.Provenance.ProviderId, ["fixture.a"], default);
                    var block = provider.BlockNextLoad();
                    var late = manager.LoadProviderToolsAsync(provider.Provenance.ProviderId, ["fixture.a"], default);
                    Check(block.Entered.Task.IsCompleted && !late.IsCompleted, "Discovery boundary was not reached.");
                    manager.RemoveProviderTools(provider.Provenance.ProviderId);
                    var versionAfterRemoval = registry.Version;
                    block.Release.TrySetResult();
                    try { await late; throw new Exception("Removed provider discovery republished a tool."); }
                    catch (InvalidOperationException) { }
                    Check(registry.Version == versionAfterRemoval && registry.Tools.Count == 0,
                        "Late discovery changed the removed registry.");
                    // A NEW explicit load is allowed; removal does not permanently ban the provider.
                    var current = (await manager.LoadProviderToolsAsync(provider.Provenance.ProviderId,
                        ["fixture.a"], default)).Single();
                    await current.Executor.ExecuteAsync(Call("fixture.a"), default);
                    Check(provider.Executed.Count == 1, "Fresh explicit discovery did not recover.");
                }));

        foreach (var permanent in new[] { false, true })
            test("AR-064 revocation retained descriptor is unavailable after removal permanent=" + permanent,
                () => Isolated(async () =>
                {
                    var registry = new ToolRegistry();
                    await using var manager = new CapabilityProviderManager(registry);
                    var provider = new ControlledProvider(); manager.Register(provider);
                    var descriptor = (await manager.LoadProviderToolsAsync(provider.Provenance.ProviderId,
                        ["fixture.a"], default)).Single();
                    Check(descriptor.CurrentReadiness.State == ToolReadinessState.Ready, "Fixture was not ready.");
                    if (permanent) await manager.UnregisterProviderAsync(provider.Provenance.ProviderId, default);
                    else manager.RemoveProviderTools(provider.Provenance.ProviderId);
                    // This provider intentionally retains cached Ready health on disposal. Host revocation
                    // must be authoritative independently of backend health or a stale UI copy.
                    Check(descriptor.CurrentReadiness.State == ToolReadinessState.Unavailable,
                        "Revoked descriptor still advertises Ready.");
                    Rejected(() => descriptor.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult(),
                        "Revoked executor started work.");
                    Check(provider.Executed.Count == 0, "Revocation called the backend.");
                }));

        test("AR-064 revocation older discovery cannot overwrite a newer committed selected binding",
            () => Isolated(async () =>
            {
                var registry = new ToolRegistry(); var provider = new ControlledProvider();
                var adapter = new CapabilityProviderToolRegistryAdapter(registry);
                var block = provider.BlockNextLoad();
                var older = adapter.LoadSelectedAsync(provider, ["fixture.a"], default);
                Check(block.Entered.Task.IsCompleted, "Older load did not reach boundary.");
                provider.ToolVersion = "2.0.0";
                var latest = (await adapter.LoadSelectedAsync(provider, ["fixture.a"], default)).Single();
                var version = registry.Version; block.Release.TrySetResult();
                try { await older; throw new Exception("Older discovery overwrote newer binding."); }
                catch (InvalidOperationException) { }
                Check(registry.Version == version && ReferenceEquals(registry.Tools.Single(), latest)
                    && latest.Provenance!.ToolVersion == "2.0.0", "Newer tool identity was replaced.");
                await latest.Executor.ExecuteAsync(Call("fixture.a"), default);
            }));

        test("AR-064 revocation disjoint loads preserve unrelated descriptor and executor",
            () => Isolated(async () =>
            {
                var registry = new ToolRegistry(); var provider = new ControlledProvider();
                var adapter = new CapabilityProviderToolRegistryAdapter(registry);
                var block = provider.BlockNextLoad();
                var first = adapter.LoadSelectedAsync(provider, ["fixture.a"], default);
                var other = (await adapter.LoadSelectedAsync(provider, ["fixture.b"], default)).Single();
                block.Release.TrySetResult(); var selected = (await first).Single();
                Check(registry.Tools.Count == 2 && registry.TryGet("fixture.b", out var same)
                    && ReferenceEquals(same, other), "Disjoint discovery invalidated unrelated tool.");
                await selected.Executor.ExecuteAsync(Call("fixture.a"), default);
                await other.Executor.ExecuteAsync(Call("fixture.b"), default);
                Check(provider.Executed.SequenceEqual(new[] { "fixture.a", "fixture.b" }), "Wrong provider calls.");
            }));

        test("AR-064 revocation reload invalidates readiness only for the selected binding",
            () => Isolated(async () =>
            {
                var registry = new ToolRegistry(); var provider = new ControlledProvider();
                var adapter = new CapabilityProviderToolRegistryAdapter(registry);
                var initial = await adapter.LoadSelectedAsync(provider, ["fixture.a", "fixture.b"], default);
                var oldA = initial.Single(d => d.Name == "fixture.a");
                var oldB = initial.Single(d => d.Name == "fixture.b");
                var current = (await adapter.LoadSelectedAsync(provider, ["fixture.a"], default)).Single();
                Check(oldA.CurrentReadiness.State == ToolReadinessState.Unavailable,
                    "Replaced descriptor still advertises Ready.");
                Check(oldB.CurrentReadiness.State == ToolReadinessState.Ready
                    && current.CurrentReadiness.State == ToolReadinessState.Ready, "Unrelated binding was invalidated.");
                await oldB.Executor.ExecuteAsync(Call("fixture.b"), default);
                await current.Executor.ExecuteAsync(Call("fixture.a"), default);
            }));
    }

    private sealed class LoadBoundary
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledProvider : ICapabilityProvider
    {
        private LoadBoundary? _next;
        public string ToolVersion { get; set; } = "1.0.0";
        public List<string> Executed { get; } = [];
        public ProviderProvenance Provenance { get; } = new("provider.revocation", "1.0.0", "fixture", "fixture");
        public ProviderHealthState Health { get; } = new(ProviderHealthStatus.Ready, DateTime.UtcNow);
        public LoadBoundary BlockNextLoad() => _next = new();
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderNamespaceSummary>>([]);
        public Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderToolSummary>>([]);
        public async Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(IReadOnlyList<string> names, CancellationToken ct)
        {
            var captured = names.Select(name => new ProviderToolDefinition(new(name, "fixture", "Fixture",
                AgentToolAccess.ReadOnly, AgentToolRisk.Low, true, "v1", "fixture", "fixture", ToolVersion), Json("{}"))).ToArray();
            var boundary = Interlocked.Exchange(ref _next, null);
            if (boundary is not null)
            {
                boundary.Entered.TrySetResult();
                await boundary.Release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            return captured;
        }
        public Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ProviderResourceSummary>>([]);
        public Task<string> ReadResourceAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<string> ExecuteToolAsync(string name, JsonElement args, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Executed.Add(name); return ValueTask.FromResult("{}"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
