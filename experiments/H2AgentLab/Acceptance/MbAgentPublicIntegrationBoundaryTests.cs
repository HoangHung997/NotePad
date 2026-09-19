using System.Reflection;
using System.Text.Json;
using H2AgentLab.Integration;

namespace H2AgentLab.Acceptance;

public static class MbAgentPublicIntegrationBoundaryTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-121 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var results = new List<object>();
        var failed = 0;

        async Task Test(string id, string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add($"PASS {id} {name}");
                results.Add(new { id, name, passed = true, error = (string?)null });
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add($"FAIL {id} {name}: {ex.GetType().Name}: {ex.Message}");
                results.Add(new { id, name, passed = false, error = ex.Message });
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test(
            "MB121-SURFACE",
            "public boundary has exactly seven H2-facing operations and no provider/runtime types",
            () =>
            {
                var type = typeof(IAgentIntegrationBoundary);
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                var names = methods.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Check(names.SequenceEqual(new[]
                {
                    "Cancel",
                    "InspectTask",
                    "LoadProjectContext",
                    "ObserveProgress",
                    "ProvideApproval",
                    "StartTaskAsync",
                    "WaitForFinalResultAsync"
                }),
                    "IAgentIntegrationBoundary operation set drifted.");

                var forbidden = new[]
                {
                    "AiProfile",
                    "IAgentTransport",
                    "AgentRuntime",
                    "AgentOrchestrator",
                    "AgentTools",
                    "ToolRegistry",
                    "Provider",
                    "Model"
                };
                foreach (var method in methods)
                {
                    var signature = method.ReturnType.FullName + " "
                        + string.Join(" ", method.GetParameters().Select(x => x.ParameterType.FullName));
                    foreach (var item in forbidden)
                        Check(!signature.Contains(item, StringComparison.Ordinal),
                            $"Public integration method '{method.Name}' leaks internal type marker '{item}'.");
                }

                var repo = FindRepoRoot();
                var publicSource = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Integration",
                    "AgentIntegrationBoundary.cs"));
                foreach (var marker in new[]
                {
                    "AiProfile",
                    "IAgentTransport",
                    "AgentRuntimeFactory",
                    "AgentRuntime ",
                    "AgentOrchestrator",
                    "AgentTools",
                    "ToolRegistry"
                })
                    Check(!publicSource.Contains(marker, StringComparison.Ordinal),
                        "Public boundary source leaks implementation detail: " + marker);

                return Task.CompletedTask;
            });

        await Test(
            "MB121-LIFECYCLE",
            "loaded project context flows through start progress inspect evidence and final result",
            async () =>
            {
                var executor = new HappyExecutor();
                using var boundary = new AgentIntegrationCoordinator(executor);
                var workspace = Path.Combine(root, "lifecycle-workspace");
                Directory.CreateDirectory(workspace);
                boundary.LoadProjectContext(new AgentIntegrationProjectContext(
                    "project-1",
                    workspace,
                    "Project context fixture.",
                    version: 7));

                var taskId = await boundary.StartTaskAsync(
                    new AgentIntegrationTaskRequest(
                        "project-1",
                        "Read current project state and return a concise result.",
                        readOnly: true));

                var final = await boundary.WaitForFinalResultAsync(
                    taskId,
                    new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                var snapshot = boundary.InspectTask(taskId);
                var progress = boundary.ObserveProgress(taskId);

                Check(final.Status == AgentIntegrationTaskStatus.Completed
                      && final.Text == "integration-ok",
                    "Final result did not preserve executor completion.");
                Check(snapshot.Status == AgentIntegrationTaskStatus.Completed
                      && snapshot.FinalResult?.TaskId == taskId
                      && snapshot.Evidence.Count == 1,
                    "InspectTask did not expose terminal state/evidence.");
                Check(progress.Any(x => x.Code == "started")
                      && progress.Any(x => x.Code == "fixture-observed")
                      && progress.Any(x => x.Code == "completed"),
                    "Progress stream did not expose lifecycle/executor/final events.");
                Check(executor.SeenProject?.ProjectId == "project-1"
                      && executor.SeenProject.Version == 7
                      && executor.SeenProject.WorkspaceRoot == Path.GetFullPath(workspace),
                    "Executor did not receive the captured project context.");
                Check(boundary.ObserveProgress(taskId, progress[^2].Sequence).Count == 1,
                    "Progress cursor did not return only newer events.");
            });

        await Test(
            "MB121-APPROVAL",
            "approval handshake is task-scoped and supports grant and denial",
            async () =>
            {
                using var boundary = new AgentIntegrationCoordinator(new ApprovalExecutor());
                var workspace = Path.Combine(root, "approval-workspace");
                Directory.CreateDirectory(workspace);
                boundary.LoadProjectContext(new AgentIntegrationProjectContext(
                    "project-approval",
                    workspace,
                    "Approval fixture."));

                var allowedTask = await boundary.StartTaskAsync(
                    new AgentIntegrationTaskRequest(
                        "project-approval",
                        "Perform one approved fixture action.",
                        readOnly: false));
                var allowedSnapshot = await WaitUntil(
                    () => boundary.InspectTask(allowedTask),
                    x => x.Status == AgentIntegrationTaskStatus.WaitingForApproval,
                    TimeSpan.FromSeconds(5));
                var approval = allowedSnapshot.PendingApproval
                    ?? throw new InvalidOperationException("Pending approval was not projected.");

                Check(!boundary.ProvideApproval(
                        allowedTask,
                        Guid.NewGuid(),
                        approved: true),
                    "Wrong approval ID was accepted.");
                Check(boundary.ProvideApproval(
                        allowedTask,
                        approval.ApprovalId,
                        approved: true),
                    "Correct approval ID was not accepted.");

                var allowed = await boundary.WaitForFinalResultAsync(
                    allowedTask,
                    new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                Check(allowed.Status == AgentIntegrationTaskStatus.Completed
                      && allowed.Text == "approved",
                    "Granted approval did not continue to completion.");

                var deniedTask = await boundary.StartTaskAsync(
                    new AgentIntegrationTaskRequest(
                        "project-approval",
                        "Perform another approved fixture action.",
                        readOnly: false));
                var deniedSnapshot = await WaitUntil(
                    () => boundary.InspectTask(deniedTask),
                    x => x.Status == AgentIntegrationTaskStatus.WaitingForApproval,
                    TimeSpan.FromSeconds(5));
                Check(boundary.ProvideApproval(
                        deniedTask,
                        deniedSnapshot.PendingApproval!.ApprovalId,
                        approved: false),
                    "Denied approval response was not accepted by host.");

                var denied = await boundary.WaitForFinalResultAsync(
                    deniedTask,
                    new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                Check(denied.Status == AgentIntegrationTaskStatus.Blocked
                      && denied.Text == "approval-denied",
                    "Denied approval did not produce a blocked terminal result.");
            });

        await Test(
            "MB121-CANCEL-CONTEXT",
            "cancellation propagates and project context versions cannot silently regress",
            async () =>
            {
                using var boundary = new AgentIntegrationCoordinator(new BlockingExecutor());
                var workspace = Path.Combine(root, "cancel-workspace");
                Directory.CreateDirectory(workspace);
                boundary.LoadProjectContext(new AgentIntegrationProjectContext(
                    "project-cancel",
                    workspace,
                    "Context v2.",
                    version: 2));

                try
                {
                    boundary.LoadProjectContext(new AgentIntegrationProjectContext(
                        "project-cancel",
                        workspace,
                        "Context v1.",
                        version: 1));
                    throw new InvalidOperationException("Older project context replaced a newer version.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("cannot move backwards", StringComparison.Ordinal))
                {
                }

                var taskId = await boundary.StartTaskAsync(
                    new AgentIntegrationTaskRequest(
                        "project-cancel",
                        "Wait until host cancellation.",
                        readOnly: true));
                _ = await WaitUntil(
                    () => boundary.InspectTask(taskId),
                    x => x.Status == AgentIntegrationTaskStatus.Running,
                    TimeSpan.FromSeconds(5));

                boundary.Cancel(taskId);
                var final = await boundary.WaitForFinalResultAsync(
                    taskId,
                    new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
                Check(final.Status == AgentIntegrationTaskStatus.Cancelled,
                    "Cancellation did not reach terminal Cancelled state.");
                Check(boundary.ObserveProgress(taskId)
                    .Any(x => x.Code == "cancel-requested"),
                    "Cancellation was not visible in progress.");
            });

        await Test(
            "MB121-INTEGRATION-GATE",
            "boundary is documented while H2 Notes remains unintegrated before MB-122",
            () =>
            {
                var repo = FindRepoRoot();
                var document = File.ReadAllText(Path.Combine(
                    repo,
                    "docs",
                    "H2_AGENT_PUBLIC_INTEGRATION_BOUNDARY.md"));
                foreach (var method in new[]
                {
                    "LoadProjectContext",
                    "StartTaskAsync",
                    "ObserveProgress",
                    "InspectTask",
                    "Cancel",
                    "ProvideApproval",
                    "WaitForFinalResultAsync"
                })
                    Check(document.Contains(method, StringComparison.Ordinal),
                        "Boundary document is missing operation: " + method);

                Check(document.Contains(
                        "no H2 Notes production integration yet",
                        StringComparison.OrdinalIgnoreCase)
                      && document.Contains("MB-122", StringComparison.Ordinal),
                    "Boundary document lost the user-acceptance integration gate.");

                var src = Path.Combine(repo, "src");
                foreach (var path in Directory.EnumerateFiles(
                             src,
                             "*",
                             SearchOption.AllDirectories)
                         .Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                             || x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                {
                    var source = File.ReadAllText(path);
                    Check(!source.Contains("H2AgentLab.Integration", StringComparison.Ordinal)
                          && !source.Contains("experiments/H2AgentLab/H2AgentLab.csproj", StringComparison.Ordinal)
                          && !source.Contains("experiments\\H2AgentLab\\H2AgentLab.csproj", StringComparison.Ordinal),
                        "H2 Notes production source integrated Agent Lab before MB-122: " + path);
                }

                return Task.CompletedTask;
            });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-agent-public-integration-boundary-tests.txt"),
            lines);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-agent-public-integration-boundary-tests.json"),
            JsonSerializer.Serialize(new
            {
                task = "MB-121",
                passed = results.Count - failed,
                failed,
                results
            }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task<T> WaitUntil<T>(
        Func<T> read,
        Func<T, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = read();
            if (predicate(value))
                return value;
            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for Agent integration state.");
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

    private sealed class HappyExecutor : IAgentIntegrationExecutor
    {
        public AgentIntegrationProjectContext? SeenProject { get; private set; }

        public Task<AgentIntegrationExecutionResult> ExecuteAsync(
            AgentIntegrationExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SeenProject = context.Project;
            context.ReportProgress(
                "fixture",
                "fixture-observed",
                "Observed project fixture.");
            context.AddEvidence(new AgentIntegrationEvidence(
                "fixture:evidence:1",
                "fixture",
                new string('a', 64),
                "Deterministic fixture evidence."));
            return Task.FromResult(new AgentIntegrationExecutionResult(
                AgentIntegrationExecutionDisposition.Completed,
                "integration-ok"));
        }
    }

    private sealed class ApprovalExecutor : IAgentIntegrationExecutor
    {
        public async Task<AgentIntegrationExecutionResult> ExecuteAsync(
            AgentIntegrationExecutionContext context,
            CancellationToken cancellationToken)
        {
            var approved = await context.RequestApprovalAsync(
                "Approve fixture action",
                "Synthetic MB-121 approval; no external side effect.",
                cancellationToken);
            return approved
                ? new AgentIntegrationExecutionResult(
                    AgentIntegrationExecutionDisposition.Completed,
                    "approved")
                : new AgentIntegrationExecutionResult(
                    AgentIntegrationExecutionDisposition.Blocked,
                    "approval-denied");
        }
    }

    private sealed class BlockingExecutor : IAgentIntegrationExecutor
    {
        public async Task<AgentIntegrationExecutionResult> ExecuteAsync(
            AgentIntegrationExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
