using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.FirstPartyExtensions.Calculator;
using H2AgentLab.Prompting;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Extensions;

public static class MbExtensionRegistrationTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-60 test directory.");
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

        await Test("MB-60 one extension boundary registers tool skill verifier provider and metadata", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);

            var calculator = new CalculatorExtension();
            var registered = extensions.Register(calculator);

            Check(registered.Metadata.ExtensionId == "calculator-extension"
                && registered.Metadata.Version == "1.0.0"
                && registered.Metadata.Properties?["category"] == "fixture",
                "Extension metadata was not retained by the registration boundary.");
            Check(tools.TryGet("calculator.add", out var descriptor)
                && descriptor.Namespace.Name == "calculator"
                && descriptor.Provenance?.ProviderId == "calculator.extension",
                "Extension tool was not registered into ToolRegistry.");
            Check(skills.Search("deterministic addition", 5)
                    .Any(x => x.Name == "calculator-basics"
                        && x.Identity.SourceId == "calculator-extension"),
                "Extension skill source was not registered into canonical SkillCatalog.");
            Check(verifiers.VerifierIds.Contains(
                    CalculatorArtifactVerifier.Id,
                    StringComparer.Ordinal),
                "Extension verifier was not registered.");
            Check(providers.Providers.Any(x =>
                    x.ProviderId == "calculator.provider"
                    && x.ProviderVersion == "1.0.0"),
                "Extension provider lifecycle was not registered.");
            Check(extensions.Extensions.Count == 1
                && extensions.Extensions[0].Metadata.ExtensionId == "calculator-extension",
                "Extension registry did not retain installed extension identity.");

            return;
        });

        await Test("MB-60 registered verifier and provider lifecycle remain owned by host registries", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);
            var calculator = new CalculatorExtension();
            extensions.Register(calculator);

            var namespaces = await providers.DiscoverNamespacesAsync(
                CancellationToken.None);
            Check(calculator.Provider.ConnectCount == 1
                && calculator.Provider.Health.Status == ProviderHealthStatus.Ready
                && namespaces.Any(x => x.Name == "calculator"),
                "Host provider manager did not own/connect registered provider lifecycle.");

            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Verify calculator result.",
                "calculator:test",
                null,
                null,
                ["do not mutate"],
                ["verified result"],
                [new AgentAcceptanceCriterion(
                    "result",
                    "Calculator result is mechanically verified.")],
                AgentTaskRiskClass.ReadOnly,
                new AgentVerificationPolicy(requireVerification: true));
            var target = new ArtifactVerificationTarget(
                "calc-result-1",
                "application/x-calculator-result");
            var verifier = verifiers.Resolve(target).Single();
            var report = await verifier.VerifyAsync(
                new ArtifactVerificationRequest(
                    contract,
                    target,
                    ["result"]),
                CancellationToken.None);

            Check(report.Passed
                && report.VerifierId == CalculatorArtifactVerifier.Id
                && report.Criteria.Single().EvidenceIds.Any(x =>
                    x.Contains("calc-result-1", StringComparison.Ordinal)),
                "Registered verifier did not execute through ArtifactVerifierRegistry.");
        });

        await Test("MB-60 AgentRuntime uses CalculatorExtension tool through ToolRegistry without core changes", async () =>
        {
            var tools = new ToolRegistry();
            var skills = new H2AgentLab.Skills.SkillCatalog();
            var verifiers = new ArtifactVerifierRegistry();
            await using var providers = new CapabilityProviderManager(tools);
            var extensions = new AgentExtensionRegistry(
                tools,
                skills,
                verifiers,
                providers);
            extensions.Register(new CalculatorExtension());

            var transport = new CalculatorRuntimeTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                tools);

            var result = await runtime.RunAsync(
                Request(),
                CancellationToken.None);

            Check(result.FinalText == "calculator-extension-ok",
                "AgentRuntime did not finish after extension tool execution.");
            Check(transport.ToolResultObserved,
                "Model continuation did not receive calculator.add result.");
            Check(result.LoadedToolSchemas.Contains(
                    "calculator.add",
                    StringComparer.Ordinal),
                "Extension tool schema was not loaded through normal deferred ToolRegistry discovery.");
        });

        await Test("MB-60 Agent core contains no Calculator-specific registration logic", () =>
        {
            var repo = FindRepoRoot();
            var runtimeSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Runtime",
                    "AgentRuntime.cs"));
            var extensionSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Extensions",
                    "AgentExtension.cs"));
            var calculatorSource = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "FirstPartyExtensions",
                "Calculator",
                "CalculatorExtension.cs");

            Check(!runtimeSource.Contains("CalculatorExtension", StringComparison.Ordinal)
                && !runtimeSource.Contains("calculator.add", StringComparison.Ordinal),
                "AgentRuntime was edited with Calculator-specific knowledge.");
            Check(!extensionSource.Contains("calculator.add", StringComparison.Ordinal)
                && !extensionSource.Contains("AutoCAD", StringComparison.Ordinal)
                && !extensionSource.Contains("Word", StringComparison.Ordinal),
                "Generic extension registration boundary contains application-family logic.");
            Check(File.Exists(calculatorSource),
                "CalculatorExtension fixture is not located outside Agent core/runtime.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var reportPath = Path.Combine(root, "mb-extension-registration-tests.txt");
        await File.WriteAllLinesAsync(reportPath, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request()
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            "Add 12.5 and 7.25 using the registered extension.",
            "calculator:test",
            null,
            null,
            ["do not mutate"],
            ["return deterministic sum"],
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
                CurrentState: "MB-60 extension fixture"),
            PromptCacheKey: "mb60",
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

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class CalculatorRuntimeTransport : IAgentTransport
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
                    "MB-60 initial runtime surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "extension-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "add two numbers deterministically",
                    max_results = 3
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(
                "mb60-1",
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
                if (request.NewlyLoadedTools?.Single().Name != "calculator.add")
                    throw new InvalidOperationException(
                        "Normal deferred discovery did not load calculator.add.");

                yield return AgentTransportEvent.Tool(new(
                    "calculator-add",
                    "calculator.add",
                    JsonSerializer.Serialize(new
                    {
                        left = 12.5,
                        right = 7.25
                    })));
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb60-2",
                    "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                using var json = JsonDocument.Parse(result.Content);
                var sum = json.RootElement
                    .GetProperty("sum")
                    .GetDouble();
                ToolResultObserved = !result.IsError
                    && result.ToolName == "calculator.add"
                    && Math.Abs(sum - 19.75) < 0.0000001;

                yield return AgentTransportEvent.TextDeltaEvent(
                    "calculator-extension-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete(
                    "mb60-3",
                    "stop");
                yield break;
            }

            throw new InvalidOperationException(
                "Unexpected MB-60 continuation count.");
        }

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
