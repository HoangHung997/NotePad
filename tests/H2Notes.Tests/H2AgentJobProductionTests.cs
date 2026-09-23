using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Transport;
using H2Notes.Core;

/// <summary>E2: the ordinary production factory/registry, real owned PowerShell subprocesses,
/// real disposable files and Agent journal. Only the model transport is scripted. No network,
/// personal documents, external service, Office or E4/E5 certification.</summary>
internal static class H2AgentJobProductionTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
            test("AR-040 production stable job poll output result and archive roundtrip " + (project ? "Project" : "Global"), () => InWorkspace(root =>
            {
                File.WriteAllText(Path.Combine(root, "unchanged.txt"), "KEEP");
                var wire = new Wire("[IO.File]::AppendAllText('count.txt','ONE'); [Console]::Write('READY'); Start-Sleep -Seconds 2; [Console]::Write('ĐÃ XONG')");
                var (summary, progress) = Execute(root, wire, project: project);
                Check(summary.Status == H2AgentTaskStatus.Completed, Detail(summary, wire));
                Check(File.ReadAllText(Path.Combine(root, "count.txt")) == "ONE" && File.ReadAllText(Path.Combine(root, "unchanged.txt")) == "KEEP", "Effect duplicated or unrelated fixture modified.");
                Check(wire.Results.Count(x => x.ToolName == "start_command_job") == 1 && wire.Polls >= 2, "Job was not polled without a replacement process.");
                var started = wire.Results.Single(x => x.ToolName == "start_command_job").Outcome!;
                Check(started.Job?.JobId == wire.JobId && started.Status == H2AgentLab.Tools.ToolOutcomeStatus.Running, "Start did not retain pending job identity.");
                Check(progress.Any(p => p.ToolOutcome?.JobId == wire.JobId && p.ToolOutcome.Status == H2ToolRunStatus.Running)
                    && progress.Any(p => p.ToolOutcome?.JobId == wire.JobId && p.ToolOutcome.Status == H2ToolRunStatus.Succeeded), "Production progress lost the running-to-terminal transition.");
                Check(wire.Output.Contains("ĐÃ XONG", StringComparison.Ordinal), "Real Unicode output did not reach the wire.");
                using var reopened = new H2ProductionAgentAdapter(Path.Combine(root, "state"), Model);
                var restored = reopened.GetTaskSummary(summary.TaskId);
                var op = restored.Recovery!.Operations.Single(o => o.ToolName == "start_command_job");
                Check(restored.Status == H2AgentTaskStatus.Completed && !restored.Recovery.ReconcileRequired
                    && op.Job is { Status: "Succeeded", RootExited: true, AllProcessesExited: true, StreamsDrained: true, ExitCode: 0 }
                    && op.Job.JobId == wire.JobId && op.Job.OwnerTaskId == summary.TaskId && op.Job.OutputArtifacts.Count == 2,
                    "Archive lost native terminal ownership or left stale Running debt.");
                var store = new ArtifactStore(Path.Combine(root, "state", "tasks", summary.TaskId.ToString("N")));
                Check(op.Job.OutputArtifacts.Any(id => store.ReadText(id).Contains("ĐÃ XONG", StringComparison.Ordinal)), "Output handles did not resolve in the existing store.");
                Check(op.Job.OutputArtifacts.All(id => restored.Evidence.Any(e => e.EvidenceId == id)), "Process output was not linked into existing Agent evidence.");
                Check(wire.Results.Where(x => x.ToolName == "poll_command_job").All(x => Read(x).GetProperty("job").GetProperty("JobId").GetString() == wire.JobId), "Poll changed process identity.");
                Save(root, "roundtrip-" + project, new { summary, restored, wire.JobId, wire.Polls, wire.Results });
            }));

        test("AR-040 production stdin opt-in writes once and keeps the started process", () => InWorkspace(root =>
        {
            var wire = new Wire("$a=[Console]::ReadLine(); [IO.File]::WriteAllText('input.txt',$a); [Console]::Write($a); Start-Sleep -Seconds 1", Mode.Input);
            var (summary, _) = Execute(root, wire);
            Check(summary.Status == H2AgentTaskStatus.Completed, Detail(summary, wire));
            Check(File.ReadAllText(Path.Combine(root, "input.txt")) == "Dữ liệu đầu vào" && wire.Output.Contains("Dữ liệu đầu vào"), "Stdin did not reach the same native job.");
            var input = wire.Results.Where(r => r.ToolName == "write_command_stdin").Select(Read).ToArray();
            Check(input.Length == 2 && input.All(x => x.GetProperty("Status").GetString() == "Accepted")
                && input[0].GetProperty("ContentHash").GetString() == input[1].GetProperty("ContentHash").GetString(), "Input retry did not preserve its idempotent receipt.");
            Check(wire.Results.Count(x => x.ToolName == "start_command_job") == 1, "Input started a replacement process.");
        }));
        test("AR-040 production start does not satisfy a missing user output", () => InWorkspace(root =>
        {
            var wire = new Wire("[IO.File]::WriteAllText('first.txt','ONE'); Start-Sleep -Seconds 1");
            var (summary, _) = Execute(root, wire, goal: "1. Create first.txt\n2. Create second.txt");
            Check(summary.Status != H2AgentTaskStatus.Completed && !File.Exists(Path.Combine(root, "second.txt")), "Exit zero erased an unfulfilled user outcome.");
        }));
        test("AR-040 production premature final cannot complete a running job and cleanup stops it", () => InWorkspace(root =>
        {
            var wire = new Wire("Start-Sleep -Seconds 60", Mode.Premature);
            var (summary, progress) = Execute(root, wire);
            Check(summary.Status == H2AgentTaskStatus.Blocked, Detail(summary, wire));
            var first = Read(wire.Results.Single(r => r.ToolName == "start_command_job")).GetProperty("job");
            AssertDead(first.GetProperty("ProcessId").GetInt32(), first.GetProperty("ProcessStartedUtc").GetDateTime());
            using var reopened = new H2ProductionAgentAdapter(Path.Combine(root, "state"), Model);
            Check(reopened.GetTaskSummary(summary.TaskId).Recovery!.ReconcileRequired, "Cancelled running work became verified after reopening.");
            Save(root, "premature", new { summary, progress });
        }));
        test("AR-040 production typed cancel preserves unknown side effects and cannot report completion", () => InWorkspace(root =>
        {
            var wire = new Wire("[IO.File]::WriteAllText('partial.txt','BEFORE CANCEL'); Start-Sleep -Seconds 60", Mode.Cancel);
            wire.ReadyToCancel = () => File.Exists(Path.Combine(root, "partial.txt"));
            var (summary, progress) = Execute(root, wire);
            Check(summary.Status != H2AgentTaskStatus.Completed, Detail(summary, wire));
            Check(File.ReadAllText(Path.Combine(root, "partial.txt")) == "BEFORE CANCEL", "Cancellation fixture never performed its actual effect.");
            Check(progress.Any(p => p.ToolOutcome is { Status: H2ToolRunStatus.Cancelled, Effect: H2ToolMutationEffect.Unknown }), "Cancel was flattened into generic success/failure.");
        }));
        foreach (var mode in new[] { Mode.Fail, Mode.Deadline })
            test("AR-040 production terminal process failure remains distinct from verified effects " + mode, () => InWorkspace(root =>
            {
                var wire = new Wire(mode == Mode.Fail ? "Start-Sleep -Seconds 1; exit 7" : "Start-Sleep -Seconds 60", mode);
                var (summary, progress) = Execute(root, wire);
                Check(summary.Status != H2AgentTaskStatus.Completed, Detail(summary, wire));
                Check(progress.Any(p => p.ToolOutcome is { Status: H2ToolRunStatus.Failed, Effect: H2ToolMutationEffect.Unknown }), "Terminal failure lost typed effect uncertainty.");
            }));
        foreach (var mode in new[] { Mode.ForeignOwner, Mode.Malformed })
            test("AR-040 production rejects model-supplied identity or invalid input before starting a child " + mode, () => InWorkspace(root =>
            {
                var wire = new Wire("[IO.File]::WriteAllText('not-allowed.txt','BAD')", mode);
                var (summary, progress) = Execute(root, wire);
                Check(summary.Status == H2AgentTaskStatus.Blocked && !File.Exists(Path.Combine(root, "not-allowed.txt")), Detail(summary, wire));
                Check(progress.Any(p => p.ToolOutcome is { Status: H2ToolRunStatus.Rejected, Effect: H2ToolMutationEffect.None }), "Invalid arguments did not preserve no-effect preflight evidence.");
            }));
        foreach (var permission in new[] { "scoped", "read-only", "expired" })
            test("AR-040 production long jobs do not bypass permission " + permission, () => InWorkspace(root =>
            {
                var wire = new Wire("[IO.File]::WriteAllText('not-allowed.txt','BAD')");
                var (summary, _) = Execute(root, wire, permission: permission);
                Check(summary.Status == H2AgentTaskStatus.Blocked && !File.Exists(Path.Combine(root, "not-allowed.txt")), Detail(summary, wire));
                Check(wire.JobId is null, "Disallowed mode created an owned job.");
            }));
        test("AR-040 production large retained output is paged and linked without entering the model in full", () => InWorkspace(root =>
        {
            var wire = new Wire("[Console]::Write(('Z'*1200000)); [Console]::Error.Write('stderr-ĐÚNG'); Start-Sleep -Seconds 1", Mode.Large);
            var (summary, _) = Execute(root, wire);
            Check(summary.Status == H2AgentTaskStatus.Completed, Detail(summary, wire));
            Check(wire.Results.All(r => r.Content.Length <= 16_384), "Long-job output exceeded advertised wire bound.");
            var terminal = Read(wire.Results.Last(r => r.ToolName == "get_command_result"));
            Check(!terminal.GetProperty("job").GetProperty("OutputComplete").GetBoolean(), "Retained excerpt claimed full output.");
            var handles = terminal.GetProperty("output_artifacts").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var store = new ArtifactStore(Path.Combine(root, "state", "tasks", summary.TaskId.ToString("N")));
            Check(handles.Length == 2 && store.ReadText(handles[0]) == new string('Z', 1_000_000)
                && store.ReadText(handles[1]) == "stderr-ĐÚNG", "Bounded retained output or exact stderr was lost.");
            Check(wire.Output.Length <= 2048, "Test transport downloaded all stdout instead of a page.");
        }));
        test("AR-040 production observer failure before Resume cannot execute job code", () => InWorkspace(root =>
        {
            var armed = false;
            var wire = new Wire("[IO.File]::WriteAllText('not-resumed.txt','BAD')");
            wire.BeforeStart = () => armed = true;
            var (summary, _) = Execute(root, wire, configure: adapter =>
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var archive = typeof(H2ProductionAgentAdapter).GetField("_archive", flags)!.GetValue(adapter)!;
                var store = (string)archive.GetType().GetField("_store", flags)!.GetValue(archive)!;
                archive.GetType().GetField("_fault", flags)!.SetValue(archive, (Action<string>)(boundary =>
                {
                    if (!armed || boundary != "journal-activated") return;
                    var path = Directory.EnumerateFiles(store, "event-*.json").Order(StringComparer.Ordinal).Last();
                    using var record = JsonDocument.Parse(File.ReadAllText(path));
                    if (record.RootElement.GetProperty("Payload").TryGetProperty("Job", out var job) && job.ValueKind == JsonValueKind.Object)
                        throw new IOException("controlled archive failure before Resume");
                }));
            });
            Check(summary.Status != H2AgentTaskStatus.Completed && !File.Exists(Path.Combine(root, "not-resumed.txt")), "Journal failure permitted a process effect.");
        }));
    }

    private enum Mode { Normal, Input, Premature, Cancel, Fail, Deadline, ForeignOwner, Malformed, Large }
    private sealed class Wire(string command, Mode mode = Mode.Normal) : IAgentTransportFactory
    {
        public List<AgentToolResult> Results { get; } = [];
        public string? JobId { get; private set; }
        public string Output { get; private set; } = "";
        public int Polls { get; private set; }
        public Action? BeforeStart { get; set; }
        public Func<bool>? ReadyToCancel { get; set; }
        private int _stage, _serial;
        private string _lastStatus = "Running";
        private readonly string[] _tools = ["start_command_job", "poll_command_job", "read_command_output", "get_command_result", "write_command_stdin", "cancel_command_job"];
        public IAgentTransport Create(AiProfile p, string key, AgentRunTelemetry t) => new Transport(this);
        private AgentTransportToolCall? Next()
        {
            if (_serial > 48) throw new InvalidOperationException("Bounded fixture request budget exhausted.");
            if (_stage < _tools.Length) return Call("tool_search", new { query = _tools[_stage++] });
            if (_stage == _tools.Length)
            {
                _stage++; BeforeStart?.Invoke();
                if (mode == Mode.ForeignOwner) return Call("start_command_job", new { command, owner_task_id = Guid.NewGuid().ToString() });
                if (mode == Mode.Malformed) return Call("start_command_job", new { command, lifetime_seconds = "not-a-number" });
                return Call("start_command_job", new { command, lifetime_seconds = mode == Mode.Deadline ? 2 : 30, allow_stdin = mode == Mode.Input });
            }
            if (JobId is null || mode is Mode.Premature or Mode.ForeignOwner or Mode.Malformed) return null;
            if (mode == Mode.Input && _stage < _tools.Length + 3)
            { _stage++; return Call("write_command_stdin", new { job_id = JobId, input_id = "input-1", text = "Dữ liệu đầu vào\n", close = true }); }
            if (mode == Mode.Cancel && Polls >= 1 && (ReadyToCancel?.Invoke() ?? true) && _stage == _tools.Length + 1)
            { _stage++; return Call("cancel_command_job", new { job_id = JobId }); }
            if (_lastStatus == "Running") { Polls++; return Call("poll_command_job", new { job_id = JobId, wait_seconds = 1 }); }
            if (_stage < 20) { _stage = 20; return Call("read_command_output", new { job_id = JobId, stream = "stdout", max_characters = 2048 }); }
            if (_stage == 20) { _stage++; return Call("get_command_result", new { job_id = JobId }); }
            return null;
        }
        private AgentTransportToolCall Call(string tool, object args) => new("native-" + ++_serial, tool, JsonSerializer.Serialize(args));
        private void Observe(IReadOnlyList<AgentToolResult> results)
        {
            Results.AddRange(results);
            foreach (var result in results)
            {
                if (result.IsError) continue;
                if (result.ToolName == "start_command_job") JobId = result.Outcome?.Job?.JobId;
                if (result.ToolName is "start_command_job" or "poll_command_job" or "get_command_result")
                {
                    var payload = Read(result);
                    if (payload.TryGetProperty("job", out var job)) _lastStatus = job.GetProperty("Status").GetString()!;
                }
                if (result.ToolName == "cancel_command_job") _lastStatus = Read(result).GetProperty("Status").GetString()!;
                if (result.ToolName == "read_command_output") Output += Read(result).GetProperty("Text").GetString();
            }
        }
        private sealed class Transport(Wire wire) : IAgentTransport
        {
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
            public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken ct = default) => Emit(ct);
            public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken ct = default)
            { wire.Observe(request.ToolResults); return Emit(ct); }
            private async IAsyncEnumerable<AgentTransportEvent> Emit([EnumeratorCancellation] CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); await Task.CompletedTask;
                var call = wire.Next();
                if (call is not null) yield return AgentTransportEvent.Tool(call);
                else yield return AgentTransportEvent.TextDeltaEvent("Scripted candidate. Host must verify process state and preserve every user obligation.");
                yield return AgentTransportEvent.Complete();
            }
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static JsonElement Read(AgentToolResult result)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(result.Content));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone(); // Only the leading domain JSON; host evidence suffix is separate.
    }
    private static H2ProductionAgentModel Model() => new(new AiProfile { Model = "scripted-process-test", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" }, "");
    private static (H2AgentTaskSummary Summary, IReadOnlyList<H2AgentProgress> Progress) Execute(string root, Wire wire,
        bool project = false, string permission = "full", string goal = "Run command", Action<H2ProductionAgentAdapter>? configure = null)
    {
        var now = DateTime.UtcNow;
        var grant = permission == "scoped" ? null : new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess,
            H2AgentResourceScopeKind.Machine, H2AgentPermissionScope.CurrentMachineResourceKey, true, false,
            permission == "expired" ? now.AddMinutes(-2) : now, permission == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5));
        var context = new H2AgentTaskContext(root, "Disposable process fixture; no external service.", PermissionScope: grant);
        using var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "state"), Model, wire);
        configure?.Invoke(adapter);
        try
        {
            var id = adapter.StartTaskAsync(project ? Guid.NewGuid() : null, goal, context, permission == "read-only").GetAwaiter().GetResult();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(50))
            {
                var summary = adapter.GetTaskSummary(id);
                if (summary.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled)
                {
                    var progress = adapter.ObserveTask(id).Progress;
                    Save(root, "task-" + id.ToString("N"), new { summary, progress, wire.Results, wire.JobId, wire.Polls });
                    return (summary, progress);
                }
                Thread.Sleep(10);
            }
            adapter.CancelTask(id); throw new TimeoutException("Production owned-job fixture exceeded finite deadline.");
        }
        finally { adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
    }
    private static void AssertDead(int pid, DateTime started)
    {
        try { using var process = Process.GetProcessById(pid); Check(process.StartTime.ToUniversalTime() != started || process.HasExited, "Owned native process survived task cleanup."); }
        catch (ArgumentException) { }
    }
    private static string Detail(H2AgentTaskSummary summary, Wire wire) => JsonSerializer.Serialize(new { summary, wire.Results });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Save(string root, string name, object value)
    {
        var evidence = Environment.GetEnvironmentVariable("H2_AR040_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidence)) return;
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "production-" + name + ".json"), JsonSerializer.Serialize(value));
    }
    private static void InWorkspace(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar040-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
