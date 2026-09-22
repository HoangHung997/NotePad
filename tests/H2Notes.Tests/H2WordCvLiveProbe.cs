using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;

internal static class H2WordCvLiveProbe
{
    public static int CloseFixtures(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, ".h2-agent-test-fixture"))) throw new IOException("Synthetic marker required.");
        var type = Type.GetTypeFromProgID("Word.Application") ?? throw new IOException("Word is not installed.");
        var clsid = type.GUID;
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(GetActiveObject(ref clsid, IntPtr.Zero, out var instance));
        dynamic word = instance ?? throw new IOException("No active Word application.");
        var closed = 0;
        try
        {
            for (var i = (int)word.Documents.Count; i >= 1; i--)
            {
                dynamic document = word.Documents[i];
                try
                {
                    var path = Path.GetFullPath((string)document.FullName);
                    if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(path).StartsWith("h2-cv-regression-", StringComparison.OrdinalIgnoreCase))
                    { document.Close(0); closed++; }
                }
                finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(document); }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(word); }
        Console.WriteLine($"Closed {closed} synthetic CV documents only; saved result copies retained.");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IUnknown)] out object? instance);

    public static async Task<int> Run(string root)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, ".h2-agent-test-fixture"))) throw new IOException("Synthetic fixture marker required.");
        using var client = new OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"));
        var cloud = new List<H2AgentCapabilityLiveProbe.Scenario>();
        for (var round = 1; round <= 6; round++)
        {
            var path = Path.Combine(root, $"h2-cv-regression-{round}.docx");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            WordDocumentInfo? document = null;
            for (var attempt = 0; attempt < 20 && document is null; attempt++)
            {
                await Task.Delay(500);
                document = (await client.DiscoverWordAsync()).Documents.SingleOrDefault(d => d.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            if (document is null) throw new IOException("Synthetic Word fixture did not open.");
            if (round > 3)
            {
                cloud.Add(new($"cv-gemma-{round - 3}", $"Sửa file Word đang mở {path}, session_id {document.SessionId}, với nội dung giới thiệu về bạn viết theo kiểu CV bằng tiếng Việt có dấu. Sửa tiêu đề paragraph index 0 và phần nội dung paragraph index 1; phần thân cần ít nhất 5 đoạn Word riêng biệt, giới thiệu, khả năng, cách làm việc, giới hạn. Giữ tất cả nội dung/định dạng khác, bảng, header/footer và KEEP-{round}. Dùng công cụ word, không dùng script. Đọc snapshot mới, thực hiện rồi dùng word.save_copy lưu cv-gemma-result-{round - 3}.docx trong workspace.", root,
                    SessionId: document.SessionId, Application: "word", DocumentPath: path));
                continue;
            }
            var before = await client.SnapshotWordAsync(document.SessionId);
            WordParagraphPatch[] patches = [new(0, "HỒ SƠ H2 AGENT", Bold: true),
                new(1, "Giới thiệu: Trợ lý AI H2\r\nKỹ năng: Soạn văn bản\n\nCách làm việc: Kiểm tra kết quả\nGiới hạn: Phụ thuộc công cụ", Bold: false)];
            object Args(IEnumerable<WordParagraphPatch> items) => new { session_id = before.SessionId, state_token = before.StateToken, paragraphs = items };
            var script = new Script([
                new("search", "tool_search", "{\"query\":\"word.read_paragraphs word.replace_range\"}"),
                new("read", "word.read_paragraphs", JsonSerializer.Serialize(new { session_id = before.SessionId })),
                new("reject", "word.replace_range", JsonSerializer.Serialize(Args([patches[0], patches[0], patches[1]]))),
                new("retry", "word.replace_range", JsonSerializer.Serialize(Args(patches))) ]);
            var now = DateTime.UtcNow;
            var scope = new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Session,
                "session:" + before.SessionId, true, false, now, now.AddMinutes(5), H2ApplicationKind.Word,
                documentSessionId: before.SessionId, documentPath: path);
            using var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "script-runtime-" + round),
                () => new(new AiProfile { Model = "scripted-regression", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" }, ""), script);
            var task = await adapter.StartTaskAsync(null, "Rewrite the synthetic CV; recover a rejected batch and verify the actual Word document.",
                new(root, "Synthetic fixture only", PermissionScope: scope), false);
            H2AgentTaskSummary done;
            do { await Task.Delay(200); done = adapter.GetTaskSummary(task); }
            while (!H2AgentActivity.IsTerminal(done.Status) && DateTime.UtcNow - now < TimeSpan.FromMinutes(2));
            var after = await client.SnapshotWordAsync(before.SessionId);
            var verified = OfficeMutationReadback.VerifyWordPatch(before, after, patches);
            var saved = await client.SaveWordCopyAsync(new(before.SessionId, after.StateToken, true, Path.Combine(root, $"cv-script-result-{round}.docx")));
            await File.WriteAllTextAsync(Path.Combine(root, $"script-result-{round}.json"), JsonSerializer.Serialize(new { before, after, patches, verified, done, script.Results, saved }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Word CV {round}: {done.Status}; readback={verified}; {done.Error}");
            if (done.Status != H2AgentTaskStatus.Completed || !verified || !script.Results.Any(r => r.ToolCallId == "reject" && r.IsError)) return 1;
        }
        await File.WriteAllTextAsync(Path.Combine(root, "gemma-manifest.json"), JsonSerializer.Serialize(cloud));
        return 0;
    }

    private sealed class Script(AgentTransportToolCall[] calls) : IAgentTransportFactory
    {
        public List<AgentToolResult> Results = [];
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) => new Transport(this, calls);
        private sealed class Transport(Script script, AgentTransportToolCall[] calls) : IAgentTransport
        {
            private int _next;
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
            public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken ct = default) => Round(ct);
            public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken ct = default)
            {
                script.Results.AddRange(request.ToolResults);
                foreach (var result in request.ToolResults.Where(r => r.ToolCallId == "read" && !r.IsError))
                {
                    using var snapshot = JsonDocument.Parse(result.Content.Split("\r\n[evidence:")[0]);
                    var token = snapshot.RootElement.GetProperty("StateToken").GetString();
                    for (var i = _next; i < calls.Length; i++)
                    {
                        var args = System.Text.Json.Nodes.JsonNode.Parse(calls[i].ArgumentsJson)!;
                        args["state_token"] = token;
                        calls[i] = calls[i] with { ArgumentsJson = args.ToJsonString() };
                    }
                }
                return Round(ct);
            }
            private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask; ct.ThrowIfCancellationRequested();
                if (_next < calls.Length) yield return AgentTransportEvent.Tool(calls[_next++]);
                else yield return AgentTransportEvent.TextDeltaEvent("Đã sửa CV và kiểm tra tài liệu.");
                yield return AgentTransportEvent.Complete();
            }
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
