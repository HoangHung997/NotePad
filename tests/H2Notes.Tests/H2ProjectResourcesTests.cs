using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ProjectResourcesTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project resources projection combines links saved files and Agent evidence without mirroring filesystem metadata", () =>
        {
            var root = Folder();
            var linkedPath = Path.Combine(root, "linked.txt");
            var savedPath = Path.Combine(root, "saved.pdf");
            File.WriteAllText(linkedPath, "linked");
            File.WriteAllText(savedPath, "saved");

            var now = DateTime.UtcNow;
            var project = new ProjectRecord
            {
                Name = "Resources project",
                UpdatedAtUtc = now,
                Links =
                [
                    new ProjectLink(Guid.NewGuid(), "OpenAI docs", "https://example.com/docs"),
                    new ProjectLink(Guid.NewGuid(), "Linked file", linkedPath)
                ],
                Conversations =
                [
                    new AiConversation
                    {
                        Messages =
                        [
                            new AiMessage
                            {
                                Role = "assistant",
                                SavedFiles =
                                [
                                    new AiSavedFile("saved.pdf", savedPath, new string('b', 64), now.AddMinutes(1)),
                                    // Duplicate an explicit ProjectLink target; projection must not make
                                    // a second resource row for the same local target.
                                    new AiSavedFile("linked.txt", linkedPath, new string('c', 64), now.AddMinutes(2))
                                ]
                            }
                        ]
                    }
                ]
            };
            var agent = new ResourceAgentFake(
                project.Id,
                now.AddMinutes(3),
                new H2AgentEvidence("ev-web", "web", new string('d', 64), "Official source inspected"));

            var before = JsonSerializer.Serialize(project);
            var service = new H2ProjectResourceProjectionService(agent);
            var resources = service.Build(project);

            Check(resources.Count == 4, "Expected two links + one distinct saved file + one Agent evidence.");
            Check(resources.Count(item => item.Source == H2ProjectResourceSource.ProjectLink) == 2,
                "ProjectLink resources missing.");
            Check(resources.Count(item => item.Source == H2ProjectResourceSource.SavedFile) == 1,
                "Saved-file resource dedupe failed.");
            Check(resources.Count(item => item.Source == H2ProjectResourceSource.AgentEvidence) == 1,
                "Agent evidence resource missing.");

            var evidence = resources.Single(item => item.Source == H2ProjectResourceSource.AgentEvidence);
            Check(evidence.EvidenceId == "ev-web" && evidence.AgentTaskId == agent.TaskId,
                "Agent evidence identity/provenance was lost.");
            Check(evidence.Target is null,
                "Agent evidence summary was incorrectly guessed into an openable target.");

            var linked = resources.Single(item => item.Label == "Linked file");
            Check(linked.Target == linkedPath && linked.Kind == H2ProjectResourceKind.LocalPath,
                "Known ProjectLink file did not remain a local resource target.");

            Check(before == JsonSerializer.Serialize(project),
                "Resource projection mutated ProjectRecord or mirrored filesystem metadata.");
            foreach (var forbidden in new[] { "FileSize", "LastWriteTime", "Exists", "MimeType", "DirectoryEntries" })
                Check(typeof(ProjectRecord).GetProperty(forbidden) is null,
                    "ProjectRecord gained mirrored filesystem metadata: " + forbidden);
        });

        test("Resource target policy allows only http https and existing absolute paths", () =>
        {
            var root = Folder();
            var file = Path.Combine(root, "safe.txt");
            File.WriteAllText(file, "safe");

            var web = H2ResourceTargetPolicy.Evaluate("https://example.com/a");
            Check(web.Kind == H2ResourceTargetKind.Web && web.CanOpen && !web.CanReveal,
                "HTTPS target policy is wrong.");

            var local = H2ResourceTargetPolicy.Evaluate(file);
            Check(local.Kind == H2ResourceTargetKind.File && local.CanOpen,
                "Existing absolute local file should be openable.");
            Check(local.CanReveal == OperatingSystem.IsWindows(),
                "Reveal capability must be Windows-only.");

            var missing = H2ResourceTargetPolicy.Evaluate(Path.Combine(root, "missing.txt"));
            Check(missing.Kind == H2ResourceTargetKind.MissingLocal && !missing.CanOpen,
                "Missing local target must fail closed.");

            foreach (var unsafeTarget in new[]
            {
                "javascript:alert(1)",
                "ftp://example.com/file",
                "relative\\file.txt",
                "cmd.exe /c whoami"
            })
            {
                var decision = H2ResourceTargetPolicy.Evaluate(unsafeTarget);
                Check(!decision.CanOpen && !decision.CanReveal,
                    "Unsafe resource target became actionable: " + unsafeTarget);
            }
        });

        test("Resources detail is one click and exposes safe actions without opening Agent evidence as a path", () =>
        {
            var root = Folder();
            var file = Path.Combine(root, "project-resource.txt");
            File.WriteAllText(file, "fixture");
            var now = DateTime.UtcNow;

            var project = new ProjectRecord
            {
                Name = "Resource UI project",
                Links = [new ProjectLink(Guid.NewGuid(), "Project file", file)]
            };
            var board = new NoteRecord
            {
                Title = "Resource UI board",
                NoteKind = "project-hub",
                Projects = [project]
            };
            var agent = new ResourceAgentFake(
                project.Id,
                now,
                new H2AgentEvidence("ev-ui", "file-observation", new string('e', 64), "Agent observed a file"));
            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board) { Width = 1040, Height = 760 };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();

                window.FindControl<Button>("ResourcesTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                var pane = window.FindControl<Border>("ProjectResourcesPane")!;
                var editorSplit = window.FindControl<Grid>("EditorSplit")!;
                var list = window.FindControl<ListBox>("ProjectResourcesList")!;
                Check(pane.IsVisible && !editorSplit.IsVisible,
                    "Resources detail did not replace task/note detail surface.");

                var items = list.ItemsSource!.Cast<object>().ToArray();
                Check(items.Length == 2, "Resources detail did not show ProjectLink + Agent evidence.");

                var fileItem = items.Single(item => Text(item, "Label") == "Project file");
                list.SelectedItem = fileItem;
                Pump();
                Check(window.FindControl<Button>("ProjectResourceOpenButton")!.IsEnabled,
                    "Existing project file should enable Open.");
                Check(window.FindControl<Button>("ProjectResourceRevealButton")!.IsEnabled == OperatingSystem.IsWindows(),
                    "Reveal button did not follow safe platform policy.");

                var evidenceItem = items.Single(item => Text(item, "SourceText") == "Bằng chứng Agent");
                list.SelectedItem = evidenceItem;
                Pump();
                Check(!window.FindControl<Button>("ProjectResourceOpenButton")!.IsEnabled
                    && !window.FindControl<Button>("ProjectResourceRevealButton")!.IsEnabled,
                    "Agent evidence without explicit target became directly openable.");
                Check(Text(evidenceItem, "TargetText").Contains("Agent", StringComparison.Ordinal),
                    "Agent evidence identity is not visible to the user.");

                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Border>("AiHostBorder")!.IsVisible
                    && !window.FindControl<Border>("ProjectResourcesPane")!.IsVisible,
                    "Resources → Agent one-click navigation failed.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project resources UI does not execute shell command strings or persist resource caches", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.Resources.cs"));
            var projection = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "H2ProjectResources.cs"));

            foreach (var forbidden in new[]
            {
                "cmd.exe", "powershell", "pwsh", "ProcessStartInfo(\"cmd", "ProcessStartInfo(\"powershell",
                "ResourceDatabase", "ResourceStore", "FilesystemMetadata"
            })
                Check(!source.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                    && !projection.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Unsafe/duplicate resource subsystem marker found: " + forbidden);

            Check(source.Contains("H2ResourceTargetPolicy.Evaluate", StringComparison.Ordinal),
                "Open/reveal actions bypass safe target classification.");
            Check(projection.Contains("project.Links", StringComparison.Ordinal)
                && projection.Contains("message.SavedFiles", StringComparison.Ordinal)
                && projection.Contains("task.Evidence", StringComparison.Ordinal),
                "Resource projection is not built from the three real sources.");
        });
    }

    private static string Text(object value, string property)
        => value.GetType().GetProperty(property)!.GetValue(value)?.ToString() ?? "";

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "H2Notes-resource-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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

    private sealed class ResourceAgentFake : IH2AgentAdapter
    {
        private readonly H2AgentTaskSummary _task;
        public Guid TaskId => _task.TaskId;

        public ResourceAgentFake(Guid projectId, DateTime updated, H2AgentEvidence evidence)
        {
            _task = new H2AgentTaskSummary(
                Guid.NewGuid(),
                projectId,
                "Inspect resources",
                H2AgentTaskStatus.Completed,
                null,
                [evidence],
                "done",
                null,
                updated.AddMinutes(-1),
                updated);
        }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(_task, Array.Empty<H2AgentProgress>());
        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => _task;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => projectId is null || projectId == _task.ProjectId ? [_task] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _task.Evidence.FirstOrDefault(evidence => evidence.EvidenceId == evidenceId);
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}
