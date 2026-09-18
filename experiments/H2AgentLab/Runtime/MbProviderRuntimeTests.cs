using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public static class MbProviderRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-12 test directory.");
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

        await Test("MB-12 transport factory routes accepted profiles to existing provider-neutral transports", async () =>
        {
            var telemetry = new AgentRunTelemetry();
            var standard = new AgentTransportFactory();

            await using (var ollama = standard.Create(
                new AiProfile
                {
                    Protocol = AiProtocol.Ollama,
                    BaseUrl = "http://localhost:11434",
                    Model = "fixture"
                },
                "",
                telemetry))
            {
                Check(ollama is OllamaTransport,
                    "Ollama profile did not route to OllamaTransport.");
            }

            await using (var chat = standard.Create(
                new AiProfile
                {
                    Protocol = AiProtocol.OpenAiChat,
                    BaseUrl = "https://example.invalid/v1",
                    Model = "fixture"
                },
                "key",
                telemetry))
            {
                Check(chat is ChatCompletionsTransport,
                    "Chat profile did not route to ChatCompletionsTransport.");
            }

            var responsesProfile = new AiProfile
            {
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-fixture"
            };

            await using (var responses = standard.Create(
                responsesProfile,
                "key",
                telemetry))
            {
                Check(responses is OpenAiResponsesTransport,
                    "Responses profile did not default to HTTP Responses transport.");
            }

            var websocketFactory = new AgentTransportFactory(
                new AgentTransportFactoryOptions(
                    OpenAiResponsesTransportPreference.WebSocket));
            await using var websocket = websocketFactory.Create(
                responsesProfile,
                "key",
                telemetry);
            Check(websocket is OpenAiResponsesWebSocketTransport,
                "Explicit Responses WebSocket selection did not route to WebSocket transport.");
        });

        await Test("MB-12 AgentRuntime contains no duplicated provider HTTP or protocol switch", () =>
        {
            var repo = FindRepoRoot();
            var runtime = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Runtime", "AgentRuntime.cs"));
            var orchestrator = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestrator.cs"));
            var facade = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));

            foreach (var source in new[] { runtime, orchestrator, facade })
            {
                Check(!source.Contains("new HttpClient", StringComparison.Ordinal)
                    && !source.Contains("HttpRequestMessage", StringComparison.Ordinal)
                    && !source.Contains(".SendAsync(", StringComparison.Ordinal),
                    "Provider HTTP implementation leaked outside transport layer.");
            }

            Check(!runtime.Contains("AiProtocol.", StringComparison.Ordinal)
                && !runtime.Contains("OllamaTransport", StringComparison.Ordinal)
                && !runtime.Contains("OpenAiResponsesTransport", StringComparison.Ordinal)
                && !runtime.Contains("ChatCompletionsTransport", StringComparison.Ordinal),
                "AgentRuntime contains provider-specific selection logic.");
            return Task.CompletedTask;
        });

        await Test("MB-12 tool-call identity and provider usage survive start continuation and final", async () =>
        {
            var registry = new ToolRegistry();
            registry.Register(ReadTool());
            var transport = new IdentityUsageTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Read fixture through provider-neutral continuation."),
                CancellationToken.None);

            Check(result.FinalText == "identity-ok",
                "Provider-neutral runtime final text mismatch.");
            Check(transport.IdentityPreserved,
                "Tool-call ID/name were not preserved across continuation.");
            Check(result.ToolRounds == 2 && result.ToolCalls == 2,
                "Runtime round/call counts are wrong.");
            Check(result.Usage.InputTokens == 60
                && result.Usage.CachedInputTokens == 7
                && result.Usage.CacheWriteInputTokens == 3
                && result.Usage.OutputTokens == 12
                && result.Usage.TotalTokens == 72,
                "Provider usage metrics were not aggregated correctly.");
        });

        await Test("MB-12 incomplete proposed tool call cannot execute", async () =>
        {
            var executions = 0;
            var registry = new ToolRegistry();
            registry.Register(ReadTool((call, ct) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult("{}");
            }));

            await using var runtime = new AgentRuntime(
                new IncompleteProposalTransport(),
                new AgentContextManager(),
                registry);

            try
            {
                _ = await runtime.RunAsync(
                    Request("Do not execute an incomplete proposal."),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Incomplete transport response unexpectedly completed.");
            }
            catch (IOException ex) when (
                ex.Message.Contains("without a completed response", StringComparison.OrdinalIgnoreCase))
            {
            }

            Check(executions == 0,
                "Tool executed even though the provider response never completed.");
        });

        await Test("MB-12 normal facade preserves runtime usage while provider switch leaves runtime architecture unchanged", async () =>
        {
            var factory = new SwitchingRuntimeFactory();
            var orchestrator = new AgentOrchestrator(runtimeFactory: factory);
            var workspace = Path.Combine(root, "switch-workspace");
            var state = Path.Combine(root, "switch-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);
            var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });

            async Task<(string Final, AgentMetricsSnapshot Metrics)> RunProfile(AiProtocol protocol)
            {
                var session = new global::H2AgentLab.LabSession { Workspace = workspace };
                var telemetry = new AgentRunTelemetry();
                var outputs = new List<(string Kind, string Text)>();
                var run = new AgentOrchestratedRun(orchestrator);
                _ = await run.RunAsync(
                    new AiProfile
                    {
                        Protocol = protocol,
                        BaseUrl = protocol == AiProtocol.Ollama
                            ? "http://localhost:11434"
                            : "https://example.invalid/v1",
                        Model = "fixture"
                    },
                    "",
                    session,
                    tools,
                    "provider switch fixture",
                    (kind, text) => outputs.Add((kind, text)),
                    () => { },
                    telemetry,
                    readOnly: true,
                    CancellationToken.None);
                return (
                    outputs.Last(x => x.Kind == "final").Text,
                    telemetry.Metrics.Snapshot(telemetry.Trace));
            }

            var ollama = await RunProfile(AiProtocol.Ollama);
            var chat = await RunProfile(AiProtocol.OpenAiChat);

            Check(ollama.Final == "Ollama-runtime"
                && chat.Final == "OpenAiChat-runtime",
                "Injected provider switch did not preserve one AgentRuntime facade.");
            Check(ollama.Metrics.InputTokens == 9
                && ollama.Metrics.OutputTokens == 2
                && ollama.Metrics.ModelCalls == 1
                && chat.Metrics.InputTokens == 9
                && chat.Metrics.OutputTokens == 2
                && chat.Metrics.ModelCalls == 1,
                "Normal facade did not preserve AgentRuntime usage metrics.");
            Check(factory.Protocols.SequenceEqual(new[]
            {
                AiProtocol.Ollama,
                AiProtocol.OpenAiChat
            }), "Provider switch did not stay behind the runtime factory seam.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-provider-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string userInput)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "fixture",
            null,
            null,
            ["do not mutate"],
            ["return fixture evidence"],
            [],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

        return new AgentRuntimeRequest(
            contract,
            userInput,
            new Prompting.AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: userInput,
                CurrentState: "fixture-state"),
            MaxToolRounds: 8,
            MaxRepairRounds: 1);
    }

    private static ToolDescriptor ReadTool(
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>>? execute = null)
    {
        const string name = "fixture.read";
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name,
                description = "Read deterministic fixture evidence.",
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            }
        });

        return new ToolDescriptor(
            name,
            new ToolNamespace("fixture", "MB-12 fixture tools."),
            "Read deterministic fixture evidence.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v1",
            callableSchema: schema,
            executor: new DelegatingToolExecutor(
                "mb12-fixture",
                execute ?? ((call, ct) =>
                    ValueTask.FromResult(
                        JsonSerializer.Serialize(new
                        {
                            value = "fixture"
                        })))),
            provenance: new ToolProvenance(
                "mb12-fixture",
                "1.0.0",
                "fixture",
                "1.0.0"));
    }

    private sealed class IdentityUsageTransport : IAgentTransport
    {
        private int _continuations;
        public bool IdentityPreserved { get; private set; }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.OpenAiResponsesWebSocket;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "search-provider-1",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "fixture read evidence"
                })));
            yield return AgentTransportEvent.Meter(new(
                InputTokens: 10,
                CachedInputTokens: 2,
                CacheWriteInputTokens: 1,
                OutputTokens: 2,
                TotalTokens: 12));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("p1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                CheckResult(request, "search-provider-1", DeferredToolDiscovery.SearchToolName);
                if (request.NewlyLoadedTools?.Single().Name != "fixture.read")
                    throw new InvalidOperationException("Deferred schema identity was lost.");
                yield return AgentTransportEvent.Tool(new(
                    "provider-call-42",
                    "fixture.read",
                    "{}"));
                yield return AgentTransportEvent.Meter(new(
                    InputTokens: 20,
                    CachedInputTokens: 3,
                    CacheWriteInputTokens: 1,
                    OutputTokens: 4,
                    TotalTokens: 24));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("p2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                CheckResult(request, "provider-call-42", "fixture.read");
                IdentityPreserved = true;
                yield return AgentTransportEvent.TextDeltaEvent("identity-ok");
                yield return AgentTransportEvent.Meter(new(
                    InputTokens: 30,
                    CachedInputTokens: 2,
                    CacheWriteInputTokens: 1,
                    OutputTokens: 6,
                    TotalTokens: 36));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("p3", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected continuation count.");
        }

        private static void CheckResult(
            AgentTransportContinuationRequest request,
            string expectedId,
            string expectedName)
        {
            var result = request.ToolResults.Single();
            if (result.ToolCallId != expectedId
                || result.ToolName != expectedName)
                throw new InvalidOperationException(
                    "Tool call identity was not preserved across continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IncompleteProposalTransport : IAgentTransport
    {
        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "incomplete-1",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "fixture read"
                })));
            await Task.Yield();
            // Intentionally no Completed event.
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Incomplete proposal must never continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SwitchingRuntimeFactory : IAgentRuntimeFactory
    {
        public List<AiProtocol> Protocols { get; } = [];

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            global::H2AgentLab.AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
        {
            Protocols.Add(profile.Protocol);
            return new AgentRuntime(
                new UsageFinalTransport(profile.Protocol + "-runtime"),
                contextManager,
                new ToolRegistry());
        }
    }

    private sealed class UsageFinalTransport : IAgentTransport
    {
        private readonly string _text;

        public UsageFinalTransport(string text)
        {
            _text = text;
        }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Started("switch");
            yield return AgentTransportEvent.TextDeltaEvent(_text);
            yield return AgentTransportEvent.Meter(new(
                InputTokens: 9,
                OutputTokens: 2,
                TotalTokens: 11));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("switch", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Direct switch fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
