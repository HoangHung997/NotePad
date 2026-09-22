using System.Text.Json;
using H2AgentLab.Integration;
using H2Notes.Avalonia;
using H2Notes.Core;

// Manual, explicit opt-in acceptance against the production adapter and the configured cloud
// model. Fixtures/results are isolated from the user's settings, projects and conversations.
internal static class H2AgentCapabilityLiveProbe
{
    public static async Task<int> OfficeManifest(string root)
    {
        root = Path.GetFullPath(root);
        for (var round = 1; round <= 3; round++)
        {
            var work = Path.Combine(root, "round-" + round);
            if (!File.Exists(Path.Combine(work, ".h2-agent-test-fixture"))) throw new IOException("Missing fixture marker.");
            foreach (var (extension, kind) in new[] { ("docx", "word"), ("xlsx", "excel") })
            {
                var destination = Path.Combine(work, $"live-{kind}-{round}.{extension}");
                if (!File.Exists(destination))
                {
                    File.Copy(Path.Combine(work, "seed." + extension), destination);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(destination) { UseShellExecute = true });
                    await Task.Delay(1500);
                }
            }
        }
        using var client = new H2AgentLab.Office.OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"));
        var word = await client.DiscoverWordAsync(); var excel = await client.DiscoverExcelAsync();
        var scenarios = new List<Scenario>();
        for (var round = 1; round <= 3; round++)
        {
            var work = Path.Combine(root, "round-" + round);
            var doc = word.Documents.Single(d => d.FullName.Equals(Path.Combine(work, $"live-word-{round}.docx"), StringComparison.OrdinalIgnoreCase));
            var book = excel.Workbooks.Single(b => b.FullName.Equals(Path.Combine(work, $"live-excel-{round}.xlsx"), StringComparison.OrdinalIgnoreCase));
            scenarios.Add(new($"word-live-{round}", $"Dùng công cụ word để sửa tài liệu Word đang mở có session_id {doc.SessionId}, đường dẫn {doc.FullName}. Đọc snapshot mới. Đổi đoạn thứ hai (paragraph index 1) thành LIVE-WORD-{round}; đặt đoạn này in đậm. Giữ tiêu đề và đoạn KEEP-{round}. Đọc lại kiểm tra rồi dùng word.save_copy lưu live-word-result-{round}.docx vào workspace. Không sửa tài liệu khác, không dùng script thay thế công cụ word.", work, SessionId: doc.SessionId, Application: "word", DocumentPath: doc.FullName));
            scenarios.Add(new($"excel-live-{round}", $"Dùng công cụ excel sửa workbook đang mở session_id {book.SessionId}, đường dẫn {book.FullName}. Đọc snapshot mới. Sheet Data: B2 thành 15, B4 vẫn công thức =SUM(B2:B3), đặt B2 in đậm. Tính lại, xác nhận tổng là 45 và D1 KEEP-{round} còn nguyên. Dùng excel.save_copy lưu live-excel-result-{round}.xlsx vào workspace. Không sửa workbook khác, không dùng script thay thế công cụ excel.", work, SessionId: book.SessionId, Application: "excel", DocumentPath: book.FullName));
        }
        await File.WriteAllTextAsync(Path.Combine(root, "office.json"), JsonSerializer.Serialize(scenarios, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Prepared {scenarios.Count} scoped live Office tasks; discovery returns metadata only.");
        return 0;
    }
    internal sealed record Scenario(string Id, string Prompt, string Workspace, bool FullAccess = false,
        string[]? Attachments = null, string? SessionId = null, string? Application = null, string? DocumentPath = null,
        string? OcrEngine = null, string? OcrRuntime = null);

    public static async Task<int> Run(string manifestPath, string output)
    {
        var scenarios = JsonSerializer.Deserialize<Scenario[]>(await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a fresh result directory to preserve earlier attempts.");
        Directory.CreateDirectory(output);
        var profile = ProjectWorkspaceStore.Clone(LocalConfiguration.Read().Ai.Profiles.First(p =>
            p.Protocol == AiProtocol.Ollama && p.Model == "gemma4:cloud"));
        profile.TimeoutSeconds = 180;
        var failures = 0;
        foreach (var scenario in scenarios)
        {
            var folder = Path.Combine(output, scenario.Id);
            Directory.CreateDirectory(folder);
            var workspace = Path.GetFullPath(scenario.Workspace);
            if (!File.Exists(Path.Combine(workspace, ".h2-agent-test-fixture")))
                throw new IOException("Workspace must carry the synthetic-fixture marker.");
            var now = DateTime.UtcNow;
            var app = scenario.Application == "word" ? H2ApplicationKind.Word : H2ApplicationKind.Excel;
            var scope = scenario.FullAccess
                ? new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess, H2AgentResourceScopeKind.Machine,
                    H2AgentPermissionScope.CurrentMachineResourceKey, true, false, now, now.AddMinutes(20))
                : scenario.SessionId is not null
                    ? new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Session,
                        "session:" + scenario.SessionId, true, false, now, now.AddMinutes(20), app,
                        documentSessionId: scenario.SessionId, documentPath: scenario.DocumentPath)
                    : new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Workspace,
                        "workspace:" + workspace, true, false, now, now.AddMinutes(20), documentPath: workspace);
            using var adapter = new H2ProductionAgentAdapter(Path.Combine(folder, "runtime"), () => new(profile, ""));
            var attachments = (scenario.Attachments ?? []).Select(p => AiDocuments.Read(Path.GetFileName(p), File.ReadAllBytes(p))).ToArray();
            if (scenario.OcrEngine is not null)
            {
                var originalHashes = attachments.Select(a => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Data))).ToArray();
                var prepared = await AiPdfProcessor.PrepareAttachmentsAsync(attachments, profile,
                    new() { Engine = Enum.Parse<AiPdfEngine>(scenario.OcrEngine), RuntimeRoot = scenario.OcrRuntime!, OcrImages = true, TimeoutSeconds = 300 },
                    Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "convert.py"), new Progress<string>(Console.WriteLine));
                if (!originalHashes.SequenceEqual(attachments.Select(a => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Data)))))
                    throw new IOException("OCR changed original bytes.");
                attachments = prepared.ToArray();
                await File.WriteAllTextAsync(Path.Combine(folder, "ocr-prepared.json"), JsonSerializer.Serialize(new {
                    originalHashes, attachments = attachments.Select(a => new { a.Name, a.Text, a.IsPdf, a.IsImage }) }));
            }
            var prompt = "Kiểm thử được người dùng yêu cầu. Chỉ đọc/sửa/xóa dữ liệu mẫu trong thư mục " + workspace
                + ". Không đọc tài liệu cá nhân hay cấu hình/tài khoản, không đóng ứng dụng/tài liệu có sẵn. Không cài phần mềm. "
                + "Tự thực hiện bằng công cụ, kiểm tra kết quả thật; nếu lỗi hãy sửa và thử lại. " + scenario.Prompt;
            Console.WriteLine($"START {scenario.Id} {DateTime.UtcNow:O}");
            var task = await adapter.StartTaskAsync(null, prompt, new(workspace, "Synthetic acceptance fixtures only.",
                PermissionScope: scope, ThreadId: Guid.NewGuid(), Attachments: attachments,
                Images: AiDocuments.NativeImages(attachments), Files: AiDocuments.NativeFiles(attachments)), false);
            var sequence = -1L;
            H2AgentTaskSummary done;
            var progress = new List<H2AgentProgress>();
            var start = DateTime.UtcNow;
            while (true)
            {
                var observation = adapter.ObserveTask(task, sequence); done = observation.Summary;
                foreach (var item in observation.Progress)
                {
                    progress.Add(item); sequence = item.Sequence;
                    Console.WriteLine($"{scenario.Id} {item.Code}: {item.Message}");
                }
                if (done.PendingApproval is not null || DateTime.UtcNow - start > TimeSpan.FromMinutes(9))
                {
                    adapter.CancelTask(task);
                    await Task.Delay(500);
                    done = adapter.GetTaskSummary(task);
                    break;
                }
                if (H2AgentActivity.IsTerminal(done.Status)) break;
                await Task.Delay(500);
            }
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
                { scenario, model = profile.Model, startedUtc = start, finishedUtc = DateTime.UtcNow, done, progress },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"END {scenario.Id}: {done.Status} {done.Error} {done.FinalText}");
            if (done.Status != H2AgentTaskStatus.Completed) failures++;
        }
        // Terminal state is only a transport/runtime result. The fixture verifier separately
        // checks file contents, formatting, formulas, preservation and deleted test copies.
        return failures == 0 ? 0 : 1;
    }
}
