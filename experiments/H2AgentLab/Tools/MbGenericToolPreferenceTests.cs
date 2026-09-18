using System.Text.Json;

namespace H2AgentLab.Tools;

public static class MbGenericToolPreferenceTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-61 test directory.");
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

        await Test("MB-61 generic metadata orders structured accessibility visual then escape hatch", () =>
        {
            var candidates = new[]
            {
                Result(
                    Tool(
                        "model.visual.inspect",
                        "Inspect model elements and parameters.",
                        ToolInteractionFidelity.Visual),
                    100),
                Result(
                    Tool(
                        "model.escape.inspect",
                        "Inspect model elements and parameters with a generic escape hatch.",
                        ToolInteractionFidelity.EscapeHatch,
                        explicitOnly: true,
                        explicitTerms: ["fallback"]),
                    200),
                Result(
                    Tool(
                        "model.access.inspect",
                        "Inspect model elements and parameters through accessibility.",
                        ToolInteractionFidelity.Accessibility),
                    50),
                Result(
                    Tool(
                        "model.structured.inspect",
                        "Inspect model elements and parameters through a typed structured interface.",
                        ToolInteractionFidelity.Structured),
                    1)
            };

            var ranked = DocumentToolPreference.Apply(
                "inspect model elements parameters fallback",
                candidates,
                10);

            Check(ranked.Select(x => x.Descriptor.Name).SequenceEqual(
                new[]
                {
                    "model.structured.inspect",
                    "model.access.inspect",
                    "model.visual.inspect",
                    "model.escape.inspect"
                }),
                "Generic preference did not preserve structured > accessibility > visual > escape-hatch order.");
            return Task.CompletedTask;
        });

        await Test("MB-61 explicit-only behavior is provider metadata not hard-coded application knowledge", () =>
        {
            var normal = new[]
            {
                Result(
                    Tool(
                        "model.structured.inspect",
                        "Inspect model geometry through typed API.",
                        ToolInteractionFidelity.Structured),
                    5),
                Result(
                    Tool(
                        "model.custom.escape",
                        "Run a custom unsupported transform.",
                        ToolInteractionFidelity.EscapeHatch,
                        explicitOnly: true,
                        explicitTerms: ["custom", "script"]),
                    6)
            };

            var ordinary = DocumentToolPreference.Apply(
                "inspect model geometry",
                normal,
                10);
            Check(ordinary.Count == 1
                && ordinary[0].Descriptor.Name == "model.structured.inspect",
                "Explicit-only escape hatch leaked without provider-declared request term.");

            var explicitRequest = DocumentToolPreference.Apply(
                "use custom transform for model geometry",
                normal,
                10);
            Check(explicitRequest.Any(x =>
                    x.Descriptor.Name == "model.custom.escape")
                && explicitRequest[0].Descriptor.Name == "model.structured.inspect",
                "Provider-declared explicit request did not expose escape hatch while retaining structured preference.");
            return Task.CompletedTask;
        });

        await Test("MB-61 interaction adapter chooser is application-neutral and honors explicit adapter request", () =>
        {
            var adapters = new[]
            {
                new InteractionAdapterCandidate(
                    "model.pixel",
                    "model-active",
                    ToolInteractionFidelity.Visual),
                new InteractionAdapterCandidate(
                    "model.uia",
                    "model-active",
                    ToolInteractionFidelity.Accessibility),
                new InteractionAdapterCandidate(
                    "model.typed",
                    "model-active",
                    ToolInteractionFidelity.Structured),
                new InteractionAdapterCandidate(
                    "model.escape",
                    "model-active",
                    ToolInteractionFidelity.EscapeHatch,
                    explicitRequestOnly: true,
                    explicitRequestTerms: ["fallback"])
            };

            var preferred = InteractionAdapterPreference.Choose(
                "inspect active model",
                adapters);
            Check(preferred?.AdapterId == "model.typed",
                "Generic adapter preference did not select structured typed interface first.");

            var exact = InteractionAdapterPreference.Choose(
                "model.pixel",
                adapters);
            Check(exact?.AdapterId == "model.pixel",
                "Explicit adapter identity did not override generic fidelity preference.");

            var fallback = InteractionAdapterPreference.Choose(
                "inspect active model fallback",
                adapters);
            Check(fallback?.AdapterId == "model.typed",
                "Explicitly available escape hatch displaced a better structured adapter.");
            return Task.CompletedTask;
        });

        await Test("MB-61 new Revit-like structured provider ranks correctly without core edits", () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "revit.typed.inspect",
                "Inspect Revit-like model elements, parameters and constraints.",
                ToolInteractionFidelity.Structured,
                family: "model-active"));
            registry.Register(Tool(
                "revit.uia.inspect",
                "Inspect Revit-like model elements, parameters and constraints through accessibility UIA.",
                ToolInteractionFidelity.Accessibility,
                family: "model-active"));
            registry.Register(Tool(
                "revit.pixel.inspect",
                "Inspect Revit-like model elements, parameters and constraints from screenshot pixels.",
                ToolInteractionFidelity.Visual,
                family: "model-active"));

            var discovery = new DeferredToolDiscovery(registry);
            var results = discovery.Search(
                "inspect model elements parameters constraints",
                3);

            Check(results.Count == 3
                && results[0].Descriptor.Name == "revit.typed.inspect"
                && results[1].Descriptor.Name == "revit.uia.inspect"
                && results[2].Descriptor.Name == "revit.pixel.inspect",
                "A newly registered structured modeling provider required application-specific core ranking.");

            var legacy = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(
                legacy,
                new DelegatingToolExecutor(
                    "mb61-legacy-fixture",
                    (call, ct) => ValueTask.FromResult("{}")));
            Check(legacy.TryGet("run_python", out var escape)
                    && escape.Preference?.InteractionFidelity == ToolInteractionFidelity.EscapeHatch
                    && escape.Preference.ExplicitRequestOnly
                && legacy.TryGet("word_paragraphs", out var structured)
                    && structured.Preference?.InteractionFidelity == ToolInteractionFidelity.Structured
                && legacy.TryGet("inspect_window", out var accessibility)
                    && accessibility.Preference?.InteractionFidelity == ToolInteractionFidelity.Accessibility,
                "Compatibility providers did not declare generic interaction preference metadata.");

            var repo = FindRepoRoot();
            foreach (var relative in new[]
            {
                Path.Combine("Tools", "DocumentToolPreference.cs"),
                Path.Combine("Tools", "InteractionAdapterPreference.cs"),
                Path.Combine("Tools", "DeferredToolDiscovery.cs")
            })
            {
                var source = File.ReadAllText(
                    Path.Combine(
                        repo,
                        "experiments",
                        "H2AgentLab",
                        relative))
                    .ToLowerInvariant();
                foreach (var forbidden in new[]
                {
                    "word",
                    "excel",
                    "office",
                    "python",
                    "run_python",
                    "autocad",
                    "revit"
                })
                    Check(!source.Contains(forbidden, StringComparison.Ordinal),
                        $"Generic preference core still contains application-specific token '{forbidden}' in {relative}.");
            }

            var runtime = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Runtime",
                    "AgentRuntime.cs"));
            Check(!runtime.Contains("revit", StringComparison.OrdinalIgnoreCase),
                "AgentRuntime gained Revit-specific knowledge for the MB-61 acceptance provider.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-generic-tool-preference-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolSearchResult Result(
        ToolDescriptor descriptor,
        double score)
        => new(
            descriptor,
            score,
            ["inspect", "model"]);

    private static ToolDescriptor Tool(
        string name,
        string description,
        ToolInteractionFidelity fidelity,
        bool explicitOnly = false,
        IReadOnlyList<string>? explicitTerms = null,
        string family = "model-active")
        => new(
            name,
            new ToolNamespace(
                "model",
                "Generic model interaction provider."),
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
            executor: new DelegatingToolExecutor(
                "mb61-fixture",
                (call, ct) => ValueTask.FromResult("{}")),
            provenance: new ToolProvenance(
                "mb61-provider",
                "1.0.0",
                "fixture",
                "1.0.0"),
            preference: new ToolPreferenceMetadata(
                family,
                fidelity,
                explicitOnly,
                explicitTerms));

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
