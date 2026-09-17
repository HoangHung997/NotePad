using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab;

public static class RecoveryLiveEvaluation
{
    public static async Task<int> Run(string[] args)
    {
        var index = Array.IndexOf(args, "--recovery-live");
        if (args.Length < index + 4) throw new IOException("--recovery-live loopback-endpoint model NEW-directory");
        var endpoint = new Uri(args[index + 1]); if (!endpoint.IsLoopback) throw new IOException("Fixture only accepts a loopback Ollama endpoint.");
        var root = Path.GetFullPath(args[index + 3]); if (Directory.Exists(root)) throw new IOException("Use a new fixture directory.");
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace); var state = Path.Combine(root, "state");
        var source = AiArtifacts.Create(new() { FileName = "promt.docx", Text = "Dự án kiểm thử: Cầu Bình Minh. Mã chứng cứ H2-REC-731. Việc duy nhất chưa xong: kiểm khối lượng. Không có ngày hoàn thành được xác nhận." });
        File.WriteAllBytes(Path.Combine(workspace, "promt.docx"), source);
        var session = new LabSession { Workspace = workspace }; var log = new List<string>(); var readEvidence = false;
        void Log(string text) { log.Add(text); File.WriteAllLines(Path.Combine(root, "live.txt"), log); }
        var tools = new AgentTools(new(workspace), state, (_, _) => Task.FromResult(false), (kind, text) =>
        {
            session.Add(kind, text);
            if (kind == "tool-result" && text.StartsWith("read_file") && text.Contains("H2-REC-731")) readEvidence = true;
        });
        // Deliberately seed a real missing-file tool result. The benchmark measures
        // diagnosis of a supplied failure, not whether the model naturally mistypes.
        var failure = await tools.Execute(new("seed", "read_file", JsonSerializer.SerializeToElement(new { path = "prompt.docx", offset = "0" })), default);
        Log("SEEDED_REAL_READ_FAILURE " + failure);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(8)); using var runner = new AgentRunner();
        try
        {
            var final = "";
            await runner.Run(new() { BaseUrl = endpoint.ToString(), Model = args[index + 2], OllamaThinking = args.Contains("--no-thinking") ? false : null }, "", session, tools,
                "Đọc prompt.docx trong thư mục đã chọn, nêu đúng mã chứng cứ và việc chưa xong. Lần đọc vừa rồi báo lỗi và đã có kết quả công cụ trong nhật ký. Hãy tự kiểm nguyên nhân, dùng tên thực tế nếu chỉ có một tệp phù hợp; đọc được nội dung mới kết luận. Không sửa tệp và không hỏi lại khi đã có bằng chứng duy nhất rõ ràng.",
                (kind, text) => { if (kind is "tool" or "recovery" or "status" or "final" or "metrics") Log($"{DateTimeOffset.Now:O} {kind} {text}"); if (kind == "final") final = text; }, () => session.Save(state), stop.Token);
            var pass = readEvidence && final.Contains("H2-REC-731") && final.Contains("khối lượng", StringComparison.OrdinalIgnoreCase) && source.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace, "promt.docx")));
            Log("ACTUAL_RECOVERY_PASS=" + pass); return pass ? 0 : 1;
        }
        catch (Exception ex) { Log("NOT_COMPLETE " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
