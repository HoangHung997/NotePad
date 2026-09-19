using System.Text.Json;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public static class MbTaskCapabilityPinningTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-75 test directory.");
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
            if (!condition)
                throw new InvalidOperationException(message);
        }

        await Test("MB-75 snapshot contains only used or selected capabilities plus model and policy identity", () =>
        {
            var fixture = Fixture();
            var snapshot = TaskCapabilitySnapshotBuilder.CaptureUsed(
                Guid.NewGuid(),
                1,
                fixture.Registry,
                fixture.Providers,
                fixture.Plugins,
                fixture.Selection,
                "task-used capability capture");
            var evidence = TaskCapabilitySnapshotBuilder.Evidence(snapshot);

            Check(snapshot.Tools.Select(x => x.ToolName)
                    .SequenceEqual(["used.read"]),
                "Snapshot copied unrelated registry tools.");
            Check(snapshot.Providers.Select(x => x.ProviderId)
                    .SequenceEqual(["used.provider"]),
                "Snapshot copied unrelated providers.");
            Check(snapshot.Plugins.Select(x => x.PluginId)
                    .SequenceEqual(["used.plugin"]),
                "Snapshot copied unrelated plugins.");
            Check(snapshot.Skills.Select(x => x.Identity.SkillId)
                    .SequenceEqual(["used-skill"]),
                "Snapshot copied unrelated skills.");
            Check(evidence.ModelIdentity
                    == "openai-responses@v1;model=gpt-fixture"
                && evidence.PolicyVersions
                    .SequenceEqual(["permission-policy@7"])
                && evidence.ToolVersions.Single()
                    .Contains(
                        "used.read@tool=2.0.0;schema=v3;provider=plugin.used.plugin@1.4.0",
                        StringComparison.Ordinal)
                && evidence.ProviderVersions
                    .SequenceEqual(["used.provider@4.2.0"])
                && evidence.PluginVersions
                    .SequenceEqual(["used.plugin@1.4.0"])
                && evidence.SkillHashes.Single()
                    .Contains(
                        fixture.UsedSkill.Identity.Sha256,
                        StringComparison.Ordinal),
                "Usage evidence omitted required model/provider/tool/plugin/skill/policy identity.");
            return Task.CompletedTask;
        });

        await Test("MB-75 unrelated plugin and registry changes do not invalidate a task that never used them", () =>
        {
            var fixture = Fixture();
            var snapshot = TaskCapabilitySnapshotBuilder.CaptureUsed(
                Guid.NewGuid(),
                1,
                fixture.Registry,
                fixture.Providers,
                fixture.Plugins,
                fixture.Selection,
                "task-start");
            var guard = new TaskCapabilityPinGuard(snapshot);

            fixture.Registry.Register(Tool(
                "new.unrelated",
                "Unrelated newly installed tool.",
                "plugin.new.plugin",
                "9.0.0",
                "1.0.0",
                "v1"));

            var providers = fixture.Providers
                .Append(new ProviderProvenance(
                    "new.provider",
                    "9.0.0",
                    "new-server",
                    "mcp"))
                .ToArray();
            var plugins = fixture.Plugins
                .Append((
                    Manifest(
                        "new.plugin",
                        "9.0.0",
                        ["new.unrelated"],
                        [],
                        ["new.provider"]),
                    "new-root"))
                .ToArray();
            var skills = fixture.Skills
                .Append(Skill(
                    "new-skill",
                    "new.plugin",
                    "9.0.0",
                    new string('c', 64)))
                .ToArray();
            var policies = fixture.Selection.Policies
                .Append(new TaskPolicyPin(
                    "unrelated-policy",
                    "1"))
                .ToArray();

            guard.EnsureStillPinned(
                fixture.Registry,
                providers,
                plugins,
                skills,
                fixture.Selection.Model,
                policies);

            Check(fixture.Registry.Version != snapshot.RegistryVersion,
                "Fixture did not actually change ToolRegistry.Version.");
            Check(guard.Current.Revision == 1,
                "Unrelated extension install forced an unnecessary snapshot revision.");
            return Task.CompletedTask;
        });

        await Test("MB-75 changing a used tool or used plugin is rejected until an explicit usage revision", () =>
        {
            var fixture = Fixture();
            var snapshot = TaskCapabilitySnapshotBuilder.CaptureUsed(
                Guid.NewGuid(),
                1,
                fixture.Registry,
                fixture.Providers,
                fixture.Plugins,
                fixture.Selection,
                "task-start");
            var guard = new TaskCapabilityPinGuard(snapshot);

            fixture.Registry.UnregisterWhere(x =>
                x.Name == "used.read");
            fixture.Registry.Register(Tool(
                "used.read",
                "Used tool changed version.",
                "plugin.used.plugin",
                "1.5.0",
                "3.0.0",
                "v4"));

            try
            {
                guard.EnsureStillPinned(
                    fixture.Registry,
                    fixture.Providers,
                    fixture.Plugins,
                    fixture.Skills,
                    fixture.Selection.Model,
                    fixture.Selection.Policies);
                throw new InvalidOperationException(
                    "Changed used tool was accepted without revision.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains(
                    "used.read",
                    StringComparison.Ordinal))
            {
            }

            var pluginV2 = Manifest(
                "used.plugin",
                "1.5.0",
                ["used.read"],
                ["used-skill"],
                ["used.provider"]);
            var skillV2 = Skill(
                "used-skill",
                "used.plugin",
                "1.5.0",
                new string('d', 64));
            var providerV2 = new ProviderProvenance(
                "used.provider",
                "4.3.0",
                "used-server",
                "mcp");

            var revision = TaskCapabilitySnapshotBuilder.ReviseUsed(
                snapshot,
                fixture.Registry,
                [providerV2],
                [(pluginV2, "used-root-v2")],
                fixture.Selection with
                {
                    SelectedSkills = [skillV2]
                },
                "used extension explicitly changed");
            guard.ReplaceWithExplicitRevision(revision);

            guard.EnsureStillPinned(
                fixture.Registry,
                [providerV2],
                [(pluginV2, "used-root-v2")],
                [skillV2],
                fixture.Selection.Model,
                fixture.Selection.Policies);

            var evidence = TaskCapabilitySnapshotBuilder.Evidence(
                guard.Current);
            Check(guard.Current.Revision == 2
                && evidence.ToolVersions.Single()
                    .Contains("tool=3.0.0", StringComparison.Ordinal)
                && evidence.PluginVersions
                    .SequenceEqual(["used.plugin@1.5.0"])
                && evidence.ProviderVersions
                    .SequenceEqual(["used.provider@4.3.0"])
                && evidence.SkillHashes.Single()
                    .Contains(
                        skillV2.Identity.Sha256,
                        StringComparison.Ordinal),
                "Explicit used-capability revision did not update exact evidence.");
            return Task.CompletedTask;
        });

        await Test("MB-75 source guard never uses whole-registry version as a correctness pin", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "TaskCapabilitySnapshot.cs"));

            Check(source.Contains(
                    "TaskCapabilitySelection",
                    StringComparison.Ordinal)
                && source.Contains(
                    "CaptureUsed",
                    StringComparison.Ordinal)
                && source.Contains(
                    "UsedToolNames",
                    StringComparison.Ordinal)
                && !source.Contains(
                    "if (registry.Version != _current.RegistryVersion)",
                    StringComparison.Ordinal),
                "Task capability pinning still depends on whole-machine registry state.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-task-capability-pinning-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static FixtureState Fixture()
    {
        var registry = new ToolRegistry();
        registry.Register(Tool(
            "used.read",
            "Read data used by this task.",
            "plugin.used.plugin",
            "1.4.0",
            "2.0.0",
            "v3"));
        registry.Register(Tool(
            "unused.read",
            "Installed but unused tool.",
            "plugin.unused.plugin",
            "5.0.0",
            "1.0.0",
            "v1"));

        var providers = new[]
        {
            new ProviderProvenance(
                "used.provider",
                "4.2.0",
                "used-server",
                "mcp"),
            new ProviderProvenance(
                "unused.provider",
                "8.0.0",
                "unused-server",
                "native")
        };

        var usedPlugin = Manifest(
            "used.plugin",
            "1.4.0",
            ["used.read"],
            ["used-skill"],
            ["used.provider"]);
        var unusedPlugin = Manifest(
            "unused.plugin",
            "5.0.0",
            ["unused.read"],
            ["unused-skill"],
            ["unused.provider"]);
        var plugins = new[]
        {
            (usedPlugin, "used-root"),
            (unusedPlugin, "unused-root")
        };

        var usedSkill = Skill(
            "used-skill",
            "used.plugin",
            "1.4.0",
            new string('a', 64));
        var unusedSkill = Skill(
            "unused-skill",
            "unused.plugin",
            "5.0.0",
            new string('b', 64));

        var selection = new TaskCapabilitySelection(
            new TaskModelPin(
                "openai-responses",
                "v1",
                "gpt-fixture"),
            ["used.read"],
            ["used.provider"],
            ["used.plugin"],
            [usedSkill],
            [new TaskPolicyPin(
                "permission-policy",
                "7")]);

        return new FixtureState(
            registry,
            providers,
            plugins,
            [usedSkill, unusedSkill],
            usedSkill,
            selection);
    }

    private static ToolDescriptor Tool(
        string name,
        string description,
        string providerId,
        string providerVersion,
        string toolVersion,
        string schemaVersion)
        => new(
            name,
            new ToolNamespace(
                "fixture",
                "MB-75 fixture tools."),
            description,
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion,
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
                        properties = new { }
                    }
                }
            }),
            new DelegatingToolExecutor(
                "mb75-fixture",
                (call, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult("{}");
                }),
            new ToolProvenance(
                providerId,
                providerVersion,
                "fixture-server",
                toolVersion),
            new ToolResourceScope(
                "fixture",
                "fixture"));

    private static H2PluginManifest Manifest(
        string id,
        string version,
        IReadOnlyList<string> tools,
        IReadOnlyList<string> skills,
        IReadOnlyList<string> providers)
        => new(
            id,
            id,
            version,
            "2.0.0",
            "fixture.publisher",
            "sha256:" + new string('e', 64),
            tools,
            skills,
            providers,
            ["workspace.read"]);

    private static SkillSummary Skill(
        string id,
        string pluginId,
        string pluginVersion,
        string sha)
        => new(
            new SkillIdentity(
                SkillSourceKind.Plugin,
                "plugins",
                pluginId,
                pluginVersion,
                id,
                sha),
            id,
            "Fixture selected skill " + id + ".",
            "installed",
            "plugin");

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(
            Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "AGENTS.md"))
                && Directory.Exists(
                    Path.Combine(
                        current.FullName,
                        "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }

    private sealed record FixtureState(
        ToolRegistry Registry,
        IReadOnlyList<ProviderProvenance> Providers,
        IReadOnlyList<(H2PluginManifest Manifest, string VersionRoot)> Plugins,
        IReadOnlyList<SkillSummary> Skills,
        SkillSummary UsedSkill,
        TaskCapabilitySelection Selection);
}
