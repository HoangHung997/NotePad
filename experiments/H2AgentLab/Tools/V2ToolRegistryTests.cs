using System.Diagnostics;
using System.Text.Json;

namespace H2AgentLab.Tools;

public static class V2ToolRegistryTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 tool-registry test directory.");
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
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static (ToolRegistry Registry, IAgentToolExecutor Executor) Registry()
        {
            var executor = new DelegatingToolExecutor(
                "tool-regression-fixture",
                (call, ct) => ValueTask.FromResult("ok:" + call.Name));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);
            return (registry, executor);
        }

        await Test("Relevant callable names rank first for representative intents", () =>
        {
            var (registry, _) = Registry();
            var index = new ToolSearchIndex(registry);
            foreach (var expected in new[]
            {
                "read_file",
                "write_text",
                "run_python",
                "click_control",
                "word_paragraphs",
                "list_skills"
            })
            {
                var results = index.Search(expected, 3);
                Check(results.Count > 0 && results[0].Descriptor.Name == expected,
                    $"Expected {expected} first, got {(results.Count == 0 ? "<none>" : results[0].Descriptor.Name)}.");
            }

            var desktop = index.Search("inspect selected window controls", 3);
            Check(desktop.Any(x => x.Descriptor.Namespace.Name == "desktop"),
                "Natural-language desktop query did not retrieve desktop namespace.");
            return Task.CompletedTask;
        });

        await Test("Search cache is stable and lexical query loop remains lightweight", () =>
        {
            var (registry, _) = Registry();
            var index = new ToolSearchIndex(registry);
            _ = index.Search("workspace file");
            var rebuilds = index.RebuildCount;

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 1_000; i++)
            {
                _ = index.Search(i % 3 switch
                {
                    0 => "read workbook file",
                    1 => "inspect window control",
                    _ => "python artifact output"
                }, 5);
            }
            clock.Stop();

            Check(index.RebuildCount == rebuilds,
                "Repeated queries rebuilt unchanged registry search corpus.");
            Check(clock.Elapsed < TimeSpan.FromSeconds(10),
                $"1,000 lexical searches exceeded lightweight regression budget: {clock.Elapsed}.");
            return Task.CompletedTask;
        });

        await Test("Deferred exposure keeps detailed schema payload bounded", () =>
        {
            var (registry, _) = Registry();
            var discovery = new DeferredToolDiscovery(registry);
            var initial = discovery.BuildInitialExposure();
            var loaded = discovery.SearchAndLoad("read excel workbook file", 3);

            var initialChars = initial.CallableSchemas.Sum(x => x.GetRawText().Length);
            var loadedChars = loaded.CallableSchemas.Sum(x => x.GetRawText().Length);
            var fullChars = registry.Tools.Sum(x => x.CallableSchema.GetRawText().Length);
            var approximateTokens = (initialChars + loadedChars + 3) / 4;

            Check(initial.CallableSchemas.Count <= 2,
                "Initial exposure exceeded tool_search + stable-core schema count.");
            Check(loaded.CallableSchemas.Count <= 3,
                "Deferred search loaded more schemas than requested bound.");
            Check(initialChars + loadedChars < fullChars,
                "Deferred model request is not smaller than eager full-registry schema payload.");
            Check(approximateTokens <= 6_000,
                $"Deferred schema token estimate exceeded 6k budget: {approximateTokens}.");
            return Task.CompletedTask;
        });

        await Test("Scheduler keeps overlapping mutations serialized", async () =>
        {
            var active = 0;
            var maxActive = 0;
            var executor = new DelegatingToolExecutor(
                "serialization-fixture",
                async (call, ct) =>
                {
                    var now = Interlocked.Increment(ref active);
                    int observed;
                    do
                    {
                        observed = Volatile.Read(ref maxActive);
                        if (observed >= now) break;
                    }
                    while (Interlocked.CompareExchange(ref maxActive, now, observed) != observed);

                    await Task.Delay(40, ct);
                    Interlocked.Decrement(ref active);
                    return call.Id;
                });

            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = "mutate_fixture",
                    description = "Mutation serialization fixture.",
                    parameters = new { type = "object" }
                }
            });
            var descriptor = new ToolDescriptor(
                "mutate_fixture",
                new ToolNamespace("fixture", "Fixture namespace."),
                "Mutation serialization fixture.",
                AgentToolRisk.Medium,
                AgentToolAccess.Mutating,
                supportsParallel: true,
                "v1",
                schema,
                executor);
            var args = JsonSerializer.SerializeToElement(new { });

            using var scheduler = new ToolExecutionScheduler();
            var results = await scheduler.ExecuteBatchAsync(
                [
                    new ToolExecutionRequest(
                        descriptor,
                        new global::H2AgentLab.ToolCall("a", "mutate_fixture", args),
                        "resource:/same"),
                    new ToolExecutionRequest(
                        descriptor,
                        new global::H2AgentLab.ToolCall("b", "mutate_fixture", args),
                        "resource:/same")
                ],
                CancellationToken.None);

            Check(maxActive == 1, "Same-resource mutations overlapped.");
            Check(results.Select(x => x.Output).SequenceEqual(new[] { "a", "b" }),
                "Mutation scheduler did not preserve input result order.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-tool-registry-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }
}
