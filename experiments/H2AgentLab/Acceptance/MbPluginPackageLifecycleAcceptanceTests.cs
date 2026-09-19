using System.IO.Compression;
using System.Text;
using System.Text.Json;
using H2AgentLab.Plugins;
using H2AgentLab.Tools;

namespace H2AgentLab.Acceptance;

public static class MbPluginPackageLifecycleAcceptanceTests
{
    private sealed record AcceptanceCase(
        string Id,
        string Requirement,
        string Evidence,
        bool Passed,
        string? Failure);

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-115 plugin lifecycle acceptance directory.");
        Directory.CreateDirectory(root);

        var cases = new List<AcceptanceCase>();

        async Task Case(
            string id,
            string requirement,
            string evidence,
            Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                cases.Add(new AcceptanceCase(id, requirement, evidence, true, null));
            }
            catch (Exception ex)
            {
                cases.Add(new AcceptanceCase(
                    id,
                    requirement,
                    evidence,
                    false,
                    ex.GetType().Name + ": " + ex.Message));
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Case(
            "PLUGIN-INSTALL-HOT",
            "install stages, self-tests, activates and hot-registers exact tool provenance",
            "PluginManager install/activation + ToolRegistry execution",
            async () =>
            {
                var state = Path.Combine(root, "install-state");
                Directory.CreateDirectory(state);
                var registry = new ToolRegistry();
                var manager = new PluginManager(
                    state,
                    registry,
                    new FixturePluginResolver());

                var v1 = BuildPluginPackage(
                    root,
                    "1.0.0",
                    "Install fixture skill.",
                    ["workspace.read"],
                    selfTestOk: true);

                var result = manager.InstallFromArchive(
                    v1.Path,
                    v1.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);

                Check(result.Activated
                    && result.RegisteredTools.SequenceEqual(["plugin.echo"])
                    && Directory.Exists(result.VersionRoot)
                    && File.Exists(Path.Combine(
                        state,
                        "plugins",
                        PluginId,
                        "active.json")),
                    "Plugin install was not staged/versioned/activated atomically.");

                var descriptor = Descriptor(registry);
                Check(descriptor.Provenance?.ProviderId == "plugin." + PluginId
                    && descriptor.Provenance.ProviderVersion == "1.0.0"
                    && descriptor.Provenance.ToolVersion == "1.0.0",
                    "Hot-registered descriptor lost exact plugin/tool provenance.");

                var output = await descriptor.Executor.ExecuteAsync(
                    Call("install"),
                    CancellationToken.None).ConfigureAwait(false);
                Check(output.Contains("\"version\":\"1.0.0\"", StringComparison.Ordinal),
                    "Hot-registered plugin tool did not execute the active version.");
            }).ConfigureAwait(false);

        await Case(
            "PLUGIN-UPDATE-SAFE-BOUNDARY-PERMISSION",
            "updates hot-register only at safe boundary and broader permissions require fresh approval",
            "EnterToolCall activation boundary + permission delta enforcement",
            async () =>
            {
                var state = Path.Combine(root, "update-state");
                Directory.CreateDirectory(state);
                var registry = new ToolRegistry();
                var manager = new PluginManager(
                    state,
                    registry,
                    new FixturePluginResolver());

                var v1 = BuildPluginPackage(
                    root,
                    "1.0.0",
                    "Initial version.",
                    ["workspace.read"],
                    selfTestOk: true);
                var v2 = BuildPluginPackage(
                    root,
                    "2.0.0",
                    "Safe update.",
                    ["workspace.read"],
                    selfTestOk: true);

                _ = manager.InstallFromArchive(
                    v1.Path,
                    v1.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);

                using (manager.EnterToolCall())
                {
                    try
                    {
                        _ = manager.InstallFromArchive(
                            v2.Path,
                            v2.CatalogEntry,
                            DeveloperPolicy(),
                            userApproved: true);
                        throw new InvalidOperationException(
                            "Plugin update activated during in-flight tool execution.");
                    }
                    catch (InvalidOperationException ex) when (
                        ex.Message.Contains("in flight", StringComparison.OrdinalIgnoreCase))
                    {
                    }

                    Check(manager.GetActive(PluginId)?.Manifest.Version == "1.0.0"
                        && Descriptor(registry).Provenance?.ProviderVersion == "1.0.0",
                        "Blocked update changed active/registered version.");
                }

                _ = manager.InstallFromArchive(
                    v2.Path,
                    v2.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);
                Check(manager.GetActive(PluginId)?.Manifest.Version == "2.0.0"
                    && Descriptor(registry).Provenance?.ProviderVersion == "2.0.0",
                    "Safe-boundary update did not activate/register v2.");

                var broader = BuildPluginPackage(
                    root,
                    "3.0.0",
                    "Broader permission update.",
                    ["workspace.read", "workspace.write"],
                    selfTestOk: true,
                    trustState: PluginTrustState.TrustedOfficial);
                var trustedPolicy = new PluginInstallPolicy(
                    PluginInstallMode.TrustedOfficialOnly,
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        Publisher
                    },
                    AllowNativeHelpers: false,
                    AllowLifecycleHooks: false);

                try
                {
                    _ = manager.InstallFromArchive(
                        broader.Path,
                        broader.CatalogEntry,
                        trustedPolicy,
                        userApproved: false);
                    throw new InvalidOperationException(
                        "Broader plugin permissions inherited previous approval.");
                }
                catch (UnauthorizedAccessException ex) when (
                    ex.Message.Contains("broader permissions", StringComparison.OrdinalIgnoreCase))
                {
                }

                Check(manager.GetActive(PluginId)?.Manifest.Version == "2.0.0"
                    && Descriptor(registry).Provenance?.ProviderVersion == "2.0.0",
                    "Denied permission-delta update changed active/registered version.");
            }).ConfigureAwait(false);

