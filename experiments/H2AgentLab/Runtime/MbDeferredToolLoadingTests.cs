using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbDeferredToolLoadingTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-32 test directory.");
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

        await Test("MB-32 tool_search loads schema and selected tool executes in the same task without a new user turn", async () =>
        {
            var registry = new ToolRegistry();
            var executions = 0;
            registry.Register(Tool(
                "excel.read_formulas",
                "excel",
                "Read formulas from the active workbook range.",
                (call, ct) =>
                {
                    Interlocked.Increment(ref executions);
                    return ValueTask.FromResult(JsonSerializer.Serialize(new
                    {
                        formulas = new[] { "=A1+1" }
                    }));
                }));

            var transport = new SameTaskTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(Request(), CancellationToken.None);

            Check(result.FinalText == "formula-read-ok",
                "Runtime did not finish the same task after deferred schema loading.");
            Check(executions == 1,
                "Selected deferred tool did not execute exactly once.");
            Check(transport.StartCalls == 1
                && transport.ContinuationCalls == 2,
                "Deferred tool loading required an unexpected extra model start/user turn.");
            Check(result.LoadedToolSchemas.Contains("excel.read_formulas", StringComparer.Ordinal),
                "Loaded schema was not retained for the running task.");
        });

        await Test("MB-32 duplicate schema loads coalesce within one discovery session", () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "files.read_text",
                "files",
                "Read text from an approved workspace file.",
                (call, ct) => ValueTask.FromResult("{}")));

            var discovery = new DeferredToolDiscovery(registry);
            var first = discovery.SearchAndLoad("read approved workspace text", 8);
            var second = discovery.SearchAndLoad("read approved workspace text", 8);

            Check(first.Trace.NewlyLoadedNames.SequenceEqual(new[] { "files.read_text" }),
                "First matching search did not load the selected schema.");
            Check(first.CallableSchemas.Count == 1,
                "First matching search did not return exactly one detailed schema.");
            Check(second.Trace.SelectedNames.Contains("files.read_text", StringComparer.Ordinal),
                "Duplicate search stopped selecting the relevant tool.");
            Check(second.Trace.NewlyLoadedNames.Count == 0
                && second.CallableSchemas.Count == 0,
                "Duplicate schema load was not coalesced.");
            Check(discovery.LoadedSchemaNames.Count(x => x == "files.read_text") == 1,
                "Loaded schema set contains duplicate tool identities.");
            return Task.CompletedTask;
        });

        await Test("MB-32 registry version change invalidates lexical search corpus and discovers newly registered tool", () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "files.read",
                "files",
                "Read workspace file content.",
                (call, ct) => ValueTask.FromResult("{}")));

            var search = new ToolSearchIndex(registry);
            var discovery = new DeferredToolDiscovery(registry, search);

            _ = discovery.Search("workspace file content", 8);
            var versionBefore = registry.Version;
            var rebuildBefore = search.RebuildCount;

            registry.Register(Tool(
                "excel.read_formulas",
                "excel",
                "Read formulas from the active workbook range.",
                (call, ct) => ValueTask.FromResult("{}")));

            Check(registry.Version > versionBefore,
                "Registering a new tool did not change ToolRegistry.Version.");

            var batch = discovery.SearchAndLoad("active workbook formulas", 8);
            Check(search.CachedRegistryVersion == registry.Version
                && search.RebuildCount == rebuildBefore + 1,
                "ToolSearchIndex did not rebuild exactly once for the new registry version.");
            Check(batch.Trace.RegistryVersion == registry.Version
                && batch.Trace.SelectedNames.Contains("excel.read_formulas", StringComparer.Ordinal)
                && batch.Trace.NewlyLoadedNames.Contains("excel.read_formulas", StringComparer.Ordinal),
                "Deferred discovery did not select/load the newly registered tool after invalidation.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-deferred-tool-loading-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request()
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            "Read formulas.",
            "excel:active",
            null,
            null,
            ["do not mutate"],
            ["return formulas"],
            [],
            AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification: false));

        return new AgentRuntimeRequest(
            contract,
            "Read formulas from the active workbook.",
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "excel fixture"),
            PromptCacheKey: "mb32");
    }

    private static ToolDescriptor Tool(
        string name,
        string ns,
        string description,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => new(
            name,
            new ToolNamespace(ns, ns + " MB-32 fixture namespace."),
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
            executor: new DelegatingToolExecutor("mb32-fixture", execute),
            provenance: new ToolProvenance(
                "mb32-provider",
                "1.0.0",
                "fixture",
                "1.0.0"));

    private sealed class SameTaskTransport : IAgentTransport
    {
        public int StartCalls { get; private set; }
        public int ContinuationCalls { get; private set; }

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StartCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Tools.Select(x => x.Name).SequenceEqual(new[] { DeferredToolDiscovery.SearchToolName }))
                throw new InvalidOperationException("MB-32 initial surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "search-1",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "active workbook formulas"
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb32-1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ContinuationCalls++;
            cancellationToken.ThrowIfCancellationRequested();

            if (ContinuationCalls == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "excel.read_formulas")
                    throw new InvalidOperationException("Selected formula schema was not added to same-task continuation.");
                yield return AgentTransportEvent.Tool(new(
                    "formula-1",
                    "excel.read_formulas",
                    "{}"));
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mb32-2", "tool_calls");
                yield break;
            }

            if (ContinuationCalls == 2)
            {
                var result = request.ToolResults.Single();
                using var parsed = JsonDocument.Parse(result.Content);
                var formula = parsed.RootElement
                    .GetProperty("formulas")[0]
                    .GetString();
                if (result.IsError
                    || result.ToolName != "excel.read_formulas"
                    || formula != "=A1+1")
                    throw new InvalidOperationException(
                        $"Same-task deferred tool result was not preserved: name={result.ToolName}, error={result.IsError}, formula={formula}.");
                yield return AgentTransportEvent.TextDeltaEvent("formula-read-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mb32-3", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected MB-32 continuation count.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
