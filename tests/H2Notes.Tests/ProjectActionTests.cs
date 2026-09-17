using H2Notes.Core;

internal static class ProjectActionTests
{
    internal static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Project action assertion failed"); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { return; } throw new Exception("Unsafe project action accepted"); }
        const string valid = "Đề xuất\n```h2-actions\n[{\"kind\":\"add_task\",\"text\":\"Kiểm tra hồ sơ\"},{\"kind\":\"append_note\",\"text\":\"9. Gửi trước thứ sáu\"}]\n```";

        test("Project actions parse the expanded allowlist but reject unknown capabilities", () =>
        {
            var actions = AiProjectActions.Parse(valid); Check(actions.Count == 2); Check(actions[0].Text == "Kiểm tra hồ sơ");
            Check(AiProjectActions.WithoutBlocks(valid) == "Đề xuất");
            Reject(() => AiProjectActions.Parse(valid.Replace("add_task", "delete_project")));
            Reject(() => AiProjectActions.Parse(valid + valid));
            Reject(() => AiProjectActions.Parse("```h2-actions\n[{\"kind\":\"add_task\",\"text\":\"A\",\"path\":\"C:/outside\"}]\n```"));
        });

        test("Read-only blocks project writes; confirm requires approval; full access does not", () =>
        {
            var project = new ProjectRecord(); var actions = AiProjectActions.Parse(valid);
            Reject(() => AiProjectActions.Validate(project, actions, AiPermissionMode.ReadOnly, true));
            Reject(() => AiProjectActions.Validate(project, actions, AiPermissionMode.ConfirmChanges, false));
            Reject(() => AiProjectActions.Validate(project, actions, (AiPermissionMode)900, true));
            AiProjectActions.Validate(project, actions, AiPermissionMode.ConfirmChanges, true);
            AiProjectActions.Validate(project, actions, AiPermissionMode.ProjectAccess, false);
            Check(project.ChecklistItems.Count == 0 && project.NotesText == "");
        });

        test("Project action validates entire batch before any mutation", () =>
        {
            var project = new ProjectRecord { Notes = new string('a', 600_000) };
            Reject(() => AiProjectActions.Validate(project, AiProjectActions.Parse(valid), AiPermissionMode.ProjectAccess, false));
            Check(project.ChecklistItems.Count == 0);
            Reject(() => AiProjectActions.Parse(valid.Replace("Kiểm tra hồ sơ", new string('b', 4001))));
            Reject(() => AiProjectActions.Parse("```h2-actions\n[]\n```"));
        });

        test("AI can update and delete exact tasks by stable id", () =>
        {
            var first = new TaskRecord { Text = "Mục tiêu hôm nay", IsCompleted = false };
            var second = new TaskRecord { Text = "Giữ nguyên" };
            var project = new ProjectRecord { ChecklistItems = [first, second] };
            var json = $"```h2-actions\n[{{\"kind\":\"update_task\",\"taskId\":\"{first.Id}\",\"text\":\"Đang thực hiện mục tiêu hôm nay\",\"completed\":false}},{{\"kind\":\"delete_task\",\"taskId\":\"{second.Id}\"}}]\n```";
            var actions = AiProjectActions.Parse(json);
            AiProjectActions.Validate(project, actions, AiPermissionMode.ProjectAccess, false);
            AiProjectActions.Apply(project, actions, new DateTime(2026, 9, 17, 8, 30, 0, DateTimeKind.Utc));
            Check(project.ChecklistItems.Count == 1);
            Check(project.ChecklistItems[0].DisplayText == "Đang thực hiện mục tiêu hôm nay");
            Check(project.ChecklistItems[0].UpdatedAtUtc == new DateTime(2026, 9, 17, 8, 30, 0, DateTimeKind.Utc));
        });

        test("AI edits existing note instead of appending a duplicate and preserves surrounding rich formatting", () =>
        {
            var project = new ProjectRecord();
            var doc = RichDocument.Plain("7. Đã hoàn thành hợp đồng.\n8. Mục tiêu ngày 17/09/2026: Hoàn thành hồ sơ.\n9. Giữ nguyên.");
            var keepStart = doc.Text.IndexOf("7.", StringComparison.Ordinal);
            doc.Format(keepStart, 2, s => s with { Bold = true, Color = "#2167AD" });
            project.NotesRich = doc;
            const string json = "```h2-actions\n[{\"kind\":\"replace_note\",\"match\":\"8. Mục tiêu ngày 17/09/2026: Hoàn thành hồ sơ.\",\"text\":\"8. Đang thực hiện ngày 17/09/2026: Hoàn thành hồ sơ.\",\"format\":[{\"match\":\"Đang thực hiện\",\"bold\":true,\"color\":\"#A4573D\"}]}]\n```";
            var actions = AiProjectActions.Parse(json);
            AiProjectActions.Apply(project, actions);
            Check(project.NotesText.Contains("8. Đang thực hiện ngày 17/09/2026"));
            Check(!project.NotesText.Contains("Mục tiêu ngày 17/09/2026"));
            Check(project.NotesText.Split("Đang thực hiện").Length == 2);
            Check(project.ReadNotes().StyleAt(0).Bold && project.ReadNotes().StyleAt(0).Color == "#2167AD");
            var styled = project.NotesText.IndexOf("Đang thực hiện", StringComparison.Ordinal);
            Check(project.ReadNotes().StyleAt(styled).Bold && project.ReadNotes().StyleAt(styled).Color == "#A4573D");
        });

        test("AI can continue a note list naturally and style its own inserted label", () =>
        {
            var project = new ProjectRecord { Notes = "7. Việc cũ\n8. Việc hiện tại" };
            const string json = "```h2-actions\n[{\"kind\":\"append_note\",\"text\":\"9. Cập nhật tiến độ: Đã bắt đầu hồ sơ hoàn công.\",\"format\":[{\"match\":\"Cập nhật tiến độ:\",\"bold\":true}]}]\n```";
            var actions = AiProjectActions.Parse(json);
            AiProjectActions.Apply(project, actions);
            Check(project.NotesText.EndsWith("9. Cập nhật tiến độ: Đã bắt đầu hồ sơ hoàn công."));
            var start = project.NotesText.IndexOf("Cập nhật tiến độ:", StringComparison.Ordinal);
            Check(project.ReadNotes().StyleAt(start).Bold);
        });

        test("Exact note mutations reject missing or ambiguous targets before mutation", () =>
        {
            var project = new ProjectRecord { Notes = "Mục tiêu\nMục tiêu\nKhác" };
            var before = project.NotesText;
            var ambiguous = AiProjectActions.Parse("```h2-actions\n[{\"kind\":\"delete_note\",\"match\":\"Mục tiêu\"}]\n```");
            Reject(() => AiProjectActions.Apply(project, ambiguous));
            Check(project.NotesText == before);
            var missing = AiProjectActions.Parse("```h2-actions\n[{\"kind\":\"replace_note\",\"match\":\"Không tồn tại\",\"text\":\"Mới\"}]\n```");
            Reject(() => AiProjectActions.Apply(project, missing));
            Check(project.NotesText == before);
        });

        test("Action instructions tell the model to infer local list style and edit rather than duplicate", () =>
        {
            var full = AiProjectActions.Instructions(AiPermissionMode.ProjectAccess);
            Check(full.Contains("TOÀN QUYỀN") && full.Contains("SỬA/XÓA chính nội dung đó") && full.Contains("1./2./3."));
            Check(full.Contains("replace_note") && full.Contains("delete_task") && full.Contains("format"));
        });
    }
}