        await Case(
            "PLUGIN-INTEGRITY-SELFTEST",
            "archive hash mismatch and declarative self-test failure fail closed without disturbing active version",
            "archive SHA-256 + staged declarative self-test",
            () =>
            {
                var state = Path.Combine(root, "integrity-state");
                Directory.CreateDirectory(state);
                var registry = new ToolRegistry();
                var manager = new PluginManager(
                    state,
                    registry,
                    new FixturePluginResolver());

                var v1 = BuildPluginPackage(
                    root,
                    "1.0.0",
                    "Working baseline.",
                    ["workspace.read"],
                    selfTestOk: true);
                _ = manager.InstallFromArchive(
                    v1.Path,
                    v1.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);

                var hashFail = BuildPluginPackage(
                    root,
                    "2.0.0",
                    "Hash failure candidate.",
                    ["workspace.read"],
                    selfTestOk: true);
                try
                {
                    _ = manager.InstallFromArchive(
                        hashFail.Path,
                        hashFail.CatalogEntry with
                        {
                            ArchiveSha256 = "sha256:" + new string('0', 64)
                        },
                        DeveloperPolicy(),
                        userApproved: true);
                    throw new InvalidOperationException(
                        "Archive hash mismatch was accepted.");
                }
                catch (InvalidDataException ex) when (
                    ex.Message.Contains("archive hash", StringComparison.OrdinalIgnoreCase))
                {
                }

                var selfTestFail = BuildPluginPackage(
                    root,
                    "2.1.0",
                    "Self-test failure candidate.",
                    ["workspace.read"],
                    selfTestOk: false);
                try
                {
                    _ = manager.InstallFromArchive(
                        selfTestFail.Path,
                        selfTestFail.CatalogEntry,
                        DeveloperPolicy(),
                        userApproved: true);
                    throw new InvalidOperationException(
                        "Plugin with failed self-test activated.");
                }
                catch (InvalidDataException ex) when (
                    ex.Message.Contains("self-test", StringComparison.OrdinalIgnoreCase))
                {
                }

                Check(manager.GetActive(PluginId)?.Manifest.Version == "1.0.0"
                    && Descriptor(registry).Provenance?.ProviderVersion == "1.0.0"
                    && !Directory.Exists(Path.Combine(
                        state,
                        "plugins",
                        PluginId,
                        "2.1.0")),
                    "Failed integrity/self-test update disturbed working active version or left final install directory.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        await Case(
            "PLUGIN-ROLLBACK",
            "rollback atomically reactivates previous version and replaces ToolRegistry descriptors",
            "PluginManager.Rollback + registry provenance/execution",
            async () =>
            {
                var state = Path.Combine(root, "rollback-state");
                Directory.CreateDirectory(state);
                var registry = new ToolRegistry();
                var manager = new PluginManager(
                    state,
                    registry,
                    new FixturePluginResolver());

                var v1 = BuildPluginPackage(
                    root,
                    "1.0.0",
                    "Rollback baseline.",
                    ["workspace.read"],
                    selfTestOk: true);
                var v2 = BuildPluginPackage(
                    root,
                    "2.0.0",
                    "Rollback candidate.",
                    ["workspace.read"],
                    selfTestOk: true);

                _ = manager.InstallFromArchive(
                    v1.Path,
                    v1.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);
                _ = manager.InstallFromArchive(
                    v2.Path,
                    v2.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);

                var rolled = manager.Rollback(PluginId);
                var descriptor = Descriptor(registry);
                var output = await descriptor.Executor.ExecuteAsync(
                    Call("rollback"),
                    CancellationToken.None).ConfigureAwait(false);

                Check(rolled.Version == "1.0.0"
                    && manager.GetActive(PluginId)?.Manifest.Version == "1.0.0"
                    && descriptor.Provenance?.ProviderVersion == "1.0.0"
                    && output.Contains("\"version\":\"1.0.0\"", StringComparison.Ordinal),
                    "Rollback did not restore previous manifest and runtime descriptor/executor.");
            }).ConfigureAwait(false);

        await Case(
            "PLUGIN-QUARANTINE",
            "quarantining active bad version preserves diagnostics and rolls runtime back to previous version",
            "quarantine marker + automatic rollback + registry provenance",
            async () =>
            {
                var state = Path.Combine(root, "quarantine-state");
                Directory.CreateDirectory(state);
                var registry = new ToolRegistry();
                var manager = new PluginManager(
                    state,
                    registry,
                    new FixturePluginResolver());

                var v1 = BuildPluginPackage(
                    root,
                    "1.0.0",
                    "Quarantine baseline.",
                    ["workspace.read"],
                    selfTestOk: true);
                var v2 = BuildPluginPackage(
                    root,
                    "2.0.0",
                    "Quarantine bad version.",
                    ["workspace.read"],
                    selfTestOk: true);

                _ = manager.InstallFromArchive(
                    v1.Path,
                    v1.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);
                _ = manager.InstallFromArchive(
                    v2.Path,
                    v2.CatalogEntry,
                    DeveloperPolicy(),
                    userApproved: true);

                manager.Quarantine(
                    PluginId,
                    "2.0.0",
                    "acceptance regression");

                var marker = Path.Combine(
                    state,
                    "plugins",
                    PluginId,
                    "2.0.0",
                    "quarantine.json");
                Check(File.Exists(marker),
                    "Quarantine diagnostics marker was not persisted.");
                var quarantineJson = File.ReadAllText(marker);
                Check(quarantineJson.Contains("acceptance regression", StringComparison.Ordinal)
                    && manager.GetActive(PluginId)?.Manifest.Version == "1.0.0"
                    && Descriptor(registry).Provenance?.ProviderVersion == "1.0.0",
                    "Quarantine did not preserve reason and roll runtime back to previous version.");
            }).ConfigureAwait(false);

        var passed = cases.Count(x => x.Passed);
        var failed = cases.Count - passed;
        var gatePassed = failed == 0;

        var lines = cases.Select(x =>
                (x.Passed ? "PASS " : "FAIL ")
                + x.Id + " " + x.Requirement
                + " | evidence=" + x.Evidence
                + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        lines.Add($"RESULT: {passed} passed, {failed} failed.");
        lines.Add($"GATE: {(gatePassed ? "PASS" : "FAIL")} MB-115 plugin package lifecycle acceptance.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-plugin-package-lifecycle-acceptance-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-plugin-package-lifecycle-acceptance-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-115",
                    passed = gatePassed,
                    passedCases = passed,
                    failedCases = failed,
                    cases
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return gatePassed ? 0 : 1;
    }

    private const string PluginId = "h2.fixture.productivity";
    private const string Publisher = "fixture.publisher";

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(StringComparer.Ordinal)
            {
                Publisher
            },
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);

    private static ToolDescriptor Descriptor(ToolRegistry registry)
        => registry.TryGet("plugin.echo", out var descriptor)
            ? descriptor
            : throw new InvalidOperationException(
                "Active plugin.echo descriptor is missing.");

    private static global::H2AgentLab.ToolCall Call(string suffix)
        => new(
            "mb115-" + suffix,
            "plugin.echo",
            JsonSerializer.SerializeToElement(new
            {
                text = suffix
            }));

    private static BuiltPluginPackage BuildPluginPackage(
        string root,
        string version,
        string skillBody,
        IReadOnlyList<string> permissions,
        bool selfTestOk,
        PluginTrustState trustState = PluginTrustState.LocalDeveloper)
    {
        var packageDir = Path.Combine(root, "plugin-packages");
        Directory.CreateDirectory(packageDir);
        var path = Path.Combine(
            packageDir,
            "mb115-" + version.Replace('.', '-')
            + "-" + Guid.NewGuid().ToString("N") + ".zip");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(
                   memory,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteZip(
                zip,
                "tools.json",
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            name = "plugin.echo",
                            @namespace = "plugin-fixture",
                            description = "Echo acceptance input through the activated plugin executor.",
                            access = "ReadOnly",
                            risk = "Low",
                            supportsParallel = true,
                            schemaVersion = "v1",
                            toolVersion = version,
                            resourceScope = "workspace-a",
                            serializationKey = "plugin-fixture",
                            schema = new
                            {
                                type = "function",
                                function = new
                                {
                                    name = "plugin.echo",
                                    description = "Echo acceptance input.",
                                    parameters = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            text = new
                                            {
                                                type = "string"
                                            }
                                        },
                                        required = new[] { "text" },
                                        additionalProperties = false
                                    }
                                }
                            }
                        }
                    }));
            WriteZip(
                zip,
                "skills/audit/SKILL.md",
                string.Join(
                    ((char)10).ToString(),
                    "---",
                    "name: audit",
                    "description: MB-115 plugin lifecycle audit skill",
                    "---",
                    "",
                    skillBody));
            WriteZip(
                zip,
                "selftest.json",
                JsonSerializer.Serialize(new
                {
                    ok = selfTestOk,
                    requiredFiles = new[]
                    {
                        "tools.json",
                        "skills/audit/SKILL.md"
                    }
                }));
        }

        memory.Position = 0;
        string payloadHash;
        using (var read = new ZipArchive(
                   memory,
                   ZipArchiveMode.Read,
                   leaveOpen: true))
        {
            payloadHash = PluginManager.ComputePayloadHash(read);
        }

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            PluginId,
            "Fixture Productivity",
            version,
            "2.0.0",
            Publisher,
            "sha256:" + payloadHash,
            ["plugin.echo"],
            ["audit"],
            ["native-host"],
            permissions,
            NativeHelpers: [],
            LifecycleHooks: [],
            SelfTestFile: "selftest.json");
        var manifestJson = JsonSerializer.Serialize(manifest);

        using (var update = new ZipArchive(
                   memory,
                   ZipArchiveMode.Update,
                   leaveOpen: true))
        {
            WriteZip(update, "manifest.json", manifestJson);
        }

        var bytes = memory.ToArray();
        File.WriteAllBytes(path, bytes);
        var archiveHash = global::H2AgentLab.SafeWorkspace
            .Hash(bytes)
            .ToLowerInvariant();

        var entry = new PluginCatalogEntry(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            "MB-115 lifecycle acceptance plugin.",
            manifest.Publisher,
            ["plugin", "lifecycle", "acceptance"],
            ["audit"],
            manifest.MinAgentVersion,
            trustState,
            "sha256:" + archiveHash,
            path);

        return new BuiltPluginPackage(path, entry);
    }

    private static void WriteZip(
        ZipArchive zip,
        string path,
        string content)
    {
        var entry = zip.CreateEntry(
            path,
            CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: false);
        writer.Write(content);
    }

    private sealed record BuiltPluginPackage(
        string Path,
        PluginCatalogEntry CatalogEntry);

    private sealed class FixturePluginResolver : IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
            => new DelegatingToolExecutor(
                "mb115-plugin-fixture",
                (call, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        JsonSerializer.Serialize(new
                        {
                            plugin = manifest.Id,
                            version = manifest.Version,
                            tool = tool.Name,
                            arguments = call.Arguments
                        }));
                });
    }
}
