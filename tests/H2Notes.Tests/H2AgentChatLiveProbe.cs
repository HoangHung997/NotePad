using System.Text.Json;
using H2AgentLab.Integration;
using H2Notes.Avalonia;
using H2Notes.Core;

// Explicit opt-in only. Synthetic documents in an isolated output folder; no live project is opened.
internal static class H2AgentChatLiveProbe
{
    public static async Task<int> Run(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Use a new probe folder.");
        var profile = LocalConfiguration.Read().Ai.Profiles.First(p => p.Protocol == AiProtocol.Ollama && p.Model == "gemma4:cloud");
        profile.TimeoutSeconds = 180;
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "agent-workspace"); Directory.CreateDirectory(workspace);
        var state = Path.Combine(root, "agent-runtime");
        var now = DateTime.UtcNow;
        var scope = new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Workspace,
            "workspace:" + workspace, true, false, now, now.AddMinutes(20), documentPath: workspace);
        using var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""));
        var task = await adapter.StartTaskAsync(null,
            "Kiểm thử tài liệu mẫu: hãy tạo 3 tệp bằng công cụ run_python: gioi-thieu.docx giới thiệu H2 Assistant trong 2 đoạn ngắn; bang-mau.csv có 5 hàng tên và số lượng; gioi-thieu.pdf có dòng H2 Assistant preview test. " +
            "Chỉ dùng dữ liệu mẫu, không đọc tài liệu có sẵn. Lưu các tệp vào output trong môi trường Python rồi công bố kết quả. Dùng python-docx và reportlab. Trả lời tiếng Việt ngắn gọn kèm danh sách tệp và bảng Markdown 3 dòng.",
            new(workspace, "Synthetic acceptance fixture only.", PermissionScope: scope, ThreadId: Guid.NewGuid()), false);
        var sequence = -1L; var supplemented = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        H2AgentTaskSummary done;
        while (true)
        {
            var observation = adapter.ObserveTask(task, sequence); done = observation.Summary;
            foreach (var item in observation.Progress) { Console.WriteLine(item.Kind + ": " + item.Code + " " + item.Message); sequence = item.Sequence; }
            if (!supplemented && observation.Progress.Any(p => p.Code == "tool-ok"))
                supplemented = adapter.SupplementTask(task, Guid.NewGuid(), "Bổ sung vào câu trả lời cuối: ghi đúng cụm PHU-LUC-CHAT để kiểm tra đã nhận lời nhắn này. Giữ nguyên yêu cầu tạo ba tệp.");
            if (done.PendingApproval is { } approval)
            { adapter.CancelTask(task); throw new InvalidOperationException("Scoped probe unexpectedly requested additional permission: " + approval.Title); }
            if (H2AgentActivity.IsTerminal(done.Status)) break;
            try { await Task.Delay(500, timeout.Token); }
            catch (OperationCanceledException) { adapter.CancelTask(task); throw; }
        }
        var pdf = done.Evidence.FirstOrDefault(e => e.Kind == "artifact" && Path.GetExtension(e.LocalPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        string? previewError = null;
        if (pdf is not null)
        {
            try { var page = await adapter.PreviewPdfAsync(pdf.EvidenceId, 0); await File.WriteAllBytesAsync(Path.Combine(root, "pdf-page.png"), page.Png); }
            catch (Exception ex) { previewError = ex.ToString(); }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { done, previewError, supplemented }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Status={done.Status}; artifacts={done.Evidence.Count(e => e.Kind == "artifact")}; pdfPreview={pdf is not null && previewError is null}");
        return done.Status == H2AgentTaskStatus.Completed && pdf is not null && previewError is null
            && supplemented && done.FinalText?.Contains("PHU-LUC-CHAT") == true ? 0 : 1;
    }
}
