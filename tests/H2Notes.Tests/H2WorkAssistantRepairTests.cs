using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantRepairTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant composer uses one bottom toolbar and switches voice to send without overlap", () =>
        {
            var window = new WorkAssistantCompactWindow(new());
            window.ConfigureModels([new AiProfile { Model = "a-model-with-a-very-long-name", Name = "Local", ReasoningEffort = "high" }], null);
            window.SelectedPermissionMode = H2AgentPermissionMode.FullAccess;
            window.Show();
            try
            {
                foreach (var width in new[] { 380, 460, 640, 922 })
                {
                    window.Width = width; Pump(window);
                    var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
                    var prompt = controls.Single(c => c.Name == "WorkAssistantPrompt");
                    var names = new[] { "WorkAssistantChooseFolder", "WorkAssistantPermissionPreset", "WorkAssistantModelPicker", "WorkAssistantDictation", "WorkAssistantSendButton" };
                    var previousRight = 0d; double? centerY = null;
                    foreach (var name in names)
                    {
                        var control = controls.Single(c => c.Name == name);
                        var p = control.TranslatePoint(default, window)!.Value;
                        Check(p.X >= previousRight - 1 && p.X + control.Bounds.Width <= window.ClientSize.Width, $"Toolbar overlap/clipping: {name} at width {width}: x={p.X}, right={p.X + control.Bounds.Width}, previous={previousRight}, client={window.ClientSize.Width}");
                        var y = p.Y + control.Bounds.Height / 2;
                        Check(centerY is null || Math.Abs(centerY.Value - y) <= 2, "Toolbar is not a single row");
                        centerY = y; previousRight = p.X + control.Bounds.Width;
                        Check(p.Y >= prompt.TranslatePoint(default, window)!.Value.Y + prompt.Bounds.Height - 1, "Toolbar is above text input");
                    }
                    var send = (Button)controls.Single(c => c.Name == "WorkAssistantSendButton");
                    Check(((H2Notes.Avalonia.Controls.AppIcon)send.Content!).Kind == H2Notes.Avalonia.Controls.IconKind.Waveform, "Empty composer has no voice action");
                }
                window.PromptText = "Xin chào"; Pump(window);
                var button = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "WorkAssistantSendButton");
                Check(((H2Notes.Avalonia.Controls.AppIcon)button.Content!).Kind == H2Notes.Avalonia.Controls.IconKind.ArrowUp, "Text did not switch action to send");
            }
            finally { window.Close(); }
        });

        test("Work Assistant full access writes and verifies an absolute path outside workspace without approval", () => InWorkspace(root =>
        {
            var workspace = Path.Combine(root, "work"); Directory.CreateDirectory(workspace);
            var destination = Path.Combine(root, "outside.txt");
            var script = new Script([
                new("load", "tool_search", "{\"query\":\"write_text\"}"),
                new("write", "write_text", JsonSerializer.Serialize(new { path = destination, text = "full access evidence", expectedHash = "" })),
                new("loadread", "tool_search", "{\"query\":\"read_file\"}"),
                new("read", "read_file", JsonSerializer.Serialize(new { path = destination, offset = "0" }))]);
            using var adapter = Adapter(root, script);
            var context = new H2AgentTaskContext(workspace, "", PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Write and verify exactly \"" + destination + "\"", context, false).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && H2AgentVerification.IsVerified(done), done.Error ?? done.Status.ToString());
            Check(File.ReadAllText(destination) == "full access evidence" && script.Results.All(r => !r.IsError), "Full access did not execute outside workspace");
        }));

        test("Work Assistant full access runs local commands while scoped mode cannot discover or execute them", () => InWorkspace(root =>
        {
            foreach (var full in new[] { false, true })
            {
                var marker = Path.Combine(root, full ? "full.txt" : "scoped.txt");
                var script = new Script([
                    new("load", "tool_search", "{\"query\":\"exec_command\"}"),
                    new("exec", "exec_command", JsonSerializer.Serialize(new { command = "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', 'command-result'); Write-Output 'command-result'", timeout_seconds = 20 }))]);
                using var adapter = Adapter(root, script);
                var context = full ? new H2AgentTaskContext(root, "", PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope) : Context(root);
                // Budget covers the explicit 20-second command deadline plus teardown margin.
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Run command", context, false).Result, 30_000);
                Check(File.Exists(marker) == full, "Local command escaped scoped policy or full access did not execute: " + full + " " + done.Error + string.Join("\n", script.Results.Select(r => r.Content)));
                if (full) Check(done.Status == H2AgentTaskStatus.Completed && script.Results.Any(r => r.Content.Contains("command-result") && !r.IsError), done.Error ?? "Shell failed");
                else Check(done.Status == H2AgentTaskStatus.Blocked && !script.Loaded.Contains("exec_command"), "Scoped mode exposed command execution");
            }
        }));

        test("AR-001 forced wait timeout drains owned command and preserves the primary failure", () =>
        {
            string? workspace = null;
            int processId = 0;
            Exception? observed = null;
            try
            {
                InWorkspace(root =>
                {
                    workspace = root;
                    var pidFile = Path.Combine(root, "owned-process.txt");
                    var script = new Script([
                        new("load", "tool_search", "{\"query\":\"exec_command\"}"),
                        new("exec", "exec_command", JsonSerializer.Serialize(new { command =
                            "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 60",
                            timeout_seconds = 90 }))]);
                    using var adapter = Adapter(root, script);
                    var context = new H2AgentTaskContext(root, "", PermissionScope:
                        WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
                    var id = adapter.StartTaskAsync(null, "Disposable command teardown fixture", context, false).Result;
                    var deadline = Environment.TickCount64 + 30_000;
                    while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && Environment.TickCount64 < deadline)
                    { Pump(); Thread.Sleep(10); }
                    Check(File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out processId), "Fixture process did not start.");
                    _ = Wait(adapter, id, 50);
                });
            }
            catch (Exception ex) { observed = ex; }
            Check(observed is TimeoutException, "The original timeout was lost or replaced by cleanup: " + observed);
            Check(workspace is not null && !Directory.Exists(workspace), "Workspace deleted before drain or leaked after shutdown.");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                Check(process.HasExited, "Owned command survived awaited adapter shutdown.");
            }
            catch (ArgumentException) { /* The owned PID no longer exists. */ }
        });

        test("Work Assistant full access expires and never survives read-only override", () => InWorkspace(root =>
        {
            foreach (var readOnly in new[] { false, true })
            {
                var now = DateTime.UtcNow;
                var grant = new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess, H2AgentResourceScopeKind.Machine,
                    H2AgentPermissionScope.CurrentMachineResourceKey, true, false,
                    readOnly ? now : now.AddHours(-2), readOnly ? now.AddMinutes(30) : now.AddHours(-1));
                var script = new Script([new("load", "tool_search", "{\"query\":\"write_text\"}"),
                    new("write", "write_text", "{\"path\":\"denied.txt\",\"text\":\"bad\",\"expectedHash\":\"\"}")]);
                using var adapter = Adapter(root, script);
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Write denied file", new(root, "", PermissionScope: grant), readOnly).Result);
                Check(done.Status == H2AgentTaskStatus.Blocked && !File.Exists(Path.Combine(root, "denied.txt")), "Expired/read-only permission executed mutation");
            }
        }));

        test("Work Assistant local command nonzero exit and timeout block completion", () => InWorkspace(root =>
        {
            foreach (var command in new[] { "Write-Output 'failed'; exit 7", "Start-Sleep -Seconds 30" })
            {
                var script = new Script([new("load", "tool_search", "{\"query\":\"exec_command\"}"),
                    new("exec", "exec_command", JsonSerializer.Serialize(new { command, timeout_seconds = 1 }))]);
                using var adapter = Adapter(root, script);
                var context = new H2AgentTaskContext(root, "", PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Run local command", context, false).Result);
                Check(done.Status == H2AgentTaskStatus.Blocked && script.Results.Any(r => r.ToolName == "exec_command" && r.IsError), "Failed command reported completion");
                if (command.StartsWith("Start-Sleep"))
                {
                    var result = script.Results.Single(r => r.ToolName == "exec_command");
                    Check(result.Content.Contains("\"timed_out\":true"), "Timeout not reported");
                    Check(result.Outcome?.Effect == H2AgentLab.Tools.ToolMutationEffect.Unknown
                        && result.Outcome.Error?.RetryClass == H2AgentLab.Tools.ToolRetryClass.ReconcileRequired
                        && !result.Content.Contains("Correct the command and retry"), "Deadline suggested an unsafe retry.");
                }
            }
        }));

        test("Work Assistant cancelling a local command kills the running process", () => InWorkspace(root =>
        {
            var pidFile = Path.Combine(root, "pid.txt");
            var script = new Script([new("load", "tool_search", "{\"query\":\"exec_command\"}"),
                new("exec", "exec_command", JsonSerializer.Serialize(new { command = "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 90" }))]);
            using var adapter = Adapter(root, script);
            var context = new H2AgentTaskContext(root, "", PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
            var id = adapter.StartTaskAsync(null, "Long local command", context, false).Result;
            var until = Environment.TickCount64 + 5000;
            while (!File.Exists(pidFile) && Environment.TickCount64 < until) { Pump(); Thread.Sleep(10); }
            Check(File.Exists(pidFile), "Command never started");
            var pid = int.Parse(File.ReadAllText(pidFile));
            adapter.CancelTask(id);
            Check(Wait(adapter, id).Status == H2AgentTaskStatus.Cancelled, "Cancellation did not reach task");
            until = Environment.TickCount64 + 5000;
            bool Alive() { try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
            while (Alive() && Environment.TickCount64 < until) { Pump(); Thread.Sleep(10); }
            Check(!Alive(), "Cancelled command is still running");
        }));

        test("Work Assistant Enter submits once and Shift Enter keeps a multiline draft", () =>
        {
            var window = new WorkAssistantCompactWindow(new() { Enabled = true }); var submitted = 0;
            window.SubmitRequested += (_, _) => { submitted++; return Task.CompletedTask; };
            window.OpenFromHotkey(); Pump(window);
            try
            {
                window.PromptText = "Dòng một";
                window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null); Pump(window);
                Check(submitted == 0 && window.PromptText.Contains('\n'), "Shift Enter sent or dropped the newline");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump(window);
                Check(submitted == 1, "Enter failed to submit exactly once");
            }
            finally { window.Close(); }
        });

        test("Work Assistant conversation pins composer below long scrollable multiline history", () =>
        {
            var window = new WorkAssistantCompactWindow(new() { Enabled = true });
            var now = DateTime.UtcNow; var id = Guid.NewGuid();
            var longText = string.Join("\n", Enumerable.Range(1, 80).Select(i => "Dòng lịch sử " + i));
            window.ShowHistory([new(id, null, "Yêu cầu trước", H2AgentTaskStatus.Completed, null, [], longText, null, now, now)], null);
            window.Show();
            try
            {
                foreach (var size in new[] { new Size(460, 610), new Size(380, 460), new Size(620, 750) })
                {
                    window.Width = size.Width; window.Height = size.Height; Pump(window);
                    var controls = window.GetVisualDescendants().OfType<Control>().ToArray();
                    var composer = controls.Single(c => c.Name == "WorkAssistantComposerSurface");
                    var scroll = controls.OfType<ScrollViewer>().Single(c => c.Name == "WorkAssistantHistoryScroll");
                    var send = controls.Single(c => c.Name == "WorkAssistantSendButton");
                    var composerTop = composer.TranslatePoint(default, window)!.Value.Y;
                    Check(scroll.Bounds.Height > 50 && scroll.Extent.Height > scroll.Viewport.Height, "History cannot scroll");
                    Check(composerTop >= scroll.TranslatePoint(default, window)!.Value.Y + scroll.Bounds.Height - 1, "Composer overlaps history");
                    Check(send.TranslatePoint(new Point(0, send.Bounds.Height), window)!.Value.Y <= window.ClientSize.Height, "Send is clipped");
                    Check(controls.OfType<H2Notes.Avalonia.Controls.MarkdownMessageView>().Any(c => c.Markdown == longText), "History lost text/newlines");
                }
                Check(window.ConversationTurns.Count == 2 && window.ConversationTurns[1].Content == longText, "Follow-up lost prior conversation");
            }
            finally { window.Close(); }
        });

        test("Work Assistant panel follows bubble and stays inside negative-origin and high-DPI monitors", () =>
        {
            var placement = typeof(WorkAssistantBubbleWindow).Assembly.GetType("H2Notes.Avalonia.WorkAssistantPlacement")!;
            var attach = placement.GetMethod("AttachPanel", BindingFlags.Static | BindingFlags.NonPublic)!;
            PixelPoint Resolve(PixelPoint bubble, PixelRect area, double scale)
                => (PixelPoint)attach.Invoke(null, [bubble, new PixelSize((int)(74 * scale), (int)(74 * scale)),
                    new PixelSize((int)(460 * scale), (int)(610 * scale)), area, (int)(10 * scale)])!;
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
            {
                var area = new PixelRect(-2560, -200, 2560, 1440);
                var first = Resolve(new(-900, 950), area, scale);
                var moved = Resolve(new(-1000, 870), area, scale);
                Check(moved.X == first.X - 100 && moved.Y == first.Y - 80, "Panel did not track the same drag delta");
                foreach (var point in new[] { new PixelPoint(-2560, -200), new PixelPoint(-80, 1160) })
                {
                    var panel = Resolve(point, area, scale);
                    Check(panel.X >= area.X && panel.Y >= area.Y && panel.X + 460 * scale <= area.Right
                        && panel.Y + 610 * scale <= area.Bottom, "Panel escaped screen work area");
                }
            }
        });

        foreach (var name in new[] { "files:write", "made_up_tool" })
            test("AR-067 Work Assistant unknown tool returns Agent-authored blocked explanation: " + name, () => InWorkspace(root =>
            {
                var script = new Script(
                    [new("bad", name, "{\"path\":\"hello.txt\",\"content\":\"xin chào\"}")],
                    finalText: "Không thể hoàn tất: unknown_tool không tồn tại cho tệp hello.txt. Tôi không đổi sang một thao tác ghi khác khi chưa có callable phù hợp; cần nạp/cài công cụ tương ứng rồi tiếp tục.");
                using var adapter = Adapter(root, script);
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Create requested file", Context(root), false).Result);
                Check(done.Status == H2AgentTaskStatus.Blocked
                    && done.FinalText?.Contains("unknown_tool", StringComparison.Ordinal) == true
                    && string.IsNullOrWhiteSpace(done.Error), done.Error ?? done.FinalText ?? done.Status.ToString());
                Check(!File.Exists(Path.Combine(root, "hello.txt")), "Unknown tool executed a write");
                Check(script.Results.Single().IsError, "Failure was not sent to model");
                Check(script.RecoveryStates.Any(x => x.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)
                    && x.Contains("\"errorCode\":\"unknown_tool\"", StringComparison.Ordinal)
                    && x.Contains("\"safeRecoveryCandidates\"", StringComparison.Ordinal)),
                    "Structured failure state was not returned on the production tool-result continuation.");
            }));

        test("AR-067 Work Assistant model continuation outage exposes technical card without claiming completion", () => InWorkspace(root =>
        {
            var script = new Script(
                [new("bad", "definitely_missing_ar067_tool", "{\"path\":\"hello.txt\",\"content\":\"xin chào\"}")],
                failRecoveryContinuation: true);
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Create requested file", Context(root), false).Result);
            Check(done.Status == H2AgentTaskStatus.Blocked
                && done.FinalText?.Contains("model_continuation", StringComparison.Ordinal) == true
                && done.FinalText.Contains("khôi phục kết nối/cấu hình model", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(done.Error),
                done.Error ?? done.FinalText ?? done.Status.ToString());
            Check(script.RecoveryStates.Any(x => x.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)),
                "Model outage fixture did not receive structured recovery state before continuation failed.");
        }));

        test("Work Assistant recovers qualified callable with selected-folder permission and verified write", () => InWorkspace(root =>
        {
            var script = new Script([
                new("bad", "files:write", "{\"path\":\"hello.txt\",\"content\":\"xin chào\"}"),
                new("write", "files:write_text", "{\"path\":\"hello.txt\",\"text\":\"xin chào\",\"expectedHash\":\"\"}")]);
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Create requested file", Context(root), false).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && H2AgentVerification.IsVerified(done), done.Error ?? done.Status.ToString());
            Check(File.ReadAllText(Path.Combine(root, "hello.txt")) == "xin chào", "Readback differs");
            Check(script.Loaded.Contains("write_text"), "Recovery did not expose actual schema");
            Check(script.Results.Last().ToolName == "files:write_text", "Original transport call name was not preserved");
        }));

        test("Work Assistant cannot clear failed operation by writing another path", () => InWorkspace(root =>
        {
            var script = new Script([
                new("bad", "files:write", "{\"path\":\"requested.txt\",\"content\":\"xin chào\"}"),
                new("unrelated", "write_text", "{\"path\":\"different.txt\",\"text\":\"xin chào\",\"expectedHash\":\"\"}")]);
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Create requested file", Context(root), false).Result);
            Check(done.Status == H2AgentTaskStatus.Blocked && !File.Exists(Path.Combine(root, "requested.txt")), "Unrelated success cleared the original failure");
        }));

        test("Work Assistant recovers stale hashes using raw executor output behind evidence projection", () => InWorkspace(root =>
        {
            var path = Path.Combine(root, "requested.txt"); File.WriteAllText(path, "before");
            var hash = H2AgentLab.SafeWorkspace.Hash(File.ReadAllBytes(path));
            var script = new Script([
                new("search", "tool_search", "{\"query\":\"write_text\"}"),
                new("stale", "write_text", JsonSerializer.Serialize(new { path = "requested.txt", text = "after", expectedHash = "stale" })),
                new("fresh", "write_text", JsonSerializer.Serialize(new { path = "requested.txt", text = "after", expectedHash = hash }))]);
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Update requested file", Context(root), false).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && File.ReadAllText(path) == "after", done.Error ?? "Stale retry remained blocked");
        }));

        test("Work Assistant PDF read exposes replacement hash without claiming to read scan content", () => InWorkspace(root =>
        {
            File.WriteAllText(Path.Combine(root, "scan.pdf"), "%PDF-1.7\nfixture");
            var script = new Script([
                new("search", "tool_search", "{\"query\":\"read_file\"}"),
                new("read", "read_file", "{\"path\":\"scan.pdf\",\"offset\":\"0\"}")]);
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Inspect scan metadata", Context(root), false).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && script.Results.Any(r => !r.IsError && r.Content.Contains("contentAvailable") && r.Content.Contains("false")), "PDF hash read failed or invented content");
        }));

        test("Work Assistant folder scope rejects escaping paths and mismatched folder grants", () => InWorkspace(root =>
        {
            foreach (var mismatch in new[] { false, true })
            {
                var script = new Script([
                    new("search", "tool_search", "{\"query\":\"write_text\"}"),
                    new("write", "write_text", JsonSerializer.Serialize(new { path = mismatch ? "hello.txt" : "../escaped.txt", text = "bad", expectedHash = "" }))]);
                using var adapter = Adapter(root, script);
                var context = Context(root);
                if (mismatch) { var other = Path.Combine(root, "other"); Directory.CreateDirectory(other); context = context with { WorkspaceRoot = other }; }
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Create file", context, false).Result);
                Check(done.Status is H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed, "Out-of-scope write reported success");
                Check(!File.Exists(Path.Combine(root, "other", "hello.txt")), "Mismatched root grant executed");
            }
        }));
    }

    private static H2AgentTaskContext Context(string root) => new(root, "Synthetic test",
        PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.AllowScopedChanges, root, DateTime.UtcNow).PermissionScope);
    [ThreadStatic] private static List<H2ProductionAgentAdapter>? _workspaceAdapters;
    private static H2ProductionAgentAdapter Adapter(string root, Script script)
    {
        var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "state-" + Guid.NewGuid().ToString("N")),
            () => new(new AiProfile { Name = "test", Model = "script", BaseUrl = "https://example.test/v1", Protocol = AiProtocol.OpenAiChat }, ""), script);
        _workspaceAdapters?.Add(adapter);
        return adapter;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void InWorkspace(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-assistant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = _workspaceAdapters;
        var owned = _workspaceAdapters = new List<H2ProductionAgentAdapter>();
        Exception? failure = null;
        var cleanupErrors = new List<Exception>();
        try { body(root); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var adapter in owned)
            {
                try { adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (cleanupErrors.Count == 0)
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            _workspaceAdapters = previous;
        }
        if (cleanupErrors.Count > 0)
            throw new AggregateException("Fixture teardown failed; evidence retained at " + root,
                failure is null ? cleanupErrors : new[] { failure }.Concat(cleanupErrors));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump(Window? window = null)
    { for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); window?.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id, int budgetMilliseconds = 10000)
    { var until = Environment.TickCount64 + budgetMilliseconds; while (Environment.TickCount64 < until) {
        var result = adapter.GetTaskSummary(id);
        if (result.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled) return result;
        Pump(); Thread.Sleep(10); } throw new TimeoutException(JsonSerializer.Serialize(adapter.ObserveTask(id))); }
    private sealed class Script(AgentTransportToolCall[] calls, string finalText = "Tôi đã xong.",
        bool failRecoveryContinuation = false) : IAgentTransportFactory
    {
        private readonly AgentTransportToolCall[] _calls = calls;
        private readonly string _finalText = finalText;
        private readonly bool _failRecoveryContinuation = failRecoveryContinuation;
        public List<AgentToolResult> Results = []; public List<string> Loaded = []; public List<string> RecoveryStates = [];
        public IAgentTransport Create(AiProfile p, string key, AgentRunTelemetry t) => new Transport(this);
        private sealed class Transport(Script script) : IAgentTransport
        {
            private int _next;
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
            public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r, CancellationToken ct = default) => Round(ct);
            public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r, CancellationToken ct = default)
            {
                script.Results.AddRange(r.ToolResults);
                script.Loaded.AddRange(r.NewlyLoadedTools?.Select(x => x.Name) ?? []);
                script.RecoveryStates.AddRange(r.ToolResults.Select(x => x.Content)
                    .Where(x => x.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)));
                if (script._failRecoveryContinuation
                    && r.ToolResults.Any(x => x.Content.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)))
                    return FailedRound(ct);
                return Round(ct);
            }
            private async IAsyncEnumerable<AgentTransportEvent> FailedRound([EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield(); ct.ThrowIfCancellationRequested();
                throw new HttpRequestException("synthetic AR-067 model continuation outage");
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }
            private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
            { await Task.CompletedTask; ct.ThrowIfCancellationRequested();
                if (_next < script._calls.Length) yield return AgentTransportEvent.Tool(script._calls[_next++]);
                else yield return AgentTransportEvent.TextDeltaEvent(script._finalText);
                yield return AgentTransportEvent.Complete(); }
            public void Cancel() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
