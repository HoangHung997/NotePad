using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ProjectEvidenceInspectorTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Evidence inspector resolves authoritative web and file provenance from Agent", () =>
        {
            var root = Folder();
            var file = Path.Combine(root, "verified-report.pdf");
            File.WriteAllText(file, "fixture");
            var now = DateTime.UtcNow;
            var project = new ProjectRecord { Name = "Evidence project", UpdatedAtUtc = now };
            var agent = new EvidenceAgentFake(project.Id, now, file);

            var before = JsonSerializer.Serialize(project);
            var service = new H2EvidenceInspectionProjectionService(agent);
            var items = service.Build(project);

            Check(items.Count == 2, "Expected one web and one file evidence item.");
            var web = items.Single(item => item.EvidenceId == "web-official");
            Check(web.InspectionKind == H2EvidenceInspectionKind.Web,
                "Official web evidence was not classified as web.");
            Check(web.SourceUri == "https://official.example.gov/source",
                "Authoritative web source URI was not resolved from Agent.");
            Check(web.Provenance == "official-web · agency fixture",
                "Web provenance was not preserved.");
            Check(web.Summary == "Official publication inspected.",
                "Inspector used stale task-summary evidence instead of Agent GetEvidence().");

            var local = items.Single(item => item.EvidenceId == "file-proof");
            Check(local.InspectionKind == H2EvidenceInspectionKind.File,
                "File evidence was not classified as file.");
            Check(local.LocalPath == Path.GetFullPath(file),
                "File evidence local path was not preserved.");
            Check(local.Sha256 == new string('f', 64),
                "File evidence hash was not preserved.");
            Check(local.Provenance == "agent-file-verification",
                "File provenance was not preserved.");

            agent.ReplaceWebProvenance("official-web · refreshed");
            var refreshed = service.Build(project)
                .Single(item => item.EvidenceId == "web-official");
            Check(refreshed.Provenance == "official-web · refreshed",
                "Inspector cached stale evidence instead of re-fetching Agent authority.");
            Check(before == JsonSerializer.Serialize(project),
                "Evidence inspection mutated ProjectRecord truth.");
        });

        test("Evidence inspector UI exposes provenance and safe source actions in one click", () =>
        {
            var root = Folder();
            var file = Path.Combine(root, "evidence.txt");
            File.WriteAllText(file, "evidence");
            var now = DateTime.UtcNow;
            var project = new ProjectRecord
            {
                Name = "Evidence UI project",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var board = new NoteRecord
            {
                Title = "Evidence UI board",
                NoteKind = "project-hub",
                Projects = [project]
            };
            var agent = new EvidenceAgentFake(project.Id, now, file);
            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board) { Width = 1120, Height = 780 };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();
                window.FindControl<Button>("EvidenceTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                var pane = window.FindControl<Border>("ProjectEvidencePane")!;
                var list = window.FindControl<ListBox>("ProjectEvidenceList")!;
                Check(pane.IsVisible, "Evidence inspector did not open.");
                Check(!window.FindControl<Grid>("EditorSplit")!.IsVisible
                    && !window.FindControl<Border>("ProjectHistoryPane")!.IsVisible,
                    "Evidence inspector did not become the active project detail.");
                var items = list.ItemsSource!.Cast<object>().ToArray();
                Check(items.Length == 2, "Evidence inspector did not show Agent evidence.");

                var web = items.Single(item => Text(item, "EvidenceId") == "web-official");
                list.SelectedItem = web;
                Pump();
                Check(window.FindControl<TextBlock>("ProjectEvidenceIdText")!.Text!.Contains("web-official", StringComparison.Ordinal),
                    "Evidence identity is not visible.");
                Check(window.FindControl<TextBlock>("ProjectEvidenceProvenanceText")!.Text!.Contains("official-web", StringComparison.Ordinal),
                    "Official web provenance is not visible.");
                Check(window.FindControl<TextBlock>("ProjectEvidenceTargetText")!.Text!.Contains("https://official.example.gov/source", StringComparison.Ordinal),
                    "Official web source URI is not inspectable.");
                Check(window.FindControl<Button>("ProjectEvidenceOpenButton")!.IsEnabled,
                    "Official HTTP(S) evidence source should be safely openable.");
                Check(!window.FindControl<Button>("ProjectEvidenceRevealButton")!.IsEnabled,
                    "Web evidence must not enable filesystem reveal.");

                var fileItem = items.Single(item => Text(item, "EvidenceId") == "file-proof");
                list.SelectedItem = fileItem;
                Pump();
                Check(window.FindControl<TextBlock>("ProjectEvidenceTargetText")!.Text!.Contains(file, StringComparison.Ordinal),
                    "File evidence path is not inspectable.");
                Check(window.FindControl<TextBlock>("ProjectEvidenceHashText")!.Text!.Contains(new string('f', 64), StringComparison.Ordinal),
                    "File evidence hash is not inspectable.");
                Check(window.FindControl<Button>("ProjectEvidenceOpenButton")!.IsEnabled,
                    "Existing absolute evidence file should be openable.");
                Check(window.FindControl<Button>("ProjectEvidenceRevealButton")!.IsEnabled == OperatingSystem.IsWindows(),
                    "Evidence file reveal must follow platform-safe policy.");

                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Border>("AiHostBorder")!.IsVisible
                    && !window.FindControl<Border>("ProjectEvidencePane")!.IsVisible,
                    "Evidence → Agent one-click navigation failed.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Evidence inspector has no H2 ResearchStore and keeps evidence Agent-owned", () =>
        {
            var repo = FindRepoRoot();
            var core = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "H2EvidenceInspection.cs"));
            var ui = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.AgentEvidenceInspector.cs"));

            Check(core.Contains("_agent.GetEvidence(", StringComparison.Ordinal),
                "Evidence inspector does not re-fetch authoritative Agent evidence.");
            Check(core.Contains("EvidenceId", StringComparison.Ordinal)
                && core.Contains("Provenance", StringComparison.Ordinal),
                "Evidence identity/provenance projection is missing.");

            foreach (var forbidden in new[]
            {
                "ResearchStore", "EvidenceStore", "ResearchDatabase", "EvidenceDatabase",
                "VectorDatabase", "EmbeddingStore", "File.Write", "AtomicWrite("
            })
                Check(!core.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                    && !ui.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Duplicate evidence/research persistence marker found: " + forbidden);

            foreach (var name in new[]
            {
                "Evidence", "EvidenceItems", "Research", "ResearchItems",
                "ResearchStore", "EvidenceStore"
            })
                Check(typeof(ProjectRecord).GetProperty(name) is null
                    && typeof(ProjectRecord).GetField(name) is null,
                    "ProjectRecord gained Agent evidence/research truth: " + name);
        });
    }

    private static string Text(object value, string property)
        => value.GetType().GetProperty(property)!.GetValue(value)?.ToString() ?? "";

    private static string Folder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "H2Notes-evidence-inspector-tests",
            Guid.NewGuid().ToString("N"));
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

    private sealed class EvidenceAgentFake : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private readonly Guid _taskId = Guid.NewGuid();
        private readonly DateTime _updated;
        private readonly string _file;
        private readonly Dictionary<string, H2AgentEvidence> _authoritative = new(StringComparer.Ordinal);

        public EvidenceAgentFake(Guid projectId, DateTime updated, string file)
        {
            _projectId = projectId;
            _updated = updated;
            _file = file;
            _authoritative["web-official"] = new(
                "web-official",
                "web-citation",
                new string('a', 64),
                "Official publication inspected.",
                SourceUri: "https://official.example.gov/source",
                Provenance: "official-web · agency fixture");
            _authoritative["file-proof"] = new(
                "file-proof",
                "file-verification",
                new string('f', 64),
                "Verified report file.",
                LocalPath: file,
                Provenance: "agent-file-verification");
        }

        public void ReplaceWebProvenance(string value)
            => _authoritative["web-official"] = _authoritative["web-official"] with
            {
                Provenance = value
            };

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(GetTaskSummary(taskId), Array.Empty<H2AgentProgress>());

        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
        {
            if (taskId != _taskId) throw new KeyNotFoundException();
            return Summary();
        }

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => projectId is null || projectId == _projectId ? [Summary()] : Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _authoritative.TryGetValue(evidenceId, out var evidence) ? evidence : null;

        public bool AttachProject(Guid taskId, Guid projectId) => false;

        private H2AgentTaskSummary Summary()
            => new(
                _taskId,
                _projectId,
                "Inspect official source and verified file",
                H2AgentTaskStatus.Completed,
                null,
                [
                    // Summary references are deliberately stale/minimal; inspector must GetEvidence().
                    new H2AgentEvidence("web-official", "web", null, "stale summary"),
                    new H2AgentEvidence("file-proof", "file", null, "stale file summary")
                ],
                "done",
                null,
                _updated.AddMinutes(-1),
                _updated);
    }
}
