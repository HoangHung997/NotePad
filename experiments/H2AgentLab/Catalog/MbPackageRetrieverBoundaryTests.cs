using System.IO.Compression;
using System.Text;
using System.Text.Json;
using H2AgentLab.Plugins;
using H2AgentLab.Tools;

namespace H2AgentLab.Catalog;

public static class MbPackageRetrieverBoundaryTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException(
                "Use a new MB-81 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(
            string name,
            Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add(
                    "FAIL " + name + ": "
                    + ex.GetType().Name + ": "
                    + ex.Message);
            }
        }

        static void Check(
            bool condition,
            string message)
        {
            if (!condition)
                throw new InvalidOperationException(
                    message);
        }

        await Test("MB-81 retriever stages immutable bytes and verifies expected archive hash without execution", async () =>
        {
            var fixture = RawFixture(
                root,
                "success",
                32 * 1024);
            var retriever = new LocalPackageRetriever(
                fixture.StagingRoot,
                [fixture.SourceRoot]);

            var result = await retriever.RetrieveAsync(
                Request(
                    fixture.SourcePath,
                    fixture.Hash,
                    fixture.Bytes.LongLength + 1,
                    TimeSpan.FromSeconds(5)),
                CancellationToken.None);

            Check(File.Exists(result.StagedPath)
                && result.Sha256 == fixture.Hash[7..]
                && result.Bytes == fixture.Bytes.LongLength
                && File.ReadAllBytes(result.StagedPath)
                    .SequenceEqual(fixture.Bytes)
                && Path.GetFullPath(result.StagedPath)
                    .StartsWith(
                        Path.GetFullPath(
                            fixture.StagingRoot)
                            + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase),
                "Retriever did not stage and verify exact immutable package bytes.");
        });

        await Test("MB-81 bad archive hash and oversize packages fail before a staged result survives", async () =>
        {
            var hashFixture = RawFixture(
                root,
                "bad-hash",
                8 * 1024);
            var hashRetriever = new LocalPackageRetriever(
                hashFixture.StagingRoot,
                [hashFixture.SourceRoot]);

            try
            {
                _ = await hashRetriever.RetrieveAsync(
                    Request(
                        hashFixture.SourcePath,
                        "sha256:" + new string('0', 64),
                        64 * 1024,
                        TimeSpan.FromSeconds(5)),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Bad archive hash was accepted.");
            }
            catch (InvalidDataException)
            {
            }

            Check(!Directory.EnumerateFiles(
                    hashFixture.StagingRoot)
                    .Any(),
                "Bad-hash retrieval left staged bytes behind.");

            var sizeFixture = RawFixture(
                root,
                "oversize",
                4 * 1024);
            var sizeRetriever = new LocalPackageRetriever(
                sizeFixture.StagingRoot,
                [sizeFixture.SourceRoot]);
            try
            {
                _ = await sizeRetriever.RetrieveAsync(
                    Request(
                        sizeFixture.SourcePath,
                        sizeFixture.Hash,
                        MaxBytes: 1,
                        TimeSpan.FromSeconds(5)),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Oversize package was accepted.");
            }
            catch (IOException)
            {
            }

            Check(!Directory.EnumerateFiles(
                    sizeFixture.StagingRoot)
                    .Any(),
                "Oversize rejection created a staged package.");
        });

        await Test("MB-81 explicit cancellation and total retrieval timeout both fail closed", async () =>
        {
            var cancelFixture = RawFixture(
                root,
                "cancel",
                4 * 1024);
            var retriever = new LocalPackageRetriever(
                cancelFixture.StagingRoot,
                [cancelFixture.SourceRoot]);

            using (var cancelled =
                new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    _ = await retriever.RetrieveAsync(
                        Request(
                            cancelFixture.SourcePath,
                            cancelFixture.Hash,
                            64 * 1024,
                            TimeSpan.FromSeconds(5)),
                        cancelled.Token);
                    throw new InvalidOperationException(
                        "Cancelled retrieval completed.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            try
            {
                _ = await retriever.RetrieveAsync(
                    Request(
                        cancelFixture.SourcePath,
                        cancelFixture.Hash,
                        64 * 1024,
                        TimeSpan.FromTicks(1)),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Expired total retrieval timeout completed.");
            }
            catch (TimeoutException)
            {
            }

            Check(!Directory.EnumerateFiles(
                    cancelFixture.StagingRoot)
                    .Any(),
                "Cancellation/timeout left staged package bytes behind.");
        });

        await Test("MB-81 staged plugin bytes cannot register or execute before PluginManager verification", async () =>
        {
            var sourceRoot = Path.Combine(
                root,
                "invalid-plugin-source");
            var stagingRoot = Path.Combine(
                root,
                "invalid-plugin-staging");
            var stateRoot = Path.Combine(
                root,
                "invalid-plugin-state");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(stateRoot);

            var packagePath = BuildInvalidPayloadPlugin(
                sourceRoot);
            var bytes = await File.ReadAllBytesAsync(
                packagePath);
            var archiveHash = "sha256:"
                + global::H2AgentLab.SafeWorkspace
                    .Hash(bytes)
                    .ToLowerInvariant();

            var registry = new ToolRegistry();
            var resolver =
                new CountingPluginToolResolver();
            var manager = new PluginManager(
                stateRoot,
                registry,
                resolver);
            var retriever = new LocalPackageRetriever(
                stagingRoot,
                [sourceRoot]);

            var staged = await retriever.RetrieveAsync(
                Request(
                    packagePath,
                    archiveHash,
                    4 * 1024 * 1024,
                    TimeSpan.FromSeconds(5)),
                CancellationToken.None);

            Check(resolver.ResolveCount == 0
                && !registry.TryGet(
                    "fixture.unverified",
                    out _)
                && manager.GetActive(
                    "fixture.invalid-payload") is null,
                "Retrieval alone activated or resolved plugin code.");

            try
            {
                _ = manager.InstallFromArchive(
                    staged.StagedPath,
                    new PluginCatalogEntry(
                        "fixture.invalid-payload",
                        "Fixture Invalid Payload",
                        "1.0.0",
                        "Invalid payload fixture.",
                        "fixture.publisher",
                        ["fixture.unverified"],
                        [],
                        "2.0.0",
                        PluginTrustState.LocalDeveloper,
                        archiveHash,
                        staged.StagedPath),
                    DeveloperPolicy(),
                    userApproved: true);
                throw new InvalidOperationException(
                    "PluginManager accepted an invalid payload hash.");
            }
            catch (InvalidDataException)
            {
            }

            Check(resolver.ResolveCount == 0
                && !registry.TryGet(
                    "fixture.unverified",
                    out _)
                && manager.GetActive(
                    "fixture.invalid-payload") is null,
                "Invalid package bytes reached executor resolution/activation before verification.");
        });

        await Test("MB-81 source guard keeps retriever and PluginManager responsibilities separate", () =>
        {
            var repo = FindRepoRoot();
            var retriever = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Catalog",
                    "PackageRetriever.cs"));
            var pluginManager = File.ReadAllText(
                Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Plugins",
                    "PluginManager.cs"));

            Check(!retriever.Contains(
                    "using H2AgentLab.Plugins;",
                    StringComparison.Ordinal)
                && !retriever.Contains(
                    "InstallFromArchive(",
                    StringComparison.Ordinal)
                && !retriever.Contains(
                    "new PluginManager",
                    StringComparison.Ordinal)
                && !pluginManager.Contains(
                    "IPackageRetriever",
                    StringComparison.Ordinal)
                && !pluginManager.Contains(
                    "RetrieveAsync(",
                    StringComparison.Ordinal),
                "Package retrieval and PluginManager verification/install responsibilities were merged.");
            return Task.CompletedTask;
        });

        lines.Add(
            $"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(
            root,
            "mb-package-retriever-boundary-tests.txt");
        await File.WriteAllLinesAsync(
            report,
            lines);
        Console.WriteLine(
            string.Join(
                Environment.NewLine,
                lines));
        return failed == 0 ? 0 : 1;
    }

    private static PackageRetrievalRequest Request(
        string sourcePath,
        string hash,
        long MaxBytes,
        TimeSpan timeout)
        => new(
            "fixture.package",
            "1.0.0",
            sourcePath,
            hash,
            MaxBytes,
            timeout);

    private static RawPackageFixture RawFixture(
        string root,
        string name,
        int size)
    {
        var sourceRoot = Path.Combine(
            root,
            name + "-source");
        var stagingRoot = Path.Combine(
            root,
            name + "-staging");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(stagingRoot);
        var sourcePath = Path.Combine(
            sourceRoot,
            "fixture.h2pkg");
        var bytes = Enumerable.Range(0, size)
            .Select(i => (byte)(i % 251))
            .ToArray();
        File.WriteAllBytes(
            sourcePath,
            bytes);
        return new RawPackageFixture(
            sourceRoot,
            stagingRoot,
            sourcePath,
            bytes,
            "sha256:"
                + global::H2AgentLab.SafeWorkspace
                    .Hash(bytes)
                    .ToLowerInvariant());
    }

    private static string BuildInvalidPayloadPlugin(
        string sourceRoot)
    {
        var path = Path.Combine(
            sourceRoot,
            "invalid-payload.h2pkg");
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
                            name = "fixture.unverified",
                            @namespace = "fixture",
                            description = "Must never load before package verification.",
                            access = "ReadOnly",
                            risk = "Low",
                            supportsParallel = true,
                            schemaVersion = "v1",
                            toolVersion = "1.0.0",
                            resourceScope = "fixture",
                            serializationKey = "fixture",
                            schema = new
                            {
                                type = "function",
                                function = new
                                {
                                    name = "fixture.unverified",
                                    description = "Unverified fixture.",
                                    parameters = new
                                    {
                                        type = "object",
                                        properties = new { }
                                    }
                                }
                            }
                        }
                    }));
        }

        memory.Position = 0;
        var manifest = new H2PluginManifest(
            "fixture.invalid-payload",
            "Fixture Invalid Payload",
            "1.0.0",
            "2.0.0",
            "fixture.publisher",
            "sha256:" + new string('0', 64),
            ["fixture.unverified"],
            [],
            [],
            ["workspace.read"]);
        using (var zip = new ZipArchive(
            memory,
            ZipArchiveMode.Update,
            leaveOpen: true))
        {
            WriteZip(
                zip,
                "manifest.json",
                JsonSerializer.Serialize(manifest));
        }

        File.WriteAllBytes(
            path,
            memory.ToArray());
        return path;
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

    private static PluginInstallPolicy DeveloperPolicy()
        => new(
            PluginInstallMode.DeveloperLocal,
            new HashSet<string>(
                StringComparer.Ordinal)
            {
                "fixture.publisher"
            },
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);

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

    private sealed record RawPackageFixture(
        string SourceRoot,
        string StagingRoot,
        string SourcePath,
        byte[] Bytes,
        string Hash);

    private sealed class CountingPluginToolResolver
        : IPluginToolExecutorResolver
    {
        public int ResolveCount { get; private set; }

        public IAgentToolExecutor Resolve(
            H2PluginManifest manifest,
            PluginToolDefinition tool)
        {
            ResolveCount++;
            return new DelegatingToolExecutor(
                "mb81-unverified",
                (call, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        "{\"executed\":true}");
                });
        }
    }
}
