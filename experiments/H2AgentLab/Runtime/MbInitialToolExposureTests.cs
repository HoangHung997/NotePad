using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbInitialToolExposureTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-30 test directory.");
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

        await Test("MB-30 first request exposes only tool_search plus minimum stable core schema", async () =>
        {
            var workspace = Path.Combine(root, "workspace");
            var state = Path.Combine(root, "state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { });

            var registry = NormalRuntimeToolRegistry.Create(tools);
            AgentTransportStartRequest? captured = null;
            await using var runtime = new AgentRuntime(
                new CaptureTransport(request => captured = request),
                new AgentContextManager(),
                registry);

            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Inspect workspace safely.",
                "workspace",
                null,
                null,
                ["do not mutate"],
                ["answer"],
                [],
                AgentTaskRiskClass.ReadOnly,
                new AgentVerificationPolicy(requireVerification: false));

            _ = await runtime.RunAsync(
                new AgentRuntimeRequest(
                    contract,
                    "Inspect workspace safely.",
                    new AgentPromptStablePrefix(
                        AgentVersions.Current,
                        "BASE POLICY",
                        "SECURITY POLICY",
                        "MODEL POLICY",
                        ""),
                    new AgentContextInput(
                        TaskContract: contract.UserGoal,
                        CurrentState: "fixture"),
                    PromptCacheKey: "mb30"),
                CancellationToken.None);

            var start = captured
                ?? throw new InvalidOperationException("Transport start request was not captured.");
            var names = start.Tools.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Check(names.Contains(DeferredToolDiscovery.SearchToolName, StringComparer.Ordinal),
                "Initial V2 request did not expose tool_search.");
            Check(names.All(x => x is DeferredToolDiscovery.SearchToolName or "update_plan"),
                "Initial V2 request exposed a non-core detailed tool schema: " + string.Join(", ", names));

            foreach (var heavy in new[]
            {
                "list_running_apps",
                "launch_app",
                "wait_for_app_window",
                "activate_app",
                "run_python",
                "word_paragraphs",
                "check_word",
                "inspect_window",
                "click_control",
                "type_control"
            })
            {
                Check(!names.Contains(heavy, StringComparer.Ordinal),
                    "Heavy tool schema leaked into initial V2 request: " + heavy);
            }

            var fullBytes = JsonSerializer.SerializeToUtf8Bytes(
                registry.Tools.Select(x => x.CallableSchema).ToArray()).Length;
            var initialBytes = JsonSerializer.SerializeToUtf8Bytes(start.Tools).Length;
            Check(initialBytes < fullBytes,
                $"Initial V2 tool schema bytes ({initialBytes}) are not smaller than the full canonical registry ({fullBytes}).");
            Check(initialBytes * 2 < fullBytes,
                $"Initial V2 tool surface was not materially smaller: {initialBytes} vs {fullBytes} bytes.");

            var prompt = string.Join(
                "\n",
                start.Messages
                    .Where(x => x.Role == AgentTransportMessageRole.System)
                    .Select(x => x.Content));
            Check(prompt.Contains("Available tool namespaces:", StringComparison.Ordinal),
                "Initial request lost bounded namespace metadata.");
            Check(prompt.Contains("files", StringComparison.Ordinal)
                && prompt.Contains("office", StringComparison.Ordinal)
                && prompt.Contains("desktop", StringComparison.Ordinal)
                && prompt.Contains("python", StringComparison.Ordinal),
                "Namespace metadata did not advertise deferred capability families.");
        });

        await Test("MB-30 normal AgentRuntime source never serializes AgentTools.Definitions", () =>
        {
            var repo = FindRepoRoot();
            var runtimeSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Runtime", "AgentRuntime.cs"));
            var orchestratedSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));

            Check(!runtimeSource.Contains("AgentTools.Definitions", StringComparison.Ordinal),
                "AgentRuntime directly serializes legacy AgentTools.Definitions.");
            Check(!orchestratedSource.Contains("AgentTools.Definitions", StringComparison.Ordinal),
                "Normal orchestrated path serializes legacy AgentTools.Definitions.");
            Check(runtimeSource.Contains("BuildInitialExposure", StringComparison.Ordinal),
                "AgentRuntime is not using deferred initial tool exposure.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-initial-tool-exposure-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private sealed class CaptureTransport : IAgentTransport
    {
        private readonly Action<AgentTransportStartRequest> _capture;

        public CaptureTransport(Action<AgentTransportStartRequest> capture)
        {
            _capture = capture;
        }

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _capture(request);
            yield return AgentTransportEvent.TextDeltaEvent("ok");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb30", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("MB-30 fixture should not continue.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
