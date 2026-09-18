using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Extensions;
using H2AgentLab.FirstPartyExtensions.Cad;
using H2AgentLab.FirstPartyExtensions.Computer;
using H2AgentLab.FirstPartyExtensions.Mcp;
using H2AgentLab.FirstPartyExtensions.Office;
using H2AgentLab.FirstPartyExtensions.Python;
using H2AgentLab.FirstPartyExtensions.Web;
using H2AgentLab.Prompting;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2AgentLab.Web;

namespace H2AgentLab.Extensions;

public static class MbFirstPartyExtensionCardsTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-63 test directory.");
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

        await Test("MB-63 all preserved first-party families have extension cards on one bus", async () =>
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

            var web = new WebResearchHost(
                new FixtureWebBackend(),
                providerId: "web-reference",
                providerVersion: "1.0.0");
            await using var mcpConnection = new McpServerConnection(
                McpDefinition(),
                new FixtureMcpTransport());
            var mcp = new McpToolProvider(
                mcpConnection,
                McpPolicy());

            extensions.Register(new OfficeFirstPartyExtension(executor));
            extensions.Register(new WebFirstPartyExtension(web));
            extensions.Register(new DesktopComputerExtension(executor));
            extensions.Register(new AutoCadFirstPartyExtension(executor));
            extensions.Register(new McpFirstPartyExtension(mcp));
            extensions.Register(new FileSystemComputerExtension(executor));
            extensions.Register(new ProcessShellComputerExtension(executor));
            extensions.Register(new PythonSandboxFirstPartyExtension(executor));

            var ids = extensions.Extensions
                .Select(x => x.Metadata.ExtensionId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var id in new[]
            {
                "office-first-party-extension",
                "web-first-party-extension",
                "desktop-computer-extension",
                "autocad-first-party-extension",
                "mcp-first-party-extension",
                "filesystem-computer-extension",
                "process-shell-computer-extension",
                "python-sandbox-first-party-extension"
            })
                Check(ids.Contains(id), "Missing first-party extension card: " + id);

            foreach (var tool in new[]
            {
                "word.read_range",
                "excel.read_formulas",
                "autocad.query_entities",
                "uia.inspect_tree",
                "screen.capture",
                "filesystem.read",
                "process.list",
                "shell.run_test",
                "run_python",
                "inspect_artifact"
            })
                Check(registry.TryGet(tool, out _),
                    "Preserved first-party tool was not registered: " + tool);

            Check(providers.Providers.Any(x => x.ProviderId == "web-reference")
                && providers.Providers.Any(x => x.ProviderId == "mcp-reference"),
                "Web/MCP provider cards were not registered on provider lifecycle.");
        });

        await Test("MB-63 Web and MCP provider cards lazy-load through provider-neutral ToolRegistry adapter", async () =>
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

            var web = new WebResearchHost(
                new FixtureWebBackend(),
                providerId: "web-reference",
                providerVersion: "1.0.0");
            var mcpConnection = new McpServerConnection(
                McpDefinition(),
                new FixtureMcpTransport());
            var mcp = new McpToolProvider(
                mcpConnection,
                McpPolicy());

            extensions.Register(new WebFirstPartyExtension(web));
            extensions.Register(new McpFirstPartyExtension(mcp));

            Check(!registry.TryGet("web.search", out _)
                && !registry.TryGet("mcp.echo", out _),
                "Provider card eagerly injected detailed schemas before selection.");

            var webLoaded = await providers.LoadProviderToolsAsync(
                "web-reference",
                ["web.search"],
                CancellationToken.None);
            var mcpLoaded = await providers.LoadProviderToolsAsync(
                "mcp-reference",
                ["mcp.echo"],
                CancellationToken.None);

            Check(webLoaded.Single().Name == "web.search"
                && mcpLoaded.Single().Name == "mcp.echo",
                "Provider-neutral selected-schema loader lost Web/MCP tools.");

            var webResult = await registry.Tools
                .Single(x => x.Name == "web.search")
                .Executor.ExecuteAsync(
                    Call(
                        "web-search",
                        "web.search",
                        new { query = "fixture", max_results = 2 }),
                    CancellationToken.None);
            Check(webResult.Contains("https://example.test/fixture", StringComparison.Ordinal),
                "Existing WebResearchHost implementation did not execute through the extension/provider path.");

            var mcpResult = await registry.Tools
                .Single(x => x.Name == "mcp.echo")
                .Executor.ExecuteAsync(
                    Call(
                        "mcp-echo",
                        "mcp.echo",
                        new { value = "hello" }),
                    CancellationToken.None);
            Check(mcpResult.Contains("mcp-ok", StringComparison.Ordinal),
                "Existing McpToolProvider implementation did not execute through the extension/provider path.");
        });

        await Test("MB-63 Python card preserves compatibility executor and Windows sandbox security profile", async () =>
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

            extensions.Register(
                new PythonSandboxFirstPartyExtension(executor));

            var python = registry.GetNamespace("python");
            Check(python.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(new[]
                    {
                        "inspect_artifact",
                        "publish_artifact",
                        "read_run",
                        "run_python",
                        "view_artifact"
                    }),
                "Python extension did not preserve the existing compatibility tool surface.");
            Check(python.All(x =>
                    x.Executor.ExecutorId == executor.ExecutorId
                    && x.Provenance?.ProviderId == "firstparty.python-sandbox"),
                "Python extension rewrote execution instead of preserving the supplied sandbox executor path.");
            Check(global::H2AgentLab.WindowsPythonSandbox.SecurityProfile.AppContainer
                && !global::H2AgentLab.WindowsPythonSandbox.SecurityProfile.NetworkCapability
                && global::H2AgentLab.WindowsPythonSandbox.SecurityProfile.MaxProcesses == 1,
                "Preserved WindowsPythonSandbox security profile regressed.");
        });

        await Test("MB-63 AgentRuntime uses AutoCAD reference card through ToolRegistry without core changes", async () =>
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
                new AutoCadFirstPartyExtension(FixtureExecutor()));

            var transport = new AutoCadRuntimeTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request(),
                CancellationToken.None);

            Check(result.FinalText == "first-party-card-ok",
                "AgentRuntime did not complete using AutoCAD extension card.");
            Check(transport.ToolResultObserved
                && result.LoadedToolSchemas.Contains(
                    "autocad.list_documents",
                    StringComparer.Ordinal),
                "AutoCAD reference card was not discovered/executed through ordinary ToolRegistry flow.");
        });

        await Test("MB-63 reference cards reuse existing implementations and AgentRuntime remains application-neutral", () =>
        {
            var repo = FindRepoRoot();
            var requiredExisting = new[]
            {
                Path.Combine("Office", "StructuredOfficeCapabilities.cs"),
                Path.Combine("Web", "WebResearchHost.cs"),
                Path.Combine("Desktop", "DesktopHostClient.cs"),
                Path.Combine("Cad", "AutoCadProviderContract.cs"),
                Path.Combine("Providers", "McpToolProvider.cs"),
                Path.Combine("Computer", "FilesystemCapabilities.cs"),
                Path.Combine("Computer", "ProcessShellCapabilities.cs"),
                "WindowsPythonSandbox.cs"
            };
            foreach (var relative in requiredExisting)
                Check(File.Exists(Path.Combine(
                        repo,
                        "experiments",
                        "H2AgentLab",
                        relative)),
                    "Working implementation was deleted during extension migration: " + relative);

            var references = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Path.Combine("FirstPartyExtensions", "Office", "OfficeFirstPartyExtension.cs")]
                    = "StructuredOfficeCapabilityCatalog",
                [Path.Combine("FirstPartyExtensions", "Web", "WebFirstPartyExtension.cs")]
                    = "WebResearchHost",
                [Path.Combine("FirstPartyExtensions", "Cad", "AutoCadFirstPartyExtension.cs")]
                    = "AutoCadProviderPolicy",
                [Path.Combine("FirstPartyExtensions", "Mcp", "McpFirstPartyExtension.cs")]
                    = "McpToolProvider",
                [Path.Combine("FirstPartyExtensions", "Python", "PythonSandboxFirstPartyExtension.cs")]
                    = "V1ToolRegistryAdapter"
            };
            foreach (var pair in references)
            {
                var source = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    pair.Key));
                Check(source.Contains(pair.Value, StringComparison.Ordinal),
                    "First-party card does not reuse existing implementation seam: " + pair.Key);
            }

            var runtimeSource = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Runtime",
                "AgentRuntime.cs"));
            foreach (var forbidden in new[]
            {
                "OfficeFirstPartyExtension",
                "WebFirstPartyExtension",
                "DesktopComputerExtension",
                "AutoCadFirstPartyExtension",
                "McpFirstPartyExtension",
                "FileSystemComputerExtension",
                "ProcessShellComputerExtension",
                "PythonSandboxFirstPartyExtension"
            })
                Check(!runtimeSource.Contains(forbidden, StringComparison.Ordinal),
                    "AgentRuntime gained first-party application knowledge: " + forbidden);
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-first-party-extension-cards-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static IAgentToolExecutor FixtureExecutor()
        => new DelegatingToolExecutor(
            "mb63-fixture",
            (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    call.Name == "autocad.list_documents"
                        ? JsonSerializer.Serialize(new
                        {
                            documents = new[] { "fixture.dwg" }
                        })
                        : "{}");
            });

    private static global::H2AgentLab.ToolCall Call(
        string id,
        string name,
        object arguments)
        => new(
            id,
            name,
            JsonSerializer.SerializeToElement(arguments));

    private static AgentRuntimeRequest Request()
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            "List available AutoCAD documents through installed capabilities.",
            "cad:fixture",
            null,
            null,
            ["do not mutate"],
            ["return observed document list"],
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
                CurrentState: "MB-63 first-party extension fixture"),
            PromptCacheKey: "mb63",
            MaxToolRounds: 4);
    }

    private static McpServerDefinition McpDefinition()
        => new(
            "mcp-reference",
            "1.0.0",
            "mcp-fixture-server",
            "fixture-command",
            Array.Empty<string>(),
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(2));

    private static CapabilityProviderPolicy McpPolicy()
        => new(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "provider:mcp-reference"
            },
            new HashSet<string>(StringComparer.Ordinal),
            AllowParallelReadOnly: true);

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

    private sealed class FixtureWebBackend : IWebResearchBackend
    {
        public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WebSearchHit>>(
            [
                new WebSearchHit(
                    "https://example.test/fixture",
                    "Fixture result",
                    "Fixture publisher",
                    "Fixture snippet")
            ]);
        }

        public Task<WebFetchedDocument> FetchAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WebFetchedDocument(
                url,
                "text/plain",
                Encoding.UTF8.GetBytes("fixture body"),
                "Fixture result",
                "Fixture publisher"));
        }

        public Task<string> OpenBrowserFallbackAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(url);
        }
    }

    private sealed class FixtureMcpTransport : IMcpRpcTransport
    {
        public bool IsRunning { get; private set; }

        public Task StartAsync(
            McpServerDefinition definition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task<JsonElement> CallAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = "2025-06-18"
                }),
                "tools/list" => JsonSerializer.SerializeToElement(new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "mcp.echo",
                            description = "Echo fixture value.",
                            annotations = new
                            {
                                readOnlyHint = true
                            },
                            inputSchema = new
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
                    }
                }),
                "tools/call" => JsonSerializer.SerializeToElement(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "mcp-ok"
                        }
                    }
                }),
                "resources/list" => JsonSerializer.SerializeToElement(new
                {
                    resources = Array.Empty<object>()
                }),
                _ => throw new InvalidOperationException(
                    "Unexpected MCP fixture method: " + method)
            };
            return Task.FromResult(result);
        }

        public Task NotifyAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AutoCadRuntimeTransport : IAgentTransport
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
                    "MB-63 initial runtime surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "cad-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "autocad.list_documents",
                    max_results = 1
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb63-1",
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
                    != "autocad.list_documents")
                    throw new InvalidOperationException(
                        "Deferred discovery did not load AutoCAD card tool.");

                yield return AgentTransportEvent.Tool(new(
                    "cad-list",
                    "autocad.list_documents",
                    "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb63-2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                using var json = JsonDocument.Parse(result.Content);
                var documents = json.RootElement
                    .GetProperty("documents")
                    .EnumerateArray()
                    .Select(x => x.GetString())
                    .ToArray();
                ToolResultObserved = !result.IsError
                    && documents.SequenceEqual(new[] { "fixture.dwg" });

                yield return AgentTransportEvent.TextDeltaEvent(
                    "first-party-card-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb63-3",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-63 continuation count.");
        }

        public void Cancel() { }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
