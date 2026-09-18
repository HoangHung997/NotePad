using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Extensions;
using H2AgentLab.FirstPartyExtensions.Computer;
using H2AgentLab.Prompting;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Computer;

public static class MbComputerExtensionRegistrationTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-62 test directory.");
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
                lines.Add("FAIL " + name + ": "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("MB-62 independent computer extensions own their registered namespaces", async () =>
        {
            var registry = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(registry);
            var extensions = new AgentExtensionRegistry(
                registry,
                skills,
                verifiers,
                providers);
            var executor = FixtureExecutor();

            extensions.Register(new FileSystemComputerExtension(executor));
            extensions.Register(new ProcessShellComputerExtension(executor));
            extensions.Register(new DesktopComputerExtension(executor));
            extensions.Register(new BrowserComputerExtension(executor));

            var expectedProviders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["filesystem"] = "firstparty.computer.filesystem",
                ["process"] = "firstparty.computer.process-shell",
                ["shell"] = "firstparty.computer.process-shell",
                ["app"] = "firstparty.computer.desktop",
                ["window"] = "firstparty.computer.desktop",
                ["uia"] = "firstparty.computer.desktop",
                ["input"] = "firstparty.computer.desktop",
                ["screen"] = "firstparty.computer.desktop",
                ["browser"] = "firstparty.computer.browser"
            };

            foreach (var pair in expectedProviders)
            {
                var tools = GeneralComputerCapabilityCatalog.Namespace(
                    registry,
                    pair.Key);
                Check(tools.Count > 0
                    && tools.All(x =>
                        x.Provenance?.ProviderId == pair.Value),
                    "Namespace '" + pair.Key
                    + "' is not owned by its independent provider extension.");
            }

            Check(!GeneralComputerCapabilityCatalog.ContainsMonolithicUnsafeControl(registry),
                "Registered computer tools contain a monolithic unsafe control command.");
        });

        await Test("MB-62 partial registration does not invent unregistered capability families", async () =>
        {
            var registry = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(registry);
            var extensions = new AgentExtensionRegistry(
                registry,
                skills,
                verifiers,
                providers);

            extensions.Register(
                new FileSystemComputerExtension(FixtureExecutor()));

            Check(GeneralComputerCapabilityCatalog.Namespace(
                    registry,
                    "filesystem").Count > 0,
                "Registered filesystem extension was not projected.");
            foreach (var absent in new[]
            {
                "process", "shell", "app", "window",
                "uia", "input", "screen", "browser"
            })
                Check(GeneralComputerCapabilityCatalog.Namespace(
                        registry,
                        absent).Count == 0,
                    "Unregistered capability family appeared from a central inventory: "
                    + absent);
        });

        await Test("MB-62 a new computer family is discoverable without editing central catalog or AgentRuntime", async () =>
        {
            var registry = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(registry);
            var extensions = new AgentExtensionRegistry(
                registry,
                skills,
                verifiers,
                providers);

            extensions.Register(new CameraFixtureExtension());

            var camera = GeneralComputerCapabilityCatalog.Namespace(
                registry,
                "camera");
            Check(camera.Count == 1
                && camera[0].Name == "camera.inspect"
                && camera[0].Provenance?.ProviderId
                    == "firstparty.computer.camera",
                "Registry projection could not expose a new provider-owned computer family.");

            var repo = FindRepoRoot();
            var projectionSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Computer",
                    "GeneralComputerCapabilityCatalog.cs"));
            var runtimeSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Runtime",
                    "AgentRuntime.cs"));
            Check(!projectionSource.Contains("camera.inspect", StringComparison.Ordinal)
                && !runtimeSource.Contains("camera.inspect", StringComparison.Ordinal)
                && !runtimeSource.Contains("filesystem.list", StringComparison.Ordinal)
                && !runtimeSource.Contains("browser.navigate", StringComparison.Ordinal),
                "Adding a new computer family required central/runtime application knowledge.");
        });

        await Test("MB-62 AgentRuntime executes registered computer tool through ToolRegistry only", async () =>
        {
            var registry = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(registry);
            var extensions = new AgentExtensionRegistry(
                registry,
                skills,
                verifiers,
                providers);

            extensions.Register(
                new FileSystemComputerExtension(FixtureExecutor()));

            var transport = new ComputerRuntimeTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request(),
                CancellationToken.None);

            Check(result.FinalText == "computer-extension-ok",
                "AgentRuntime did not complete through provider-registered computer tool.");
            Check(transport.ToolResultObserved,
                "Registered filesystem tool result did not reach model continuation.");
            Check(result.LoadedToolSchemas.Contains(
                    "filesystem.list",
                    StringComparer.Ordinal),
                "Deferred ToolRegistry discovery did not load filesystem.list.");
        });

        await Test("MB-62 source guard leaves no master capability list in compatibility catalog", () =>
        {
            var repo = FindRepoRoot();
            var projectionSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Computer",
                    "GeneralComputerCapabilityCatalog.cs"));
            foreach (var forbidden in new[]
            {
                "filesystem.list",
                "process.list",
                "shell.run_bounded",
                "app.list_running_apps",
                "window.enumerate",
                "uia.inspect_tree",
                "input.click",
                "screen.capture",
                "browser.navigate"
            })
                Check(!projectionSource.Contains(forbidden, StringComparison.Ordinal),
                    "Compatibility catalog still owns capability definition: "
                    + forbidden);

            foreach (var providerFile in new[]
            {
                "FileSystemComputerExtension.cs",
                "ProcessShellComputerExtension.cs",
                "DesktopComputerExtension.cs",
                "BrowserComputerExtension.cs"
            })
                Check(File.Exists(Path.Combine(
                        repo,
                        "experiments",
                        "H2AgentLab",
                        "FirstPartyExtensions",
                        "Computer",
                        providerFile)),
                    "Missing independent computer provider card: "
                    + providerFile);
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-computer-extension-registration-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static IAgentToolExecutor FixtureExecutor()
        => new DelegatingToolExecutor(
            "mb62-computer-fixture",
            (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    call.Name == "filesystem.list"
                        ? JsonSerializer.Serialize(new
                        {
                            files = new[] { "a.txt", "b.txt" }
                        })
                        : "{}");
            });

    private static AgentRuntimeRequest Request()
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            "List supported paths in the approved workspace.",
            "computer:filesystem",
            null,
            null,
            ["do not mutate"],
            ["return file list"],
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
                CurrentState: "MB-62 computer provider fixture"),
            PromptCacheKey: "mb62",
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

    private sealed class CameraFixtureExtension : IAgentExtension
    {
        public AgentExtensionMetadata Metadata { get; } = new(
            "camera-computer-extension",
            "1.0.0",
            "Camera Computer Extension",
            "MB-62 new-family fixture.");

        public void Register(AgentExtensionRegistration registration)
        {
            const string name = "camera.inspect";
            const string description =
                "Inspect a registered camera-like device through a typed provider.";
            registration.RegisterTool(new ToolDescriptor(
                name,
                new ToolNamespace(
                    "camera",
                    "Typed camera-like device capabilities."),
                description,
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                supportsParallel: true,
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
                            properties = new { },
                            additionalProperties = false
                        }
                    }
                }),
                executor: FixtureExecutor(),
                provenance: new ToolProvenance(
                    "firstparty.computer.camera",
                    "1.0.0",
                    "fixture",
                    "v1"),
                preference: new ToolPreferenceMetadata(
                    "computer-interaction",
                    ToolInteractionFidelity.Structured)));
        }
    }

    private sealed class ComputerRuntimeTransport : IAgentTransport
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
                    "MB-62 initial surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "computer-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "filesystem.list",
                    max_results = 1
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb62-1",
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
                    != "filesystem.list")
                    throw new InvalidOperationException(
                        "Deferred discovery did not load provider-registered filesystem.list.");

                yield return AgentTransportEvent.Tool(new(
                    "filesystem-list",
                    "filesystem.list",
                    "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb62-2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                using var json = JsonDocument.Parse(result.Content);
                var files = json.RootElement
                    .GetProperty("files")
                    .EnumerateArray()
                    .Select(x => x.GetString())
                    .ToArray();
                ToolResultObserved = !result.IsError
                    && result.ToolName == "filesystem.list"
                    && files.SequenceEqual(new[] { "a.txt", "b.txt" });

                yield return AgentTransportEvent.TextDeltaEvent(
                    "computer-extension-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb62-3",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-62 continuation count.");
        }

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
