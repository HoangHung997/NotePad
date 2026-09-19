using System.Net;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab;

public static class LabTests
{
    private static ToolCall Call(string name, object args) => new("test", name, JsonSerializer.SerializeToElement(args));
    public static async Task<int> Run(string[] args)
    {
        var report = args.Length > 1 ? Path.GetFullPath(args[^1]) : Path.Combine(Path.GetTempPath(), "h2-agent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report); var workspace = Path.Combine(report, "workspace"); Directory.CreateDirectory(workspace); var state = Path.Combine(report, "state");
        var lines = new List<string>(); var failed = 0;
        async Task Test(string name, Func<Task> action)
        { try { await action(); lines.Add("PASS " + name); } catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex.Message); } }
        void Check(bool value, string reason = "Assertion failed") { if (!value) throw new Exception(reason); }
        var scope = new SafeWorkspace(workspace); var events = new List<string>(); var approvals = 0;
        Task<bool> Yes(Approval a, CancellationToken ct) { ct.ThrowIfCancellationRequested(); approvals++; return Task.FromResult(true); }
        var tools = new AgentTools(scope, state, Yes, (k, t) => events.Add(k + " " + t));
        await Test("Traversal, absolute paths, ADS, secrets and root writes rejected", () =>
        {
            foreach (var path in new[] { "../outside.txt", "..\\outside.txt", "C:\\x.txt", "file.txt:secret", ".env", ".ssh/id_rsa", "x.pem", ".", "CON.txt", "nested/LPT1" })
            { var blocked = false; try { scope.Resolve(path); } catch (IOException) { blocked = true; } Check(blocked, path); }
            return Task.CompletedTask;
        });
        await Test("Read-only prevents writes without asking", async () =>
        { var response = await tools.Execute(Call("write_text", new { path = "a.txt", text = "hello", expectedHash = "" }), default); Check(response.Contains("false") && approvals == 0 && !File.Exists(Path.Combine(workspace, "a.txt"))); });
        tools.ReadOnly = false;
        await Test("Desktop control is unavailable until one DesktopHost window is explicitly selected", async () =>
        {
            Check(tools.Desktop is null, "Desktop controller was created without a selected window.");
            var response = await tools.Execute(
                Call("inspect_window", new { reason = "fixture" }),
                default);
            Check(response.Contains("\"success\":false", StringComparison.Ordinal)
                  && response.Contains("unavailable", StringComparison.Ordinal),
                "Unselected desktop scope did not fail closed.");
        });
        await Test("Create then read returns exact text and hash", async () =>
        {
            await tools.Execute(Call("write_text", new { path = "a.txt", text = "Tiếng Việt · first", expectedHash = "" }), default);
            var response = await tools.Execute(Call("read_file", new { path = "a.txt", offset = "0" }), default);
            using var j = JsonDocument.Parse(response); Check(j.RootElement.GetProperty("content").GetString() == "Tiếng Việt · first"); Check(response.Contains("Tiếng Việt"), "Tool content must remain readable Vietnamese, not literal Unicode escape sequences for the model"); Check(approvals == 1);
        });
        await Test("Stale hash cannot overwrite, fresh hash backs up", async () =>
        {
            var before = scope.Read("a.txt");
            await tools.Execute(Call("write_text", new { path = "a.txt", text = "WRONG", expectedHash = "STALE" }), default); Check(scope.Read("a.txt").SequenceEqual(before));
            await tools.Execute(Call("write_text", new { path = "a.txt", text = "second", expectedHash = SafeWorkspace.Hash(before) }), default);
            Check(Encoding.UTF8.GetString(scope.Read("a.txt")) == "second"); Check(Directory.GetFiles(Path.Combine(state, "backups")).Length == 1);
        });
        await Test("Denied write and cancelled approval never write", async () =>
        {
            var no = new AgentTools(scope, state, (_, _) => Task.FromResult(false), (_, _) => { }) { ReadOnly = false };
            await no.Execute(Call("write_text", new { path = "denied.txt", text = "x", expectedHash = "" }), default); Check(!File.Exists(Path.Combine(workspace, "denied.txt")));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await no.Execute(Call("write_text", new { path = "cancelled.txt", text = "x", expectedHash = "" }), cancel.Token); throw new Exception("Not cancelled"); } catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(workspace, "cancelled.txt")));
        });
        await Test("Existing Word structure and indexed paragraphs can be inspected without edits", async () =>
        {
            var bytes = AiArtifacts.Create(new AiArtifact { FileName = "plan.docx", Text = "KẾ HOẠCH\nLập dự toán.\nGửi hồ sơ." });
            File.WriteAllBytes(Path.Combine(workspace, "plan.docx"), bytes);
            var check = await tools.Execute(Call("check_word", new { path = "plan.docx" }), default); Check(check.Contains("\"structureValid\":true")); Check(check.Contains("\"visualLayoutVerified\":false"));
            var result = await tools.Execute(Call("word_paragraphs", new { path = "plan.docx" }), default); using var parsed = JsonDocument.Parse(result);
            Check(parsed.RootElement.GetProperty("paragraphs").GetArrayLength() == 3);
            Check(scope.Read("plan.docx").SequenceEqual(bytes));
        });
        await Test("No blind Word overwrite or arbitrary shell tool", async () =>
        {
            var old = scope.Read("plan.docx"); await tools.Execute(Call("create_word", new { path = "plan.docx", text = "Wrong" }), default); Check(old.SequenceEqual(scope.Read("plan.docx")));
            Check((await tools.Execute(Call("shell", new { command = "whoami" }), default)).Contains("false"));
            Check((await tools.Execute(Call("click_control", new { token = "fake" }), default)).Contains("false"));
        });
        await Test("Binary PDF cannot silently become a filename-only text result", async () =>
        { File.WriteAllText(Path.Combine(workspace, "scan.pdf"), "%PDF-1.7 fixture"); var output = await tools.Execute(Call("read_file", new { path = "scan.pdf", offset = "0" }), default); Check(output.Contains("false")); });
        await Test("Persistent history retains prior session and isolates new context", () =>
        {
            var s = new LabSession { Workspace = workspace }; s.Add("user", "Ngày mai gửi hồ sơ"); s.Save(state); var loaded = LabSession.Load(state);
            Check(loaded.Context().Contains("Ngày mai")); var next = new LabSession { Workspace = workspace }; next.Save(state); Check(!next.Context().Contains("Ngày mai")); Check(File.Exists(Path.Combine(state, s.Id.ToString("N") + ".json"))); return Task.CompletedTask;
        });
        await Test("Ollama tool loop really reads file before final answer", async () =>
        {
            var handler = new FakeHandler(true); using var runner = new AgentRunner(handler); var s = new LabSession(); string? final = null;
            await runner.Run(new() { Model = "fixture" }, "", s, tools, "Read file", (k, t) => { if (k == "final") final = t; }, () => { }, default);
            Check(final == "Verified answer" && handler.Requests == 2 && handler.SawToolResult);
        });
        await Test("Compatible API assembles fragmented tool arguments and returns call id", async () =>
        {
            var handler = new FakeHandler(false); using var runner = new AgentRunner(handler); var s = new LabSession();
            await runner.Run(new() { Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.invalid/v1", Model = "fixture" }, "test-not-secret", s, tools, "Read file", (_, _) => { }, () => { }, default);
            Check(handler.Requests == 2 && handler.SawToolResult);
        });
        await Test("Ollama tool round trip keeps thinking in memory and honors explicit thinking setting", async () =>
        {
            var handler = new ThinkingHandler(); using var runner = new AgentRunner(handler); var session = new LabSession(); string? final = null;
            await runner.Run(new() { Model = "fixture", OllamaThinking = true }, "", session, tools, "Read", (k, t) => { if (k == "final") final = t; }, () => { }, default);
            Check(handler.SawThinking && handler.SawThinkingSetting && final == "Verified answer");
            Check(!session.Context().Contains("Fixture thinking field"), "Thinking must not enter persistent history");
        });
        await Test("Ollama length stop cannot execute a truncated tool proposal", async () =>
        {
            var before = events.Count; using var runner = new AgentRunner(new ThinkingHandler(truncated: true));
            try { await runner.Run(new() { Model = "fixture" }, "", new(), tools, "Read", (_, _) => { }, () => { }, default); throw new Exception("Truncated proposal accepted"); }
            catch (IOException ex) { Check(ex.Message.Contains("length")); }
            Check(events.Count == before);
        });
        await Test("Partial stream does not execute proposed tools", async () =>
        {
            var before = events.Count; using var runner = new AgentRunner(new FakeHandler(true, true));
            try { await runner.Run(new() { Model = "fixture" }, "", new(), tools, "Read", (_, _) => { }, () => { }, default); throw new Exception("Partial response accepted"); } catch (IOException) { }
            Check(events.Count == before);
        });
        await Test("Cancellation interrupts request without retry", async () =>
        {
            using var cancel = new CancellationTokenSource(80); using var runner = new AgentRunner(new NeverHandler());
            try { await runner.Run(new() { Model = "fixture" }, "", new(), tools, "Read", (_, _) => { }, () => { }, cancel.Token); throw new Exception("Not cancelled"); } catch (OperationCanceledException) { }
        });
        await Test("Literal search yields evidence without running code", async () =>
        { File.WriteAllText(Path.Combine(workspace, "sample.cs"), "// unique-evidence-442\nclass Example {}"); var response = await tools.Execute(Call("search_files", new { query = "unique-evidence-442" }), default); Check(response.Contains("sample.cs")); });
        await Test("Removed unsandboxed build tool cannot execute even in writable mode", async () =>
        {
            var before = approvals;
            var response = await tools.Execute(Call("dotnet_check", new { path = "Fixture.csproj", action = "build" }), default);
            Check(response.Contains("false") && approvals == before);
        });
        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed. Native computer UI and visual Word rendering NOT certified by these tests.");
        File.WriteAllLines(Path.Combine(report, "tests.txt"), lines); Console.WriteLine(string.Join("\n", lines)); return failed == 0 ? 0 : 1;
    }
    public static async Task<int> Live(string[] args)
    {
        // Only synthetic fixtures are sent to an explicitly selected local/LAN model.
        var i = Array.IndexOf(args, "--live-eval");
        if (args.Length < i + 4) throw new ArgumentException("--live-eval endpoint model new-output-directory");
        var profile = new AiProfile { BaseUrl = args[i + 1], Model = args[i + 2], OllamaThinking = args.Contains("--no-thinking") ? false : null };
        if (profile.IsOllamaCloud) throw new IOException("No cloud live evaluation in this command.");
        var root = Path.GetFullPath(args[i + 3]); if (Directory.Exists(root)) throw new IOException("Use a new test directory.");
        Directory.CreateDirectory(root); var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "brief.md"), "Dự án mẫu: Cầu Bình Minh. Đã khảo sát. Chưa xong: lập dự toán và gửi hồ sơ. Mã đối chiếu chỉ có trong tệp: H2-7429. Không có ngày hoàn thành xác nhận.");
        var s = new LabSession { Workspace = workspace }; var log = new List<string>(); var calls = new List<string>();
        var wordMode = args.Contains("--word");
        var tools = new AgentTools(new(workspace), Path.Combine(root, "state"), (a, _) => Task.FromResult(wordMode && a.Title is "Cho AI tự chạy mã trên bản sao trong lượt này" or "Lưu kết quả: plan.docx"), (k, t) => { s.Add(k, t); calls.Add(k + " " + t); }) { ReadOnly = !wordMode };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(6)); using var runner = new AgentRunner();
        try
        {
            await runner.Run(profile, "", s, tools, wordMode ? "Đọc brief.md. Tạo một tệp Word mới tên plan.docx có tên dự án, mã đối chiếu, 2 việc chưa hoàn thành, ghi rõ chưa xác nhận ngày hoàn thành. Đọc skill documents, tự viết mã và kiểm nội dung, publish_artifact rồi check_word và đọc lại. Không chỉ mô tả đề xuất, hãy gọi công cụ." : "Đọc brief.md bằng công cụ. Nêu mã đối chiếu và việc chưa xong; không đoán nội dung, không sửa tệp.", (k, t) => { if (k is "status" or "final" or "tool" or "metrics") { log.Add($"{DateTimeOffset.Now:O} {k} {t}"); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); } }, () => s.Save(Path.Combine(root, "state")), stop.Token);
            var final = s.Events.LastOrDefault(e => e.Kind == "assistant")?.Text ?? "";
            var passed = calls.Any(c => c.StartsWith("tool-result read_file")) && final.Contains("H2-7429") && final.Contains("dự toán", StringComparison.OrdinalIgnoreCase);
            if (wordMode)
            {
                var file = Path.Combine(workspace, "plan.docx"); var wordText = File.Exists(file) ? AiDocuments.Read(file).Text : "";
                passed = calls.Any(c => c.StartsWith("tool-result check_word") && c.Contains("true")) && wordText.Contains("H2-7429") && wordText.Contains("dự toán", StringComparison.OrdinalIgnoreCase) && wordText.Contains("gửi hồ sơ", StringComparison.OrdinalIgnoreCase);
            }
            log.Add("ACTUAL_READ_AND_EVIDENCE=" + passed); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); return passed ? 0 : 1;
        }
        catch (Exception ex) { log.Add(ex.GetType().Name + ": " + ex.Message); File.WriteAllLines(Path.Combine(root, "live-eval.txt"), log); return 1; }
    }
    private sealed class NeverHandler : HttpMessageHandler
    { protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { await Task.Delay(30000, ct); return new(HttpStatusCode.OK); } }
    private sealed class FakeHandler(bool ollama, bool incomplete = false) : HttpMessageHandler
    {
        public int Requests; public bool SawToolResult;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; var payload = await request.Content!.ReadAsStringAsync(ct);
            string body;
            if (Requests == 1)
            {
                if (ollama) body = JsonSerializer.Serialize(new { message = new { role = "assistant", tool_calls = new[] { new { function = new { name = "read_file", arguments = new { path = "a.txt", offset = "0" } } } } }, done = false }) + "\n" + (incomplete ? "" : "{\"done\":true}\n");
                else
                {
                    object Chunk(object delta, string? finish = null) => new { choices = new[] { new { delta, finish_reason = finish } } };
                    body = "data: " + JsonSerializer.Serialize(Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", function = new { name = "read_file", arguments = "{\"path\":\"a." } } } })) + "\n\n"
                        + "data: " + JsonSerializer.Serialize(Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "txt\",\"offset\":\"0\"}" } } } }, "tool_calls")) + "\n\n";
                }
            }
            else
            {
                using var j = JsonDocument.Parse(payload); var messages = j.RootElement.GetProperty("messages");
                SawToolResult = messages.EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool" && m.GetProperty("content").GetString()!.Contains("second") && (ollama || m.GetProperty("tool_call_id").GetString() == "call_1"));
                body = ollama ? "{\"message\":{\"content\":\"Verified answer\"},\"done\":true}\n" : "data: {\"choices\":[{\"delta\":{\"content\":\"Verified answer\"},\"finish_reason\":\"stop\"}]}\n\n";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
    private sealed class ThinkingHandler(bool truncated = false) : HttpMessageHandler
    {
        private int _requests;
        public bool SawThinking, SawThinkingSetting;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            SawThinkingSetting = payload.RootElement.TryGetProperty("think", out var setting) && setting.ValueKind == JsonValueKind.True;
            string body;
            if (++_requests == 1)
                body = "{\"message\":{\"thinking\":\"Fixture thinking field\"},\"done\":false}\n" +
                    JsonSerializer.Serialize(new { message = new { tool_calls = new[] { new { function = new { name = "read_file", arguments = new { path = "a.txt", offset = "0" } } } } }, done = true, done_reason = truncated ? "length" : "stop" }) + "\n";
            else
            {
                SawThinking = payload.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant" && m.TryGetProperty("thinking", out var thought) && thought.GetString() == "Fixture thinking field");
                body = JsonSerializer.Serialize(new { message = new { content = SawThinking ? "Verified answer" : "" }, done = true, done_reason = "stop" }) + "\n";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
