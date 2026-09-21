using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ProjectActionUiTests
{
    internal static void Run(Action<string, Action> test)
    {
        const string LegacyAction =
            "Đề xuất thao tác\n```h2-actions\n" +
            "[{\"kind\":\"add_task\",\"text\":\"Kiểm tra hồ sơ\"}]\n```";

        foreach (var permission in Enum.GetValues<AiPermissionMode>())
        {
            test("Legacy h2-actions are read-only history under " + permission, () =>
            {
                var app = new H2Notes.Avalonia.App();
                var answer = new AiMessage
                {
                    Role = "assistant",
                    Status = "complete",
                    Provider = "Legacy provider",
                    Content = LegacyAction
                };
                var conversation = new AiConversation
                {
                    PermissionMode = permission,
                    Messages = [answer]
                };
                if (permission == AiPermissionMode.ProjectAccess)
                    app.LocalSettings.Ai.ProjectAccessConversationIds.Add(conversation.Id);

                var project = new ProjectRecord
                {
                    Notes = "Original note",
                    Conversations = [conversation]
                };
                var before = MutationTruth(project);

                var panel = new AiChatPanel(app);
                panel.SetProject(project);
                var window = new Window
                {
                    Content = panel,
                    Width = 420,
                    Height = 700
                };
                window.Show();
                Pump();

                try
                {
                    var button = panel.GetVisualDescendants()
                        .OfType<Button>()
                        .SingleOrDefault(item => item.Name == "ChatProjectActions");
                    Check(button is not null, "Historical legacy action preview is missing.");
                    Check((button!.Content?.ToString() ?? "").Contains("chỉ đọc", StringComparison.OrdinalIgnoreCase),
                        "Historical legacy action is not clearly labeled read-only.");

                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();

                    Check(window.OwnedWindows.All(owned =>
                            owned.Title != "Áp dụng thay đổi vào dự án?"
                            && owned.Title != "AI muốn thay đổi dự án"),
                        "Retired pseudo-action execution still opens an apply/confirmation dialog.");
                    Check(before == MutationTruth(project),
                        "Viewing a legacy h2-actions proposal mutated project task/note truth.");
                    Check(!answer.ProjectActionsApplied,
                        "Viewing legacy proposal incorrectly marked it as applied.");
                }
                finally
                {
                    foreach (var owned in window.OwnedWindows.ToArray())
                        owned.Close();
                    window.Close();
                }
            });
        }

        test("Historically applied h2-actions keep audit evidence without re-execution", () =>
        {
            var answer = new AiMessage
            {
                Role = "assistant",
                Status = "complete",
                Provider = "Legacy provider",
                Content = LegacyAction,
                ProjectActionsApplied = true,
                ProjectActionsAudit = "appliedUtc=2026-09-01T00:00:00.0000000Z; count=1"
            };
            var conversation = new AiConversation
            {
                PermissionMode = AiPermissionMode.ProjectAccess,
                Messages = [answer]
            };
            var project = new ProjectRecord
            {
                Conversations = [conversation],
                ChecklistItems = [new TaskRecord { Text = "Existing historical result" }]
            };
            var app = new H2Notes.Avalonia.App();
            app.LocalSettings.Ai.ProjectAccessConversationIds.Add(conversation.Id);
            var before = MutationTruth(project);

            var panel = new AiChatPanel(app);
            panel.SetProject(project);
            var window = new Window { Content = panel, Width = 420, Height = 700 };
            window.Show();
            Pump();
            try
            {
                Check(panel.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text?.Contains("appliedUtc=2026-09-01", StringComparison.Ordinal) == true),
                    "Historical applied-action audit is not visible.");
                Check(before == MutationTruth(project),
                    "Rendering historical applied-action audit replayed task/note mutation.");
            }
            finally
            {
                window.Close();
            }
        });

        test("H2 Agent fenced h2-actions text is never interpreted as legacy action UI", () =>
        {
            var answer = new AiMessage
            {
                Role = "assistant",
                Status = "complete",
                Provider = "H2 Agent",
                Content = LegacyAction
            };
            var project = new ProjectRecord
            {
                Conversations =
                [
                    new AiConversation
                    {
                        PermissionMode = AiPermissionMode.ProjectAccess,
                        Messages = [answer]
                    }
                ]
            };
            var app = new H2Notes.Avalonia.App();
            var panel = new AiChatPanel(app);
            panel.SetProject(project);
            var window = new Window { Content = panel, Width = 420, Height = 700 };
            window.Show();
            Pump();
            try
            {
                Check(!panel.GetVisualDescendants().OfType<Button>()
                    .Any(item => item.Name == "ChatProjectActions"),
                    "H2 Agent output was reinterpreted through retired h2-actions UI.");
                Check(project.ChecklistItems.Count == 0,
                    "H2 Agent fenced text mutated project state.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project action UI source has no legacy execution hooks", () =>
        {
            var repo = FindRepoRoot();
            var actions = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.Actions.cs"));
            var responsive = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.Responsive.cs"));
            var legacy = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "AiLegacyRequestContext.cs"));

            foreach (var forbidden in new[]
            {
                "AiProjectActions.Validate",
                "AiProjectActions.Apply",
                "Dialogs.Confirm",
                "ProjectActionsRequested",
                "ApplyAutomaticProjectActions"
            })
                Check(!actions.Contains(forbidden, StringComparison.Ordinal),
                    "Legacy action UI still contains execution hook: " + forbidden);

            Check(!responsive.Contains("ProjectActionsRequested", StringComparison.Ordinal),
                "MainWindow still subscribes to legacy project action execution.");
            Check(!legacy.Contains("AiProjectActions.Instructions", StringComparison.Ordinal),
                "Standalone legacy prompt still instructs the model to emit h2-actions.");
        });
    }

    private static string MutationTruth(ProjectRecord project)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            project.Notes,
            NotesRich = project.NotesRich,
            project.UpdatedAtUtc,
            ChecklistItems = project.ChecklistItems
        });

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
