using System.Text.Json;
using H2AgentLab.Catalog;
using H2AgentLab.Plugins;
using H2AgentLab.Skills;
using H2AgentLab.Tools;

namespace H2AgentLab.Capabilities;

public static class MbThinCapabilityResolverTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-73 test directory.");
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

        await Test("MB-73 installed metadata short-circuits catalog without planning semantics", async () =>
        {
            var registry = new ToolRegistry();
            registry.Register(Tool(
                "ledger.reconcile",
                "Reconcile ledger rows and report deterministic totals."));

            var installed = new InstalledCapabilityIndex();
            installed.Rebuild(
                registry,
                new H2AgentLab.Skills.SkillCatalog());

            var remote = Package(
                "remote.ledger",
                toolSummaries:
                [
                    "Reconcile remote ledger rows and report totals."
                ]);
            var source = new InMemoryCatalogSource(
                "mb73",
                100,
                PluginTrustState.TrustedOfficial,
                [remote]);
            var catalogs = new CatalogSourceManager();
            catalogs.Register(source);

            var resolver = Resolver(
                installed,
                catalogs,
                _ => true);

            var result = await resolver.ResolveAsync(
                "reconcile ledger totals",
                CancellationToken.None);

            Check(result.Status == CapabilityResolutionStatus.INSTALLED
                && result.Candidates.Count > 0
                && result.Candidates[0].CapabilityId == "ledger.reconcile"
                && source.FetchCount == 0
                && !result.PackageDownloaded
                && !result.InstallationAttempted,
                "Resolver did more than installed metadata availability reporting.");
        });

        await Test("MB-73 tool-only plugin is a first-class available package candidate", async () =>
        {
            var installed = EmptyInstalled();
            var toolOnly = Package(
                "tools.ledger",
                toolSummaries:
                [
                    "Reconcile ledger rows, calculate totals and inspect invoice balances."
                ],
                skills: Array.Empty<AvailableSkillMetadata>(),
                providers: Array.Empty<string>());
            var catalogs = Catalog(toolOnly);
            var resolver = Resolver(
                installed,
                catalogs,
                x => x.PluginId == "tools.ledger");

            var result = await resolver.ResolveAsync(
                "reconcile ledger totals invoice balances",
                CancellationToken.None);

            var candidate = result.Candidates.Single();
            Check(result.Status == CapabilityResolutionStatus.AVAILABLE
                && candidate.Status == CapabilityResolutionStatus.AVAILABLE
                && candidate.CapabilityId == "tools.ledger"
                && candidate.PluginId == "tools.ledger"
                && candidate.SkillId is null
                && candidate.AvailablePackage?.ToolSummaries.Count == 1
                && candidate.AvailablePackage.Skills.Count == 0
                && !result.PackageDownloaded
                && !result.InstallationAttempted,
                "Resolver still assumes a missing capability must be a skill.");
        });

        await Test("MB-73 multi-skill package remains one package candidate for model-side selection", async () =>
        {
            var package = Package(
                "mixed.productivity",
                toolSummaries:
                [
                    "Inspect structured document ranges and metadata."
                ],
                skills:
                [
                    new AvailableSkillMetadata(
                        "skill-a",
                        "skill-a",
                        "Inspect document ranges, styles and metadata."),
                    new AvailableSkillMetadata(
                        "skill-b",
                        "skill-b",
                        "Inspect document ranges and verify formatting evidence.")
                ],
                providers:
                [
                    "document.native"
                ]);
            var resolver = Resolver(
                EmptyInstalled(),
                Catalog(package),
                _ => true);

            var result = await resolver.ResolveAsync(
                "inspect document ranges metadata",
                CancellationToken.None);

            Check(result.Status == CapabilityResolutionStatus.AVAILABLE
                && result.Candidates.Count == 1,
                "Resolver exploded one package into semantic child candidates.");
            var candidate = result.Candidates.Single();
            Check(candidate.CapabilityId == "mixed.productivity"
                && candidate.SkillId is null
                && candidate.AvailablePackage?.Skills.Count == 2
                && candidate.Description.Contains(
                    "Inspect document ranges",
                    StringComparison.OrdinalIgnoreCase),
                "Resolver chose a specific skill instead of reporting package metadata.");
        });

        await Test("MB-73 resolver only reports compatibility and host policy status", async () =>
        {
            var blocked = Package(
                "blocked.package",
                toolSummaries:
                [
                    "Generate deterministic blocked fixture reports."
                ]);
            var incompatible = Package(
                "future.package",
                minAgentVersion: "99.0.0",
                toolSummaries:
                [
                    "Generate deterministic future fixture reports."
                ]);

            var blockedResult = await Resolver(
                    EmptyInstalled(),
                    Catalog(blocked),
                    _ => false)
                .ResolveAsync(
                    "blocked fixture reports",
                    CancellationToken.None);
            Check(blockedResult.Status
                    == CapabilityResolutionStatus.BLOCKED_BY_POLICY
                && blockedResult.Candidates.Single().Status
                    == CapabilityResolutionStatus.BLOCKED_BY_POLICY,
                "Resolver did not report host metadata policy status.");

            var incompatibleResult = await Resolver(
                    EmptyInstalled(),
                    Catalog(incompatible),
                    _ => true)
                .ResolveAsync(
                    "future fixture reports",
                    CancellationToken.None);
            Check(incompatibleResult.Status
                    == CapabilityResolutionStatus.INCOMPATIBLE
                && incompatibleResult.Candidates.Single().Status
                    == CapabilityResolutionStatus.INCOMPATIBLE,
                "Resolver did not report compatibility status.");
        });

        await Test("MB-73 provider-only package is reported without inventing a skill", async () =>
        {
            var providerOnly = Package(
                "provider.telemetry",
                toolSummaries: Array.Empty<string>(),
                skills: Array.Empty<AvailableSkillMetadata>(),
                providers:
                [
                    "telemetry metrics events"
                ]);
            var resolver = Resolver(
                EmptyInstalled(),
                Catalog(providerOnly),
                _ => true);

            var result = await resolver.ResolveAsync(
                "telemetry metrics events",
                CancellationToken.None);

            var candidate = result.Candidates.Single();
            Check(result.Status == CapabilityResolutionStatus.AVAILABLE
                && candidate.CapabilityId == "provider.telemetry"
                && candidate.SkillId is null
                && candidate.AvailablePackage?.Providers.Count == 1
                && candidate.AvailablePackage.ToolSummaries.Count == 0,
                "Resolver still assumes a package must expose a skill/tool child candidate.");
        });

        await Test("MB-73 source guard keeps resolver thin and model-neutral", () =>
        {
            var repo = FindRepoRoot();
            var resolverSource = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Capabilities",
                    "CapabilityResolver.cs"));
            var lower = resolverSource.ToLowerInvariant();

            Check(!resolverSource.Contains(
                    "matchingSkills",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "private static double Score",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    ".Take(5)",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "SkillId == ",
                    StringComparison.Ordinal),
                "CapabilityResolver still performs child skill semantic planning.");

            foreach (var forbidden in new[]
            {
                "autocad",
                "\"dynamic\" =>",
                "\"legal\" =>",
                "synonym"
            })
                Check(!lower.Contains(
                        forbidden.ToLowerInvariant(),
                        StringComparison.Ordinal),
                    "CapabilityResolver contains domain-specific semantic knowledge: "
                    + forbidden);

            Check(!resolverSource.Contains(
                    "PluginManager",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "IPackageRetriever",
                    StringComparison.Ordinal)
                && !resolverSource.Contains(
                    "InstallFromArchive",
                    StringComparison.Ordinal),
                "CapabilityResolver performs install/download work instead of metadata/status reporting.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-thin-capability-resolver-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static InstalledCapabilityIndex EmptyInstalled()
    {
        var installed = new InstalledCapabilityIndex();
        installed.Rebuild(
            new ToolRegistry(),
            new H2AgentLab.Skills.SkillCatalog());
        return installed;
    }

    private static CapabilityResolver Resolver(
        InstalledCapabilityIndex installed,
        CatalogSourceManager catalogs,
        Func<AvailableCapabilityRecord, bool> policy)
        => new(
            installed,
            new AvailableCapabilityIndex(),
            catalogs,
            new PredicateCapabilityInstallPolicy(policy));

    private static CatalogSourceManager Catalog(
        params AvailableCapabilityRecord[] records)
    {
        var catalogs = new CatalogSourceManager();
        catalogs.Register(new InMemoryCatalogSource(
            "mb73",
            100,
            PluginTrustState.TrustedOfficial,
            records));
        return catalogs;
    }

    private static AvailableCapabilityRecord Package(
        string pluginId,
        string minAgentVersion = "2.0.0",
        IReadOnlyList<string>? toolSummaries = null,
        IReadOnlyList<AvailableSkillMetadata>? skills = null,
        IReadOnlyList<string>? providers = null)
        => new(
            pluginId,
            "1.0.0",
            "MB-73 Fixture Publisher",
            PluginTrustState.TrustedOfficial,
            minAgentVersion,
            new string('a', 64),
            "/fixture/" + pluginId + ".zip",
            toolSummaries ?? Array.Empty<string>(),
            skills ?? Array.Empty<AvailableSkillMetadata>(),
            providers ?? Array.Empty<string>(),
            Array.Empty<string>(),
            "mb73",
            100,
            DateTime.UtcNow,
            MetadataStale: false);

    private static ToolDescriptor Tool(
        string name,
        string description)
        => new(
            name,
            new ToolNamespace(
                "fixture",
                "MB-73 installed capability fixture."),
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
                "mb73-fixture",
                (call, ct) => ValueTask.FromResult("{}")),
            provenance: new ToolProvenance(
                "mb73.fixture",
                "1.0.0",
                "fixture",
                "1.0.0"));

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

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }
}
