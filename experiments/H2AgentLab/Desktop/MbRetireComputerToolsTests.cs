using System.Text.Json;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Tools;

namespace H2AgentLab.Desktop;

public static class MbRetireComputerToolsTests
{
    public static async Task<int> Run(
        string outputDirectory,
        string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-94 test directory.");
        Directory.CreateDirectory(root);
        hostExecutable = Path.GetFullPath(hostExecutable);

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

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-94 selected-window DesktopHost parity covers inspect click type and observe-after-act", async () =>
        {
            using var client = new DesktopHostClient(
                hostExecutable,
                fixtureMode: true,
                defaultTimeout: TimeSpan.FromSeconds(5));
            var target = (await client.ListWindowsAsync()).Single();
            using var controller = new SelectedDesktopWindowController(
                client,
                target,
                ownsClient: false);

            var approvals = 0;
            Task<bool> Yes(global::H2AgentLab.Approval request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                approvals++;
                return Task.FromResult(true);
            }

            var first = JsonSerializer.SerializeToElement(
                await controller.Inspect(Yes, CancellationToken.None));
            var button = first.GetProperty("controls")
                .EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Increment");
            var click = JsonSerializer.SerializeToElement(
                await controller.Act(
                    "click_control",
                    button.GetProperty("Token").GetString()!,
                    "",
                    Yes,
                    CancellationToken.None));
            Check(click.GetProperty("mutationVerifiedByNewObservation").GetBoolean()
                  && !click.GetProperty("verifiedTaskOutcome").GetBoolean(),
                "Desktop click did not require/obtain a newer observation or incorrectly claimed business success.");

            var second = JsonSerializer.SerializeToElement(
                await controller.Inspect(Yes, CancellationToken.None));
            var textbox = second.GetProperty("controls")
                .EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Fixture text");
            var typed = JsonSerializer.SerializeToElement(
                await controller.Act(
                    "type_control",
                    textbox.GetProperty("Token").GetString()!,
                    "mb94-typed",
                    Yes,
                    CancellationToken.None));
            Check(typed.GetProperty("mutationVerifiedByNewObservation").GetBoolean()
                  && typed.GetProperty("valueObserved").GetBoolean(),
                "Desktop type did not verify the post-action observed value.");

            var third = JsonSerializer.SerializeToElement(
                await controller.Inspect(Yes, CancellationToken.None));
            Check(third.GetProperty("controls")
                    .EnumerateArray()
                    .Any(x => x.GetProperty("name").GetString() == "Fixture text"
                        && x.GetProperty("Value").GetString() == "mb94-typed"),
                "Explicit post-action inspect did not observe the typed value.");
            Check(approvals == 5,
                "Selected-window compatibility contract did not ask once per read/action.");
        });

        await Test("MB-94 denied desktop mutation fails before DesktopHost changes state", async () =>
        {
            using var client = new DesktopHostClient(
                hostExecutable,
                fixtureMode: true,
                defaultTimeout: TimeSpan.FromSeconds(5));
            var target = (await client.ListWindowsAsync()).Single();
            using var controller = new SelectedDesktopWindowController(
                client,
                target,
                ownsClient: false);

            Task<bool> Yes(global::H2AgentLab.Approval request, CancellationToken cancellationToken)
                => Task.FromResult(true);
            Task<bool> No(global::H2AgentLab.Approval request, CancellationToken cancellationToken)
                => Task.FromResult(false);

            var inspected = JsonSerializer.SerializeToElement(
                await controller.Inspect(Yes, CancellationToken.None));
            var button = inspected.GetProperty("controls")
                .EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Increment");
            var before = await client.ObserveAsync(target.SessionId);

            try
            {
                _ = await controller.Act(
                    "click_control",
                    button.GetProperty("Token").GetString()!,
                    "",
                    No,
                    CancellationToken.None);
                throw new InvalidOperationException("Denied desktop mutation executed.");
            }
            catch (global::H2AgentLab.AgentFaultException ex) when (
                ex.Code == "denied")
            {
            }

            var after = await client.ObserveAsync(target.SessionId);
            Check(before.StateId == after.StateId
                  && before.ObservedMutationId is null
                  && after.ObservedMutationId is null,
                "Denied desktop mutation changed fixture state.");
        });

