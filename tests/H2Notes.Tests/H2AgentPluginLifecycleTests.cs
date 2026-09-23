using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Session;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

internal static class H2AgentPluginLifecycleTests
{
    internal const string Id = "h2.ar064.fixture";
    internal static readonly PluginInstallPolicy Policy = new(PluginInstallMode.DeveloperLocal,
        new HashSet<string>(StringComparer.Ordinal), false, false);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static ToolCall Call(string name = "fixture.echo") => new("ar064-call", name, Json("{}"));
    private static ToolDescriptor Descriptor(string name, string ns = "fixture", string description = "Fixture namespace.")
        => new(name, new(ns, description), "Fixture", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true,
            "v1", Json("{}"), new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}")),
            new("provider.fixture", "1.0.0", "fixture", "1.0.0"));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected rejection before activation/execution.");
    }
    internal static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "H2-AR064-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }

    public static void Run(Action<string, Action> test)
    {
        H2AgentProviderRevocationTests.Run(test);
        H2AgentPluginOutcomeBoundaryTests.Run(test);
        test("AR-064 registry duplicate registration cannot leak a new namespace", () =>
        {
            var registry = new ToolRegistry(); registry.Register(Descriptor("fixture.a")); var version = registry.Version;
            Reject(() => registry.Register(Descriptor("fixture.a", "leaked")));
            Check(registry.Version == version && registry.Namespaces.Count == 1, "Failed registration changed registry.");
        });
        foreach (var fault in new[] { "duplicate", "namespace", "commit" })
            test("AR-064 registry invalid batch preserves previous descriptors and version " + fault, () =>
            {
                var registry = new ToolRegistry(); var original = Descriptor("fixture.a"); registry.Register(original);
                registry.Register(Descriptor("other.b", "other")); var version = registry.Version;
                var replacements = fault == "duplicate"
                    ? new[] { Descriptor("fixture.c"), Descriptor("other.b", "other") }
                    : fault == "namespace"
                        ? new[] { Descriptor("fixture.c"), Descriptor("other.c", "other", "Mismatch") }
                        : new[] { Descriptor("fixture.c") };
                Reject(() => registry.ReplaceWhere(d => d.Name == "fixture.a", replacements,
                    fault == "commit" ? () => throw new IOException("fixture write failure") : null));
                Check(registry.Version == version && registry.TryGet("fixture.a", out var same)
                    && ReferenceEquals(same, original) && !registry.TryGet("fixture.c", out _), "Partial registry update.");
            });
        test("AR-064 registry atomic replacement retains unrelated descriptor identity", () =>
        {
            var registry = new ToolRegistry(); registry.Register(Descriptor("fixture.a")); var other = Descriptor("other.b", "other");
            registry.Register(other); var committed = false;
            registry.ReplaceWhere(d => d.Name == "fixture.a", [Descriptor("fixture.c")], () => committed = true);
            Check(committed && !registry.TryGet("fixture.a", out _) && registry.TryGet("fixture.c", out _)
                && registry.TryGet("other.b", out var same) && ReferenceEquals(same, other), "Replacement invalidated unrelated tool.");
        });
        test("AR-064 registry snapshots never expose a partially published batch", () =>
        {
            var registry = new ToolRegistry(); registry.Register(Descriptor("fixture.a"));
            var writer = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++) registry.ReplaceWhere(_ => true,
                    i % 2 == 0 ? [Descriptor("fixture.b"), Descriptor("fixture.c")] : [Descriptor("fixture.a")]);
            });
            for (int i = 0; i < 500; i++)
            {
                var names = registry.Tools.Select(d => d.Name).ToArray();
                Check(names.SequenceEqual(new[] { "fixture.a" }) || names.SequenceEqual(new[] { "fixture.b", "fixture.c" }), "Partial batch observed.");
            }
            writer.GetAwaiter().GetResult();
        });
        foreach (var fault in new[] { "schema", "unselected", "duplicate", "cancel" })
            test("AR-064 provider rejected projection preserves currently callable tools " + fault, () =>
            {
                var registry = new ToolRegistry(); var original = Descriptor("fixture.a"); registry.Register(original);
                var version = registry.Version; using var cts = new CancellationTokenSource();
                var provider = new DefinitionProvider();
                var first = Definition("fixture.a");
                provider.Definitions = fault switch
                {
                    "schema" => [first, Definition("fixture.b") with { CallableSchema = Json("[]") }],
                    "unselected" => [Definition("foreign.c")],
                    "duplicate" => [first, first],
                    _ => [first]
                };
                provider.AfterLoad = fault == "cancel" ? () => cts.Cancel() : null;
                try
                {
                    new CapabilityProviderToolRegistryAdapter(registry).LoadSelectedAsync(provider,
                        ["fixture.a", "fixture.b"], cts.Token).GetAwaiter().GetResult();
                    throw new Exception("Invalid provider projection accepted.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OperationCanceledException) { }
                Check(registry.Version == version && registry.TryGet("fixture.a", out var same)
                    && ReferenceEquals(same, original), "Rejected provider load removed old tools.");
            });

        test("AR-064 provider removal revokes a previously captured executor before disconnect", () =>
        {
            var registry = new ToolRegistry(); var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")] };
            var manager = new CapabilityProviderManager(registry); manager.Register(provider);
            var old = manager.LoadProviderToolsAsync(provider.Provenance.ProviderId, ["fixture.a"], default).Result.Single();
            old.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult();
            Check(manager.UnregisterProviderAsync(provider.Provenance.ProviderId, default).Result, "Provider not removed.");
            Reject(() => old.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult());
            Check(provider.Calls == 1 && registry.Tools.Count == 0, "Removed provider remained callable.");
        });
        test("AR-064 provider reload revokes selected binding without changing an unrelated provider", () =>
        {
            var registry = new ToolRegistry(); var other = Descriptor("other.b", "other"); registry.Register(other);
            var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")] };
            var adapter = new CapabilityProviderToolRegistryAdapter(registry);
            var old = adapter.LoadSelectedAsync(provider, ["fixture.a"], default).Result.Single();
            var current = adapter.LoadSelectedAsync(provider, ["fixture.a"], default).Result.Single();
            Reject(() => old.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult());
            current.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult();
            Check(provider.Calls == 1 && registry.TryGet("other.b", out var same) && ReferenceEquals(other, same), "Reload invalidated another provider.");
        });
        test("AR-064 provider cannot resurrect from discovery that finishes after unregistration", () =>
        {
            var registry = new ToolRegistry(); var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")], LoadRelease = new() };
            var manager = new CapabilityProviderManager(registry); manager.Register(provider);
            var loading = manager.LoadProviderToolsAsync(provider.Provenance.ProviderId, ["fixture.a"], default);
            Check(!loading.IsCompleted, "Discovery did not block.");
            manager.UnregisterProviderAsync(provider.Provenance.ProviderId, default).GetAwaiter().GetResult();
            provider.LoadRelease.SetResult();
            Reject(() => loading.GetAwaiter().GetResult()); Check(registry.Tools.Count == 0, "Late discovery resurrected removed provider.");
        });
        test("AR-064 provider requested names are snapshotted before awaiting discovery", () =>
        {
            var registry = new ToolRegistry(); var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")], LoadRelease = new() };
            var names = new[] { "fixture.a" };
            var loading = new CapabilityProviderToolRegistryAdapter(registry).LoadSelectedAsync(provider, names, default);
            names[0] = "foreign.tool"; provider.LoadRelease.SetResult(); loading.GetAwaiter().GetResult();
            Check(registry.TryGet("fixture.a", out _) && !registry.TryGet("foreign.tool", out _), "Caller changed accepted selection while loading.");
        });
        test("AR-064 provider disconnection prevents a retained executor from starting work", () =>
        {
            var registry = new ToolRegistry(); var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")] };
            var old = new CapabilityProviderToolRegistryAdapter(registry).LoadSelectedAsync(provider, ["fixture.a"], default).Result.Single();
            provider.DisconnectAsync(default).GetAwaiter().GetResult();
            Reject(() => old.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask().GetAwaiter().GetResult());
            Check(provider.Calls == 0, "Disconnected backend executed.");
        });
        test("AR-064 provider in-flight lease prevents disposal until the actual call ends", () =>
        {
            var registry = new ToolRegistry(); var provider = new DefinitionProvider { Definitions = [Definition("fixture.a")], ExecuteRelease = new() };
            var manager = new CapabilityProviderManager(registry); manager.Register(provider);
            var old = manager.LoadProviderToolsAsync(provider.Provenance.ProviderId, ["fixture.a"], default).Result.Single();
            var executing = old.Executor.ExecuteAsync(Call("fixture.a"), default).AsTask();
            try { Reject(() => manager.UnregisterProviderAsync(provider.Provenance.ProviderId, default).GetAwaiter().GetResult()); }
            finally { provider.ExecuteRelease.SetResult(); }
            executing.GetAwaiter().GetResult();
            Check(!provider.Disposed, "In-flight provider was disposed.");
            manager.UnregisterProviderAsync(provider.Provenance.ProviderId, default).GetAwaiter().GetResult();
            Check(provider.Disposed && registry.Tools.Count == 0, "Provider not disposed after safe boundary.");
        });

        test("AR-064 only-version quarantine revokes both discovery and captured executor", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var resolver = new Resolver(); var manager = new PluginManager(root, registry, resolver);
            Install(manager, Package(root, "1.0.0")); var old = registry.Tools.Single();
            Check(old.Executor.ExecuteAsync(Call(), default).AsTask().Result.Contains("1.0.0"), "Fixture not executed.");
            manager.Quarantine(Id, "1.0.0", "test containment");
            Check(manager.GetActive(Id) is null && registry.Tools.Count == 0
                && old.CurrentReadiness.State == ToolReadinessState.Unavailable, "Quarantined version remains discoverable.");
            Reject(() => old.Executor.ExecuteAsync(Call(), default).AsTask().GetAwaiter().GetResult());
            Check(resolver.Calls == 1 && File.Exists(Path.Combine(manager.Root, Id, "1.0.0", "quarantine.json")), "Revocation ran old code.");
        }));
        test("AR-064 quarantine selects clean fallback and cannot roll back to quarantined bytes", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, new Resolver());
            Install(manager, Package(root, "1.0.0")); Install(manager, Package(root, "2.0.0"));
            manager.Quarantine(Id, "2.0.0", "test");
            Check(manager.GetActive(Id)?.Manifest.Version == "1.0.0", "Clean fallback not selected.");
            Reject(() => manager.Rollback(Id));
            Check(registry.Tools.Single().Executor.ExecuteAsync(Call(), default).AsTask().Result.Contains("1.0.0"), "Quarantined fallback resurrected.");
        }));
        test("AR-064 disable and re-enable never resurrect an old captured executor", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, new Resolver());
            Install(manager, Package(root, "1.0.0")); var old = registry.Tools.Single();
            manager.Disable(Id); manager.Enable(Id, "1.0.0");
            Reject(() => old.Executor.ExecuteAsync(Call(), default).AsTask().GetAwaiter().GetResult());
            Check(registry.Tools.Single().Executor.ExecuteAsync(Call(), default).AsTask().Result.Contains("1.0.0"), "Fresh binding did not work.");
        }));
        test("AR-064 update revokes old binding without changing unrelated tool", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var other = Descriptor("other.b", "other"); registry.Register(other);
            var manager = new PluginManager(root, registry, new Resolver()); Install(manager, Package(root, "1.0.0"));
            var old = registry.Tools.Single(d => d.Name == "fixture.echo"); Install(manager, Package(root, "2.0.0"));
            Reject(() => old.Executor.ExecuteAsync(Call(), default).AsTask().GetAwaiter().GetResult());
            Check(registry.TryGet("other.b", out var same) && ReferenceEquals(same, other), "Unrelated task binding changed.");
            Check(registry.Tools.Single(d => d.Name == "fixture.echo").Executor.ExecuteAsync(Call(), default).AsTask().Result.Contains("2.0.0"), "New version not active.");
        }));
        test("AR-064 actual in-flight executor fences only its plugin and releases after cancellation", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var resolver = new Resolver { Hold = true }; var manager = new PluginManager(root, registry, resolver);
            Install(manager, Package(root, "1.0.0")); using var cts = new CancellationTokenSource();
            var running = registry.Tools.Single().Executor.ExecuteAsync(Call(), cts.Token).AsTask();
            resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            try
            {
                Reject(() => Install(manager, Package(root, "2.0.0")));
                Reject(() => manager.Quarantine(Id, "1.0.0", "busy"));
                Install(manager, Package(root, "1.0.0", "h2.ar064.other", "other.echo"));
            }
            finally { cts.Cancel(); }
            try { running.GetAwaiter().GetResult(); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            resolver.Hold = false; Install(manager, Package(root, "2.0.0"));
        }));
        test("AR-064 task version pin blocks its update but permits explicit revocation", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, new Resolver());
            Install(manager, Package(root, "1.0.0")); var pin = manager.PinVersion(Id, "1.0.0");
            try
            {
                Reject(() => Install(manager, Package(root, "2.0.0")));
                Install(manager, Package(root, "1.0.0", "h2.ar064.other", "other.echo"));
                manager.Disable(Id); Check(manager.GetActive(Id) is null, "Pin prevented explicit disable.");
            }
            finally { pin.Dispose(); pin.Dispose(); }
            manager.Enable(Id, "1.0.0"); Install(manager, Package(root, "2.0.0"));
        }));
        foreach (var file in new[] { "tools.json", "manifest.json", "admission.json" })
            test("AR-064 altered admitted file is rejected before executor invocation " + file, () => WithRoot(root =>
            {
                var registry = new ToolRegistry(); var resolver = new Resolver(); var manager = new PluginManager(root, registry, resolver);
                Install(manager, Package(root, "1.0.0")); var old = registry.Tools.Single();
                File.WriteAllText(Path.Combine(manager.Root, Id, "1.0.0", file), "{}");
                Reject(() => old.Executor.ExecuteAsync(Call(), default).AsTask().GetAwaiter().GetResult());
                Check(resolver.Calls == 0, "Altered package executed.");
            }));
        foreach (var fault in new[] { "hash", "trust", "permission", "selftest", "minimum" })
            test("AR-064 failed installation preserves the previous active package " + fault, () => WithRoot(root =>
            {
                var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, new Resolver());
                Install(manager, Package(root, "1.0.0")); var old = registry.Tools.Single();
                var package = Package(root, "2.0.0", selfTest: fault != "selftest", minimum: fault == "minimum" ? "999.0.0" : "2.0.0",
                    permissions: fault == "permission" ? ["read", "write"] : ["read"]);
                if (fault == "hash") package = package with { Entry = package.Entry with { ArchiveSha256 = "sha256:" + new string('0', 64) } };
                if (fault == "trust") package = package with { Entry = package.Entry with { TrustState = PluginTrustState.Untrusted } };
                Reject(() => manager.InstallFromArchive(package.Path,
                    fault == "permission" ? package.Entry with { TrustState = PluginTrustState.TrustedOfficial } : package.Entry,
                    fault == "permission" ? Policy with { Mode = PluginInstallMode.TrustedOfficialOnly } : Policy,
                    userApproved: fault != "permission"));
                Check(manager.GetActive(Id)?.Manifest.Version == "1.0.0" && ReferenceEquals(old, registry.Tools.Single()), "Rejected update changed active package.");
            }));
        test("AR-064 second descriptor namespace conflict cannot partially activate a package", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); registry.Register(Descriptor("other.b", "occupied", "Different owner"));
            var manager = new PluginManager(root, registry, new Resolver()); Install(manager, Package(root, "1.0.0"));
            var old = registry.Tools.Single(d => d.Name == "fixture.echo");
            Reject(() => Install(manager, Package(root, "2.0.0", secondNamespace: "occupied")));
            Check(manager.GetActive(Id)?.Manifest.Version == "1.0.0" && registry.TryGet("fixture.echo", out var same)
                && ReferenceEquals(same, old) && !registry.TryGet("fixture.second", out _), "Failed activation leaked a subset.");
        }));
        test("AR-064 uninstall removes only package bytes and keeps historical artifact readable", () => WithRoot(root =>
        {
            var registry = new ToolRegistry(); var manager = new PluginManager(root, registry, new Resolver());
            Install(manager, Package(root, "1.0.0")); var old = registry.Tools.Single();
            var artifacts = new ArtifactStore(root); var proof = artifacts.StoreText(AgentArtifactKind.ToolOutput,
                "ar064-proof", "fixture.echo", "Bằng chứng =SUM($A$1:$A$3)", "fixture receipt", 1).Handle;
            manager.Uninstall(Id, "1.0.0");
            Check(artifacts.ReadText(proof.Id) == "Bằng chứng =SUM($A$1:$A$3)", "Uninstall removed Agent evidence.");
            Reject(() => old.Executor.ExecuteAsync(Call(), default).AsTask().GetAwaiter().GetResult());
        }));
        test("AR-064 skill-only package disappears from canonical skill source after disable", () => WithRoot(root =>
        {
            var manager = new PluginManager(root, new ToolRegistry(), new Resolver()); Install(manager, Package(root, "1.0.0", skillOnly: true));
            var source = new PluginSkillSource(manager); Check(source.SnapshotMetadata().Count == 1, "Skill-only package not discovered.");
            manager.Disable(Id); Check(source.SnapshotMetadata().Count == 0, "Disabled skill still advertised.");
        }));
        test("AR-064 restart preserves disabled state and requires a fresh verified binding", () => WithRoot(root =>
        {
            var manager = new PluginManager(root, new ToolRegistry(), new Resolver()); Install(manager, Package(root, "1.0.0")); manager.Disable(Id);
            var registry = new ToolRegistry(); var next = new PluginManager(root, registry, new Resolver());
            Check(next.GetActive(Id) is null && next.ActivePlugins().Count == 0 && registry.Tools.Count == 0, "Disabled persisted state ignored.");
            next.Enable(Id, "1.0.0"); Check(registry.Tools.Count == 1, "Verified re-enable failed.");
        }));
        foreach (var version in new[] { "../outside", "1.0.0/../other", "01.0.0", "1.0.0:stream" })
            test("AR-064 lifecycle refuses noncanonical version path " + version, () => WithRoot(root =>
            {
                var manager = new PluginManager(root, new ToolRegistry(), new Resolver());
                Reject(() => manager.Quarantine(Id, version, "test")); Reject(() => manager.Enable(Id, version));
                Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0, "Unsafe version wrote metadata.");
            }));
        foreach (var entry in new[] { "ADMISSION.JSON", "../outside.txt", "/rooted.txt", "folder./file.txt" })
            test("AR-064 package rejects reserved or escaping archive entry " + entry, () => WithRoot(root =>
            {
                var manager = new PluginManager(root, new ToolRegistry(), new Resolver());
                Reject(() => Install(manager, Package(root, "1.0.0", extraEntry: entry)));
                Check(manager.ActivePlugins().Count == 0, "Invalid entry activated.");
            }));
    }

    internal sealed record BuiltPackage(string Path, PluginCatalogEntry Entry);
    internal static PluginInstallResult Install(PluginManager manager, BuiltPackage package)
        => manager.InstallFromArchive(package.Path, package.Entry, Policy, userApproved: true);
    internal static BuiltPackage Package(string root, string version, string id = Id, string toolName = "fixture.echo",
        bool selfTest = true, string minimum = "2.0.0", string[]? permissions = null, string? secondNamespace = null,
        bool skillOnly = false, string? extraEntry = null, bool mutating = false)
    {
        var path = Path.Combine(root, id + "-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var definitions = new List<object>();
            object Def(string name, string ns) => new { name, @namespace = ns, description = "AR064 local fixture", access = mutating ? "Mutating" : "ReadOnly",
                risk = mutating ? "Medium" : "Low", supportsParallel = false, schemaVersion = "v1", toolVersion = version, resourceScope = "fixture",
                serializationKey = "fixture", schema = new { type = "function", function = new { name,
                    parameters = new { type = "object", properties = new { }, additionalProperties = false } } } };
            if (!skillOnly) definitions.Add(Def(toolName, "fixture"));
            if (secondNamespace is not null) definitions.Add(Def("fixture.second", secondNamespace));
            Write(zip, "tools.json", JsonSerializer.Serialize(definitions));
            Write(zip, "skills/audit/SKILL.md", "---\nname: audit\ndescription: AR064 fixture audit\n---\nExact fixture content " + version);
            Write(zip, "selftest.json", JsonSerializer.Serialize(new { ok = selfTest, requiredFiles = new[] { "tools.json", "skills/audit/SKILL.md" } }));
            if (extraEntry is not null) Write(zip, extraEntry, "not approved");
        }
        memory.Position = 0; string payload;
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Read, true)) payload = PluginManager.ComputePayloadHash(zip);
        var manifest = new H2PluginManifest(id, "AR064 fixture", version, minimum, "ar064.publisher", "sha256:" + payload,
            skillOnly ? [] : secondNamespace is null ? [toolName] : [toolName, "fixture.second"], ["audit"], [], permissions ?? (mutating ? ["read", "write"] : ["read"]), SelfTestFile: "selftest.json");
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Update, true)) Write(zip, "manifest.json", JsonSerializer.Serialize(manifest));
        var bytes = memory.ToArray(); File.WriteAllBytes(path, bytes);
        return new(path, new(id, manifest.Name, version, "AR064 fixture", manifest.Publisher, ["fixture"], ["audit"], minimum,
            PluginTrustState.LocalDeveloper, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), path));
    }
    private static void Write(ZipArchive zip, string path, string content)
    { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
    internal sealed class Resolver : IPluginToolExecutorResolver
    {
        public int Calls; public bool Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAgentToolExecutor Resolve(H2PluginManifest manifest, PluginToolDefinition tool)
            => new DelegatingToolExecutor("ar064-fixture", async (_, ct) =>
            {
                Interlocked.Increment(ref Calls); Entered.TrySetResult();
                if (Hold) await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { version = manifest.Version });
            });
    }
    private static ProviderToolDefinition Definition(string name) => new(new(name, "fixture", "Fixture", AgentToolAccess.ReadOnly,
        AgentToolRisk.Low, true, "v1", "fixture", "fixture", "1.0.0"), Json("{}"));
    private sealed class DefinitionProvider : ICapabilityProvider
    {
        public ProviderProvenance Provenance => new("provider.fixture", "1.0.0", "fixture", "fixture");
        public ProviderHealthState Health { get; private set; } = new(ProviderHealthStatus.Ready, DateTime.UtcNow);
        public TaskCompletionSource? LoadRelease, ExecuteRelease;
        public int Calls; public bool Disposed;
        public IReadOnlyList<ProviderToolDefinition> Definitions = [];
        public Action? AfterLoad;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) { Health = new(ProviderHealthStatus.Disconnected, DateTime.UtcNow); return Task.CompletedTask; }
        public Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderNamespaceSummary>>([]);
        public Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderToolSummary>>([]);
        public async Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(IReadOnlyList<string> names, CancellationToken ct)
        { if (LoadRelease is not null) await LoadRelease.Task.WaitAsync(ct).ConfigureAwait(false); AfterLoad?.Invoke(); return Definitions; }
        public Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderResourceSummary>>([]);
        public Task<string> ReadResourceAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public async ValueTask<string> ExecuteToolAsync(string name, JsonElement args, CancellationToken ct)
        { Calls++; if (ExecuteRelease is not null) await ExecuteRelease.Task.WaitAsync(ct).ConfigureAwait(false); return "{}"; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
