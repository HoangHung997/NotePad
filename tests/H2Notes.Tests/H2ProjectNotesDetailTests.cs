using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2ProjectNotesDetailTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project Notes detail keeps rich human knowledge in ProjectRecord NotesRich", () =>
        {
            var now = DateTime.UtcNow.AddMinutes(-5);
            var notes = RichDocument.Plain("Project knowledge");
            notes.Format(0, 7, style => style with { Bold = true, Color = "#663399" });

            var project = new ProjectRecord
            {
                Name = "Notes detail project",
                Notes = "legacy fallback",
                NotesRich = notes,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var board = new NoteRecord
            {
                Title = "Notes board",
                NoteKind = "project-hub",
                Projects = [project]
            };
            var app = new App();
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board) { Width = 1040, Height = 760 };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();

                window.FindControl<Button>("NotesTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                var pane = window.FindControl<Border>("NotesPane")!;
                var editor = window.FindControl<RichEditor>("NotesEditor")!;
                Check(pane.IsVisible && editor.IsVisible && editor.IsEnabled,
                    "Notes detail did not expose the existing rich project notes editor.");
                Check(editor.Snapshot().Text == "Project knowledge",
                    "Notes detail did not load ProjectRecord.NotesRich.");
                Check(editor.Snapshot().StyleAt(0).Bold
                    && editor.Snapshot().StyleAt(0).Color == "#663399",
                    "Existing rich note formatting was lost on open.");

                editor.Editor.CaretOffset = editor.Editor.Text.Length;
                editor.Editor.Document.Insert(editor.Editor.Text.Length, "\nHuman decision");
                editor.Editor.Select("Project knowledge".Length + 1, "Human decision".Length);
                editor.Apply(style => style with { Italic = true, Highlight = "#FFF2AA" });

                var beforeFlushUpdated = project.UpdatedAtUtc;
                window.FlushNotes();

                Check(project.NotesRich is not null
                    && project.NotesRich.Text == "Project knowledge\nHuman decision",
                    "Rich Notes detail did not persist into ProjectRecord.NotesRich.");
                Check(project.NotesRich.StyleAt("Project knowledge".Length + 1).Italic
                    && project.NotesRich.StyleAt("Project knowledge".Length + 1).Highlight == "#FFF2AA",
                    "Rich note formatting was not preserved in ProjectRecord.");
                Check(project.UpdatedAtUtc > beforeFlushUpdated,
                    "Human note edit did not update project timestamp.");

                // Switching to Agent and back must reuse the same ProjectRecord rich note.
                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                window.FindControl<Button>("NotesTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                Check(window.FindControl<RichEditor>("NotesEditor")!.Snapshot().Text
                    == "Project knowledge\nHuman decision",
                    "Agent/Notes navigation lost project knowledge.");
                Check(project.NotesRich.StyleAt("Project knowledge".Length + 1).Italic,
                    "Agent/Notes navigation lost rich-note formatting.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project Notes remain human project knowledge without KnowledgeGraph or ResearchStore", () =>
        {
            foreach (var type in new[] { typeof(ProjectRecord), typeof(ProjectLayout) })
                foreach (var forbidden in new[]
                {
                    "KnowledgeGraph", "KnowledgeStore", "ResearchStore", "ResearchItems",
                    "EvidenceStore", "EvidenceItems", "AgentNotes", "AgentKnowledge"
                })
                    Check(type.GetProperty(forbidden) is null && type.GetField(forbidden) is null,
                        $"{type.Name} gained premature knowledge/research persistence: {forbidden}");

            var project = new ProjectRecord { Name = "Human knowledge boundary" };
            project.NotesRich = RichDocument.Plain("Human-owned note");
            var before = JsonSerializer.Serialize(project);

            var repo = FindRepoRoot();
            var main = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.axaml.cs"));
            var xaml = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.axaml"));

            Check(main.Contains("NotesEditor.Snapshot()", StringComparison.Ordinal)
                && main.Contains("_notesProject.NotesRich", StringComparison.Ordinal),
                "Project Notes no longer persist through ProjectRecord.NotesRich.");
            Check(xaml.Contains("x:Name=\"NotesEditor\"", StringComparison.Ordinal)
                && xaml.Contains("x:Name=\"NotesToolbar\"", StringComparison.Ordinal),
                "Existing rich Notes editor/toolbar was not reused.");

            foreach (var forbidden in new[]
            {
                "KnowledgeGraph", "KnowledgeStore", "ResearchStore",
                "EvidenceStore", "VectorDatabase", "EmbeddingStore"
            })
                Check(!main.Contains(forbidden, StringComparison.Ordinal)
                    && !xaml.Contains(forbidden, StringComparison.Ordinal),
                    "Project Notes UI introduced premature knowledge subsystem: " + forbidden);

            Check(before == JsonSerializer.Serialize(project),
                "Architecture inspection mutated ProjectRecord notes truth.");
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
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
