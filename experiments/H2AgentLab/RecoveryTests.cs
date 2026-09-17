using System.Net;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab;

public static class RecoveryTests
{
    private static ToolCall Call(string name, object args) => new("fixture", name, JsonSerializer.SerializeToElement(args));
    private static JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task<int> Run(string root)
    {
        root = Path.GetFullPath(root); if (Directory.Exists(root)) throw new IOException("Use a new fixture directory.");
        Directory.CreateDirectory(root); var lines = new List<string>(); var failed = 0;
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); lines.Add("PASS " + name); }
            catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex); }
            File.WriteAllLines(Path.Combine(root, "tests.txt"), lines);
        }
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        File.WriteAllBytes(Path.Combine(workspace, "promt.docx"), AiArtifacts.Create(new() { FileName = "promt.docx", Text = "Recovery fixture. Reference H2-REC-731. Only pending task: review quantities." }));
        File.WriteAllText(Path.Combine(workspace, "sample.txt"), "evidence-731");
        var events = new List<string>();
        var tools = new AgentTools(new(workspace), Path.Combine(root, "state"), (_, _) => Task.FromResult(true), (k, t) => events.Add(k + " " + t));

        await Test("Missing file returns observed spelling candidates, not invented file contents", async () =>
        {
            var result = Parse(await tools.Execute(Call("read_file", new { path = "prompt.docx", offset = "0" }), default));
            Check(result.GetProperty("recovery").GetProperty("code").GetString() == "not_found", "Missing structured diagnosis");
            var candidates = result.GetProperty("diagnostic").GetProperty("matches");
            Check(candidates.EnumerateArray().Any(c => c.GetProperty("path").GetString() == "promt.docx"), "No actual spelling candidate");
            Check(!result.ToString().Contains("H2-REC-731"), "Name discovery unexpectedly read document contents");
        });
        await Test("Ambiguous matching names remain separate; discovery never selects or modifies one", async () =>
        {
            var folder = Path.Combine(workspace, "ambiguous"); Directory.CreateDirectory(Path.Combine(folder, "a")); Directory.CreateDirectory(Path.Combine(folder, "b"));
            File.WriteAllText(Path.Combine(folder, "a", "promt.txt"), "A"); File.WriteAllText(Path.Combine(folder, "b", "promt.txt"), "B");
            var r = Parse(await tools.Execute(Call("find_files", new { query = "prompt.txt", path = "ambiguous" }), default));
            Check(r.GetProperty("matches").GetArrayLength() == 2, "Lost ambiguous match");
            Check(File.ReadAllText(Path.Combine(folder, "a", "promt.txt")) == "A", "Discovery mutated a file");
        });
        await Test("Discovery reports truncated scans and does not cross protected scope", async () =>
        {
            var folder = Path.Combine(workspace, "many"); Directory.CreateDirectory(folder);
            for (var n = 0; n < 215; n++) File.WriteAllText(Path.Combine(folder, $"{n:000}.txt"), "fixture");
            File.WriteAllText(Path.Combine(folder, ".env"), "synthetic-only");
            var list = Parse(await tools.Execute(Call("list_files", new { path = "many" }), default));
            Check(list.GetProperty("Truncated").GetBoolean(), "No truncation flag");
            var found = Parse(await tools.Execute(Call("find_files", new { query = "214.txt", path = "many" }), default));
            Check(found.GetProperty("matches").EnumerateArray().Any(m => m.GetProperty("exact").GetBoolean() && m.GetProperty("path").GetString()!.EndsWith("214.txt")), "Cannot discover exact file beyond list's first 200");
            Check(found.GetProperty("searchedSupportedFiles").GetInt32() == 215, "Incomplete scan counted as complete");
            var denied = Parse(await tools.Execute(Call("read_file", new { path = "../outside.txt", offset = "0" }), default));
            Check(!denied.GetProperty("recovery").GetProperty("recoverable").GetBoolean() && !denied.TryGetProperty("diagnostic", out _), "Boundary triggered broader search");
        });
        foreach (var local in new[] { true, false })
        await Test((local ? "Ollama" : "API") + " premature missing-file conclusion is corrected through discovery and actual read", async () =>
        {
            using var handler = new Sequence(local, (n, _) => n switch
            {
                1 => Step.Tool("read_file", new { path = "prompt.docx", offset = "0" }),
                2 => Step.Final("Không có file. Tôi không làm được."),
                3 => Step.Tool("find_files", new { query = "prompt.docx", path = "." }),
                4 => Step.Tool("read_file", new { path = "promt.docx", offset = "0" }),
                _ => Step.Final("Đã đọc promt.docx: H2-REC-731. Việc còn lại: review quantities.")
            });
            var session = new LabSession(); var final = await RunLoop(handler, tools, session);
            Check(handler.Requests == 5 && final.Contains("H2-REC-731"), "Did not recover before conclusion");
            Check(session.Events.Any(e => e.Kind == "unverified-draft") && handler.SawRecovery, "Premature draft not challenged");
        });
        await Test("Wrong skill name recovers through actual catalog, not another invented tool", async () =>
        {
            using var handler = new Sequence(true, (n, _) => n switch
            {
                1 => Step.Tool("read_skill", new { name = "excel-editor", path = "SKILL.md" }),
                2 => Step.Tool("list_skills", new { query = "" }),
                3 => Step.Tool("read_skill", new { name = "spreadsheets", path = "SKILL.md" }),
                _ => Step.Final("Đã đọc kỹ năng spreadsheets thực tế.")
            });
            Check((await RunLoop(handler, tools, new())).StartsWith("Đã đọc"), "Valid skill did not recover the wrong name");
        });
        await Test("Wrong resource path is corrected by reading actual SKILL.md", async () =>
        {
            using var handler = new Sequence(true, (n, _) => n switch
            {
                1 => Step.Tool("read_skill", new { name = "documents", path = "word.md" }),
                2 => Step.Tool("read_skill", new { name = "documents", path = "SKILL.md" }),
                _ => Step.Final("Đã sửa đường dẫn skill.")
            });
            Check((await RunLoop(handler, tools, new())).StartsWith("Đã sửa"), "Resource correction failed");
        });
        await Test("Assertion failure is diagnosed, revised and read back through a real sandbox run", async () =>
        {
            using var handler = new Sequence(true, (n, payload) => n switch
            {
                1 => Step.Tool("run_python", new { code = "assert 2+2==5, 'incorrect expected result'", inputs = "", previous_run = "" }),
                2 => Step.Final("Không làm được vì Python lỗi."),
                3 => Step.Tool("read_skill", new { name = "coding", path = "references/recovery.md" }),
                4 => Step.Tool("run_python", new { code = "from pathlib import Path\nassert 2+2==4\np=Path('output/answer.txt'); p.write_text('4')\nassert p.read_text()=='4'", inputs = "", previous_run = "" }),
                5 => Step.Tool("inspect_artifact", new { run_id = LastTool(payload).GetProperty("runId").GetString(), path = "answer.txt" }),
                _ => Step.Final("Đã kiểm lại kết quả 4 trong answer.txt; chưa xuất vào kho gốc.")
            });
            var result = await RunLoop(handler, tools, new());
            Check(handler.Requests == 6 && result.StartsWith("Đã kiểm"), "Assertion recovery did not complete");
            Check(!File.Exists(Path.Combine(workspace, "answer.txt")), "Read-only script changed original workspace");
        });
        await Test("Identical failing calls stop executing and an unverified success draft is not delivered", async () =>
        {
            var before = events.Count(e => e == "tool-start read_file");
            using var handler = new Sequence(true, (n, _) => n <= 5 ? Step.Tool("read_file", new { path = "missing.txt", offset = "0" }) : Step.Final("Đã hoàn thành mọi thứ."));
            var final = await RunLoop(handler, tools, new());
            Check(events.Count(e => e == "tool-start read_file") - before == 2, "Repeated failed call executed more than twice");
            Check(final.StartsWith("Chưa xác minh") && !final.Contains("Đã hoàn thành mọi thứ"), "Unverified completion leaked through");
            Check(handler.Requests == 8, "Recovery continuation not bounded");
        });
        await Test("Invalid Word output is a failed validation; corrected structure must be checked again", async () =>
        {
            var file = Path.Combine(workspace, "invalid.docx");
            var valid = AiArtifacts.Create(new() { FileName = "invalid.docx", Text = "Structure fixture" }); File.WriteAllBytes(file, valid);
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(file, true))
                doc.MainDocumentPart!.Document!.Body!.PrependChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Table()));
            var call = Call("check_word", new { path = "invalid.docx" }); var guard = new RecoverySupervisor();
            var result = await tools.Execute(call, default); var invalid = Parse(result);
            Check(invalid.GetProperty("recovery").GetProperty("code").GetString() == "validation_failed", "Bad structure counted as success");
            guard.Observe(call, result); Check(guard.HasPending, "Failed validation not tracked");
            File.WriteAllBytes(file, valid); guard.Observe(call, await tools.Execute(call, default));
            Check(!guard.HasPending, "Corrected document validation did not resolve fault");
        });
        await Test("Denied mutation is sticky across retries and alternate publication tool", async () =>
        {
            var asks = 0; var denied = new AgentTools(new(workspace), Path.Combine(root, "denied"), (_, _) => { asks++; return Task.FromResult(false); }, (_, _) => { }) { ReadOnly = false };
            await denied.Execute(Call("write_text", new { path = "denied.txt", text = "A", expectedHash = "" }), default);
            await denied.Execute(Call("write_text", new { path = "denied.txt", text = "B", expectedHash = "" }), default);
            var bypass = Parse(await denied.Execute(Call("publish_artifact", new { run_id = "invalid", path = "out.txt", destination = "denied.txt", expected_hash = "" }), default));
            Check(asks == 1 && bypass.GetProperty("recovery").GetProperty("code").GetString() == "denied", "Permission refusal re-prompted or bypassed");
            Check(!File.Exists(Path.Combine(workspace, "denied.txt")), "Denied write executed");
        });
        await Test("Stale hash requires actual reread before a newly approved write", async () =>
        {
            var writable = new AgentTools(new(workspace), Path.Combine(root, "state"), (_, _) => Task.FromResult(true), (_, _) => { }) { ReadOnly = false };
            var stale = Parse(await writable.Execute(Call("write_text", new { path = "sample.txt", text = "updated", expectedHash = "OLD" }), default));
            Check(stale.GetProperty("recovery").GetProperty("code").GetString() == "stale_state", "Stale hash not classified");
            var current = Parse(await writable.Execute(Call("read_file", new { path = "sample.txt", offset = "0" }), default));
            await writable.Execute(Call("write_text", new { path = "sample.txt", text = "updated", expectedHash = current.GetProperty("hash").GetString() }), default);
            Check(File.ReadAllText(Path.Combine(workspace, "sample.txt")) == "updated", "Fresh hash did not allow approved correction");
        });
        await Test("Malformed completed tool JSON is corrected without executing the malformed batch", async () =>
        {
            var before = events.Count(e => e == "tool-start read_file");
            using var handler = new Sequence(false, (n, _) => n switch
            {
                1 => new("read_file", null, "{not json", ""),
                2 => Step.Tool("read_file", new { path = "sample.txt", offset = "0" }),
                _ => Step.Final("Đã đọc lại tham số đúng.")
            });
            Check((await RunLoop(handler, tools, new())).StartsWith("Đã đọc"), "Malformed recovery did not finish");
            Check(events.Count(e => e == "tool-start read_file") - before == 1, "Malformed call executed");
        });
        await Test("Uncertain side effects require observation before repeat; a plan is not observation", () =>
        {
            var guard = new RecoverySupervisor(); var call = Call("click_control", new { token = "fixture" });
            guard.Observe(call, new System.Text.Json.Nodes.JsonObject { ["recovery"] = RecoveryPolicy.ToJson(new("tool_error", "outcome unknown", true, "inspect")) }.ToJsonString());
            Check(guard.Block(call) is not null, "Blind duplicate side effect allowed");
            guard.Observe(Call("update_plan", new { plan = "retry" }), "{\"saved\":true}");
            Check(guard.Block(call) is not null, "Plan treated as actual observation");
            guard.Observe(Call("read_file", new { path = "unrelated.txt", offset = "0" }), "{\"content\":\"not the window\"}");
            Check(guard.Block(call) is not null, "Unrelated file read allowed repeating an uncertain window action");
            guard.Observe(Call("inspect_window", new { reason = "check outcome" }), "{\"controls\":[]}");
            Check(guard.Block(call) is null, "Fresh observation not recognized"); return Task.CompletedTask;
        });
        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed. Model steps simulated; file/Python operations real.");
        File.WriteAllLines(Path.Combine(root, "tests.txt"), lines); return failed == 0 ? 0 : 1;
    }

    private static JsonElement LastTool(JsonElement payload) => Parse(payload.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!);
    private static async Task<string> RunLoop(Sequence handler, AgentTools tools, LabSession session)
    {
        using var runner = new AgentRunner(handler); var final = "";
        await runner.Run(new() { Protocol = handler.Local ? AiProtocol.Ollama : AiProtocol.OpenAiChat, BaseUrl = handler.Local ? "http://localhost:11434" : "https://example.invalid/v1", Model = "fixture" }, "", session, tools,
            "Complete this synthetic recovery task within the selected workspace.", (kind, text) => { if (kind == "final") final = text; }, () => { }, default);
        return final;
    }
    private sealed record Step(string? Name, object? Args, string? RawArgs, string Text)
    {
        public static Step Tool(string name, object args) => new(name, args, null, "");
        public static Step Final(string text) => new(null, null, null, text);
    }
    private sealed class Sequence(bool local, Func<int, JsonElement, Step> respond) : HttpMessageHandler
    {
        public bool Local => local; public int Requests; public bool SawRecovery;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; if (Requests > 25) throw new IOException("Fixture loop exceeded bound");
            var payload = Parse(await request.Content!.ReadAsStringAsync(ct));
            SawRecovery |= payload.ToString().Contains("Your proposed final answer is not verified");
            var step = respond(Requests, payload);
            object delta = step.Name is null ? new { content = step.Text } : new { tool_calls = new[] { new { index = 0, id = "call_" + Requests, type = "function", function = new { name = step.Name, arguments = step.RawArgs is not null ? (object)step.RawArgs : local ? step.Args : JsonSerializer.Serialize(step.Args) } } } };
            var body = local ? JsonSerializer.Serialize(new { message = delta, done = true }) + "\n" : "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = step.Name is null ? "stop" : "tool_calls" } } }) + "\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
