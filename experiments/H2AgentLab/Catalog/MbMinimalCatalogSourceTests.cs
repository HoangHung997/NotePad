using System.Text.Json;
using H2AgentLab.Plugins;

namespace H2AgentLab.Catalog;

public static class MbMinimalCatalogSourceTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-80 test directory.");
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

        await Test("MB-80 configured local source lists searches compact metadata and resolves exact package location", async () =>
        {
            var sourceRoot = Path.Combine(root, "catalog-a");
            Directory.CreateDirectory(
                Path.Combine(sourceRoot, "packages"));
            var packagePath = Path.Combine(
                sourceRoot,
                "packages",
                "sheet-audit.h2pkg");
            var packageBytes = System.Text.Encoding.UTF8.GetBytes(
                "MB80_PACKAGE_BYTES_NOT_EXECUTED");
            await File.WriteAllBytesAsync(
                packagePath,
                packageBytes);
            var archiveHash = "sha256:"
                + global::H2AgentLab.SafeWorkspace
                    .Hash(packageBytes)
                    .ToLowerInvariant();

            await WriteIndex(
                sourceRoot,
                new LocalCatalogPackageMetadata(
                    "fixture.sheet.audit",
                    "1.2.0",
                    "fixture.publisher",
                    "2.0.0",
                    archiveHash,
                    "packages/sheet-audit.h2pkg",
                    ToolSummaries:
                    [
                        "Inspect spreadsheet formulas and reconcile totals."
                    ],
                    Skills:
                    [
                        new Capabilities.AvailableSkillMetadata(
                            "sheet-audit",
                            "sheet-audit",
                            "Spreadsheet formula reconciliation and total audit guidance.")
                    ],
                    Providers:
                    [
                        "fixture.office"
                    ],
                    Permissions:
                    [
                        "workspace.read"
                    ]));

            ICatalogSource source = new LocalFolderCatalogSource(
                sourceRoot,
                "configured-local",
                PluginTrustState.LocalDeveloper);

            var listed = await source.FetchMetadataAsync(
                CancellationToken.None);
            var found = await source.SearchAsync(
                "spreadsheet formula reconciliation",
                5,
                CancellationToken.None);
            var resolved = await source.ResolvePackageLocationAsync(
                "fixture.sheet.audit",
                "1.2.0",
                CancellationToken.None);

            Check(listed.Entries.Count == 1
                && found.Count == 1
                && found[0].PluginId == "fixture.sheet.audit",
                "Configured source did not list/search its compact package metadata.");
            Check(found[0].Skills.Single().Description.Length < 1_500
                && !found[0].Skills.Single().Description.Contains(
                    "MB80_PACKAGE_BYTES_NOT_EXECUTED",
                    StringComparison.Ordinal),
                "Catalog search exposed package bytes/full payload instead of compact metadata.");
            Check(resolved.SourceId == "configured-local"
                && Path.GetFullPath(resolved.Location)
                    == Path.GetFullPath(packagePath)
                && resolved.ArchiveSha256 == archiveHash,
                "Catalog source did not resolve the exact selected package location/hash.");
        });

        await Test("MB-80 source root is configuration and traversal package paths fail closed", async () =>
        {
            var sourceRoot = Path.Combine(root, "catalog-b");
            Directory.CreateDirectory(sourceRoot);
            var outside = Path.Combine(root, "escape.h2pkg");
            await File.WriteAllTextAsync(
                outside,
                "outside");

            await WriteIndex(
                sourceRoot,
                new LocalCatalogPackageMetadata(
                    "fixture.escape",
                    "1.0.0",
                    "fixture.publisher",
                    "2.0.0",
                    "sha256:" + new string('a', 64),
                    "../escape.h2pkg"));

            var source = new LocalFolderCatalogSource(
                sourceRoot,
                "configured-second-root",
                PluginTrustState.LocalDeveloper);

            try
            {
                _ = await source.FetchMetadataAsync(
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Traversal package path was accepted.");
            }
            catch (InvalidDataException)
            {
            }

            Check(source.Root == Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(sourceRoot)),
                "Local catalog source ignored its configured root.");
        });

        await Test("MB-80 manager resolves through selected source without retrieving or installing bytes", async () =>
        {
            var package = Path.Combine(
                root,
                "manager-package.h2pkg");
            await File.WriteAllTextAsync(
                package,
                "manager fixture bytes");
            var bytes = await File.ReadAllBytesAsync(package);
            var hash = "sha256:"
                + global::H2AgentLab.SafeWorkspace
                    .Hash(bytes)
                    .ToLowerInvariant();

            var source = new InMemoryCatalogSource(
                "single-source",
                100,
                PluginTrustState.LocalDeveloper,
                [
                    new Capabilities.AvailableCapabilityRecord(
                        "fixture.manager",
                        "2.0.0",
                        "fixture.publisher",
                        PluginTrustState.LocalDeveloper,
                        "2.0.0",
                        hash,
                        package,
                        ["Manager resolution fixture."],
                        [],
                        [],
                        [],
                        "single-source",
                        100,
                        DateTime.UtcNow,
                        MetadataStale: false)
                ]);
            var manager = new CatalogSourceManager();
            manager.Register(source);
            _ = await manager.RefreshAsync(
                CancellationToken.None);

            var resolved = await manager.ResolvePackageLocationAsync(
                "single-source",
                "fixture.manager",
                "2.0.0",
                CancellationToken.None);

            Check(resolved.Location == package
                && source.FetchCount == 1
                && File.Exists(package),
                "Package-location resolution performed unexpected retrieval/install work.");
        });

        await Test("MB-80 source guard keeps catalog location out of AgentOrchestrator and byte retrieval out of ICatalogSource", () =>
        {
            var repo = FindRepoRoot();
            var orchestrator = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Tasking",
                    "AgentOrchestrator.cs"));
            var catalog = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Catalog",
                    "CatalogSources.cs"));

            Check(!orchestrator.Contains(
                    "LocalFolderCatalogSource",
                    StringComparison.Ordinal)
                && catalog.Contains(
                    "ResolvePackageLocationAsync",
                    StringComparison.Ordinal)
                && catalog.Contains(
                    "SearchAsync",
                    StringComparison.Ordinal)
                && !catalog.Contains(
                    "InstallFromArchive",
                    StringComparison.Ordinal)
                && !catalog.Contains(
                    "RetrieveAsync(",
                    StringComparison.Ordinal),
                "Catalog seam hard-coded source configuration or absorbed retrieval/install responsibility.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-minimal-catalog-source-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task WriteIndex(
        string root,
        params LocalCatalogPackageMetadata[] packages)
        => await File.WriteAllTextAsync(
            Path.Combine(root, "catalog.json"),
            JsonSerializer.Serialize(
                packages,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

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
}
