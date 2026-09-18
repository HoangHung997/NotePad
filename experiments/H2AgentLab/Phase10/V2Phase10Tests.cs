using System.IO.Compression;
using System.Text;
using System.Text.Json;
using H2AgentLab.Computer;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Tools;
using H2AgentLab.Verification;

namespace H2AgentLab.Phase10;

public static class V2Phase10Tests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new Phase 10 test directory.");
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

        await Test("1001 ScriptWorkspace normalizes legacy runs to v2 artifact/evidence IDs", () =>
        {
            var workspaceRoot = Path.Combine(root, "1001-workspace");
            var stateRoot = Path.Combine(root, "1001-state");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(stateRoot);
            var workspace = new global::H2AgentLab.SafeWorkspace(workspaceRoot);

            var runId = new string('a', 32);
            var runRoot = Path.Combine(stateRoot, "runs", runId);
            var outputRoot = Path.Combine(runRoot, "work", "output");
            Directory.CreateDirectory(outputRoot);
            var artifactBytes = Encoding.UTF8.GetBytes("result");
            File.WriteAllBytes(Path.Combine(outputRoot, "result.txt"), artifactBytes);
            var sha = global::H2AgentLab.SafeWorkspace.Hash(artifactBytes);

            var legacy = new global::H2AgentLab.ScriptRun(
                runId,
                workspace.Root,
                0,
                new Dictionary<string, string>(),
                [new global::H2AgentLab.ScriptArtifact("result.txt", artifactBytes.Length, sha)]);
            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(legacy));

            var scripts = new global::H2AgentLab.ScriptWorkspace(
                workspace,
                stateRoot,
                (_, _) => Task.FromResult(true));
            var evidence = scripts.Evidence(runId);
            Check(evidence.EvidenceId == "evidence:python-run:" + runId,
                "Legacy Python run did not gain deterministic v2 evidence ID.");
            Check(evidence.Artifacts.Count == 1
                && evidence.Artifacts[0].ArtifactId.StartsWith("artifact:python:", StringComparison.Ordinal)
                && evidence.Artifacts[0].EvidenceId.StartsWith("evidence:python-artifact:", StringComparison.Ordinal),
                "Legacy Python artifact did not gain v2 artifact/evidence IDs.");
            Check(scripts.Read(runId, "result.txt").SequenceEqual(artifactBytes),
                "Normalized ScriptWorkspace evidence broke artifact readback.");
            return Task.CompletedTask;
        });

        await Test("1002 WindowsPythonSandbox security profile preserves AppContainer regression contract", () =>
        {
            var profile = global::H2AgentLab.WindowsPythonSandbox.SecurityProfile;
            Check(profile.AppContainer, "Python sandbox is no longer AppContainer.");
            Check(!profile.NetworkCapability, "Python sandbox unexpectedly gained network capability.");
            Check(profile.MaxProcesses == 1
                && profile.MemoryLimitMb == 768
                && profile.TimeoutSeconds == 120
                && profile.MaxOutputMb == 128,
                "Python sandbox security limits changed.");
            foreach (var secret in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GITHUB_TOKEN" })
                Check(!profile.InheritedEnvironmentKeys.Contains(secret, StringComparer.OrdinalIgnoreCase),
                    "Python sandbox security profile exposes secret environment key: " + secret);
            return Task.CompletedTask;
        });

        await Test("1003 run_python remains deferred escape hatch rather than normal structured result", () =>
        {
            var registry = new ToolRegistry();
            var executor = new DelegatingToolExecutor(
                "phase10",
                (call, ct) => ValueTask.FromResult("{}"));

            registry.Register(Tool(
                "read_file",
                "files",
                "Read document file content safely.",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                executor));
            registry.Register(Tool(
                "run_python",
                "python",
                "Execute Python script for unsupported custom transforms.",
                AgentToolAccess.Mutating,
                AgentToolRisk.Medium,
                executor));

            var discovery = new DeferredToolDiscovery(registry);
            var initial = discovery.BuildInitialExposure();
            Check(!initial.CallableSchemas
                .Select(DeferredToolDiscovery.SchemaName)
                .Contains("run_python", StringComparer.Ordinal),
                "run_python leaked into initial tool schemas.");

            var normal = discovery.Search("read document file", 8);
            Check(!normal.Any(x => x.Descriptor.Name == "run_python"),
                "run_python was exposed while a structured file tool satisfied the task.");

            var explicitPython = discovery.Search("use python script custom transform", 8);
            Check(explicitPython.Any(x => x.Descriptor.Name == "run_python"),
                "Explicit Python intent could not discover run_python escape hatch.");
            return Task.CompletedTask;
        });

        await Test("1004 Python result verifier requires artifacts or explicit assertions beyond exit code zero", () =>
        {
            try
            {
                _ = new PythonVerificationExpectation();
                throw new InvalidOperationException("Empty Python verification expectation was accepted.");
            }
            catch (ArgumentException)
            {
            }

            var artifact = new global::H2AgentLab.ScriptArtifact(
                "result.xlsx",
                120,
                new string('a', 64))
            {
                ArtifactId = "artifact:python:fixture:1",
                EvidenceId = "evidence:python-artifact:fixture:1"
            };
            var run = new global::H2AgentLab.ScriptRunEvidence(
                "fixture",
                "evidence:python-run:fixture",
                0,
                [artifact],
                DateTime.UtcNow);

            var pass = PythonResultVerifier.Verify(
                run,
                new PythonVerificationExpectation(
                    requiredArtifacts:
                    [
                        new PythonArtifactExpectation(
                            "result.xlsx",
                            artifact.Sha256,
                            100)
                    ]));
            Check(pass.Passed, "Verified Python artifact did not pass.");

            var fail = PythonResultVerifier.Verify(
                run,
                new PythonVerificationExpectation(
                    assertions:
                    [
                        new PythonAssertionResult(
                            "formula-preserved",
                            false,
                            "evidence:assert:1",
                            "Formula changed.")
                    ]));
            Check(!fail.Passed
                && fail.Failures.Any(x => x.CriterionId == PythonResultVerifier.AssertionCriterionId),
                "Failed explicit task assertion did not block Python verification.");
            return Task.CompletedTask;
        });

        await Test("1005 unsupported Office transform falls back to Python then deterministic verifier", () =>
        {
            Check(
                PythonFallbackRouter.Choose(
                    "unsupported custom Office transform",
                    structuredOfficeSupportsTask: false,
                    pythonAvailable: true)
                == PythonFallbackDecision.PythonEscapeHatch,
                "Unsupported Office transform did not route to Python escape hatch.");
            Check(
                PythonFallbackRouter.Choose(
                    "ordinary Excel range write",
                    structuredOfficeSupportsTask: true,
                    pythonAvailable: true)
                == PythonFallbackDecision.StructuredOffice,
                "Structured Office task incorrectly routed to Python.");

            var artifact = new global::H2AgentLab.ScriptArtifact(
                "fallback.xlsx",
                256,
                new string('b', 64))
            {
                ArtifactId = "artifact:python:fallback:1",
                EvidenceId = "evidence:python-artifact:fallback:1"
            };
            var report = PythonResultVerifier.Verify(
                new global::H2AgentLab.ScriptRunEvidence(
                    "fallback",
                    "evidence:python-run:fallback",
                    0,
                    [artifact],
                    DateTime.UtcNow),
                new PythonVerificationExpectation(
                    requiredArtifacts:
                    [new PythonArtifactExpectation("fallback.xlsx", artifact.Sha256, 1)],
                    assertions:
                    [new PythonAssertionResult("office-preservation", true, "evidence:office:verified", "verified")]));
            Check(report.Passed, "Fallback Python result did not pass deterministic verification.");
            return Task.CompletedTask;
        });

        await Test("1006 provider contracts preserve MCP provenance namespaces and host-owned health", async () =>
        {
            var transport = new FakeMcpTransport();
            var definition = McpDefinition();
            await using var connection = new McpServerConnection(definition, transport);
            await using var provider = new McpToolProvider(connection, ProviderPolicy());

            await provider.ConnectAsync(CancellationToken.None);
            Check(provider.Provenance.ProviderId == "fixture-mcp"
                && provider.Provenance.ServerId == "fixture-server"
                && provider.Provenance.TransportKind == "mcp",
                "MCP provider provenance was not preserved.");
            Check(provider.Health.Status == ProviderHealthStatus.Ready,
                "MCP provider health is not host-owned Ready state.");

            var namespaces = await provider.ListNamespacesAsync(CancellationToken.None);
            Check(namespaces.Any(x => x.Name == "files"),
                "MCP namespace summary did not include files capability family.");
        });

        await Test("1007 MCP lifecycle reconnects boundedly and safe metadata does not expose secrets", async () =>
        {
            var transport = new FakeMcpTransport
            {
                FailToolsListOnce = true
            };
            var definition = McpDefinition();
            var metadata = JsonSerializer.Serialize(definition.SafeMetadata());
            Check(!metadata.Contains("super-secret-token", StringComparison.Ordinal),
                "MCP safe metadata exposed secret environment value.");
            Check(metadata.Contains("TOKEN", StringComparison.Ordinal),
                "MCP safe metadata should retain environment key names for diagnostics.");

            await using var connection = new McpServerConnection(
                definition,
                transport,
                maxReconnectAttempts: 2);
            await connection.ConnectAsync(CancellationToken.None);
            _ = await connection.CallAsync("tools/list", new { }, CancellationToken.None);

            Check(transport.StartCount == 2
                && transport.StopCount >= 1
                && connection.Health.Status == ProviderHealthStatus.Ready,
                "MCP connection did not reconnect deterministically after bounded failure.");
            Check(connection.NegotiatedProtocolVersion == "2025-06-18",
                "MCP negotiated protocol version was not preserved.");
        });

        await Test("1008 MCP tools/resources load lazily into ToolRegistry after selection", async () =>
        {
            var transport = new FakeMcpTransport();
            await using var connection = new McpServerConnection(McpDefinition(), transport);
            await using var provider = new McpToolProvider(connection, ProviderPolicy());
            await provider.ConnectAsync(CancellationToken.None);

            var summaries = await provider.ListToolSummariesAsync(CancellationToken.None);
            Check(summaries.Count == 2
                && summaries.Any(x => x.Name == "files.read")
                && summaries.Any(x => x.Name == "files.write"),
                "MCP tool summaries were not enumerated.");

            var registry = new ToolRegistry();
            Check(!registry.TryGet("files.read", out _),
                "MCP detailed schema was registered before selection.");

            var adapter = new McpToolRegistryAdapter(registry);
            var loaded = await adapter.LoadSelectedAsync(
                provider,
                ["files.read"],
                CancellationToken.None);
            Check(loaded.Count == 1
                && registry.TryGet("files.read", out var descriptor)
                && descriptor.CallableSchema.GetProperty("function").GetProperty("name").GetString() == "files.read",
                "Selected MCP schema was not normalized into ToolRegistry.");
            Check(!registry.TryGet("files.write", out _),
                "Unselected MCP schema was loaded eagerly.");

            var resources = await provider.ListResourcesAsync(CancellationToken.None);
            Check(resources.Count == 1
                && resources[0].ResourceId == "fixture://readme",
                "MCP resource summary was not normalized.");
            Check((await provider.ReadResourceAsync("fixture://readme", CancellationToken.None))
                .Contains("fixture resource", StringComparison.Ordinal),
                "MCP resource read did not return bounded content.");
        });

        await Test("1009 MCP scope permission parallel and verification-trust boundaries are enforced", async () =>
        {
            var transport = new FakeMcpTransport();
            await using var connection = new McpServerConnection(McpDefinition(), transport);
            await using var provider = new McpToolProvider(connection, ProviderPolicy());
            await provider.ConnectAsync(CancellationToken.None);
            _ = await provider.ListToolSummariesAsync(CancellationToken.None);

            var registry = new ToolRegistry();
            var manager = new CapabilityProviderManager(registry);
            manager.Register(provider);
            _ = await manager.LoadMcpToolsAsync(
                "fixture-mcp",
                ["files.read", "files.write"],
                CancellationToken.None);

            Check(registry.TryGet("files.read", out var read)
                && read.Access == AgentToolAccess.ReadOnly
                && read.SupportsParallel
                && read.ResourceScope?.ScopeId == "workspace-a"
                && !read.CanProvideVerificationEvidence,
                "Read-only MCP descriptor lost scope/parallel/trust metadata.");
            Check(registry.TryGet("files.write", out var write)
                && write.Access == AgentToolAccess.Mutating
                && !write.SupportsParallel
                && write.Risk == AgentToolRisk.High
                && !write.CanProvideVerificationEvidence,
                "Mutating MCP descriptor lost conservative metadata.");

            var readResult = await read.Executor.ExecuteAsync(
                new global::H2AgentLab.ToolCall(
                    "call-read",
                    "files.read",
                    JsonSerializer.SerializeToElement(new { path = "a.txt" })),
                CancellationToken.None);
            Check(readResult.Contains("fixture-result", StringComparison.Ordinal),
                "Allowed MCP read tool did not execute.");

            try
            {
                _ = await write.Executor.ExecuteAsync(
                    new global::H2AgentLab.ToolCall(
                        "call-write",
                        "files.write",
                        JsonSerializer.SerializeToElement(new { path = "a.txt", text = "x" })),
                    CancellationToken.None);
                throw new InvalidOperationException("Denied MCP mutation scope executed.");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var evidence = manager.BuildExecutionEvidence(read, new string('c', 64));
            Check(!evidence.TrustedAsVerificationEvidence
                && evidence.ProviderId == "fixture-mcp"
                && evidence.ScopeId == "workspace-a",
                "MCP provider result was incorrectly trusted as verifier evidence.");
        });

        await Test("1010 filesystem capability family stays inside SafeWorkspace and hash guards mutations", () =>
        {
            var workspaceRoot = Path.Combine(root, "1010-workspace");
            var stateRoot = Path.Combine(root, "1010-state");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "sub"));
            File.WriteAllText(Path.Combine(workspaceRoot, "source.txt"), "one");

            var workspace = new global::H2AgentLab.SafeWorkspace(workspaceRoot);
            var files = new FilesystemCapabilities(
                workspace,
                stateRoot,
                new FilesystemCapabilityPolicy(
                    AllowWrite: true,
                    AllowMove: true,
                    AllowDelete: true));

            Check(files.List().Contains("source.txt", StringComparer.OrdinalIgnoreCase),
                "filesystem.list missed source file.");
            Check(files.Stat("source.txt").Sha256 == files.Hash("source.txt"),
                "filesystem.stat/hash disagree.");

            var firstWatch = files.Watch();
            var sourceHash = files.Hash("source.txt");
            _ = files.Copy("source.txt", "sub/copied.txt");
            _ = files.Write("source.txt", Encoding.UTF8.GetBytes("two"), sourceHash);
            var diff = files.WatchDiff(firstWatch);
            Check(diff.Added.Contains("sub/copied.txt", StringComparer.Ordinal)
                && diff.Changed.Contains("source.txt", StringComparer.Ordinal),
                "filesystem.watch diff did not observe add/change.");

            var copiedHash = files.Hash("sub/copied.txt");
            _ = files.Move("sub/copied.txt", "sub/moved.txt", copiedHash);
            var movedHash = files.Hash("sub/moved.txt");
            files.Delete("sub/moved.txt", movedHash);
            Check(!File.Exists(Path.Combine(workspaceRoot, "sub", "moved.txt")),
                "filesystem.delete_with_policy did not remove verified file.");

            try
            {
                _ = files.Read("../outside.txt");
                throw new InvalidOperationException("filesystem.read escaped workspace.");
            }
            catch (global::H2AgentLab.AgentFaultException)
            {
            }
            return Task.CompletedTask;
        });

        await Test("1011 bounded process shell capabilities enforce executable policy timeout and secret stripping", async () =>
        {
            var workspaceRoot = Path.Combine(root, "1011-workspace");
            Directory.CreateDirectory(workspaceRoot);
            var workspace = new global::H2AgentLab.SafeWorkspace(workspaceRoot);
            using var service = new ProcessShellCapabilities(
                workspace,
                new ProcessShellPolicy(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dotnet" },
                    AllowStart: true,
                    AllowTerminate: true,
                    DefaultTimeout: TimeSpan.FromSeconds(20)));

            var result = await service.RunBoundedAsync(
                "dotnet",
                ["--version"],
                cancellationToken: CancellationToken.None);
            Check(result.ExitCode == 0
                && !result.TimedOut
                && !string.IsNullOrWhiteSpace(result.Stdout),
                "Bounded dotnet process did not complete.");

            Check(service.List().Count > 0,
                "process.list returned no bounded metadata.");

            try
            {
                _ = await service.RunBoundedAsync(
                    "cmd",
                    ["/c", "echo unsafe"],
                    cancellationToken: CancellationToken.None);
                throw new InvalidOperationException("Disallowed executable ran.");
            }
            catch (UnauthorizedAccessException)
            {
            }
        });

        await Test("1012 normalized general computer catalog exposes typed families without monolithic control tool", () =>
        {
            var required = new[]
            {
                "filesystem", "process", "shell", "app",
                "window", "uia", "input", "screen", "browser"
            };
            foreach (var ns in required)
                Check(GeneralComputerCapabilityCatalog.Namespace(ns).Count > 0,
                    "Missing normalized computer capability namespace: " + ns);

            Check(!GeneralComputerCapabilityCatalog.ContainsMonolithicUnsafeControl(),
                "General computer catalog contains monolithic unsafe control tool.");
            Check(GeneralComputerCapabilityCatalog.Namespace("browser")
                .All(x => x.Backing == ComputerCapabilityBacking.WebResearchOrBrowserFallback),
                "Browser capabilities are not explicitly marked as fallback.");
            return Task.CompletedTask;
        });

        await Test("1013 plugin manifest catalog and capability index expose compact metadata before install", () =>
        {
            var package = BuildPluginPackage(
                root,
                "1.0.0",
                "First skill content.",
                permissions: ["workspace.read"],
                selfTestOk: true);
            var manifest = H2PluginManifest.Parse(package.ManifestJson);
            Check(manifest.Id == "h2.fixture.productivity"
                && manifest.Capabilities.SequenceEqual(new[] { "plugin.echo" })
                && manifest.Skills.SequenceEqual(new[] { "audit" }),
                "Plugin manifest identity/capabilities/skills are wrong.");

            var catalog = new PluginCatalog();
            catalog.Add(package.CatalogEntry);
            var search = catalog.Search("fixture echo audit");
            Check(search.Count == 1
                && search[0].Id == manifest.Id
                && search[0].DownloadLocation == package.Path,
                "Plugin catalog metadata search failed.");
            Check(!Directory.Exists(Path.Combine(root, "1013-not-installed")),
                "Catalog search should not install/download package content.");
            return Task.CompletedTask;
        });

        await Test("1014 staged plugin install verifies integrity self-test and hot-registers only at safe boundary", () =>
        {
            var stateRoot = Path.Combine(root, "1014-state");
            Directory.CreateDirectory(stateRoot);
            var registry = new ToolRegistry();
            var manager = new PluginManager(
                stateRoot,
                registry,
                new FixturePluginResolver());

            var package = BuildPluginPackage(
                root,
                "1.0.0",
                "First skill content.",
                permissions: ["workspace.read"],
                selfTestOk: true);
            var result = manager.InstallFromArchive(
                package.Path,
                package.CatalogEntry,
                DeveloperPolicy(),
                userApproved: true);

            Check(result.Activated
                && Directory.Exists(result.VersionRoot)
                && File.Exists(Path.Combine(
                    stateRoot,
                    "plugins",
                    "h2.fixture.productivity",
                    "active.json")),
                "Plugin was not staged/versioned/atomically activated.");
            Check(registry.TryGet("plugin.echo", out var descriptor)
                && descriptor.Provenance?.ProviderId == "plugin.h2.fixture.productivity"
                && descriptor.Provenance.ProviderVersion == "1.0.0",
                "Activated plugin tool was not hot-registered with version provenance.");

            var v2 = BuildPluginPackage(
                root,
                "2.0.0",
                "Second skill content.",
                permissions: ["workspace.read"],
                selfTestOk: true);
            using (manager.EnterToolCall())
            {
                try
                {
                    _ = manager.InstallFromArchive(
                        v2.Path,
                        v2.CatalogEntry,
                        DeveloperPolicy(),
                        userApproved: true);
                    throw new InvalidOperationException("Plugin activated during in-flight tool call.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("in flight", StringComparison.OrdinalIgnoreCase))
                {
                }
            }
            return Task.CompletedTask;
        });

        await Test("1015 plugin update discovery rollback quarantine permission delta and skill hash evidence are deterministic", () =>
        {
            var stateRoot = Path.Combine(root, "1015-state");
            Directory.CreateDirectory(stateRoot);
            var registry = new ToolRegistry();
            var manager = new PluginManager(
                stateRoot,
                registry,
                new FixturePluginResolver());
            var catalog = new PluginCatalog();

            var v1 = BuildPluginPackage(
                root,
                "1.0.0",
                "First skill content.",
                permissions: ["workspace.read"],
                selfTestOk: true);
            var v2 = BuildPluginPackage(
                root,
                "2.0.0",
                "Second changed skill content.",
                permissions: ["workspace.read"],
                selfTestOk: true);
            catalog.Add(v1.CatalogEntry);
            catalog.Add(v2.CatalogEntry);

            _ = manager.InstallFromArchive(
                v1.Path,
                v1.CatalogEntry,
                DeveloperPolicy(),
                userApproved: true);
            Check(manager.DiscoverUpdates(catalog, "h2.fixture.productivity")
                .Select(x => x.Version)
                .SequenceEqual(new[] { "2.0.0" }),
                "Plugin update discovery did not find newer compatible metadata.");

            var skills = new PluginSkillCatalog(manager);
            var firstRead = skills.Read("h2.fixture.productivity", "audit");
            var repeated = skills.Read("h2.fixture.productivity", "audit");
            Check(firstRead.Summary.Sha256 == repeated.Summary.Sha256
                && repeated.LoadCount == 1,
                "Unchanged plugin skill was reread instead of using task cache.");

            _ = manager.InstallFromArchive(
                v2.Path,
                v2.CatalogEntry,
                DeveloperPolicy(),
                userApproved: true);
            var secondRead = skills.Read("h2.fixture.productivity", "audit");
            Check(secondRead.Summary.PluginVersion == "2.0.0"
                && secondRead.Summary.Sha256 != firstRead.Summary.Sha256,
                "Changed plugin skill version/hash did not invalidate cached identity.");

            var evidence = manager.BuildEvidence("h2.fixture.productivity");
            Check(evidence.PluginVersion == "2.0.0"
                && evidence.Skills.Single().Sha256 == secondRead.Summary.Sha256
                && evidence.ToolVersions.Single().Contains("@2.0.0", StringComparison.Ordinal),
                "Plugin/skill/tool exact versions were not recorded in task evidence.");

            manager.Quarantine(
                "h2.fixture.productivity",
                "2.0.0",
                "fixture regression");
            Check(manager.GetActive("h2.fixture.productivity")?.Manifest.Version == "1.0.0",
                "Quarantining active bad version did not roll back to previous version.");
            Check(File.Exists(Path.Combine(
                    stateRoot,
                    "plugins",
                    "h2.fixture.productivity",
                    "2.0.0",
                    "quarantine.json")),
                "Quarantine diagnostics marker was not retained.");

            var bad = BuildPluginPackage(
                root,
                "2.1.0",
                "Bad self-test skill.",
                permissions: ["workspace.read"],
                selfTestOk: false);
            try
            {
                _ = manager.InstallFromArchive(
                    bad.Path,
                    bad.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);
                throw new InvalidOperationException("Plugin with failed self-test activated.");
            }
            catch (InvalidDataException)
            {
            }
            Check(manager.GetActive("h2.fixture.productivity")?.Manifest.Version == "1.0.0",
                "Failed plugin update changed previous working active version.");

            var broader = BuildPluginPackage(
                root,
                "3.0.0",
                "Broader permissions.",
                permissions: ["workspace.read", "workspace.write"],
                selfTestOk: true,
                trustState: PluginTrustState.TrustedOfficial);
            var trustedPolicy = new PluginInstallPolicy(
                PluginInstallMode.TrustedOfficialOnly,
                new HashSet<string>(StringComparer.Ordinal) { "fixture.publisher" },
                AllowNativeHelpers: false,
                AllowLifecycleHooks: false);
            try
            {
                _ = manager.InstallFromArchive(
                    broader.Path,
                    broader.CatalogEntry,
                    trustedPolicy,
                    userApproved: false);
                throw new InvalidOperationException("Broader plugin permissions inherited old approval.");
            }
            catch (UnauthorizedAccessException ex) when (
                ex.Message.Contains("broader permissions", StringComparison.OrdinalIgnoreCase))
            {
            }

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var reportPath = Path.Combine(root, "v2-phase10-tests.txt");
        await File.WriteAllLinesAsync(reportPath, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolDescriptor Tool(
        string name,
        string ns,
        string description,
        AgentToolAccess access,
        AgentToolRisk risk,
        IAgentToolExecutor executor)
        => new(
            name,
            new ToolNamespace(ns, ns + " fixture namespace"),
            description,
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
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            }),
            executor: executor);

    private static McpServerDefinition McpDefinition()
        => new(
            "fixture-mcp",
            "1.2.3",
            "fixture-server",
            "fixture-mcp.exe",
            ["--stdio"],
            new Dictionary<string, string>
            {
                ["TOKEN"] = "super-secret-token"
            },
            TimeSpan.FromSeconds(2));

    private static CapabilityProviderPolicy ProviderPolicy()
        => new(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "workspace-a",
                "provider:fixture-mcp"
            },
            new HashSet<string>(StringComparer.Ordinal),
            AllowParallelReadOnly: true,
            TrustProviderVerificationClaims: false);

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "fixture.publisher"
            },
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);

    private static BuiltPluginPackage BuildPluginPackage(
        string root,
        string version,
        string skillBody,
        IReadOnlyList<string> permissions,
        bool selfTestOk,
        PluginTrustState trustState = PluginTrustState.LocalDeveloper)
    {
        var packageDir = Path.Combine(root, "plugin-packages");
        Directory.CreateDirectory(packageDir);
        var path = Path.Combine(
            packageDir,
            "fixture-" + version.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N") + ".zip");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZip(
                zip,
                "tools.json",
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            name = "plugin.echo",
                            @namespace = "plugin-fixture",
                            description = "Echo fixture input through an activated plugin executor.",
                            access = "ReadOnly",
                            risk = "Low",
                            supportsParallel = true,
                            schemaVersion = "v1",
                            toolVersion = version,
                            resourceScope = "workspace-a",
                            serializationKey = "plugin-fixture",
                            schema = new
                            {
                                type = "function",
                                function = new
                                {
                                    name = "plugin.echo",
                                    description = "Echo fixture input.",
                                    parameters = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            text = new
                                            {
                                                type = "string"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }));
            WriteZip(
                zip,
                "skills/audit/SKILL.md",
                "---
name: audit
description: Fixture audit skill
---

" + skillBody);
            WriteZip(
                zip,
                "selftest.json",
                JsonSerializer.Serialize(new
                {
                    ok = selfTestOk,
                    requiredFiles = new[]
                    {
                        "tools.json",
                        "skills/audit/SKILL.md"
                    }
                }));
        }

        memory.Position = 0;
        string payloadHash;
        using (var read = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true))
            payloadHash = PluginManager.ComputePayloadHash(read);

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            "h2.fixture.productivity",
            "Fixture Productivity",
            version,
            "2.0.0",
            "fixture.publisher",
            "sha256:" + payloadHash,
            ["plugin.echo"],
            ["audit"],
            ["native-host"],
            permissions,
            NativeHelpers: [],
            LifecycleHooks: [],
            SelfTestFile: "selftest.json");
        var manifestJson = JsonSerializer.Serialize(manifest);

        using (var update = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            WriteZip(update, "manifest.json", manifestJson);

        var bytes = memory.ToArray();
        File.WriteAllBytes(path, bytes);
        var archiveHash = global::H2AgentLab.SafeWorkspace.Hash(bytes).ToLowerInvariant();
        var entry = new PluginCatalogEntry(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            "Fixture plugin for staged activation tests.",
            manifest.Publisher,
            ["plugin", "echo", "fixture"],
            ["audit"],
            manifest.MinAgentVersion,
            trustState,
            "sha256:" + archiveHash,
            path);

        return new BuiltPluginPackage(
            path,
            manifestJson,
            entry);
    }

    private static void WriteZip(
        ZipArchive zip,
        string path,
        string content)
    {
        var entry = zip.CreateEntry(
            path,
            CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: false);
        writer.Write(content);
    }

    private sealed record BuiltPluginPackage(
        string Path,
        string ManifestJson,
        PluginCatalogEntry CatalogEntry);

    private sealed class FixturePluginResolver : IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
            => new DelegatingToolExecutor(
                "plugin-fixture",
                (call, ct) => ValueTask.FromResult(
                    JsonSerializer.Serialize(new
                    {
                        plugin = manifest.Id,
                        version = manifest.Version,
                        tool = tool.Name,
                        arguments = call.Arguments
                    })));
    }

    private sealed class FakeMcpTransport : IMcpRpcTransport
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool FailToolsListOnce { get; set; }
        private bool _failedToolsList;

        public Task StartAsync(
            McpServerDefinition definition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            StopCount++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> CallAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning)
                throw new IOException("Fake MCP transport is disconnected.");

            if (method == "tools/list"
                && FailToolsListOnce
                && !_failedToolsList)
            {
                _failedToolsList = true;
                IsRunning = false;
                throw new IOException("fixture MCP transient disconnect");
            }

            return Task.FromResult(method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = "2025-06-18",
                    serverInfo = new
                    {
                        name = "fixture",
                        version = "1.0"
                    }
                }),
                "tools/list" => ToolsList(),
                "tools/call" => JsonSerializer.SerializeToElement(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "fixture-result"
                        }
                    },
                    isError = false
                }),
                "resources/list" => JsonSerializer.SerializeToElement(new
                {
                    resources = new[]
                    {
                        new
                        {
                            uri = "fixture://readme",
                            name = "Fixture resource",
                            description = "Fixture resource description",
                            _meta = new Dictionary<string, object>
                            {
                                ["h2.namespace"] = "files",
                                ["h2.scope"] = "workspace-a"
                            }
                        }
                    }
                }),
                "resources/read" => JsonSerializer.SerializeToElement(new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = "fixture://readme",
                            text = "fixture resource body"
                        }
                    }
                }),
                _ => throw new IOException("Unsupported fake MCP method: " + method)
            });
        }

        public Task NotifyAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning)
                throw new IOException("Fake MCP transport is disconnected.");
            return Task.CompletedTask;
        }

        private static JsonElement ToolsList()
            => JsonSerializer.SerializeToElement(new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "files.read",
                        description = "Read fixture workspace file.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new
                                {
                                    type = "string"
                                }
                            }
                        },
                        annotations = new
                        {
                            readOnlyHint = true
                        },
                        _meta = new Dictionary<string, object>
                        {
                            ["h2.namespace"] = "files",
                            ["h2.scope"] = "workspace-a",
                            ["h2.parallelSafe"] = true,
                            ["h2.serializationKey"] = "workspace-a",
                            ["h2.schemaVersion"] = "v2",
                            ["h2.toolVersion"] = "2.1.0"
                        }
                    },
                    new
                    {
                        name = "files.write",
                        description = "Write fixture workspace file.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                text = new { type = "string" }
                            }
                        },
                        annotations = new
                        {
                            readOnlyHint = false,
                            destructiveHint = true
                        },
                        _meta = new Dictionary<string, object>
                        {
                            ["h2.namespace"] = "files",
                            ["h2.scope"] = "workspace-a",
                            ["h2.parallelSafe"] = false,
                            ["h2.serializationKey"] = "workspace-a",
                            ["h2.schemaVersion"] = "v2",
                            ["h2.toolVersion"] = "2.1.0"
                        }
                    }
                }
            });

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}
