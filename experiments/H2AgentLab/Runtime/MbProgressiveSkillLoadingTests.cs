using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbProgressiveSkillLoadingTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-52 test directory.");
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

        await Test("MB-52 real runtime loads metadata then SKILL.md then one explicit reference", async () =>
        {
            var skillsRoot = Path.Combine(root, "runtime-skills");
            WriteFixtureSkill(skillsRoot);
            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));

            var executor = new SkillRuntimeToolExecutor(catalog);
            var registry = SkillRegistry(executor);
            var transport = new ProgressiveSkillTransport();

            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry);

            var result = await runtime.RunAsync(
                Request("Use the installed skill guidance to check dynamic block parameters."),
                CancellationToken.None);

            Check(result.FinalText == "progressive-skill-ok",
                "Runtime did not finish after explicit progressive resource loading.");
            Check(transport.SawMetadataOnly,
                "Skill discovery exposed body/resource content instead of bounded metadata.");
            Check(transport.SawSkillWithoutResourceBodies,
                "Reading SKILL.md auto-loaded reference/script/asset contents.");
            Check(transport.SawExplicitReference,
                "Explicitly requested reference did not reach the model.");
            Check(result.LoadedToolSchemas.Contains(
                    SkillRuntimeToolExecutor.SearchToolName,
                    StringComparer.Ordinal)
                && result.LoadedToolSchemas.Contains(
                    SkillRuntimeToolExecutor.ReadToolName,
                    StringComparer.Ordinal),
                "Runtime did not load canonical skill tool schemas through deferred ToolRegistry discovery.");
        });

        await Test("MB-52 script and asset resources remain inert untrusted content", async () =>
        {
            var skillsRoot = Path.Combine(root, "inert-skills");
            WriteFixtureSkill(skillsRoot);
            var marker = Path.Combine(root, "SHOULD_NOT_EXIST.txt");

            var script = Path.Combine(
                skillsRoot,
                "cad-integrity",
                "scripts",
                "helper.py");
            File.WriteAllText(
                script,
                "from pathlib import Path\nPath(r'" + marker.Replace("\\", "\\\\") + "').write_text('executed')\n");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));
            var executor = new SkillRuntimeToolExecutor(catalog);

            var scriptResult = await executor.ExecuteAsync(
                Call(
                    "script-read",
                    SkillRuntimeToolExecutor.ReadToolName,
                    new { name = "cad-integrity", path = "scripts/helper.py" }),
                CancellationToken.None);
            var assetResult = await executor.ExecuteAsync(
                Call(
                    "asset-read",
                    SkillRuntimeToolExecutor.ReadToolName,
                    new { name = "cad-integrity", path = "assets/template.txt" }),
                CancellationToken.None);

            Check(!File.Exists(marker),
                "Reading a skill script executed it or produced a host side effect.");
            Check(scriptResult.Contains("untrusted text only", StringComparison.OrdinalIgnoreCase)
                && scriptResult.Contains("SHOULD_NOT_EXIST", StringComparison.Ordinal)
                && assetResult.Contains("data only", StringComparison.OrdinalIgnoreCase),
                "Script/asset reads were not explicitly represented as inert content.");
        });

        await Test("MB-52 selected skill and resource preserve hash version provenance", async () =>
        {
            var skillsRoot = Path.Combine(root, "hash-skills");
            WriteFixtureSkill(skillsRoot);
            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));
            var executor = new SkillRuntimeToolExecutor(catalog);

            var searchJson = await executor.ExecuteAsync(
                Call(
                    "search",
                    SkillRuntimeToolExecutor.SearchToolName,
                    new { query = "dynamic blocks parameters" }),
                CancellationToken.None);
            using var searchDoc = JsonDocument.Parse(searchJson);
            var metadata = searchDoc.RootElement.GetProperty("skills")[0];
            var discoveryHash = metadata.GetProperty("sha256").GetString()!;

            var skillJson = await executor.ExecuteAsync(
                Call(
                    "skill",
                    SkillRuntimeToolExecutor.ReadToolName,
                    new { name = "cad-integrity", path = "SKILL.md" }),
                CancellationToken.None);
            using var skillDoc = JsonDocument.Parse(skillJson);
            var skillHash = skillDoc.RootElement.GetProperty("sha256").GetString()!;
            var skillVersion = skillDoc.RootElement.GetProperty("version").GetString()!;

            var resourceJson = await executor.ExecuteAsync(
                Call(
                    "reference",
                    SkillRuntimeToolExecutor.ReadToolName,
                    new { name = "cad-integrity", path = "references/dynamic-block.md" }),
                CancellationToken.None);
            using var resourceDoc = JsonDocument.Parse(resourceJson);
            var resourceHash = resourceDoc.RootElement.GetProperty("sha256").GetString()!;
            var resourceVersion = resourceDoc.RootElement.GetProperty("version").GetString()!;

            Check(discoveryHash.Length == 64
                && discoveryHash == skillHash
                && skillVersion == "sha256:" + skillHash[..16],
                "Selected SKILL.md hash/version provenance changed between discovery and read.");
            Check(resourceHash.Length == 64
                && resourceVersion == "sha256:" + resourceHash[..16]
                && resourceHash != skillHash,
                "Explicit resource hash/version provenance was not preserved independently.");
        });

        await Test("MB-52 source guard removes mandatory resource directive parsing", () =>
        {
            var repo = FindRepoRoot();
            var continuation = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "MissingCapabilityContinuation.cs"));
            var runtimeTools = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Tools",
                    "SkillRuntimeTools.cs"));

            Check(!continuation.Contains("ParseResourceDirectives", StringComparison.Ordinal)
                && !continuation.Contains("StartsWith(prefix", StringComparison.Ordinal)
                && !continuation.Contains("skill-resource-loaded", StringComparison.Ordinal),
                "Historical continuation still parses/auto-loads custom resource directives.");
            Check(runtimeTools.Contains("ReadResource", StringComparison.Ordinal)
                && runtimeTools.Contains("Resources are inventory only and were not loaded", StringComparison.Ordinal)
                && runtimeTools.Contains("does not execute it", StringComparison.Ordinal),
                "Canonical runtime tool does not expose explicit bounded resource access semantics.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-progressive-skill-loading-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolRegistry SkillRegistry(SkillRuntimeToolExecutor executor)
    {
        var registry = new ToolRegistry();
        var definitions = JsonSerializer.SerializeToElement(global::H2AgentLab.AgentTools.Definitions);
        foreach (var schema in definitions.EnumerateArray())
        {
            var function = schema.GetProperty("function");
            var name = function.GetProperty("name").GetString()!;
            if (name is not (
                SkillRuntimeToolExecutor.SearchToolName
                or SkillRuntimeToolExecutor.ReadToolName))
                continue;

            registry.Register(new ToolDescriptor(
                name,
                new ToolNamespace(
                    "skills",
                    "Progressive skill discovery and explicit bounded guidance/resource loading."),
                function.GetProperty("description").GetString()!,
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                supportsParallel: true,
                schemaVersion: "v2",
                callableSchema: schema,
                executor: executor,
                provenance: new ToolProvenance(
                    "canonical-skill-catalog",
                    "1.0.0",
                    "local",
                    "1.0.0")));
        }

        return registry;
    }

    private static AgentRuntimeRequest Request(string userInput)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "skills:installed",
            null,
            null,
            ["do not execute skill scripts or mutate host state"],
            ["use only explicitly loaded guidance"],
            [],
            AgentTaskRiskClass.ReadOnly,
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
                CurrentState: "MB-52 progressive skill fixture"),
            PromptCacheKey: "mb52",
            MaxToolRounds: 8);
    }

    private static global::H2AgentLab.ToolCall Call(
        string id,
        string name,
        object arguments)
        => new(
            id,
            name,
            JsonSerializer.SerializeToElement(arguments));

    private static void WriteFixtureSkill(string root)
    {
        var skill = Path.Combine(root, "cad-integrity");
        Directory.CreateDirectory(skill);
        Directory.CreateDirectory(Path.Combine(skill, "references"));
        Directory.CreateDirectory(Path.Combine(skill, "scripts"));
        Directory.CreateDirectory(Path.Combine(skill, "assets"));

        File.WriteAllText(
            Path.Combine(skill, "SKILL.md"),
            "---\n"
            + "name: cad-integrity\n"
            + "description: Inspect dynamic blocks, parameters, actions and visibility-state consistency.\n"
            + "---\n"
            + "# CAD Integrity\n"
            + "Read references/dynamic-block.md only when detailed verification guidance is needed.\n");
        File.WriteAllText(
            Path.Combine(skill, "references", "dynamic-block.md"),
            "REFERENCE_SENTINEL_52: verify parameters and actions, then re-observe visibility states.");
        File.WriteAllText(
            Path.Combine(skill, "scripts", "helper.py"),
            "SCRIPT_SENTINEL_52 = 'untrusted helper source'");
        File.WriteAllText(
            Path.Combine(skill, "assets", "template.txt"),
            "ASSET_SENTINEL_52 static template");
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

    private sealed class ProgressiveSkillTransport : IAgentTransport
    {
        private int _continuations;

        public bool SawMetadataOnly { get; private set; }
        public bool SawSkillWithoutResourceBodies { get; private set; }
        public bool SawExplicitReference { get; private set; }

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Tools.Select(x => x.Name)
                .SequenceEqual(new[] { DeferredToolDiscovery.SearchToolName }))
                throw new InvalidOperationException(
                    "MB-52 initial runtime surface was not tool_search-only.");

            yield return AgentTransportEvent.Tool(new(
                "tool-search",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new
                {
                    query = "discover read selected skill guidance reference",
                    max_results = 2
                })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb52-1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _continuations++;

            if (_continuations == 1)
            {
                var loaded = (request.NewlyLoadedTools ?? [])
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.Ordinal);
                if (!loaded.SetEquals(
                    [
                        SkillRuntimeToolExecutor.SearchToolName,
                        SkillRuntimeToolExecutor.ReadToolName
                    ]))
                    throw new InvalidOperationException(
                        "Deferred discovery did not load both canonical skill tools.");

                yield return AgentTransportEvent.Tool(new(
                    "skill-search",
                    SkillRuntimeToolExecutor.SearchToolName,
                    JsonSerializer.Serialize(new
                    {
                        query = "dynamic blocks parameters actions"
                    })));
                yield return AgentTransportEvent.Complete("mb52-2", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                var result = request.ToolResults.Single();
                SawMetadataOnly = !result.IsError
                    && result.Content.Contains("cad-integrity", StringComparison.Ordinal)
                    && result.Content.Contains("dynamic blocks", StringComparison.OrdinalIgnoreCase)
                    && !result.Content.Contains("REFERENCE_SENTINEL_52", StringComparison.Ordinal)
                    && !result.Content.Contains("SCRIPT_SENTINEL_52", StringComparison.Ordinal)
                    && !result.Content.Contains("ASSET_SENTINEL_52", StringComparison.Ordinal);
                yield return AgentTransportEvent.Tool(new(
                    "skill-read",
                    SkillRuntimeToolExecutor.ReadToolName,
                    JsonSerializer.Serialize(new
                    {
                        name = "cad-integrity",
                        path = "SKILL.md"
                    })));
                yield return AgentTransportEvent.Complete("mb52-3", "tool_calls");
                yield break;
            }

            if (_continuations == 3)
            {
                var result = request.ToolResults.Single();
                SawSkillWithoutResourceBodies = !result.IsError
                    && result.Content.Contains("references/dynamic-block.md", StringComparison.Ordinal)
                    && result.Content.Contains("availableResources", StringComparison.Ordinal)
                    && !result.Content.Contains("REFERENCE_SENTINEL_52", StringComparison.Ordinal)
                    && !result.Content.Contains("SCRIPT_SENTINEL_52", StringComparison.Ordinal)
                    && !result.Content.Contains("ASSET_SENTINEL_52", StringComparison.Ordinal);
                yield return AgentTransportEvent.Tool(new(
                    "reference-read",
                    SkillRuntimeToolExecutor.ReadToolName,
                    JsonSerializer.Serialize(new
                    {
                        name = "cad-integrity",
                        path = "references/dynamic-block.md"
                    })));
                yield return AgentTransportEvent.Complete("mb52-4", "tool_calls");
                yield break;
            }

            if (_continuations == 4)
            {
                var result = request.ToolResults.Single();
                SawExplicitReference = !result.IsError
                    && result.Content.Contains("REFERENCE_SENTINEL_52", StringComparison.Ordinal)
                    && result.Content.Contains("sha256", StringComparison.Ordinal)
                    && !result.Content.Contains("SCRIPT_SENTINEL_52", StringComparison.Ordinal)
                    && !result.Content.Contains("ASSET_SENTINEL_52", StringComparison.Ordinal);
                yield return AgentTransportEvent.TextDeltaEvent("progressive-skill-ok");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("mb52-5", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected MB-52 continuation count.");
        }

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
