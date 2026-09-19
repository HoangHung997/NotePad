using System.Net;
using System.Text.Json;
using H2AgentLab.Computer;
using H2AgentLab.Desktop;
using H2AgentLab.Office;
using H2AgentLab.Phase10;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Web;

namespace H2AgentLab.Acceptance;

public static class MbPermissionScopeGateTests
{
    public static async Task<int> Run(
        string outputDirectory,
        string desktopHostExecutable,
        string officeHostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-104 test directory.");
        Directory.CreateDirectory(root);

        desktopHostExecutable = Path.GetFullPath(desktopHostExecutable);
        officeHostExecutable = Path.GetFullPath(officeHostExecutable);

        var results = new List<BoundaryResult>();

        async Task Test(string domain, string name, Func<Task> action)
        {
            try
            {
                await action();
                results.Add(new BoundaryResult(domain, name, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new BoundaryResult(
                    domain,
                    name,
                    false,
                    ex.GetType().Name + ": " + ex.Message));
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("core", "host permission policy remains authoritative", async () =>
        {
            var code = await MbPermissionRuntimeTests.Run(
                Path.Combine(root, "core-permission"));
            Check(code == 0, "MB-40 host permission regression suite failed.");
        });

        await Test("filesystem", "workspace and mutation policy fail closed", () =>
        {
            var workspaceRoot = Path.Combine(root, "filesystem-workspace");
            var stateRoot = Path.Combine(root, "filesystem-state");
            Directory.CreateDirectory(workspaceRoot);
            File.WriteAllText(Path.Combine(workspaceRoot, "allowed.txt"), "safe");
            var scope = new global::H2AgentLab.SafeWorkspace(workspaceRoot);
            var files = new FilesystemCapabilities(
                scope,
                stateRoot,
                new FilesystemCapabilityPolicy(
                    AllowWrite: false,
                    AllowMove: false,
                    AllowDelete: false));

            Check(files.Read("allowed.txt").Length == 4,
                "Allowed workspace read failed.");

            foreach (var blocked in new[]
            {
                "../escape.txt",
                "C:\\Windows\\win.ini",
                ".env",
                ".ssh/id_rsa"
            })
            {
                var rejected = false;
                try { _ = files.Read(blocked); }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException)
                {
                    rejected = true;
                }
                Check(rejected, "Filesystem boundary accepted blocked path: " + blocked);
            }

            var writeRejected = false;
            try
            {
                _ = files.Write(
                    "new.txt",
                    System.Text.Encoding.UTF8.GetBytes("no"),
                    "");
            }
            catch (UnauthorizedAccessException)
            {
                writeRejected = true;
            }
            Check(writeRejected && !File.Exists(Path.Combine(workspaceRoot, "new.txt")),
                "Read-only filesystem policy permitted mutation.");
            return Task.CompletedTask;
        });

        await Test("process-shell", "allow-list timeout and secret environment boundary", async () =>
        {
            var workspaceRoot = Path.Combine(root, "process-workspace");
            Directory.CreateDirectory(workspaceRoot);
            var scope = new global::H2AgentLab.SafeWorkspace(workspaceRoot);

            using (var denied = new ProcessShellCapabilities(
                       scope,
                       new ProcessShellPolicy(
                           new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cmd" },
                           AllowStart: false,
                           AllowTerminate: false,
                           DefaultTimeout: TimeSpan.FromSeconds(2))))
            {
                var rejected = false;
                try
                {
                    _ = await denied.RunBoundedAsync(
                        "cmd",
                        ["/d", "/c", "echo should-not-run"]);
                }
                catch (UnauthorizedAccessException)
                {
                    rejected = true;
                }
                Check(rejected, "Process policy allowed start while AllowStart=false.");
            }

            using var process = new ProcessShellCapabilities(
                scope,
                new ProcessShellPolicy(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cmd" },
                    AllowStart: true,
                    AllowTerminate: false,
                    DefaultTimeout: TimeSpan.FromSeconds(5)));

            var notAllowed = false;
            try
            {
                _ = await process.RunBoundedAsync(
                    "powershell",
                    ["-NoProfile", "-Command", "Write-Output blocked"]);
            }
            catch (UnauthorizedAccessException)
            {
                notAllowed = true;
            }
            Check(notAllowed, "Executable allow-list accepted an unapproved shell.");

            var old = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "MB104_SECRET_SENTINEL");
            try
            {
                var result = await process.RunBoundedAsync(
                    "cmd",
                    ["/d", "/c", "if defined OPENAI_API_KEY (echo SECRET_PRESENT) else (echo SECRET_ABSENT)"]);
                Check(result.ExitCode == 0
                      && result.Stdout.Contains("SECRET_ABSENT", StringComparison.Ordinal)
                      && !result.Stdout.Contains("MB104_SECRET_SENTINEL", StringComparison.Ordinal),
                    "Bounded process inherited an API secret.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", old);
            }
        });

        await Test("plugin", "package trust permission and helper boundaries stay green", async () =>
        {
            var code = await V2Phase10Tests.Run(
                Path.Combine(root, "plugin-boundaries"));
            Check(code == 0, "Phase-10 plugin/package boundary corpus failed.");

            var unsafeManifest = JsonSerializer.Serialize(new
            {
                id = "mb104.plugin",
                name = "MB104",
                version = "1.0.0",
                minAgentVersion = "2.0.0",
                publisher = "fixture",
                packageHash = "sha256:" + new string('a', 64),
                capabilities = Array.Empty<string>(),
                skills = Array.Empty<string>(),
                providers = Array.Empty<string>(),
                permissions = Array.Empty<string>(),
                nativeHelpers = new[] { "../escape.exe" }
            });
            var rejected = false;
            try { _ = H2PluginManifest.Parse(unsafeManifest); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Plugin manifest accepted traversal native-helper path.");
        });

        await Test("provider", "provider scope policy separates read and mutation authority", () =>
        {
            var policy = new CapabilityProviderPolicy(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "provider:fixture.read"
                },
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "provider:fixture.write-approved"
                },
                AllowParallelReadOnly: true);

            Check(policy.Allows(
                    "provider:fixture.read",
                    AgentToolAccess.ReadOnly),
                "Approved provider read scope was denied.");
            Check(!policy.Allows(
                    "provider:fixture.read",
                    AgentToolAccess.Mutating),
                "Read scope silently granted mutation authority.");
            Check(!policy.Allows(
                    "provider:unknown",
                    AgentToolAccess.ReadOnly),
                "Unknown provider scope was implicitly allowed.");
            Check(policy.Allows(
                    "provider:fixture.write-approved",
                    AgentToolAccess.Mutating),
                "Explicit provider mutation scope was not honored.");

            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Providers",
                "McpToolProvider.cs"));
            Check(source.Contains("_policy.Allows(", StringComparison.Ordinal)
                  && source.Contains("not allowed", StringComparison.Ordinal),
                "Provider execution no longer enforces CapabilityProviderPolicy scope.");
            return Task.CompletedTask;
        });

        await Test("desktop", "DesktopHost permission stale-state and observe-after-act corpus", async () =>
        {
            var code = await V2DesktopHostTests.Run(
                Path.Combine(root, "desktop-boundaries"),
                desktopHostExecutable);
            Check(code == 0, "DesktopHost boundary corpus failed.");
        });

        await Test("office", "OfficeHost permission stale-state and preservation corpus", async () =>
        {
            var code = await V2OfficeHostTests.Run(
                Path.Combine(root, "office-boundaries"),
                officeHostExecutable);
            Check(code == 0, "OfficeHost boundary corpus failed.");
        });

        await Test("web", "web access is connected explicit bounded and HTTP-only", async () =>
        {
            using var http = new HttpClient(new NoNetworkHandler());
            var backend = new HttpWebResearchBackend(http);
            await using var host = new WebResearchHost(backend);

            var disconnectedRejected = false;
            try
            {
                _ = await host.SearchAsync("fixture", 1);
            }
            catch (InvalidOperationException)
            {
                disconnectedRejected = true;
            }
            Check(disconnectedRejected,
                "WebResearchHost executed before provider connection.");

            await host.ConnectAsync(CancellationToken.None);
            var summaries = await host.ListToolSummariesAsync(CancellationToken.None);
            Check(summaries.Count > 0
                  && summaries.All(x => !string.IsNullOrWhiteSpace(x.ResourceScope))
                  && summaries.All(x => x.ResourceScope == "web:public"),
                "Web tools lost explicit public-web resource scope.");

            var badUrlRejected = false;
            try
            {
                _ = await backend.OpenBrowserFallbackAsync(
                    "file:///C:/Windows/win.ini",
                    CancellationToken.None);
            }
            catch (ArgumentException)
            {
                badUrlRejected = true;
            }
            Check(badUrlRejected,
                "Web backend accepted a non-HTTP/HTTPS URL.");
        });

        await Test("native-helper", "native helpers stay isolated behind current-user-only IPC", () =>
        {
            var repo = FindRepoRoot();
            var desktopProject = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab.DesktopHost",
                "H2AgentLab.DesktopHost.csproj"));
            var officeProject = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab.OfficeHost",
                "H2AgentLab.OfficeHost.csproj"));
            Check(!desktopProject.Contains("H2Notes.Core", StringComparison.Ordinal)
                  && !desktopProject.Contains("../H2AgentLab/H2AgentLab.csproj", StringComparison.Ordinal)
                  && !officeProject.Contains("H2Notes.Core", StringComparison.Ordinal)
                  && !officeProject.Contains("../H2AgentLab/H2AgentLab.csproj", StringComparison.Ordinal),
                "Native helper project gained model/core application coupling.");

            foreach (var path in new[]
            {
                Path.Combine(repo, "experiments", "H2AgentLab.DesktopHost", "DesktopHostServer.cs"),
                Path.Combine(repo, "experiments", "H2AgentLab.OfficeHost", "OfficeHostServer.cs")
            })
            {
                var source = File.ReadAllText(path);
                Check(source.Contains("PipeOptions.CurrentUserOnly", StringComparison.Ordinal),
                    "Native helper IPC is not current-user-only: " + path);
            }
            return Task.CompletedTask;
        });

        await Test("secrets-metadata", "secret values never enter safe provider metadata", () =>
        {
            const string secret = "MB104_SUPER_SECRET_VALUE";
            var definition = new McpServerDefinition(
                "mb104-provider",
                "1.0.0",
                "mb104-server",
                "fixture-command.exe",
                ["--token", "not-a-real-token"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OPENAI_API_KEY"] = secret,
                    ["SAFE_FLAG"] = "1"
                },
                TimeSpan.FromSeconds(5));

            var json = JsonSerializer.Serialize(definition.SafeMetadata());
            Check(json.Contains("OPENAI_API_KEY", StringComparison.Ordinal)
                  && json.Contains("SAFE_FLAG", StringComparison.Ordinal)
                  && !json.Contains(secret, StringComparison.Ordinal),
                "Safe provider metadata exposed secret environment values.");

            var sandbox = global::H2AgentLab.WindowsPythonSandbox.SecurityProfile;
            foreach (var key in new[]
            {
                "OPENAI_API_KEY",
                "ANTHROPIC_API_KEY",
                "GITHUB_TOKEN",
                "AZURE_OPENAI_API_KEY"
            })
            {
                Check(!sandbox.InheritedEnvironmentKeys.Contains(
                        key,
                        StringComparer.OrdinalIgnoreCase),
                    "Python sandbox inherited secret environment metadata: " + key);
            }
            return Task.CompletedTask;
        });

        var violations = results.Count(x => !x.Passed);
        var lines = results
            .Select(x => (x.Passed ? "PASS " : "FAIL ")
                + x.Domain + " · " + x.Name
                + (x.Error is null ? "" : ": " + x.Error))
            .ToList();
        lines.Add(
            $"RESULT: {results.Count - violations} passed, {violations} failed. Boundary violations: {violations}.");
        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-permission-scope-gate-tests.txt"),
            lines);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-permission-scope-gate-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-104",
                    passed = violations == 0,
                    boundaryViolations = violations,
                    results
                },
                new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return violations == 0 ? 0 : 1;
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

    private sealed record BoundaryResult(
        string Domain,
        string Name,
        bool Passed,
        string? Error);

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "MB-104 web fixture must not perform real network I/O.");
    }
}
