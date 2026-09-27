using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Catalog;
using H2AgentLab.Context;
using H2AgentLab.Plugins;
using H2AgentLab.Prompting;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Capabilities;

public static class MbEndToEndInstallContinueTests
{
    private const string PluginId = "fixture.verified-state";
    private const string PluginVersion = "1.0.0";
    private const string ToolName = "fixture.set_value";
    private const string SkillId = "fixture-state";
    private const string TargetValue = "verified-value";

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-82 test directory.");
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
                lines.Add(
                    "FAIL " + name + ": "
                    + ex.GetType().Name + ": "
                    + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        await Test(
            "MB-82 one AgentRuntime request discovers installs refreshes uses and verifies a missing extension without user restatement",
            async () =>
            {
                var sourceRoot = Path.Combine(root, "catalog");
                var packageRoot = Path.Combine(sourceRoot, "packages");
                var stagingRoot = Path.Combine(root, "staging");
                var stateRoot = Path.Combine(root, "state");
                Directory.CreateDirectory(packageRoot);
                Directory.CreateDirectory(stagingRoot);
                Directory.CreateDirectory(stateRoot);

                var package = BuildPlugin(packageRoot);
                WriteCatalog(sourceRoot, package);

                var registry = new ToolRegistry();
                var state = new FixtureState();
                var pluginResolver = new FixturePluginResolver(state);
                var pluginManager = new PluginManager(
                    stateRoot,
                    registry,
                    pluginResolver);

                var skills = new H2AgentLab.Skills.SkillCatalog();
                skills.Register(new PluginSkillSource(pluginManager));

                var installed = new InstalledCapabilityIndex();
                installed.Bind(
                    registry,
                    skills,
                    static () =>
                        Array.Empty<ProviderProvenance>());

                var catalogs = new CatalogSourceManager();
                catalogs.Register(new LocalFolderCatalogSource(
                    sourceRoot,
                    "mb82-local",
                    PluginTrustState.LocalDeveloper));

                var resolver = new CapabilityResolver(
                    installed,
                    new AvailableCapabilityIndex(),
                    catalogs,
                    new PredicateCapabilityInstallPolicy(
                        x => x.TrustState
                            == PluginTrustState.LocalDeveloper));

                var countingRetriever = new CountingPackageRetriever(
                    new LocalPackageRetriever(
                        stagingRoot,
                        [sourceRoot]));

                var catalogExecutor = new CatalogRuntimeToolExecutor(
                    resolver,
                    countingRetriever,
                    pluginManager,
                    DeveloperPolicy(),
                    userApproved: true);
                CatalogRuntimeTools.Register(
                    registry,
                    catalogExecutor);
                RegisterSkillTools(
                    registry,
                    new SkillRuntimeToolExecutor(skills));

                var transport =
                    new EndToEndTransport();
                var verifier =
                    new FixtureStateVerifier(state, pluginManager, registry, package.Path);

                await using var runtime = new AgentRuntime(
                    transport,
                    new AgentContextManager(),
                    registry,
                    verifier: verifier,
                    permissionPolicy:
                        new ScopedAgentRuntimePermissionPolicy(
                            _ => true));

                var result = await runtime.RunAsync(
                    Request(),
                    CancellationToken.None);

                Check(result.FinalText
                        == "mb82-end-to-end-ok",
                    "Runtime did not finish the original request after install/use/verification.");
                Check(transport.StartCalls == 1,
                    "User/model task restarted instead of continuing the original request.");
                Check(transport.SawLocalToolMiss
                        && transport.SawLocalSkillMiss,
                    "Agent did not prove installed local tools/skills were insufficient first.");
                Check(transport.SawCatalogCandidate,
                    "Model-side flow did not observe the external catalog candidate.");
                Check(transport.SawInstallResult
                        && countingRetriever.RetrieveCount == 1
                        && countingRetriever.LastResult is not null
                        && File.Exists(
                            countingRetriever.LastResult.StagedPath),
                    "Selected package was not retrieved exactly once through IPackageRetriever.");
                Check(pluginManager.GetActive(PluginId) is
                        { Manifest.Version: PluginVersion },
                    "PluginManager did not verify/install/activate the selected package.");
                Check(transport.SawSkillRefresh
                        && transport.SawSkillBody
                        && transport.SawToolRefresh,
                    "Safe same-task skill/tool refresh did not expose the installed extension.");
                Check(pluginResolver.ResolveCount == 1
                        && pluginResolver.ExecuteCount == 1
                        && state.Value == TargetValue
                        && state.Version == 1,
                    "Newly installed tool was not executed exactly once against host state.");
                Check(verifier.VerificationCount == 1
                        && verifier.InstallVerifications == 1
                        && result.VerificationHistory.Count == 2
                        && result.VerificationHistory.All(r => r.Passed)
                        && result.VerificationHistory[1].VerifierId
                            == FixtureStateVerifier.VerifierId
                        && result.VerificationHistory[1].Covers(
                            [FixtureStateVerifier.CriterionId]),
                    "Host verifier did not PASS the installed tool mutation.");
                Check(result.LoadedToolSchemas.Contains(
                        ToolName,
                        StringComparer.Ordinal),
                    "Installed tool schema was not loaded into the same running task.");
            });

        lines.Add(
            $"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-end-to-end-install-continue-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(
            string.Join(
                Environment.NewLine,
                lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request()
    {
        const string goal =
            "Set the fixture value atomically to verified-value. "
            + "If the needed capability is missing, discover and install an approved extension, "
            + "continue this same request, use the capability, and verify the mutation.";

        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            goal,
            "fixture:mb82-state",
            null,
            ["set fixture value to verified-value"],
            ["do not restart the user request"],
            ["finish only after host verification passes"],
            [
                new AgentAcceptanceCriterion(
                    FixtureStateVerifier.CriterionId,
                    "Fixture state equals verified-value after the installed tool is used."),
                new AgentAcceptanceCriterion("mb82.install-readback", "Selected installed package hashes and registry match.")
            ],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(
                requireVerification: true,
                requiredVerifierIds:
                [
                    FixtureStateVerifier.VerifierId
                ]));

        return new AgentRuntimeRequest(
            contract,
            goal,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "Host policy, package verification and verifier results are authoritative.",
                "Try installed capabilities first. If missing, use catalog_search and plugin_install, then continue the same request with refreshed tools/skills.",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState:
                    "fixture value is unset; external catalog is configured"),
            PromptCacheKey: "mb82",
            MaxToolRounds: 16,
            MaxRepairRounds: 2);
    }

    private static void RegisterSkillTools(
        ToolRegistry registry,
        SkillRuntimeToolExecutor executor)
    {
        var ns = new ToolNamespace(
            "skills",
            "Search installed skill metadata and read one explicitly selected skill or resource.");

        registry.Register(new ToolDescriptor(
            SkillRuntimeToolExecutor.SearchToolName,
            ns,
            "List and search installed skill guidance metadata.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema:
                JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name =
                            SkillRuntimeToolExecutor.SearchToolName,
                        description =
                            "List and search installed skill guidance metadata.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                query = new
                                {
                                    type = "string"
                                }
                            },
                            additionalProperties = false
                        }
                    }
                }),
            executor: executor));

        registry.Register(new ToolDescriptor(
            SkillRuntimeToolExecutor.ReadToolName,
            ns,
            "Read one installed skill SKILL.md or one explicitly selected resource.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v2",
            callableSchema:
                JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name =
                            SkillRuntimeToolExecutor.ReadToolName,
                        description =
                            "Read one installed skill or explicit resource.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new
                                {
                                    type = "string"
                                },
                                path = new
                                {
                                    type = "string"
                                }
                            },
                            required =
                                new[] { "name", "path" },
                            additionalProperties = false
                        }
                    }
                }),
            executor: executor));
    }

    private static BuiltPluginPackage BuildPlugin(
        string packageRoot)
    {
        var path = Path.Combine(
            packageRoot,
            "verified-state.h2pkg");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(
            memory,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteZip(
                zip,
                "tools.json",
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            name = ToolName,
                            @namespace = "fixture",
                            description =
                                "Set the fixture value atomically and expose state for deterministic verification.",
                            access = "Mutating",
                            risk = "Medium",
                            supportsParallel = false,
                            schemaVersion = "v1",
                            toolVersion = "1.0.0",
                            resourceScope =
                                "fixture:mb82-state",
                            serializationKey =
                                "mb82-state",
                            schema = new
                            {
                                type = "function",
                                function = new
                                {
                                    name = ToolName,
                                    description =
                                        "Set the fixture value atomically.",
                                    parameters = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            value = new
                                            {
                                                type = "string"
                                            }
                                        },
                                        required =
                                            new[] { "value" },
                                        additionalProperties = false
                                    }
                                }
                            }
                        }
                    }));

            WriteZip(
                zip,
                $"skills/{SkillId}/SKILL.md",
                string.Join(
                    "\n",
                    "---",
                    $"name: {SkillId}",
                    "description: Guidance for setting the fixture value atomically and verifying the resulting host state.",
                    "---",
                    "",
                    "# Fixture State",
                    "MB82_SKILL_SELECTED",
                    "Use fixture.set_value only for the accepted fixture state mutation, then rely on host verification."));
        }

        memory.Position = 0;
        string payloadHash;
        using (var read = new ZipArchive(
            memory,
            ZipArchiveMode.Read,
            leaveOpen: true))
        {
            payloadHash =
                PluginManager.ComputePayloadHash(read);
        }

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            PluginId,
            "MB-82 Verified State",
            PluginVersion,
            "2.0.0",
            "fixture.publisher",
            "sha256:" + payloadHash,
            [ToolName],
            [SkillId],
            [],
            ["fixture.write"],
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
            global::H2AgentLab.SafeWorkspace
                .Hash(bytes)
                .ToLowerInvariant());
    }

    private static void WriteCatalog(
        string sourceRoot,
        BuiltPluginPackage package)
    {
        var metadata =
            new LocalCatalogPackageMetadata(
                PluginId,
                PluginVersion,
                "fixture.publisher",
                "2.0.0",
                "sha256:" + package.ArchiveSha256,
                "packages/verified-state.h2pkg",
                ToolSummaries:
                [
                    ToolName + ": Set the fixture value atomically and expose state for deterministic verification."
                ],
                Skills:
                [
                    new AvailableSkillMetadata(
                        SkillId,
                        SkillId,
                        "Guidance for setting the fixture value atomically and verifying the resulting host state.")
                ],
                Providers: [],
                Permissions:
                [
                    "fixture.write"
                ]);

        File.WriteAllText(
            Path.Combine(
                sourceRoot,
                "catalog.json"),
            JsonSerializer.Serialize(
                new[] { metadata },
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(
                StringComparer.Ordinal)
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

    private sealed record BuiltPluginPackage(
        string Path,
        string ArchiveSha256);

    private sealed class FixtureState
    {
        public string Value { get; set; } = "";
        public int Version { get; set; }
    }

    private sealed class FixturePluginResolver
        : IPluginToolExecutorResolver
    {
        private readonly FixtureState _state;

        public FixturePluginResolver(
            FixtureState state)
        {
            _state = state;
        }

        public int ResolveCount { get; private set; }
        public int ExecuteCount { get; private set; }

        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
        {
            ResolveCount++;
            if (manifest.Id != PluginId
                || tool.Name != ToolName)
                throw new InvalidOperationException(
                    "Unexpected MB-82 plugin tool.");

            return new DelegatingToolExecutor(
                "mb82-plugin-tool",
                (call, cancellationToken) =>
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    var value = call.Arguments
                        .GetProperty("value")
                        .GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException(
                            "MB-82 fixture value is required.");

                    ExecuteCount++;
                    _state.Value = value;
                    _state.Version++;
                    return ValueTask.FromResult(
                        JsonSerializer.Serialize(new
                        {
                            ok = true,
                            value = _state.Value,
                            stateVersion =
                                _state.Version,
                            sentinel =
                                "MB82_TOOL_USED"
                        }));
                });
        }
    }

    private sealed class CountingPackageRetriever
        : IPackageRetriever
    {
        private readonly IPackageRetriever _inner;

        public CountingPackageRetriever(
            IPackageRetriever inner)
        {
            _inner = inner;
        }

        public int RetrieveCount { get; private set; }
        public PackageRetrievalResult? LastResult
        {
            get;
            private set;
        }

        public async Task<PackageRetrievalResult> RetrieveAsync(
            PackageRetrievalRequest request,
            CancellationToken cancellationToken)
        {
            RetrieveCount++;
            LastResult = await _inner.RetrieveAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return LastResult;
        }
    }

    private sealed class FixtureStateVerifier
        : IAgentRuntimeVerifier
    {
        public const string VerifierId =
            "mb82-state-verifier";
        public const string CriterionId =
            "mb82.state-applied";

        private readonly FixtureState _state;

        private readonly PluginManager _plugins;
        private readonly ToolRegistry _registry;
        private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
        public int InstallVerifications { get; private set; }
        public FixtureStateVerifier(FixtureState state, PluginManager plugins, ToolRegistry registry, string package)
        {
            _state = state; _plugins = plugins; _registry = registry;
            using var zip = ZipFile.OpenRead(package);
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var stream = entry.Open();
                _hashes.Add(entry.FullName, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
            }
        }

        public int VerificationCount
        {
            get;
            private set;
        }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var install = context.Calls.SingleOrDefault(x => x.Name == CatalogRuntimeToolExecutor.InstallToolName);
            if (install is not null)
            {
                InstallVerifications++;
                var active = _plugins.GetActive(PluginId);
                var ok = active is not null && active.Value.Manifest.Version == PluginVersion
                    && _registry.TryGet(ToolName, out _)
                    && _hashes.All(h => {
                        var path = Path.Combine(active.Value.VersionRoot, h.Key.Replace('/', Path.DirectorySeparatorChar));
                        return File.Exists(path) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) == h.Value;
                    });
                const string criterionId = "mb82.install-readback";
                var status = ok ? VerificationCriterionStatus.Passed : VerificationCriterionStatus.Failed;
                string[] refs = ["fixture:mb82:activated-package-hashes"];
                return Task.FromResult<VerificationReport?>(new VerificationReport(VerifierId,
                    [new(criterionId, status, refs, ok ? null : new(criterionId, "Installed package or registry differs."))])
                { CallCoverage = [new(install.Invocation!.InvocationId, criterionId, "plugin:" + PluginId,
                    "version:" + PluginVersion, status, refs)] });
            }

            var call = context.Calls.SingleOrDefault(
                x => x.Name == ToolName);
            if (call is null)
                return Task.FromResult<
                    VerificationReport?>(null);

            VerificationCount++;
            context.RawToolOutputs.TryGetValue(
                call.Id,
                out var raw);
            var outputOk = false;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc =
                    JsonDocument.Parse(raw);
                var root = doc.RootElement;
                outputOk =
                    root.TryGetProperty(
                        "value",
                        out var value)
                    && value.ValueKind
                        == JsonValueKind.String
                    && value.GetString()
                        == TargetValue
                    && root.TryGetProperty(
                        "stateVersion",
                        out var version)
                    && version.TryGetInt32(
                        out var number)
                    && number == 1
                    && root.TryGetProperty(
                        "sentinel",
                        out var sentinel)
                    && sentinel.GetString()
                        == "MB82_TOOL_USED";
            }

            var passed =
                _state.Value == TargetValue
                && _state.Version == 1
                && outputOk;
            var evidence = new[]
            {
                "mb82-state-version-"
                    + _state.Version
            };
            var criterion = passed
                ? new VerificationCriterionResult(
                    CriterionId,
                    VerificationCriterionStatus.Passed,
                    evidence)
                : new VerificationCriterionResult(
                    CriterionId,
                    VerificationCriterionStatus.Failed,
                    evidence,
                    new VerificationFailure(
                        CriterionId,
                        "Fixture state did not match the verified target.",
                        evidence));

            return Task.FromResult<
                VerificationReport?>(
                new VerificationReport(
                    VerifierId,
                    [criterion],
                    evidence));
        }
    }

    private sealed class EndToEndTransport
        : IAgentTransport
    {
        private int _step;

        public int StartCalls { get; private set; }
        public bool SawLocalToolMiss
        {
            get;
            private set;
        }
        public bool SawLocalSkillMiss
        {
            get;
            private set;
        }
        public bool SawCatalogCandidate
        {
            get;
            private set;
        }
        public bool SawInstallResult
        {
            get;
            private set;
        }
        public bool SawSkillRefresh
        {
            get;
            private set;
        }
        public bool SawSkillBody
        {
            get;
            private set;
        }
        public bool SawToolRefresh
        {
            get;
            private set;
        }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<
            AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken =
                default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            StartCalls++;

            var names = request.Tools
                .Select(x => x.Name)
                .ToHashSet(
                    StringComparer.Ordinal);
            if (!names.SetEquals(
                    [
                        DeferredToolDiscovery
                            .SearchToolName
                    ]))
                throw new InvalidOperationException(
                    "MB-82 initial callable surface was not tool_search-only.");

            yield return Tool(
                "local-tool-check",
                DeferredToolDiscovery
                    .SearchToolName,
                new
                {
                    query =
                        "set fixture value atomically",
                    max_results = 8
                });
            await Task.Yield();
            yield return Done("mb82-start");
        }

        public async IAsyncEnumerable<
            AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation]
            CancellationToken cancellationToken =
                default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            _step++;
            await Task.Yield();

            switch (_step)
            {
                case 1:
                    SawLocalToolMiss =
                        ToolSearchSelectedCount(
                            request) == 0
                        && (request.NewlyLoadedTools
                            ?? []).Count == 0;
                    if (!SawLocalToolMiss)
                        throw new InvalidOperationException(
                            "Target capability unexpectedly existed in local ToolRegistry.");

                    yield return Tool(
                        "load-discovery-tools",
                        DeferredToolDiscovery
                            .SearchToolName,
                        new
                        {
                            query =
                                "external extension catalog install package skill guidance",
                            max_results = 8
                        });
                    yield return Done("mb82-load");
                    yield break;

                case 2:
                    RequireNewTools(
                        request,
                        CatalogRuntimeToolExecutor
                            .SearchToolName,
                        CatalogRuntimeToolExecutor
                            .InstallToolName,
                        SkillRuntimeToolExecutor
                            .SearchToolName,
                        SkillRuntimeToolExecutor
                            .ReadToolName);
                    yield return Tool(
                        "local-skill-check",
                        SkillRuntimeToolExecutor
                            .SearchToolName,
                        new
                        {
                            query =
                                "set fixture value atomically"
                        });
                    yield return Done("mb82-skill-miss");
                    yield break;

                case 3:
                    SawLocalSkillMiss =
                        SkillResultCount(
                            request) == 0;
                    if (!SawLocalSkillMiss)
                        throw new InvalidOperationException(
                            "Target guidance unexpectedly existed in local SkillCatalog.");

                    yield return Tool(
                        "catalog-search",
                        CatalogRuntimeToolExecutor
                            .SearchToolName,
                        new
                        {
                            query =
                                "set fixture value atomically"
                        });
                    yield return Done("mb82-catalog");
                    yield break;

                case 4:
                    RequireResultContains(
                        request,
                        PluginId,
                        ToolName,
                        SkillId);
                    SawCatalogCandidate = true;
                    yield return Tool(
                        "plugin-install",
                        CatalogRuntimeToolExecutor
                            .InstallToolName,
                        new
                        {
                            plugin_id = PluginId,
                            version = PluginVersion
                        });
                    yield return Done("mb82-install");
                    yield break;

                case 5:
                    RequireResultContains(
                        request,
                        PluginId,
                        ToolName,
                        SkillId);
                    SawInstallResult = true;
                    yield return Tool(
                        "skill-refresh",
                        SkillRuntimeToolExecutor
                            .SearchToolName,
                        new
                        {
                            query =
                                "fixture value atomically verification"
                        });
                    yield return Done("mb82-skill-refresh");
                    yield break;

                case 6:
                    RequireResultContains(
                        request,
                        SkillId,
                        PluginId);
                    SawSkillRefresh = true;
                    yield return Tool(
                        "skill-read",
                        SkillRuntimeToolExecutor
                            .ReadToolName,
                        new
                        {
                            name = SkillId,
                            path = "SKILL.md"
                        });
                    yield return Done("mb82-skill-read");
                    yield break;

                case 7:
                    RequireResultContains(
                        request,
                        "MB82_SKILL_SELECTED",
                        ToolName);
                    SawSkillBody = true;
                    yield return Tool(
                        "tool-refresh",
                        DeferredToolDiscovery
                            .SearchToolName,
                        new
                        {
                            query =
                                "set fixture value atomically",
                            max_results = 4
                        });
                    yield return Done("mb82-tool-refresh");
                    yield break;

                case 8:
                    RequireNewTools(
                        request,
                        ToolName);
                    SawToolRefresh = true;
                    yield return Tool(
                        "apply-state",
                        ToolName,
                        new
                        {
                            value = TargetValue
                        });
                    yield return Done("mb82-apply");
                    yield break;

                case 9:
                    RequireResultContains(
                        request,
                        "MB82_TOOL_USED",
                        TargetValue);
                    yield return
                        AgentTransportEvent
                            .TextDeltaEvent(
                                "mb82-end-to-end-ok");
                    yield return
                        AgentTransportEvent
                            .Complete(
                                "mb82-final",
                                "stop");
                    yield break;

                default:
                    throw new InvalidOperationException(
                        "Unexpected MB-82 continuation step "
                        + _step
                        + ".");
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
            => AgentTransportEvent.Tool(
                new AgentTransportToolCall(
                    id,
                    name,
                    JsonSerializer.Serialize(
                        arguments)));

        private static AgentTransportEvent Done(
            string id)
            => AgentTransportEvent.Complete(
                id,
                "tool_calls");

        private static int ToolSearchSelectedCount(
            AgentTransportContinuationRequest request)
        {
            var result =
                request.ToolResults.Single();
            using var doc =
                JsonDocument.Parse(
                    result.Content);
            return doc.RootElement
                .GetProperty("selected")
                .GetArrayLength();
        }

        private static int SkillResultCount(
            AgentTransportContinuationRequest request)
        {
            var result =
                request.ToolResults.Single();
            using var doc =
                JsonDocument.Parse(
                    result.Content);
            return doc.RootElement
                .GetProperty("skills")
                .GetArrayLength();
        }

        private static void RequireNewTools(
            AgentTransportContinuationRequest request,
            params string[] expected)
        {
            var names =
                (request.NewlyLoadedTools ?? [])
                .Select(x => x.Name)
                .ToHashSet(
                    StringComparer.Ordinal);
            foreach (var name in expected)
            {
                if (!names.Contains(name))
                    throw new InvalidOperationException(
                        "Expected newly loaded tool schema '"
                        + name
                        + "'.");
            }
        }

        private static void RequireResultContains(
            AgentTransportContinuationRequest request,
            params string[] expected)
        {
            var content = string.Join(
                "\n",
                request.ToolResults
                    .Select(x => x.Content));
            foreach (var value in expected)
            {
                if (!content.Contains(
                        value,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Expected tool result to contain '"
                        + value
                        + "'.");
            }
        }
    }
}
