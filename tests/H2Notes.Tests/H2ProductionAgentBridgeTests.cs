using System.Reflection;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ProductionAgentBridgeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("H2M-093 startup composes concrete production Agent adapter", () =>
        {
            var app = new App();
            typeof(App).GetMethod("ComposeProductionAgent", Private)!.Invoke(app, null);
            Check(app.AgentAdapter is H2ProductionAgentAdapter,
                "H2 startup left the unavailable/fake adapter as production runtime.");
            (app.AgentAdapter as IDisposable)?.Dispose();
        });

        test("H2M-093 concrete bridge executes project task through real AgentRuntime and captures evidence", () =>
        {
            var root = Temp("project");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "bridge evidence");
            try
            {
                var project = new ProjectRecord
                {
                    Name = "Bridge project",
                    UpdatedAtUtc = DateTime.UtcNow
                };
                var host = new H2ProjectToolHost(id => id == project.Id ? project : null);
                var transport = new RuntimeScriptTransportFactory();
                using var adapter = Adapter(root, transport);
                adapter.BindProjectToolHost(host);

                var taskId = adapter.StartTaskAsync(
                    project.Id,
                    "Read fixture.txt and report what it contains.",
                    new H2AgentTaskContext(root, "Project=Bridge project", project.UpdatedAtUtc!.Value.Ticks),
                    readOnly: true).GetAwaiter().GetResult();

                var done = Wait(adapter, taskId);
                Check(done.Status == H2AgentTaskStatus.Completed,
                    "Concrete production bridge did not reach completed host state: " + done.Status);
                Check(done.FinalText == "AgentRuntime bridge answer.",
                    "Final text did not come back through concrete bridge.");
                Check(done.Evidence.Count > 0,
                    "Real AgentRuntime tool execution produced no evidence.");
                Check(transport.StartCalls == 1 && transport.ContinueCalls >= 2,
                    "Bridge did not execute the real deferred AgentRuntime turn/continuation path.");
                Check(transport.TaskIds.Single() == taskId,
                    "H2 TaskId was not preserved into AgentRuntime transport.");
                Check(adapter.GetEvidence(done.Evidence[0].EvidenceId) is not null,
                    "Evidence lookup is not wired to Agent-owned task state.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-093 quick task supports null ProjectId later attachment and restart durability", () =>
        {
            var root = Temp("quick");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "quick evidence");
            var stateRoot = Path.Combine(root, ".agent-state");
            var project = new ProjectRecord
            {
                Name = "Later project",
                UpdatedAtUtc = DateTime.UtcNow
            };
            var before = JsonSerializer.Serialize(project);
            Guid taskId;
            string evidenceId;
            try
            {
                using (var adapter = Adapter(root, new RuntimeScriptTransportFactory(), stateRoot))
                {
                    adapter.BindProjectToolHost(new H2ProjectToolHost(
                        id => id == project.Id ? project : null));

                    taskId = adapter.StartTaskAsync(
                        projectId: null,
                        goal: "Read fixture.txt as quick work.",
                        context: new H2AgentTaskContext(root, "Foreground quick work"),
                        readOnly: true).GetAwaiter().GetResult();

                    var done = Wait(adapter, taskId);
                    Check(done.ProjectId is null && done.Status == H2AgentTaskStatus.Completed,
                        "Unscoped production quick task did not complete as ProjectId=null.");
                    evidenceId = done.Evidence.First().EvidenceId;

                    Check(adapter.AttachProject(taskId, project.Id),
                        "Production quick task could not later attach to project.");
                    Check(adapter.GetTaskSummary(taskId).ProjectId == project.Id,
                        "Attached project correlation was not projected.");
                    Check(before == JsonSerializer.Serialize(project),
                        "Attachment copied Agent state into ProjectRecord.");
                }

                using var restarted = Adapter(root, new RuntimeScriptTransportFactory(), stateRoot);
                var recovered = restarted.GetTaskSummary(taskId);
                Check(recovered.Status == H2AgentTaskStatus.Completed
                    && recovered.ProjectId == project.Id,
                    "Completed task summary was not durable across adapter restart.");
                Check(restarted.GetEvidence(evidenceId) is not null,
                    "Agent evidence was not durable across adapter restart.");
                Check(restarted.GetRecentTasks(project.Id, 10).Any(x => x.TaskId == taskId),
                    "Durable recent project task query lost the attached quick task.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-093 production bridge keeps runtime internals out of H2 product UI", () =>
        {
            var repo = FindRepoRoot();
            var app = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));
            var adapter = File.ReadAllText(Path.Combine(repo, "experiments", "H2AgentLab", "Integration", "H2ProductionAgentAdapter.cs"));

            Check(app.Contains("H2ProductionAgentAdapter", StringComparison.Ordinal)
                && app.Contains("ComposeProductionAgent", StringComparison.Ordinal),
                "H2 composition root does not wire concrete production bridge.");
            foreach (var forbidden in new[]
            {
                "H2AgentLab.Runtime",
                "H2AgentLab.Transport",
                "ToolRegistry",
                "AgentOrchestrator",
                "AgentRuntimeFactory"
            })
                Check(!app.Contains(forbidden, StringComparison.Ordinal),
                    "H2 product UI leaked Agent runtime internal: " + forbidden);

            Check(adapter.Contains("AgentOrchestrator", StringComparison.Ordinal)
                && adapter.Contains("AgentRuntimeFactory", StringComparison.Ordinal)
                && adapter.Contains("AgentRuntimeRequest", StringComparison.Ordinal),
                "Concrete bridge is not backed by accepted AgentRuntime.");
            foreach (var forbidden in new[]
            {
                "QuickWorkSession",
                "ProjectState",
                "H2AgentTaskDatabase"
            })
                Check(!adapter.Contains(forbidden, StringComparison.Ordinal),
                    "Production bridge introduced duplicate H2 task/state subsystem: " + forbidden);
        });
    }

    private static H2ProductionAgentAdapter Adapter(
        string workspace,
        RuntimeScriptTransportFactory transport,
        string? stateRoot = null)
        => new(
            stateRoot ?? Path.Combine(workspace, ".agent-state"),
            () => new H2ProductionAgentModel(
                new AiProfile
                {
                    Name = "CI transport",
                    Protocol = AiProtocol.OpenAiChat,
                    BaseUrl = "https://example.test/v1",
                    Model = "ci-agent"
                },
                ""),
            transport);

    private static H2AgentTaskSummary Wait(
        IH2AgentAdapter adapter,
        Guid taskId,
        int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var summary = adapter.GetTaskSummary(taskId);
            if (summary.Status is H2AgentTaskStatus.Completed
                or H2AgentTaskStatus.Blocked
                or H2AgentTaskStatus.Cancelled
                or H2AgentTaskStatus.Failed)
                return summary;
            Thread.Sleep(20);
        }

        throw new TimeoutException("Timed out waiting for concrete production Agent bridge.");
    }

    private sealed class RuntimeScriptTransportFactory : IAgentTransportFactory
    {
        public int StartCalls { get; private set; }
        public int ContinueCalls { get; private set; }
        public List<Guid> TaskIds { get; } = [];

        public IAgentTransport Create(
            AiProfile profile,
            string apiKey,
            AgentRunTelemetry telemetry)
            => new RuntimeScriptTransport(this);

        private sealed class RuntimeScriptTransport : IAgentTransport
        {
            private readonly RuntimeScriptTransportFactory _owner;
            private int _continuations;

            public RuntimeScriptTransport(RuntimeScriptTransportFactory owner)
                => _owner = owner;

            public AgentTransportCapabilities Capabilities
                => AgentTransportCapabilities.ChatCompletionsFallback;

            public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
                AgentTransportStartRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _owner.StartCalls++;
                _owner.TaskIds.Add(request.TaskId);
                yield return AgentTransportEvent.Started("ci-start");
                yield return AgentTransportEvent.Tool(new AgentTransportToolCall(
                    "call-search",
                    "tool_search",
                    "{\"query\":\"read file workspace\"}"));
                yield return AgentTransportEvent.Complete("ci-start", "tool_calls");
                await Task.CompletedTask;
            }

            public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
                AgentTransportContinuationRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _owner.ContinueCalls++;
                _continuations++;
                yield return AgentTransportEvent.Started("ci-" + _continuations);

                if (_continuations == 1)
                {
                    Check(request.NewlyLoadedTools?.Any(x => x.Name == "read_file") == true,
                        "Deferred tool_search did not load read_file schema.");
                    yield return AgentTransportEvent.Tool(new AgentTransportToolCall(
                        "call-read",
                        "read_file",
                        "{\"path\":\"fixture.txt\",\"offset\":\"0\"}"));
                    yield return AgentTransportEvent.Complete("ci-read", "tool_calls");
                }
                else
                {
                    yield return AgentTransportEvent.TextDeltaEvent("AgentRuntime bridge answer.");
                    yield return AgentTransportEvent.Complete("ci-final", "stop");
                }

                await Task.CompletedTask;
            }

            public void Cancel() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static string Temp(string name)
        => Path.Combine(Path.GetTempPath(), "h2-m093-" + name + "-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