        await Test("MB-94 normal ToolRegistry desktop executor uses DesktopHost selected-window controller", async () =>
        {
            using var client = new DesktopHostClient(
                hostExecutable,
                fixtureMode: true,
                defaultTimeout: TimeSpan.FromSeconds(5));
            var target = (await client.ListWindowsAsync()).Single();
            var workspace = Path.Combine(root, "runtime-workspace");
            var state = Path.Combine(root, "runtime-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            using var host = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                (_, _) => { })
            {
                ReadOnly = false,
                Desktop = new SelectedDesktopWindowController(
                    client,
                    target,
                    ownsClient: false)
            };

            var registry = NormalRuntimeToolRegistry.Create(host);
            var inspect = Get(registry, "inspect_window");
            var inspectionJson = await inspect.Executor.ExecuteAsync(
                Call("inspect_window", new { reason = "MB-94 fixture" }),
                CancellationToken.None);
            using var inspection = JsonDocument.Parse(inspectionJson);
            var button = inspection.RootElement.GetProperty("controls")
                .EnumerateArray()
                .Single(x => x.GetProperty("name").GetString() == "Increment");

            var click = Get(registry, "click_control");
            var clickJson = await click.Executor.ExecuteAsync(
                Call("click_control", new
                {
                    token = button.GetProperty("Token").GetString()!
                }),
                CancellationToken.None);
            using var clicked = JsonDocument.Parse(clickJson);
            Check(clicked.RootElement
                    .GetProperty("mutationVerifiedByNewObservation")
                    .GetBoolean(),
                "Normal runtime desktop executor bypassed DesktopHost observe-after-act evidence.");
        });

        await Test("MB-94 repository has no legacy ComputerTools runtime path", () =>
        {
            var repo = FindRepoRoot();
            var legacyPath = Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "ComputerTools.cs");
            Check(!File.Exists(legacyPath),
                "Legacy ComputerTools.cs still exists.");

            var self = Path.GetFullPath(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Desktop",
                "MbRetireComputerToolsTests.cs"));

            foreach (var path in Directory.EnumerateFiles(
                         Path.Combine(repo, "experiments", "H2AgentLab"),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(path).Equals(self, StringComparison.OrdinalIgnoreCase))
                    continue;
                var source = File.ReadAllText(path);
                foreach (var forbidden in new[]
                {
                    "new ComputerTools(",
                    "ComputerTools.",
                    "ComputerTools?",
                    "ComputerTools Worker",
                    "ComputerTools("
                })
                {
                    Check(!source.Contains(forbidden, StringComparison.Ordinal),
                        "C# source still depends on retired ComputerTools symbol '" + forbidden + "': " + path);
                }
                Check(!source.Contains("--computer-worker", StringComparison.Ordinal),
                    "C# source still exposes legacy computer worker: " + path);
                Check(!source.Contains("WindowTarget", StringComparison.Ordinal)
                      && !source.Contains("ComputerRequest", StringComparison.Ordinal)
                      && !source.Contains("ControlInfo", StringComparison.Ordinal),
                    "C# source still references a legacy ComputerTools protocol type: " + path);
            }

            var agentTools = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "AgentTools.cs"));
            var normalRuntime = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Tools",
                "NormalRuntimeToolRegistry.cs"));
            var labWindow = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "LabWindow.cs"));
            var project = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "H2AgentLab.csproj"));
            var safety = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab.DesktopHost",
                "DesktopSafetyPolicy.cs"));

            Check(agentTools.Contains("SelectedDesktopWindowController", StringComparison.Ordinal)
                  && normalRuntime.Contains("Host.Desktop", StringComparison.Ordinal)
                  && labWindow.Contains("SelectedDesktopWindowController.ListAsync", StringComparison.Ordinal),
                "Production UI/runtime is not wired through DesktopHost selected-window controller.");
            Check(project.Contains("H2AgentLab.DesktopHost.csproj", StringComparison.Ordinal)
                  && project.Contains("CopyDesktopHostAfterBuild", StringComparison.Ordinal),
                "Agent Lab output does not build/copy the isolated DesktopHost.");
            Check(safety.Contains(
                    "H2 Agent Lab · Vùng thử an toàn",
                    StringComparison.Ordinal)
                  && safety.Contains(
                    "normalizedProcess.Equals(\"H2AgentLab\"",
                    StringComparison.Ordinal),
                "DesktopHost did not preserve the legacy H2 Agent Lab self-target boundary.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-retire-computer-tools-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolDescriptor Get(ToolRegistry registry, string name)
        => registry.TryGet(name, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException(
                "Missing normal runtime descriptor: " + name);

    private static global::H2AgentLab.ToolCall Call(
        string name,
        object arguments)
        => new(
            "mb94-" + name,
            name,
            JsonSerializer.SerializeToElement(arguments));

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
