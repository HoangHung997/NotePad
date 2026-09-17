using H2Notes.Core;

internal static class ProjectActionTests
{
    internal static void Run(Action<string, Action> test)
    {
        void Check(bool value) { if (!value) throw new Exception("Project action assertion failed"); }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { return; } throw new Exception("Unsafe project action accepted"); }
        const string valid = "Đề xuất\n```h2-actions\n[{\"kind\":\"add_task\",\"text\":\"Kiểm tra hồ sơ\"},{\"kind\":\"append_note\",\"text\":\"Gửi trước thứ sáu\"}]\n```";
        test("Project actions parse only allowlisted append operations", () =>
        {
            var actions = AiProjectActions.Parse(valid); Check(actions.Count == 2); Check(actions[0].Text == "Kiểm tra hồ sơ");
            Check(AiProjectActions.WithoutBlocks(valid) == "Đề xuất");
            Reject(() => AiProjectActions.Parse(valid.Replace("add_task", "delete_project")));
            Reject(() => AiProjectActions.Parse(valid + valid));
            Reject(() => AiProjectActions.Parse("```h2-actions\n[{\"kind\":\"add_task\",\"text\":\"A\",\"path\":\"C:/outside\"}]\n```"));
        });
        test("Read-only blocks project writes; default requires approval; project access scoped", () =>
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
    }
}
