using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Extensions;
using H2AgentLab.FirstPartyExtensions.Calculator;
using H2AgentLab.Prompting;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Acceptance;

public static class MbExtensionBusGateTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-103 test directory.");
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

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-103 new extension registers tool and skill without AgentRuntime edits", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);

            extensions.Register(new FixtureExtension());

            Check(tools.TryGet(FixtureExtension.ToolName, out var descriptor)
                  && descriptor.Namespace.Name == "mb103"
                  && descriptor.Provenance?.ProviderId == "mb103.extension",
                "Fixture extension tool was not registered through the generic extension bus.");
            Check(skills.Search("extension bus fixture", 10)
                    .Any(x => x.Name == FixtureSkillSource.SkillName
                        && x.Identity.SourceId == FixtureExtension.ExtensionId),
                "Fixture extension skill was not registered through the canonical SkillCatalog.");
            Check(extensions.Extensions.Single().Metadata.ExtensionId
                    == FixtureExtension.ExtensionId,
                "Extension registry did not retain the active fixture extension.");

            var repo = FindRepoRoot();
            var runtimeSource = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Runtime",
                "AgentRuntime.cs"));
            Check(!runtimeSource.Contains(FixtureExtension.ToolName, StringComparison.Ordinal)
                  && !runtimeSource.Contains(nameof(FixtureExtension), StringComparison.Ordinal),
                "AgentRuntime was edited with MB-103 fixture-specific knowledge.");
        });

        await Test("MB-103 model discovers and uses extension tool through ordinary runtime", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);
            extensions.Register(new FixtureExtension());

            var transport = new FixtureRuntimeTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                tools);

            var result = await runtime.RunAsync(
                Request("Use the installed MB-103 echo extension."),
                CancellationToken.None);

            Check(result.FinalText == "mb103-extension-ok",
                "AgentRuntime did not finish after using the extension tool.");
            Check(transport.ToolResultObserved,
                "Model continuation did not observe the extension tool result.");
            Check(result.LoadedToolSchemas.Contains(
                    FixtureExtension.ToolName,
                    StringComparer.Ordinal),
                "Deferred discovery did not load the extension schema.");
        });

        await Test("MB-103 disable removes tool skill and model discovery cleanly", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);
            extensions.Register(new FixtureExtension());

            var versionBefore = tools.Version;
            var disabled = await extensions.DisableAsync(
                FixtureExtension.ExtensionId,
                CancellationToken.None);

            Check(disabled,
                "Active extension was not disabled.");
            Check(!tools.TryGet(FixtureExtension.ToolName, out _)
                  && tools.Version > versionBefore,
                "Disabled extension tool remained in ToolRegistry or registry version did not advance.");
            Check(skills.Search("extension bus fixture", 10).Count == 0
                  && !skills.SourceIds.Contains(
                      FixtureExtension.ExtensionId,
                      StringComparer.Ordinal),
                "Disabled extension skill source remained in SkillCatalog.");
            Check(extensions.Extensions.Count == 0,
                "Disabled extension remained in the active extension registry.");
            Check(new ToolSearchIndex(tools)
                    .Search(FixtureExtension.ToolName, 5)
                    .Count == 0,
                "Disabled extension remained discoverable through ToolSearchIndex.");

            var transport = new DisabledRuntimeTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                tools);
            var result = await runtime.RunAsync(
                Request("Try to discover the disabled MB-103 extension."),
                CancellationToken.None);
            Check(result.FinalText == "mb103-disabled-ok"
                  && transport.NoExtensionSchemaObserved,
                "Model still discovered a disabled extension schema.");

            Check(!await extensions.DisableAsync(
                    FixtureExtension.ExtensionId,
                    CancellationToken.None),
                "Disabling an already removed extension did not report false.");
        });

        await Test("MB-103 unregister cleans verifier provider and direct contributions", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);
            var calculator = new CalculatorExtension();
            extensions.Register(calculator);

            _ = await providers.DiscoverNamespacesAsync(CancellationToken.None);
            Check(calculator.Provider.ConnectCount == 1,
                "Fixture provider was not connected before lifecycle removal.");

            var removed = await extensions.UnregisterAsync(
                "calculator-extension",
                CancellationToken.None);
            Check(removed,
                "Registered extension was not unregistered.");
            Check(!tools.TryGet("calculator.add", out _),
                "Unregistered extension tool remained in ToolRegistry.");
            Check(skills.Search("deterministic addition", 10).Count == 0,
                "Unregistered extension skill remained in SkillCatalog.");
            Check(!verifiers.VerifierIds.Contains(
                    CalculatorArtifactVerifier.Id,
                    StringComparer.Ordinal),
                "Unregistered extension verifier remained registered.");
            Check(!providers.Providers.Any(x =>
                    x.ProviderId == "calculator.provider"),
                "Unregistered extension provider remained registered.");
            Check(calculator.Provider.DisconnectCount == 1,
                "Provider lifecycle was not disconnected during extension unregister.");
            Check(extensions.Extensions.Count == 0,
                "Unregistered extension remained active.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-extension-bus-gate-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string goal)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            goal,
            "extension:mb103",
            null,
            null,
            ["do not mutate"],
            ["use only currently registered capabilities"],
            [],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

        return new AgentRuntimeRequest(
            contract,
            contract.UserGoal,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-103 extension-bus acceptance"),
            PromptCacheKey: "mb103",
            MaxToolRounds: 4);
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

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }

    private sealed class FixtureExtension : IAgentExtension
    {
        public const string ExtensionId = "mb103-extension";
        public const string ToolName = "mb103.echo";

        public AgentExtensionMetadata Metadata { get; } = new(
            ExtensionId,
            "1.0.0",
            "MB-103 Fixture Extension",
            "Test-only extension proving generic runtime discovery and lifecycle removal.");

        public void Register(AgentExtensionRegistration registration)
        {
            registration.RegisterTool(BuildTool());
            registration.RegisterSkillSource(new FixtureSkillSource());
        }

        private static ToolDescriptor BuildTool()
        {
            const string description =
                "Echo one text value deterministically for the MB-103 extension bus fixture.";
            return new ToolDescriptor(
                ToolName,
                new ToolNamespace(
                    "mb103",
                    "MB-103 extension bus fixture tools."),
                description,
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                supportsParallel: true,
                schemaVersion: "1.0.0",
                callableSchema: JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name = ToolName,
                        description,
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                value = new { type = "string" }
                            },
                            required = new[] { "value" },
                            additionalProperties = false
                        }
                    }
                }),
                executor: new DelegatingToolExecutor(
                    "mb103-extension",
                    static (call, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (call.Arguments.ValueKind != JsonValueKind.Object
                            || !call.Arguments.TryGetProperty("value", out var node)
                            || node.ValueKind != JsonValueKind.String)
                            throw new ArgumentException(
                                "MB-103 echo requires string 'value'.");
                        return ValueTask.FromResult(
                            JsonSerializer.Serialize(new
                            {
                                echo = node.GetString() ?? ""
                            }));
                    }),
                provenance: new ToolProvenance(
                    "mb103.extension",
                    "1.0.0",
                    "inproc",
                    "1.0.0"));
        }
    }

    private sealed class FixtureSkillSource : ISkillSource, IInstalledSkillMetadataSource
    {
        public const string SkillName = "mb103-extension-basics";
        private const string Content =
            "# MB-103 Extension\nUse mb103.echo only when this extension is currently installed.";
        private static readonly string Hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Content)))
            .ToLowerInvariant();
        private static readonly SkillSummary Summary = new(
            new SkillIdentity(
                SkillSourceKind.BuiltIn,
                FixtureExtension.ExtensionId,
                null,
                null,
                SkillName,
                Hash),
            SkillName,
            "Extension bus fixture guidance for deterministic echo capability.",
            "installed",
            "test-extension");

        public string SourceId => FixtureExtension.ExtensionId;
        public SkillSourceKind SourceKind => SkillSourceKind.BuiltIn;

        public IReadOnlyList<SkillSummary> Search(
            string query,
            int maxResults = 20)
            => BuiltInSkillSource.Rank(
                SnapshotMetadata(),
                query,
                maxResults);

        public IReadOnlyList<SkillSummary> SnapshotMetadata()
            => [Summary];

        public SkillContent Read(SkillIdentity identity)
        {
            Ensure(identity);
            return new SkillContent(
                Summary,
                Content,
                Array.Empty<string>());
        }

        public SkillResourceContent ReadResource(
            SkillIdentity identity,
            string relativePath)
        {
            Ensure(identity);
            throw new FileNotFoundException(
                "MB-103 fixture skill has no progressive resources.");
        }

        private static void Ensure(SkillIdentity identity)
        {
            if (identity != Summary.Identity)
                throw new InvalidOperationException(
                    "MB-103 fixture skill identity mismatch.");
        }
    }

    private sealed class FixtureRuntimeTransport : IAgentTransport
    {
        private int _continuations;
        public bool ToolResultObserved { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Tools.Select(x => x.Name)
                .SequenceEqual(new[] { DeferredToolDiscovery.SearchToolName }))
                throw new InvalidOperationException(
                    "MB-103 initial runtime surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "mb103-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = FixtureExtension.ToolName,
                    max_results = 1
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb103-1",
                "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name
                    != FixtureExtension.ToolName)
                    throw new InvalidOperationException(
                        "MB-103 deferred discovery did not load extension tool.");

                yield return AgentTransportEvent.Tool(new(
                    "mb103-echo",
                    FixtureExtension.ToolName,
                    JsonSerializer.Serialize(new { value = "hello" })));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb103-2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                using var json = JsonDocument.Parse(result.Content);
                ToolResultObserved = !result.IsError
                    && json.RootElement.GetProperty("echo").GetString() == "hello";

                yield return AgentTransportEvent.TextDeltaEvent(
                    "mb103-extension-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb103-3",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-103 continuation count.");
        }

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class DisabledRuntimeTransport : IAgentTransport
    {
        private int _continuations;
        public bool NoExtensionSchemaObserved { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "mb103-disabled-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = FixtureExtension.ToolName,
                    max_results = 1
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb103-disabled-1",
                "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;
            if (_continuations != 1)
                throw new InvalidOperationException(
                    "Unexpected disabled-extension continuation count.");

            NoExtensionSchemaObserved =
                request.NewlyLoadedTools is null
                || request.NewlyLoadedTools.Count == 0;

            yield return AgentTransportEvent.TextDeltaEvent(
                "mb103-disabled-ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb103-disabled-2",
                "stop");
        }

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
