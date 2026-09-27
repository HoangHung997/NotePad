using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>AR-001 / RC-02. E1 contract/backend fixtures and E2 concrete production runtime
/// with scripted model only. No configured model, native Office documents, or network calls.</summary>
internal static class H2AgentReliabilityContractTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-001 RC-02 production prompt discovers the canonical skill callable", () => InWorkspace(root =>
        {
            var script = new ScriptFactory([
                new("search", "tool_search", "{\"query\":\"list_skills\"}"),
                new("skills", SkillRuntimeToolExecutor.SearchToolName, "{\"query\":\"\"}")]);
            var adapter = Adapter(root, script);
            try
            {
                var id = adapter.StartTaskAsync(null, "Discover available skill guidance", new(root, "Synthetic RC-02 fixture"), true).Result;
                var summary = Wait(adapter, id);
                Check(summary.Status == H2AgentTaskStatus.Completed, summary.Error ?? summary.Status.ToString());
                var prompt = string.Join("\n", script.Start!.Messages.Select(message => message.Content));
                Check(prompt.Contains("discover skills with " + SkillRuntimeToolExecutor.SearchToolName), "Prompt did not use canonical skill metadata.");
                Check(prompt.Contains("guidance using " + SkillRuntimeToolExecutor.ReadToolName), "Prompt reader drifted.");
                Check(!prompt.Contains("search_skills", StringComparison.Ordinal), "Non-callable search_skills was advertised.");
                Check(script.Results.Any(result => result.ToolName == SkillRuntimeToolExecutor.SearchToolName && !result.IsError), "Skill call did not actually execute through production runtime.");
                Check(script.Results.All(result => !result.IsError), "Canonical prompt/registry path produced a tool error.");
            }
            finally { Drain(adapter); }
        }));

        test("AR-001 RC-02 Excel schema adapter IPC and backend share cardinality limits", () => InWorkspace(root =>
        {
            var type = typeof(H2ProductionAgentAdapter).Assembly.GetType("H2AgentLab.Integration.H2OfficeRuntimeTools", true)!;
            using var office = (IDisposable)Activator.CreateInstance(type, [new Func<bool>(() => true), root, null, null])!;
            var registry = new ToolRegistry();
            type.GetMethod("Register")!.Invoke(office, [registry]);
            var clientField = type.GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var name in new[] { "excel.write_range", "excel.set_formula", "excel.apply_format" })
            {
                Check(registry.TryGet(name, out var descriptor), "Excel callable is missing: " + name);
                var schema = descriptor.CallableSchema.GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("cells");
                Check(schema.GetProperty("minItems").GetInt32() == ExcelPatchLimits.MinCells && schema.GetProperty("maxItems").GetInt32() == ExcelPatchLimits.MaxCells, "Schema drift: " + name);
                foreach (var count in new[] { 0, 1, 128, 129, 200 })
                {
                    var valid = count is >= 1 and <= 128;
                    Check((ExcelPatchLimits.ValidationError(count) is null) == valid, "Protocol count verdict changed.");
                    Check((count >= schema.GetProperty("minItems").GetInt32() && count <= schema.GetProperty("maxItems").GetInt32()) == valid, "Schema verdict differs from protocol.");
                    if (valid) continue; // Valid writes are exercised on the isolated backend below, not on user Office.
                    var call = Call(name, new { session_id = "not-a-user-document", state_token = "unobserved", sheet_name = "Data", cells = Cells(count) });
                    using var result = JsonDocument.Parse(descriptor.Executor.ExecuteAsync(call, CancellationToken.None).AsTask().GetAwaiter().GetResult());
                    Check(result.RootElement.GetProperty("error").GetString() == ExcelPatchLimits.ErrorCode && !result.RootElement.GetProperty("mutationApplied").GetBoolean(), "Adapter did not reject before effect.");
                    Check(clientField.GetValue(office) is null, "Oversized/empty batch caused Office discovery or IPC.");
                }
            }
        }));

        foreach (var count in new[] { 0, 1, 128, 129, 200 })
        {
            var size = count;
            test("AR-001 RC-02 backend and server batch " + size + " preserves all or rejects before writes", () =>
            {
                foreach (var viaDispatch in new[] { false, true })
                {
                    var backend = new FixtureOfficeBackend(extraExcelRows: 126);
                    var before = backend.SnapshotExcel("excel-fixture-1");
                    var request = new ExcelPatchRequest(before.SessionId, before.StateToken, true, "Data", Cells(size));
                    ExcelPatchResult? result = null;
                    OfficeHostFaultException? fault = null;
                    try
                    {
                        if (viaDispatch)
                        {
                            var server = new OfficeHostServer("ar001-unused-pipe", backend, fixtureMode: true);
                            var rpc = new OfficeRpcRequest("batch", "excel.patch", JsonSerializer.SerializeToElement(request));
                            result = (ExcelPatchResult)Invoke(server, "Dispatch", rpc, CancellationToken.None)!;
                        }
                        else result = backend.PatchExcel(request);
                    }
                    catch (OfficeHostFaultException ex) { fault = ex; }
                    var after = backend.SnapshotExcel(before.SessionId);
                    if (size is >= 1 and <= 128)
                    {
                        Check(fault is null && result is not null, "Valid batch was rejected: " + fault);
                        Check(result!.ChangedCells.Count == size, "Accepted batch was truncated.");
                        for (var i = 1; i <= size; i++)
                            Check(after.Sheets.Single().Cells.Single(cell => cell.Address == "A" + i).Value == "PATCH-" + i, "Accepted target not applied: A" + i);
                        foreach (var cell in before.Sheets.Single().Cells.Where(cell => !request.Cells.Any(patch => patch.Address == cell.Address)))
                            Check(after.Sheets.Single().Cells.Single(item => item.Address == cell.Address) == cell, "Unrelated cell changed.");
                    }
                    else
                    {
                        Check(fault?.Code == ExcelPatchLimits.ErrorCode, "Backend/server did not share the size rejection code.");
                        Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after), "Rejected batch partially mutated fixture.");
                        // Invalid-count native preflight occurs before COM discovery, so no user Office is accessed.
                        OfficeHostFaultException? native = null;
                        try { new ComOfficeBackend().PatchExcel(request); }
                        catch (OfficeHostFaultException ex) { native = ex; }
                        Check(native?.Code == ExcelPatchLimits.ErrorCode && native.Message == fault!.Message, "Native preflight differs from fixture/server.");
                    }
                }
            });
        }

        test("AR-001 RC-02 client rejects oversize and cancellation without starting helper", () =>
        {
            using var client = new OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"), fixtureMode: true);
            foreach (var size in new[] { 0, 129, 200 })
            {
                OfficeHostClientException? fault = null;
                try { client.PatchExcelAsync(new("unknown", "unknown", true, "Data", Cells(size))).GetAwaiter().GetResult(); }
                catch (OfficeHostClientException ex) { fault = ex; }
                Check(fault?.Code == ExcelPatchLimits.ErrorCode && client.StartCount == 0, "Client dispatched an invalid batch to a helper.");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stopped = false;
            try { client.PatchExcelAsync(new("unknown", "unknown", true, "Data", Cells(129)), cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && client.StartCount == 0, "Cancellation was replaced with a retryable size error.");
        });

        foreach (var count in new[] { 129, 200 })
        {
            var size = count;
            test("AR-001 RC-02 production runtime rejects batch " + size + " without false completion", () => InWorkspace(root =>
            {
                var script = new ScriptFactory([
                    new("search", "tool_search", "{\"query\":\"excel.write_range\"}"),
                    new("patch", "excel.write_range", JsonSerializer.Serialize(new { session_id = "missing", state_token = "missing", sheet_name = "Data", cells = Cells(size) }))]);
                var adapter = Adapter(root, script);
                try
                {
                    var context = new H2AgentTaskContext(root, "Synthetic size rejection only", PermissionScope:
                        WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
                    var id = adapter.StartTaskAsync(null, "Apply the synthetic batch", context, false).Result;
                    var summary = Wait(adapter, id);
                    Check(summary.Status is H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed, "Rejected operation was declared completed.");
                    var output = script.Results.First(result => result.ToolName == "excel.write_range");
                    // Runtime projects a JSON body plus an evidence footer; verify both,
                    // rather than pretending the complete model projection is raw JSON.
                    var marker = output.Content.IndexOf("\n[evidence:", StringComparison.Ordinal);
                    Check(marker > 0, "Rejected mutating call lost its evidence footer.");
                    using var result = JsonDocument.Parse(output.Content[..marker]);
                    Check(output.IsError && result.RootElement.GetProperty("error").GetString() == ExcelPatchLimits.ErrorCode, "Real production runtime lost the count rejection.");
                    Check(!result.RootElement.GetProperty("mutationApplied").GetBoolean(), "Rejected effect provenance was lost.");
                }
                finally { Drain(adapter); }
            }));
        }

        test("AR-001 evidence chunks round-trip escaped text and reject real foreign handles and offsets", () => InWorkspace(root =>
        {
            var state = Path.Combine(root, "state");
            var own = new ArtifactStore(state);
            var other = new ArtifactStore(Path.Combine(root, "foreign-state"));
            var full = string.Concat(Enumerable.Repeat("Tiếng Việt\n\"\\\u0001", 2400)) + "TAIL-EXACT";
            var stored = own.StoreText(AgentArtifactKind.ToolOutput, "rc02-own", "fixture", full, "bounded", 1);
            var foreign = other.StoreText(AgentArtifactKind.ToolOutput, "rc02-foreign", "fixture", "FOREIGN-SENTINEL", "foreign", 1);
            using var tools = new AgentTools(new SafeWorkspace(root), state, (_, _) => Task.FromResult(true), (_, _) => { });
            var registry = NormalRuntimeToolRegistry.Create(tools);
            Check(registry.TryGet("read_tool_output", out var reader) && !reader.IsMutating && reader.SupportsParallel && reader.Namespace.Name == "evidence", "Evidence descriptor safety changed.");
            string Read(string id, string offset) => reader.Executor.ExecuteAsync(Call(reader.Name, new { artifact_id = id, offset }), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var actual = new StringBuilder();
            var offset = 0;
            var pages = 0;
            while (offset < full.Length && pages++ < 256)
            {
                var raw = Read(stored.Handle.Id, offset.ToString());
                Check(raw.Length < AgentRuntimeEvidenceProjector.MaxInlineToolOutputCharacters, "Evidence chunk recursively exceeds projection budget.");
                using var json = JsonDocument.Parse(raw);
                var page = json.RootElement;
                Check(page.GetProperty("Sha256").GetString() == stored.Handle.Sha256 && page.GetProperty("totalCharacters").GetInt32() == full.Length, "Source identity/extent changed.");
                actual.Append(page.GetProperty("content").GetString());
                var next = page.GetProperty("nextOffset").GetInt32();
                Check(next > offset && next <= full.Length, "Evidence cursor did not progress within bounds.");
                offset = next;
            }
            Check(actual.ToString() == full, "Paged evidence was lost, duplicated or cut off.");
            using (var eof = JsonDocument.Parse(Read(stored.Handle.Id, full.Length.ToString())))
                Check(!eof.RootElement.GetProperty("truncated").GetBoolean() && eof.RootElement.GetProperty("content").GetString() == "", "EOF is not explicit.");
            foreach (var pair in new[] { (foreign.Handle.Id, "0"), (stored.Handle.Id, "-1"), (stored.Handle.Id, (full.Length + 1).ToString()), (stored.Handle.Id, "bad") })
            {
                var raw = Read(pair.Item1, pair.Item2);
                using var result = JsonDocument.Parse(raw);
                Check(result.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean(), "Invalid/foreign evidence request was accepted.");
                Check(!raw.Contains("FOREIGN-SENTINEL"), "Foreign task evidence leaked.");
            }
        }));

        test("AR-001 async disposal waits for executor teardown after terminal summary", () => InWorkspace(root =>
        {
            var script = new ScriptFactory([], holdDisposal: true);
            var adapter = Adapter(root, script);
            try
            {
                var id = adapter.StartTaskAsync(null, "Synthetic terminal result", new(root, ""), true).Result;
                _ = Wait(adapter, id);
                script.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var shutdown = adapter.DisposeAsync().AsTask();
                Check(!shutdown.IsCompleted, "Terminal summary was mistaken for completed resource teardown.");
                var rejected = false;
                try { adapter.StartTaskAsync(null, "must not start").GetAwaiter().GetResult(); }
                catch (ObjectDisposedException) { rejected = true; }
                Check(rejected, "Shutdown admitted another task.");
                script.ReleaseDisposal.TrySetResult();
                shutdown.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                adapter.Dispose();
                Drain(adapter);
            }
            finally { script.ReleaseDisposal.TrySetResult(); Drain(adapter); }
        }));
    }

    private static ExcelCellPatch[] Cells(int count) => Enumerable.Range(1, count).Select(i => new ExcelCellPatch("A" + i, "PATCH-" + i)).ToArray();
    private static ToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.SerializeToElement(args));
    private static object? Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static H2ProductionAgentAdapter Adapter(string root, ScriptFactory script) => new(Path.Combine(root, "agent-state"),
        () => new(new AiProfile { Model = "scripted-no-network", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" }, ""), script);
    private static void Drain(H2ProductionAgentAdapter adapter) => adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid taskId)
    {
        var end = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < end)
        {
            var result = adapter.GetTaskSummary(taskId);
            if (result.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled or H2AgentTaskStatus.Failed) return result;
            Thread.Sleep(10);
        }
        throw new TimeoutException("AR-001 fixture exceeded its task budget: " + JsonSerializer.Serialize(adapter.ObserveTask(taskId)));
    }
    private static void InWorkspace(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar001-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        try { body(root); }
        catch (Exception ex) { failure = ex; }
        try { Directory.Delete(root, true); }
        catch (Exception ex) { throw new AggregateException("AR-001 cleanup failed; retained evidence at " + root, failure is null ? [ex] : [failure, ex]); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ScriptFactory(AgentTransportToolCall[] calls, bool holdDisposal = false) : IAgentTransportFactory
    {
        private readonly AgentTransportToolCall[] _calls = calls;
        private readonly bool _holdDisposal = holdDisposal;
        public AgentTransportStartRequest? Start { get; private set; }
        public List<AgentToolResult> Results { get; } = [];
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) => new Transport(this);
        private sealed class Transport(ScriptFactory owner) : IAgentTransport
        {
            private int _next;
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
            public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken cancellationToken = default)
            { owner.Start = request; return Round(cancellationToken); }
            public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken cancellationToken = default)
            { owner.Results.AddRange(request.ToolResults); return Round(cancellationToken); }
            private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                cancellationToken.ThrowIfCancellationRequested();
                if (_next < owner._calls.Length) yield return AgentTransportEvent.Tool(owner._calls[_next++]);
                else yield return AgentTransportEvent.TextDeltaEvent("Fixture response, subject to host verification.");
                yield return AgentTransportEvent.Complete();
            }
            public void Cancel() { }
            public async ValueTask DisposeAsync()
            {
                owner.DisposalEntered.TrySetResult();
                if (owner._holdDisposal) await owner.ReleaseDisposal.Task.ConfigureAwait(false);
            }
        }
    }
}
