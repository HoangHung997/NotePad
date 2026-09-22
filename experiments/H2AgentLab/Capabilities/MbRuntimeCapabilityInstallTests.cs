using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Catalog;
using H2AgentLab.Context;
using H2AgentLab.Plugins;
using H2AgentLab.Prompting;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using System.Security.Cryptography;

namespace H2AgentLab.Capabilities;

public static class MbRuntimeCapabilityInstallTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-74 test directory.");
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

        await Test("MB-74 ordinary AgentRuntime installs tool-only skill-only and provider-plus-tool plugins without restating the task", async () =>
        {
            var packageRoot = Path.Combine(root, "packages");
            var stagingRoot = Path.Combine(root, "staging");
            var stateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(stateRoot);

            var toolOnly = BuildPlugin(
                packageRoot,
                "fixture.toolonly",
                toolName: "fixture.transform_text",
                toolDescription: "Transform document text using the tool-only fixture extension.",
                skillId: null,
                skillDescription: null,
                providers: []);
            var skillOnly = BuildPlugin(
                packageRoot,
                "fixture.skillonly",
                toolName: null,
                toolDescription: null,
                skillId: "spreadsheet-audit",
                skillDescription: "Spreadsheet reconciliation audit guidance for formulas and totals.",
                providers: []);
            var providerTool = BuildPlugin(
                packageRoot,
                "fixture.provider",
                toolName: "fixture.native_diagnostics",
                toolDescription: "Run native diagnostics through a provider-backed structured tool.",
                skillId: null,
                skillDescription: null,
                providers: ["fixture.native"]);

            var source = new InMemoryCatalogSource(
                "fixture-catalog",
                100,
                PluginTrustState.LocalDeveloper,
                [
                    Available(toolOnly, "Transform document text using a tool-only extension."),
                    Available(skillOnly, "Spreadsheet reconciliation audit guidance for formulas and totals."),
                    Available(providerTool, "Native diagnostics through a provider-backed structured tool.")
                ]);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);

            var registry = new ToolRegistry();
            var pluginManager = new PluginManager(
                stateRoot,
                registry,
                new FixturePluginResolver());
            var skills = new H2AgentLab.Skills.SkillCatalog();
            skills.Register(new PluginSkillSource(pluginManager));

            var installed = new InstalledCapabilityIndex();
            installed.Bind(
                registry,
                skills,
                static () => Array.Empty<H2AgentLab.Providers.ProviderProvenance>());
            var available = new AvailableCapabilityIndex();
            var resolver = new CapabilityResolver(
                installed,
                available,
                catalogs,
                new PredicateCapabilityInstallPolicy(
                    x => x.TrustState == PluginTrustState.LocalDeveloper));
            var retriever = new LocalPackageRetriever(
                stagingRoot,
                [packageRoot]);
            var catalogExecutor = new CatalogRuntimeToolExecutor(
                resolver,
                retriever,
                pluginManager,
                DeveloperPolicy(),
                userApproved: true);

            CatalogRuntimeTools.Register(registry, catalogExecutor);
            RegisterSkillTools(
                registry,
                new SkillRuntimeToolExecutor(skills));

            Check(registry.TryGet(
                    CatalogRuntimeToolExecutor.InstallToolName,
                    out var installDescriptor)
                && !installDescriptor.CallableSchema
                    .GetRawText()
                    .Contains("approved", StringComparison.OrdinalIgnoreCase),
                "Model-callable install schema exposed host approval as a tool argument.");

            var verifier = new InstalledPackageReadbackVerifier(pluginManager, registry, skills,
                [toolOnly, skillOnly, providerTool]);
            Check(!verifier.IsInstalled(toolOnly.Manifest.Id), "Missing package was falsely verified.");
            var transport = new InstallAndContinueTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                verifier: verifier);

            var request = Request(
                "Install and use the missing tool-only, skill-only and provider-backed fixture capabilities without asking me to restate this task.");
            var result = await runtime.RunAsync(
                request,
                CancellationToken.None);

            Check(result.FinalText == "mb74-runtime-ok",
                "AgentRuntime did not finish the original task after extension installs.");
            Check(transport.StartCalls == 1,
                "Capability installation restarted the model turn or required a new user request.");
            Check(source.FetchCount == 1,
                "Ordinary runtime flow repeatedly refreshed fresh catalog metadata.");
            Check(pluginManager.GetActive("fixture.toolonly") is not null
                && pluginManager.GetActive("fixture.skillonly") is not null
                && pluginManager.GetActive("fixture.provider") is not null,
                "One or more accepted extension shapes did not activate.");
            Check(registry.TryGet("fixture.transform_text", out _)
                && registry.TryGet("fixture.native_diagnostics", out _),
                "ToolRegistry did not expose newly activated plugin tools.");
            Check(skills.Search("spreadsheet reconciliation audit", 20)
                    .Any(x => x.Identity.PluginId == "fixture.skillonly"
                        && x.Identity.SkillId == "spreadsheet-audit"),
                "Canonical SkillCatalog did not expose the newly activated skill-only plugin.");
            Check(result.LoadedToolSchemas.Contains(
                    "fixture.transform_text",
                    StringComparer.Ordinal)
                && result.LoadedToolSchemas.Contains(
                    "fixture.native_diagnostics",
                    StringComparer.Ordinal),
                "Same-task deferred discovery did not load tools registered after install.");
            Check(verifier.ObservedInstallCount == 3 && result.VerificationHistory.Count == 3
                && result.VerificationHistory.All(report => report.Passed),
                "Each actual install must have a separate independent activation/payload readback.");
            var active = pluginManager.GetActive(providerTool.Manifest.Id)!.Value;
            var payload = Path.Combine(active.VersionRoot, "tools.json");
            var acceptedBytes = File.ReadAllBytes(payload);
            File.WriteAllText(payload, "tampered fixture bytes");
            Check(!verifier.IsInstalled(providerTool.Manifest.Id), "Tampered installed payload was falsely verified.");
            File.WriteAllBytes(payload, acceptedBytes);
            Check(verifier.IsInstalled(providerTool.Manifest.Id), "Restored exact payload failed readback.");
        });

        await Test("MB-74 retired one-off continuation source is absent", () =>
        {
            var repo = FindRepoRoot();
            var retired = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Capabilities",
                "MissingCapabilityContinuation.cs");
            Check(!File.Exists(retired),
                "MissingCapabilityContinuation still exists after MB-74 retirement.");

            var runtimeTools = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Catalog",
                    "CatalogRuntimeTools.cs"));
            Check(!runtimeTools.Contains(
                    "MissingCapabilityContinuation",
                    StringComparison.Ordinal)
                && runtimeTools.Contains(
                    "Use tool_search/list_skills again",
                    StringComparison.Ordinal),
                "Catalog runtime path still depends on the retired special continuation workflow.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-runtime-capability-install-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string userInput)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "extensions:fixture-catalog",
            null,
            ["install selected extension packages"],
            ["host policy and approval remain authoritative"],
            ["continue the original task after install"],
            [new AgentAcceptanceCriterion("mb74.activation-readback", "Requested package bytes and callable registrations match the selected fixture.")],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: true,
                requiredVerifierIds: ["mb74-package-readback"]));

        return new AgentRuntimeRequest(
            contract,
            userInput,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "HOST POLICY",
                "Use ordinary tool_search, catalog_search, plugin_install and refreshed tools/skills to continue the same task.",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-74 extension install-and-continue fixture"),
            PromptCacheKey: "mb74",
            MaxToolRounds: 32);
    }

    private static void RegisterSkillTools(
        ToolRegistry registry,
        SkillRuntimeToolExecutor executor)
    {
        var toolNamespace = new ToolNamespace(
            "skills",
            "List installed skill metadata and explicitly read selected skill guidance.");

        registry.Register(new ToolDescriptor(
            SkillRuntimeToolExecutor.SearchToolName,
            toolNamespace,
            "List and search installed skills by name and description metadata.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = SkillRuntimeToolExecutor.SearchToolName,
                    description = "List and search installed skill guidance metadata.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" }
                        },
                        additionalProperties = false
                    }
                }
            }),
            executor: executor));

        registry.Register(new ToolDescriptor(
            SkillRuntimeToolExecutor.ReadToolName,
            toolNamespace,
            "Read one installed skill SKILL.md or one explicitly requested skill resource.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = SkillRuntimeToolExecutor.ReadToolName,
                    description = "Read one selected installed skill or explicit resource.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            path = new { type = "string" }
                        },
                        required = new[] { "name", "path" },
                        additionalProperties = false
                    }
                }
            }),
            executor: executor));
    }

    private static AvailableCapabilityRecord Available(
        BuiltPluginPackage package,
        string compactDescription)
        => new(
            package.Manifest.Id,
            package.Manifest.Version,
            package.Manifest.Publisher,
            PluginTrustState.LocalDeveloper,
            package.Manifest.MinAgentVersion,
            "sha256:" + package.ArchiveSha256,
            package.Path,
            package.ToolName is null
                ? []
                : [compactDescription],
            package.SkillId is null
                ? []
                :
                [
                    new AvailableSkillMetadata(
                        package.SkillId,
                        package.SkillId,
                        package.SkillDescription!)
                ],
            package.Manifest.Providers,
            package.Manifest.Permissions,
            "fixture-catalog",
            100,
            DateTime.UtcNow,
            MetadataStale: false);

    private static BuiltPluginPackage BuildPlugin(
        string packageRoot,
        string pluginId,
        string? toolName,
        string? toolDescription,
        string? skillId,
        string? skillDescription,
        IReadOnlyList<string> providers)
    {
        Directory.CreateDirectory(packageRoot);
        var path = Path.Combine(
            packageRoot,
            pluginId.Replace('.', '-')
                + "-"
                + Guid.NewGuid().ToString("N")
                + ".zip");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(
            memory,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            if (toolName is not null)
            {
                WriteZip(
                    zip,
                    "tools.json",
                    JsonSerializer.Serialize(
                        new[]
                        {
                            new
                            {
                                name = toolName,
                                @namespace = "fixture",
                                description = toolDescription!,
                                access = "ReadOnly",
                                risk = "Low",
                                supportsParallel = true,
                                schemaVersion = "v1",
                                toolVersion = "1.0.0",
                                resourceScope = "fixture:workspace",
                                serializationKey = "fixture",
                                schema = new
                                {
                                    type = "function",
                                    function = new
                                    {
                                        name = toolName,
                                        description = toolDescription!,
                                        parameters = new
                                        {
                                            type = "object",
                                            properties = new { }
                                        }
                                    }
                                }
                            }
                        }));
            }

            if (skillId is not null)
            {
                WriteZip(
                    zip,
                    $"skills/{skillId}/SKILL.md",
                    JoinLines(
                        "---",
                        $"name: {skillId}",
                        $"description: {skillDescription}",
                        "---",
                        "",
                        "# Fixture Skill",
                        "SKILL_ONLY_SENTINEL_74",
                        "Use this installed guidance only after explicit model selection."));
            }
        }

        memory.Position = 0;
        string payloadHash;
        using (var read = new ZipArchive(
            memory,
            ZipArchiveMode.Read,
            leaveOpen: true))
        {
            payloadHash = PluginManager.ComputePayloadHash(read);
        }

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            pluginId,
            pluginId,
            "1.0.0",
            "2.0.0",
            "fixture.publisher",
            "sha256:" + payloadHash,
            toolName is null ? [] : [toolName],
            skillId is null ? [] : [skillId],
            providers,
            ["workspace.read"],
            NativeHelpers: [],
            LifecycleHooks: []);
        using (var update = new ZipArchive(
            memory,
            ZipArchiveMode.Update,
            leaveOpen: true))
        {
            WriteZip(
                update,
                "manifest.json",
                JsonSerializer.Serialize(manifest));
        }

        var bytes = memory.ToArray();
        File.WriteAllBytes(path, bytes);
        return new BuiltPluginPackage(
            path,
            manifest,
            global::H2AgentLab.SafeWorkspace.Hash(bytes).ToLowerInvariant(),
            toolName,
            skillId,
            skillDescription);
    }

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "fixture.publisher"
            },
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);

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

    private static string JoinLines(params string[] lines)
        => string.Join(((char)10).ToString(), lines);

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

    private sealed record BuiltPluginPackage(
        string Path,
        H2PluginManifest Manifest,
        string ArchiveSha256,
        string? ToolName,
        string? SkillId,
        string? SkillDescription);

    private sealed class InstalledPackageReadbackVerifier : IAgentRuntimeVerifier
    {
        private readonly PluginManager _plugins;
        private readonly ToolRegistry _registry;
        private readonly H2AgentLab.Skills.SkillCatalog _skills;
        private readonly Dictionary<string, (BuiltPluginPackage Package, Dictionary<string, string> Hashes)> _expected = new(StringComparer.Ordinal);
        private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
        public int ObservedInstallCount => _observed.Count;
        public InstalledPackageReadbackVerifier(PluginManager plugins, ToolRegistry registry,
            H2AgentLab.Skills.SkillCatalog skills, BuiltPluginPackage[] packages)
        {
            _plugins = plugins; _registry = registry; _skills = skills;
            foreach (var package in packages)
            {
                using var zip = ZipFile.OpenRead(package.Path);
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/')))
                {
                    using var stream = entry.Open();
                    hashes.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(stream)));
                }
                _expected.Add(package.Manifest.Id, (package, hashes));
            }
        }
        public bool IsInstalled(string id)
        {
            if (!_expected.TryGetValue(id, out var expected)) return false;
            var active = _plugins.GetActive(id);
            if (active is null || active.Value.Manifest.Version != expected.Package.Manifest.Version) return false;
            foreach (var entry in expected.Hashes)
            {
                var file = Path.Combine(active.Value.VersionRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != entry.Value) return false;
            }
            if (expected.Package.ToolName is { } tool && (!_registry.TryGet(tool, out var descriptor)
                || descriptor.Provenance?.ProviderId != "plugin." + id || descriptor.IsMutating)) return false;
            if (expected.Package.SkillId is { } skill && !_skills.Search("", 20)
                .Any(item => item.Identity.PluginId == id && item.Identity.SkillId == skill)) return false;
            return true;
        }
        public Task<VerificationReport?> VerifyAsync(AgentRuntimeVerificationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calls = context.Calls.Where(call => call.Name == CatalogRuntimeToolExecutor.InstallToolName).ToArray();
            if (calls.Length == 0) return Task.FromResult<VerificationReport?>(null);
            foreach (var call in calls) _observed.Add(call.Arguments.GetProperty("plugin_id").GetString()!);
            var passed = _observed.All(IsInstalled);
            const string criterion = "mb74.activation-readback";
            string[] evidence = ["fixture:installed-package-bytes-and-registry"];
            return Task.FromResult<VerificationReport?>(new VerificationReport("mb74-package-readback",
                [new VerificationCriterionResult(criterion,
                    passed ? VerificationCriterionStatus.Passed : VerificationCriterionStatus.Failed,
                    evidence, passed ? null : new VerificationFailure(criterion,
                        "Active package payload, version or registry/skill contribution differs from selected fixture.", evidence))]));
        }
    }

    private sealed class FixturePluginResolver : IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
            => new DelegatingToolExecutor(
                "mb74-plugin-fixture",
                (call, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        JsonSerializer.Serialize(new
                        {
                            ok = true,
                            pluginId = manifest.Id,
                            version = manifest.Version,
                            tool = tool.Name,
                            sentinel = "PLUGIN_TOOL_EXECUTED_74"
                        }));
                });
    }

    private sealed class InstallAndContinueTransport : IAgentTransport
    {
        private int _step;
        public int StartCalls { get; private set; }
        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            var names = request.Tools
                .Select(x => x.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals([DeferredToolDiscovery.SearchToolName]))
                throw new InvalidOperationException(
                    "MB-74 initial callable surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "discover-runtime-tools",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "catalog search plugin install list skills read guidance",
                    max_results = 8
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb74-0",
                "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _step++;
            await Task.Yield();

            switch (_step)
            {
                case 1:
                    RequireNewTools(
                        request,
                        CatalogRuntimeToolExecutor.SearchToolName,
                        CatalogRuntimeToolExecutor.InstallToolName,
                        SkillRuntimeToolExecutor.SearchToolName,
                        SkillRuntimeToolExecutor.ReadToolName);
                    yield return Tool(
                        "catalog-tool-only",
                        CatalogRuntimeToolExecutor.SearchToolName,
                        new
                        {
                            query = "transform document text"
                        });
                    yield return Done();
                    yield break;

                case 2:
                    RequireResultContains(
                        request,
                        "fixture.toolonly");
                    yield return Tool(
                        "install-tool-only",
                        CatalogRuntimeToolExecutor.InstallToolName,
                        new
                        {
                            plugin_id = "fixture.toolonly",
                            version = "1.0.0"
                        });
                    yield return Done();
                    yield break;

                case 3:
                    RequireResultContains(
                        request,
                        "fixture.transform_text");
                    RequireResultNotContains(
                        request,
                        "spreadsheet-audit",
                        "fixture.native");
                    yield return Tool(
                        "discover-tool-only",
                        DeferredToolDiscovery.SearchToolName,
                        new
                        {
                            query = "transform document text",
                            max_results = 4
                        });
                    yield return Done();
                    yield break;

                case 4:
                    RequireNewTools(
                        request,
                        "fixture.transform_text");
                    yield return Tool(
                        "use-tool-only",
                        "fixture.transform_text",
                        new { });
                    yield return Done();
                    yield break;

                case 5:
                    RequireResultContains(
                        request,
                        "PLUGIN_TOOL_EXECUTED_74",
                        "fixture.toolonly");
                    yield return Tool(
                        "catalog-skill-only",
                        CatalogRuntimeToolExecutor.SearchToolName,
                        new
                        {
                            query = "spreadsheet reconciliation audit guidance"
                        });
                    yield return Done();
                    yield break;

                case 6:
                    RequireResultContains(
                        request,
                        "fixture.skillonly",
                        "spreadsheet-audit");
                    yield return Tool(
                        "install-skill-only",
                        CatalogRuntimeToolExecutor.InstallToolName,
                        new
                        {
                            plugin_id = "fixture.skillonly",
                            version = "1.0.0"
                        });
                    yield return Done();
                    yield break;

                case 7:
                    RequireResultContains(
                        request,
                        "fixture.skillonly",
                        "spreadsheet-audit");
                    RequireResultNotContains(
                        request,
                        "SKILL_ONLY_SENTINEL_74");
                    yield return Tool(
                        "list-new-skill",
                        SkillRuntimeToolExecutor.SearchToolName,
                        new
                        {
                            query = "spreadsheet reconciliation audit"
                        });
                    yield return Done();
                    yield break;

                case 8:
                    RequireResultContains(
                        request,
                        "spreadsheet-audit",
                        "fixture.skillonly");
                    yield return Tool(
                        "read-new-skill",
                        SkillRuntimeToolExecutor.ReadToolName,
                        new
                        {
                            name = "spreadsheet-audit",
                            path = "SKILL.md"
                        });
                    yield return Done();
                    yield break;

                case 9:
                    RequireResultContains(
                        request,
                        "SKILL_ONLY_SENTINEL_74");
                    yield return Tool(
                        "catalog-provider-tool",
                        CatalogRuntimeToolExecutor.SearchToolName,
                        new
                        {
                            query = "native diagnostics provider"
                        });
                    yield return Done();
                    yield break;

                case 10:
                    RequireResultContains(
                        request,
                        "fixture.provider",
                        "fixture.native");
                    yield return Tool(
                        "install-provider-tool",
                        CatalogRuntimeToolExecutor.InstallToolName,
                        new
                        {
                            plugin_id = "fixture.provider",
                            version = "1.0.0"
                        });
                    yield return Done();
                    yield break;

                case 11:
                    RequireResultContains(
                        request,
                        "fixture.native_diagnostics",
                        "fixture.native");
                    yield return Tool(
                        "discover-provider-tool",
                        DeferredToolDiscovery.SearchToolName,
                        new
                        {
                            query = "native diagnostics",
                            max_results = 4
                        });
                    yield return Done();
                    yield break;

                case 12:
                    RequireNewTools(
                        request,
                        "fixture.native_diagnostics");
                    yield return Tool(
                        "use-provider-tool",
                        "fixture.native_diagnostics",
                        new { });
                    yield return Done();
                    yield break;

                case 13:
                    RequireResultContains(
                        request,
                        "PLUGIN_TOOL_EXECUTED_74",
                        "fixture.provider",
                        "fixture.native_diagnostics");
                    yield return AgentTransportEvent.TextDeltaEvent(
                        "mb74-runtime-ok");
                    yield return AgentTransportEvent.Complete(
                        "mb74-final",
                        "stop");
                    yield break;

                default:
                    throw new InvalidOperationException(
                        "Unexpected MB-74 continuation step " + _step + ".");
            }
        }

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private static AgentTransportEvent Tool(
            string id,
            string name,
            object arguments)
            => AgentTransportEvent.Tool(new(
                id,
                name,
                JsonSerializer.Serialize(arguments)));

        private static AgentTransportEvent Done()
            => AgentTransportEvent.Complete(
                "mb74-step",
                "tool_calls");

        private static void RequireNewTools(
            AgentTransportContinuationRequest request,
            params string[] expected)
        {
            var names = (request.NewlyLoadedTools ?? [])
                .Select(x => x.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var name in expected)
            {
                if (!names.Contains(name))
                    throw new InvalidOperationException(
                        "Expected newly loaded tool schema '" + name + "'.");
            }
        }

        private static void RequireResultContains(
            AgentTransportContinuationRequest request,
            params string[] expected)
        {
            var text = string.Join(
                "\n",
                request.ToolResults.Select(x => x.Content));
            foreach (var value in expected)
            {
                if (!text.Contains(value, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Expected tool result to contain '" + value + "'.");
            }
        }

        private static void RequireResultNotContains(
            AgentTransportContinuationRequest request,
            params string[] forbidden)
        {
            var text = string.Join(
                "\n",
                request.ToolResults.Select(x => x.Content));
            foreach (var value in forbidden)
            {
                if (text.Contains(value, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Tool result unexpectedly contained '" + value + "'.");
            }
        }
    }
}
