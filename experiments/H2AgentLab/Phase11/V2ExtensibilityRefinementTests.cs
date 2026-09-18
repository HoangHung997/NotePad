using System.IO.Compression;
using System.Text;
using System.Text.Json;
using H2AgentLab.Capabilities;
using H2AgentLab.Catalog;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Phase11;

public static class V2ExtensibilityRefinementTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new extensibility refinement test directory.");
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

        await Test("1116 unified skill source finds differently named plugin skill by description and loads progressively", () =>
        {
            var builtInRoot = CreateBuiltInSkills(Path.Combine(root, "1116-builtins"));
            var stateRoot = Path.Combine(root, "1116-state");
            var packages = Path.Combine(root, "1116-packages");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(packages);

            var registry = new ToolRegistry();
            var manager = new PluginManager(stateRoot, registry, new FixturePluginResolver());
            var package = BuildCadPlugin(packages, "1.4.0", "Skill version one.");
            _ = manager.InstallFromArchive(
                package.Path,
                CatalogEntry(package),
                DeveloperPolicy(),
                userApproved: true);

            var pluginCatalog = new PluginSkillCatalog(manager);
            var unified = new UnifiedSkillCatalog();
            unified.Register(new BuiltInSkillSource(builtInRoot));
            unified.Register(new PluginSkillSource(manager, pluginCatalog));

            var results = unified.Search("audit dynamic block parameters and actions", 10);
            var selected = results.FirstOrDefault();
            Check(selected is not null
                && selected.Name == "cad-integrity"
                && selected.Identity.SourceKind == SkillSourceKind.Plugin
                && selected.Identity.PluginId == "h2.autocad.productivity"
                && selected.Identity.PluginVersion == "1.4.0"
                && selected.Identity.Sha256.Length == 64,
                "Description-aware unified search did not select differently named plugin skill.");

            var content = unified.Read(selected.Identity);
            Check(content.EntryPoint.Contains("references/dynamic-block.md", StringComparison.Ordinal)
                && !content.EntryPoint.Contains("resource:", StringComparison.OrdinalIgnoreCase)
                && content.AvailableResources.Contains("references/dynamic-block.md", StringComparer.Ordinal)
                && content.AvailableResources.Contains("scripts/helper.py", StringComparer.Ordinal)
                && content.AvailableResources.Contains("assets/template.txt", StringComparer.Ordinal),
                "Progressive skill envelope did not expose entry point/resource inventory without custom directives.");

            var reference = unified.ReadResource(selected.Identity, "references/dynamic-block.md");
            Check(reference.Content.Contains("visibility states", StringComparison.OrdinalIgnoreCase),
                "Selected skill reference could not be loaded on demand.");

            try
            {
                _ = unified.ReadResource(selected.Identity, "SKILL.md");
                throw new InvalidOperationException("Progressive resource loader allowed reloading SKILL.md as arbitrary resource.");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Check(unified.SourceIds.SequenceEqual(new[] { "built-in", "plugins" }),
                "Built-in and plugin skills do not share one logical catalog.");
            return Task.CompletedTask;
        });

        await Test("1117 installed and available capability indexes remain separate compact and offline-safe", () =>
        {
            var builtInRoot = CreateBuiltInSkills(Path.Combine(root, "1117-builtins"));
            var unified = new UnifiedSkillCatalog();
            unified.Register(new BuiltInSkillSource(new global::H2AgentLab.SkillCatalog(builtInRoot)));

            var registry = new ToolRegistry();
            registry.Register(FixtureTool(
                "files.read",
                "files",
                "Read files from the approved workspace.",
                "built-in",
                "1.0.0"));

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(
                registry,
                unified,
                [new ProviderProvenance("desktop-host", "1.0.0", "desktop", "named-pipe")]);

            var remote = new InMemoryCatalogSource(
                "remote-fixture",
                50,
                PluginTrustState.TrustedOfficial,
                []);
            Check(installed.Search("general coding support").Any(x => x.Kind == CapabilityKind.Skill)
                && installed.Search("approved workspace").Any(x => x.Kind == CapabilityKind.Tool),
                "Installed capability index did not rebuild/search built-in local state.");
            Check(remote.FetchCount == 0,
                "Installed capability search unexpectedly required remote catalog access.");

            var available = new AvailableCapabilityIndex();
            available.Rebuild(
            [
                AvailableRecord(
                    "h2.autocad.productivity",
                    "1.4.0",
                    "source-a",
                    100,
                    HashValue('a'),
                    "file:///package.h2pkg",
                    "Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility states.")
            ]);
            var candidate = available.Search("audit dynamic block parameters actions", 5).Single();
            Check(candidate.Skills.Single().Description.Length < 1_500
                && !candidate.Skills.Single().Description.Contains("SKILL.md", StringComparison.OrdinalIgnoreCase)
                && candidate.ToolSummaries.All(x => x.Length < 1_000),
                "Available capability index contains full skill/tool payload instead of compact metadata.");
            return Task.CompletedTask;
        });

        await Test("1118 catalog source manager merges priorities but never hides trust hash conflicts or outages", async () =>
        {
            var sameHash = HashValue('b');
            var sourceA = new InMemoryCatalogSource(
                "source-a",
                100,
                PluginTrustState.TrustedOfficial,
                [
                    AvailableRecord("h2.normal", "1.0.0", "source-a", 100, sameHash, "a.pkg", "Normal capability."),
                    AvailableRecord("h2.conflict", "1.0.0", "source-a", 100, HashValue('c'), "c1.pkg", "Conflict capability.")
                ]);
            var sourceB = new InMemoryCatalogSource(
                "source-b",
                80,
                PluginTrustState.OrganizationApproved,
                [
                    AvailableRecord("h2.normal", "1.0.0", "source-b", 80, sameHash, "b.pkg", "Normal capability."),
                    AvailableRecord("h2.conflict", "1.0.0", "source-b", 80, HashValue('d'), "c2.pkg", "Conflict capability.")
                ]);
            var disabled = new InMemoryCatalogSource(
                "disabled",
                200,
                PluginTrustState.TrustedOfficial,
                [AvailableRecord("h2.disabled", "1.0.0", "disabled", 200, HashValue('e'), "d.pkg", "Disabled.")])
            {
                Enabled = false
            };

            var manager = new CatalogSourceManager();
            manager.Register(sourceA);
            manager.Register(sourceB);
            manager.Register(disabled);
            var merged = await manager.RefreshAsync(CancellationToken.None);

            Check(merged.Entries.Count(x => x.PluginId == "h2.normal") == 1
                && merged.Entries.Single(x => x.PluginId == "h2.normal").SourceId == "source-a",
                "Catalog priority did not deterministically select identical trusted metadata.");
            Check(merged.Conflicts.Any(x => x.PluginId == "h2.conflict"
                    && x.Reason.Contains("different package hashes", StringComparison.Ordinal)),
                "Catalog hash conflict was silently overridden by priority.");
            Check(disabled.FetchCount == 0,
                "Disabled catalog source was fetched.");

            sourceB.ThrowOnFetch = true;
            var outage = await manager.RefreshAsync(CancellationToken.None);
            Check(outage.UnavailableSources.Contains("source-b", StringComparer.Ordinal)
                && manager.CachedView().Entries.Any(x => x.PluginId == "h2.normal"),
                "Unavailable catalog source erased cached usable metadata.");
        });

        await Test("1119 capability resolver is installed-first description-aware and metadata-only for remote candidates", async () =>
        {
            var builtInRoot = CreateBuiltInSkills(Path.Combine(root, "1119-builtins"));
            var unified = new UnifiedSkillCatalog();
            unified.Register(new BuiltInSkillSource(new global::H2AgentLab.SkillCatalog(builtInRoot)));
            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(new ToolRegistry(), unified);

            var source = new InMemoryCatalogSource(
                "catalog",
                100,
                PluginTrustState.TrustedOfficial,
                BuildLargeAvailableCatalog("catalog", 100));
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);
            var available = new AvailableCapabilityIndex();
            var resolver = new CapabilityResolver(
                installed,
                available,
                catalogs,
                new PredicateCapabilityInstallPolicy(x => x.TrustState == PluginTrustState.TrustedOfficial));

            var local = await resolver.ResolveAsync("general coding support", CancellationToken.None);
            Check(local.Status == CapabilityResolutionStatus.INSTALLED
                && source.FetchCount == 0,
                "Installed capability resolution unexpectedly touched remote catalog.");

            var remote = await resolver.ResolveAsync(
                "audit dynamic block parameters and actions",
                CancellationToken.None);
            Check(remote.Status == CapabilityResolutionStatus.AVAILABLE
                && remote.CatalogMetadataRefreshed
                && source.FetchCount == 1
                && !remote.PackageDownloaded
                && !remote.InstallationAttempted,
                "Remote capability resolution downloaded/installed or failed to refresh metadata.");
            var best = remote.Candidates.First();
            Check(best.SkillId == "cad-integrity"
                && best.CapabilityId == "cad-integrity"
                && best.Description.Contains("dynamic blocks", StringComparison.OrdinalIgnoreCase),
                "Description-aware semantic candidate reduction did not rank cad-integrity first.");
        });

        await Test("1120 task capability snapshot pins versions hashes and requires explicit revision after update", () =>
        {
            var stateRoot = Path.Combine(root, "1120-state");
            var packageRoot = Path.Combine(root, "1120-packages");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(packageRoot);

            var registry = new ToolRegistry();
            var manager = new PluginManager(stateRoot, registry, new FixturePluginResolver());
            var v1 = BuildCadPlugin(packageRoot, "1.4.0", "Skill version one.");
            _ = manager.InstallFromArchive(v1.Path, CatalogEntry(v1), DeveloperPolicy(), true);

            var unified = new UnifiedSkillCatalog();
            unified.Register(new PluginSkillSource(manager, new PluginSkillCatalog(manager)));
            var selectedV1 = unified.Search("audit dynamic block parameters actions", 5).Single();
            var providersV1 = ProvidersFor(manager);

            var snapshot = TaskCapabilitySnapshotBuilder.Capture(
                Guid.NewGuid(),
                1,
                registry,
                providersV1,
                manager.ActivePlugins(),
                [selectedV1],
                "task-start");
            var guard = new TaskCapabilityPinGuard(snapshot);
            guard.EnsureStillPinned(registry, manager.ActivePlugins());

            var v2 = BuildCadPlugin(packageRoot, "1.5.0", "Skill version two changed.");
            _ = manager.InstallFromArchive(v2.Path, CatalogEntry(v2), DeveloperPolicy(), true);

            try
            {
                guard.EnsureStillPinned(registry, manager.ActivePlugins());
                throw new InvalidOperationException("Plugin/tool update silently replaced pinned task capabilities.");
            }
            catch (InvalidOperationException)
            {
            }

            var selectedV2 = unified.Search("audit dynamic block parameters actions", 5).Single();
            var revision = TaskCapabilitySnapshotBuilder.ReviseAfterExplicitInstall(
                snapshot,
                registry,
                ProvidersFor(manager),
                manager.ActivePlugins(),
                [selectedV2],
                "h2.autocad.productivity");
            guard.ReplaceWithExplicitRevision(revision);

            var evidence = TaskCapabilitySnapshotBuilder.Evidence(guard.Current);
            Check(guard.Current.Revision == 2
                && evidence.PluginVersions.Contains("h2.autocad.productivity@1.5.0", StringComparer.Ordinal)
                && evidence.ProviderVersions.Contains("autocad.native@1.5.0", StringComparer.Ordinal)
                && evidence.SkillHashes.Single().Contains(selectedV2.Identity.Sha256, StringComparison.Ordinal),
                "Task capability evidence did not pin exact revised plugin/provider/skill versions.");
            return Task.CompletedTask;
        });

        await Test("1121 package retriever separates metadata from bytes and enforces root size hash cancel before PluginManager", async () =>
        {
            var packageRoot = Path.Combine(root, "1121-packages");
            var staging = Path.Combine(root, "1121-staging");
            var stateRoot = Path.Combine(root, "1121-state");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(stateRoot);

            var package = BuildCadPlugin(packageRoot, "1.4.0", "Retriever fixture.");
            var retriever = new LocalPackageRetriever(staging, [packageRoot]);
            var result = await retriever.RetrieveAsync(
                new PackageRetrievalRequest(
                    package.Manifest.Id,
                    package.Manifest.Version,
                    package.Path,
                    "sha256:" + package.ArchiveSha256,
                    64 * 1024 * 1024,
                    TimeSpan.FromSeconds(5)),
                CancellationToken.None);
            Check(File.Exists(result.StagedPath)
                && result.Sha256 == package.ArchiveSha256
                && result.Bytes > 0,
                "Package retriever did not stage/verify immutable package bytes.");

            try
            {
                _ = await retriever.RetrieveAsync(
                    new PackageRetrievalRequest(
                        package.Manifest.Id,
                        package.Manifest.Version,
                        package.Path,
                        "sha256:" + package.ArchiveSha256,
                        1,
                        TimeSpan.FromSeconds(5)),
                    CancellationToken.None);
                throw new InvalidOperationException("Package size bound was not enforced.");
            }
            catch (IOException)
            {
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    _ = await retriever.RetrieveAsync(
                        new PackageRetrievalRequest(
                            package.Manifest.Id,
                            package.Manifest.Version,
                            package.Path,
                            "sha256:" + package.ArchiveSha256,
                            64 * 1024 * 1024,
                            TimeSpan.FromSeconds(5)),
                        cancelled.Token);
                    throw new InvalidOperationException("Cancelled package retrieval completed.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            var registry = new ToolRegistry();
            var manager = new PluginManager(stateRoot, registry, new FixturePluginResolver());
            _ = manager.InstallFromArchive(
                result.StagedPath,
                CatalogEntry(package) with { DownloadLocation = result.StagedPath },
                DeveloperPolicy(),
                userApproved: true);
            Check(registry.TryGet("autocad.find_blocks", out _),
                "Retrieved package did not pass existing PluginManager verification/activation.");
        });

        await Test("1122 missing capability installs differently named skill and resumes original task without restatement", async () =>
        {
            var builtInRoot = CreateBuiltInSkills(Path.Combine(root, "1122-builtins"));
            var packageRoot = Path.Combine(root, "1122-packages");
            var stagingRoot = Path.Combine(root, "1122-staging");
            var stateRoot = Path.Combine(root, "1122-state");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(stateRoot);

            var package = BuildCadPlugin(packageRoot, "1.4.0", "End-to-end skill content.");
            var availableRecord = Available(package, "remote-catalog", 100);
            var source = new InMemoryCatalogSource(
                "remote-catalog",
                100,
                PluginTrustState.LocalDeveloper,
                [availableRecord]);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);

            var registry = new ToolRegistry();
            var pluginManager = new PluginManager(stateRoot, registry, new FixturePluginResolver());
            var unified = new UnifiedSkillCatalog();
            unified.Register(new BuiltInSkillSource(new global::H2AgentLab.SkillCatalog(builtInRoot)));
            unified.Register(new PluginSkillSource(pluginManager, new PluginSkillCatalog(pluginManager)));

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(registry, unified, ProvidersFor(pluginManager));
            var available = new AvailableCapabilityIndex();
            var resolver = new CapabilityResolver(
                installed,
                available,
                catalogs,
                new PredicateCapabilityInstallPolicy(x =>
                    x.TrustState == PluginTrustState.LocalDeveloper));
            var retriever = new LocalPackageRetriever(stagingRoot, [packageRoot]);

            var taskId = Guid.NewGuid();
            var initial = TaskCapabilitySnapshotBuilder.Capture(
                taskId,
                1,
                registry,
                ProvidersFor(pluginManager),
                pluginManager.ActivePlugins(),
                [],
                "task-start");
            var guard = new TaskCapabilityPinGuard(initial);
            var continuation = new MissingCapabilityContinuation(
                resolver,
                retriever,
                pluginManager,
                unified,
                installed,
                registry,
                () => ProvidersFor(pluginManager));

            const string originalQuery = "audit AutoCAD dynamic block parameters and actions";
            var result = await continuation.ResolveInstallAndContinueAsync(
                originalQuery,
                guard,
                DeveloperPolicy(),
                userApproved: true,
                CancellationToken.None);

            Check(result.OriginalQuery == originalQuery
                && !result.UserRestatementRequired,
                "Original task did not resume automatically after controlled capability install.");
            Check(result.InitialResolution.Status == CapabilityResolutionStatus.AVAILABLE
                && result.InstallResult?.PluginId == "h2.autocad.productivity",
                "Missing capability was not resolved through catalog metadata and PluginManager.");
            Check(result.SelectedSkill.Summary.Name == "cad-integrity"
                && !originalQuery.Contains("cad-integrity", StringComparison.OrdinalIgnoreCase),
                "Differently named skill was not selected from description metadata.");
            Check(result.LoadedResources.Count == 0,
                "Capability continuation auto-loaded skill resources instead of waiting for an explicit runtime/model request.");
            var explicitReference = unified.ReadResource(
                result.SelectedSkill.Summary.Identity,
                "references/dynamic-block.md");
            Check(explicitReference.Content.Contains("visibility states", StringComparison.OrdinalIgnoreCase),
                "Selected skill reference was not available through explicit on-demand access.");
            Check(result.CapabilitySnapshot.Revision == 2
                && result.CapabilityEvidence.PluginVersions.Contains("h2.autocad.productivity@1.4.0", StringComparer.Ordinal)
                && result.CapabilityEvidence.ProviderVersions.Contains("autocad.native@1.4.0", StringComparer.Ordinal)
                && result.CapabilityEvidence.ToolVersions.Any(x => x.StartsWith("autocad.find_blocks@", StringComparison.Ordinal))
                && result.CapabilityEvidence.SkillHashes.Any(x => x.Contains(result.SelectedSkill.Summary.Identity.Sha256, StringComparison.Ordinal)),
                "Continuation evidence does not record exact plugin/provider/tool/skill identities.");
            Check(result.TraceEvents.Any(x => x.Code == "capability-missing")
                && result.TraceEvents.Any(x => x.Code == "package-retrieved")
                && result.TraceEvents.Any(x => x.Code == "capability-snapshot-revised"),
                "Capability installation transition was invisible in task trace.");
            Check(source.FetchCount == 1,
                "Missing-capability flow performed unexpected repeated catalog metadata refresh.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-extensibility-refinement-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static string CreateBuiltInSkills(string root)
    {
        Directory.CreateDirectory(root);
        var skill = Path.Combine(root, "coding");
        Directory.CreateDirectory(skill);
        File.WriteAllText(
            Path.Combine(skill, "SKILL.md"),
            JoinLines(
                "---",
                "name: coding",
                "description: General coding support for local source review and deterministic tests.",
                "---",
                "",
                "# Coding",
                "Use for ordinary source-code tasks."));
        return root;
    }

    private static IReadOnlyList<AvailableCapabilityRecord> BuildLargeAvailableCatalog(
        string sourceId,
        int priority)
    {
        var records = new List<AvailableCapabilityRecord>();
        for (var i = 0; i < 60; i++)
        {
            records.Add(AvailableRecord(
                "noise.plugin." + i,
                "1.0.0",
                sourceId,
                priority,
                HashValue((char)('0' + i % 10)),
                "noise-" + i + ".pkg",
                "Unrelated image processing reporting capability " + i + "."));
        }
        records.Add(AvailableRecord(
            "h2.autocad.productivity",
            "1.4.0",
            sourceId,
            priority,
            HashValue('f'),
            "cad.pkg",
            "Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility-state consistency. Use for block audits."));
        return records;
    }

    private static AvailableCapabilityRecord AvailableRecord(
        string pluginId,
        string version,
        string sourceId,
        int priority,
        string archiveHash,
        string location,
        string skillDescription,
        PluginTrustState trust = PluginTrustState.TrustedOfficial)
        => new(
            pluginId,
            version,
            "fixture.publisher",
            trust,
            "2.0.0",
            archiveHash,
            location,
            ["autocad.find_blocks"],
            [new AvailableSkillMetadata("cad-integrity", "cad-integrity", skillDescription)],
            ["autocad.native"],
            ["workspace.read"],
            sourceId,
            priority,
            DateTime.UtcNow,
            MetadataStale: false);

    private static AvailableCapabilityRecord Available(
        BuiltPluginPackage package,
        string sourceId,
        int priority)
        => AvailableRecord(
            package.Manifest.Id,
            package.Manifest.Version,
            sourceId,
            priority,
            "sha256:" + package.ArchiveSha256,
            package.Path,
            "Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility-state consistency. Use for block audits.",
            PluginTrustState.LocalDeveloper);

    private static BuiltPluginPackage BuildCadPlugin(
        string packageRoot,
        string version,
        string skillBody)
    {
        Directory.CreateDirectory(packageRoot);
        var path = Path.Combine(
            packageRoot,
            "cad-" + version.Replace('.', '-') + "-" + Guid.NewGuid().ToString("N") + ".zip");

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZip(
                zip,
                "tools.json",
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            name = "autocad.find_blocks",
                            @namespace = "autocad",
                            description = "Find AutoCAD blocks through structured native provider.",
                            access = "ReadOnly",
                            risk = "Low",
                            supportsParallel = true,
                            schemaVersion = "v2",
                            toolVersion = "3",
                            resourceScope = "autocad:active-document",
                            serializationKey = "autocad-document",
                            schema = new
                            {
                                type = "function",
                                function = new
                                {
                                    name = "autocad.find_blocks",
                                    description = "Find blocks.",
                                    parameters = new
                                    {
                                        type = "object",
                                        properties = new { }
                                    }
                                }
                            }
                        }
                    }));
            WriteZip(
                zip,
                "skills/cad-integrity/SKILL.md",
                JoinLines(
                    "---",
                    "name: cad-integrity",
                    "description: Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility-state consistency. Use for block audits.",
                    "---",
                    "",
                    "# CAD Integrity",
                    skillBody,
                    "Read references/dynamic-block.md when detailed block verification guidance is needed."));
            WriteZip(
                zip,
                "skills/cad-integrity/references/dynamic-block.md",
                "Check parameters, actions, attributes and visibility states; preserve entity handles and verify after mutation.");
            WriteZip(
                zip,
                "skills/cad-integrity/scripts/helper.py",
                "print('deterministic helper; execution still requires host permission')");
            WriteZip(
                zip,
                "skills/cad-integrity/assets/template.txt",
                "static template not loaded by default");
            WriteZip(
                zip,
                "skills/cad-integrity/agents/metadata.json",
                JsonSerializer.Serialize(new
                {
                    displayName = "CAD Integrity",
                    invocation = "model-select"
                }));
            WriteZip(
                zip,
                "selftest.json",
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    requiredFiles = new[]
                    {
                        "tools.json",
                        "skills/cad-integrity/SKILL.md",
                        "skills/cad-integrity/references/dynamic-block.md"
                    }
                }));
        }

        memory.Position = 0;
        string payloadHash;
        using (var read = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true))
            payloadHash = PluginManager.ComputePayloadHash(read);

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            "h2.autocad.productivity",
            "H2 AutoCAD Productivity",
            version,
            "2.0.0",
            "fixture.publisher",
            "sha256:" + payloadHash,
            ["autocad.find_blocks"],
            ["cad-integrity"],
            ["autocad.native"],
            ["workspace.read"],
            NativeHelpers: [],
            LifecycleHooks: [],
            SelfTestFile: "selftest.json");
        using (var update = new ZipArchive(memory, ZipArchiveMode.Update, leaveOpen: true))
            WriteZip(update, "manifest.json", JsonSerializer.Serialize(manifest));

        var bytes = memory.ToArray();
        File.WriteAllBytes(path, bytes);
        return new BuiltPluginPackage(
            path,
            manifest,
            global::H2AgentLab.SafeWorkspace.Hash(bytes).ToLowerInvariant());
    }

    private static PluginCatalogEntry CatalogEntry(BuiltPluginPackage package)
        => new(
            package.Manifest.Id,
            package.Manifest.Name,
            package.Manifest.Version,
            "AutoCAD structured productivity fixture.",
            package.Manifest.Publisher,
            ["autocad", "blocks", "dynamic"],
            ["cad-integrity"],
            package.Manifest.MinAgentVersion,
            PluginTrustState.LocalDeveloper,
            "sha256:" + package.ArchiveSha256,
            package.Path);

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "fixture.publisher"
            },
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);

    private static IReadOnlyList<ProviderProvenance> ProvidersFor(PluginManager manager)
    {
        var active = manager.GetActive("h2.autocad.productivity");
        return active is null
            ? []
            :
            [
                new ProviderProvenance(
                    "autocad.native",
                    active.Value.Manifest.Version,
                    "local-autocad-ipc",
                    "native")
            ];
    }

    private static ToolDescriptor FixtureTool(
        string name,
        string ns,
        string description,
        string providerId,
        string version)
        => new(
            name,
            new ToolNamespace(ns, ns + " fixture namespace"),
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
                        properties = new { }
                    }
                }
            }),
            executor: new DelegatingToolExecutor(
                "fixture",
                (call, ct) => ValueTask.FromResult("{}")),
            provenance: new ToolProvenance(
                providerId,
                version,
                providerId,
                version),
            resourceScope: new ToolResourceScope("fixture", "fixture"),
            serializationKey: "fixture");

    private static void WriteZip(
        ZipArchive zip,
        string path,
        string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: false);
        writer.Write(content);
    }

    private static string JoinLines(params string[] lines)
        => string.Join(((char)10).ToString(), lines);

    private static string HashValue(char c)
        => "sha256:" + new string(Uri.IsHexDigit(c) ? char.ToLowerInvariant(c) : 'a', 64);

    private sealed record BuiltPluginPackage(
        string Path,
        H2PluginManifest Manifest,
        string ArchiveSha256);

    private sealed class FixturePluginResolver : IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
            => new DelegatingToolExecutor(
                "plugin-cad-fixture",
                (call, ct) => ValueTask.FromResult(
                    JsonSerializer.Serialize(new
                    {
                        manifest.Id,
                        manifest.Version,
                        tool.Name
                    })));
    }
}
