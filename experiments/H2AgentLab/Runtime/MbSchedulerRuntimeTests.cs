using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbSchedulerRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-33 test directory.");
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

        await Test("MB-33 independent parallel-safe reads overlap inside real AgentRuntime batch", async () =>
        {
            var active = 0;
            var maxActive = 0;

            async ValueTask<string> Read(global::H2AgentLab.ToolCall call, CancellationToken ct)
            {
                var now = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, now);
                try
                {
                    await Task.Delay(120, ct);
                    return JsonSerializer.Serialize(new { ok = true, tool = call.Name });
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }

            var registry = new ToolRegistry();
            registry.Register(ReadTool("fixture.read_alpha", "Parallel read alpha evidence.", Read));
            registry.Register(ReadTool("fixture.read_beta", "Parallel read beta evidence.", Read));

            await using var runtime = new AgentRuntime(
                new ParallelReadTransport(),
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Read alpha and beta.", "fixture:reads"),
                CancellationToken.None);

            Check(result.FinalText == "parallel-read-ok",
                "Parallel read fixture did not reach final.");
            Check(maxActive >= 2,
                $"Parallel-safe reads never overlapped; max concurrency was {maxActive}.");
        });

        await Test("MB-33 same-resource mutations serialize inside real AgentRuntime batch", async () =>
        {
            var active = 0;
            var maxActive = 0;
            var executions = 0;

            async ValueTask<string> Write(global::H2AgentLab.ToolCall call, CancellationToken ct)
            {
                Interlocked.Increment(ref executions);
                var now = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, now);
                try
                {
                    await Task.Delay(100, ct);
                    return JsonSerializer.Serialize(new { ok = true, id = call.Id });
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }

            var registry = new ToolRegistry();
            registry.Register(MutatingTool(
                "fixture.write_shared",
                "Write the shared fixture resource.",
                new ToolResourceScope("fixture:shared", "fixture:shared"),
                Write));

            await using var runtime = new AgentRuntime(
                new SameResourceMutationTransport(),
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Write shared fixture twice.", "fixture:shared"),
                CancellationToken.None);

            Check(result.FinalText == "serialized-write-ok",
                "Serialized mutation fixture did not reach final.");
            Check(executions == 2,
                "Expected two same-resource mutations.");
            Check(maxActive == 1,
                $"Same-resource mutations overlapped; max concurrency was {maxActive}.");
        });

        await Test("MB-33 mutating tool without resource identity fails closed before executor", async () =>
        {
            var executions = 0;
            var registry = new ToolRegistry();
            registry.Register(MutatingTool(
                "fixture.write_unscoped",
                "Write an unscoped fixture resource.",
                resourceScope: null,
                (call, ct) =>
                {
                    Interlocked.Increment(ref executions);
                    return ValueTask.FromResult("{}");
                }));

            await using var runtime = new AgentRuntime(
                new MissingResourceMutationTransport(),
                new AgentContextManager(),
                registry);

            try
            {
                _ = await runtime.RunAsync(
                    Request("Write unscoped fixture.", "fixture"),
                    CancellationToken.None);
                throw new InvalidOperationException("Unscoped mutation unexpectedly completed.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("requires a resource key", StringComparison.OrdinalIgnoreCase))
            {
            }

            Check(executions == 0,
                "Unscoped mutation reached executor before scheduler rejected it.");
        });

        await Test("MB-33 cancellation propagates through scheduler to active tool executor", async () =>
        {
            var executorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executorCancelled = false;

            var registry = new ToolRegistry();
            registry.Register(ReadTool(
                "fixture.long_read",
                "Long cancellable fixture read.",
                async (call, ct) =>
                {
                    executorStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        return "{}";
                    }
                    catch (OperationCanceledException)
                    {
                        executorCancelled = true;
                        throw;
                    }
                }));

            var transport = new CancellableToolTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            using var cancel = new CancellationTokenSource();
            var run = runtime.RunAsync(
                Request("Run cancellable read.", "fixture:cancel"),
                cancel.Token);

            await executorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();

            try
            {
                _ = await run;
                throw new InvalidOperationException("Cancelled runtime unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }

            Check(executorCancelled,
                "Cancellation token did not reach the active tool executor.");
            Check(transport.Cancelled,
                "AgentRuntime did not cancel the provider-neutral transport after tool cancellation.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-scheduler-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(string userInput, string scope)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            scope,
            null,
            null,
            ["preserve unrelated state"],
            ["finish fixture"],
            [],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: false));

        return new AgentRuntimeRequest(
            contract,
            userInput,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "scheduler fixture"),
            PromptCacheKey: "mb33",
            MaxToolRounds: 4);
    }

    private static ToolDescriptor ReadTool(
        string name,
        string description,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => Tool(
            name,
            description,
            AgentToolAccess.ReadOnly,
            AgentToolRisk.Low,
            supportsParallel: true,
            resourceScope: null,
            execute);

    private static ToolDescriptor MutatingTool(
        string name,
        string description,
        ToolResourceScope? resourceScope,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => Tool(
            name,
            description,
            AgentToolAccess.Mutating,
            AgentToolRisk.Medium,
            supportsParallel: false,
            resourceScope,
            execute);

    private static ToolDescriptor Tool(
        string name,
        string description,
        AgentToolAccess access,
        AgentToolRisk risk,
        bool supportsParallel,
        ToolResourceScope? resourceScope,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => new(
            name,
            new ToolNamespace("fixture", "MB-33 scheduler fixture namespace."),
            description,
            risk,
            access,
            supportsParallel,
            "v1",
            JsonSerializer.SerializeToElement(new
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
            new DelegatingToolExecutor("mb33-fixture", execute),
            provenance: new ToolProvenance(
                "mb33-provider",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: resourceScope,
            serializationKey: "mb33-fixture");

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class ParallelReadTransport : IAgentTransport
    {
        private int _continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AgentTransportEvent.Tool(new(
                "search-parallel",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "parallel read alpha beta evidence" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("pr1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _continuations++;
            if (_continuations == 1)
            {
                var loaded = request.NewlyLoadedTools?.Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                    ?? throw new InvalidOperationException("Parallel read schemas were not loaded.");
                if (!loaded.SetEquals(new[] { "fixture.read_alpha", "fixture.read_beta" }))
                    throw new InvalidOperationException("Expected both parallel read schemas.");

                yield return AgentTransportEvent.Tool(new("read-a", "fixture.read_alpha", "{}"));
                yield return AgentTransportEvent.Tool(new("read-b", "fixture.read_beta", "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("pr2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                if (request.ToolResults.Count != 2 || request.ToolResults.Any(x => x.IsError))
                    throw new InvalidOperationException("Parallel read results are incomplete.");
                yield return AgentTransportEvent.TextDeltaEvent("parallel-read-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("pr3", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected parallel-read continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SameResourceMutationTransport : IAgentTransport
    {
        private int _continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AgentTransportEvent.Tool(new(
                "search-write",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "write shared fixture resource" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("sw1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _continuations++;
            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.write_shared")
                    throw new InvalidOperationException("Shared mutation schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("write-1", "fixture.write_shared", "{}"));
                yield return AgentTransportEvent.Tool(new("write-2", "fixture.write_shared", "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("sw2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                if (request.ToolResults.Count != 2 || request.ToolResults.Any(x => x.IsError))
                    throw new InvalidOperationException("Serialized mutation results are incomplete.");
                yield return AgentTransportEvent.TextDeltaEvent("serialized-write-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("sw3", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected serialized-write continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MissingResourceMutationTransport : IAgentTransport
    {
        private int _continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AgentTransportEvent.Tool(new(
                "search-unscoped",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "write unscoped fixture resource" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mr1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _continuations++;
            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.write_unscoped")
                    throw new InvalidOperationException("Unscoped mutation schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("write-unscoped", "fixture.write_unscoped", "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mr2", "tool_calls");
                yield break;
            }

            throw new InvalidOperationException("Unscoped mutation must fail before another continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellableToolTransport : IAgentTransport
    {
        private int _continuations;
        public bool Cancelled { get; private set; }
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AgentTransportEvent.Tool(new(
                "search-cancel",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "long cancellable fixture read" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("ct1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _continuations++;
            if (_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.long_read")
                    throw new InvalidOperationException("Cancellable read schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("long-read", "fixture.long_read", "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("ct2", "tool_calls");
                yield break;
            }

            throw new InvalidOperationException("Cancelled tool must not continue.");
        }

        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
