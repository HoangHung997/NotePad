using System.Text.Json;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ProjectLayoutStorageTests
{
    public static void Run(Action<string, Action> test)
    {
        test("H2M-090 classifies ProjectLayout as portable preference only", () =>
        {
            var names = typeof(ProjectLayout)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var expected = new[]
            {
                nameof(ProjectLayout.AiDock),
                nameof(ProjectLayout.AiExplicitlyHidden),
                nameof(ProjectLayout.NotesCollapsed),
                nameof(ProjectLayout.Tab),
                nameof(ProjectLayout.TasksCollapsed)
            };
            Check(expected.SequenceEqual(names, StringComparer.Ordinal),
                "ProjectLayout contains unexpected shared fields: " + string.Join(", ", names));

            foreach (var machineLocal in new[]
            {
                "NotesFraction",
                "HasCustomSplit",
                "AiWidth",
                "AiHeight",
                "AiX",
                "AiY"
            })
                Check(!names.Contains(machineLocal, StringComparer.Ordinal),
                    machineLocal + " still belongs to shared ProjectLayout.");
        });

        test("H2M-090 keeps machine geometry in local configuration per project", () =>
        {
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var config = new LocalConfiguration();

            var first = config.GetProjectLayout(firstId);
            first.NotesFraction = .71;
            first.HasCustomSplit = true;
            first.AiWidth = 777;
            first.AiHeight = 666;
            first.AiX = 123;
            first.AiY = 234;

            var second = config.GetProjectLayout(secondId);
            Check(!ReferenceEquals(first, second), "Two projects shared one machine-local layout object.");
            Equal(.5, second.NotesFraction);
            Equal(360d, second.AiWidth);
            Equal(510d, second.AiHeight);
            Equal(-1d, second.AiX);
            Equal(-1d, second.AiY);

            var json = JsonSerializer.Serialize(config);
            Check(json.Contains(firstId.ToString(), StringComparison.OrdinalIgnoreCase),
                "Local configuration did not retain the project identity.");
            foreach (var property in new[] { "NotesFraction", "HasCustomSplit", "AiWidth", "AiHeight", "AiX", "AiY" })
                Check(json.Contains(property, StringComparison.Ordinal),
                    property + " is missing from local configuration serialization.");

            config.ResetProjectLayout(firstId);
            Check(!config.ProjectLayouts.ContainsKey(firstId), "Reset did not remove local project geometry.");
        });

        test("H2M-090 shared project JSON contains preferences but no machine geometry", () =>
        {
            var project = new ProjectRecord
            {
                Name = "Layout audit",
                Layout = new ProjectLayout
                {
                    Tab = "notes",
                    AiDock = "floating",
                    AiExplicitlyHidden = false,
                    TasksCollapsed = true,
                    NotesCollapsed = false
                }
            };

            var json = JsonSerializer.Serialize(project);
            Check(json.Contains("\"AiDock\"", StringComparison.Ordinal)
                && json.Contains("\"TasksCollapsed\"", StringComparison.Ordinal),
                "Portable per-project preferences disappeared from shared ProjectLayout.");

            foreach (var property in new[] { "NotesFraction", "HasCustomSplit", "AiWidth", "AiHeight", "AiX", "AiY" })
                Check(!json.Contains("\"" + property + "\"", StringComparison.Ordinal),
                    property + " leaked into shared ProjectRecord JSON.");
        });

        test("H2M-090 NAS project file excludes machine-specific geometry", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-mb090-" + Guid.NewGuid().ToString("N"));
            var recovery = Path.Combine(root, ".recovery-local");
            var pending = Path.Combine(root, ".pending-local");
            try
            {
                var store = new ProjectWorkspaceStore(
                    root,
                    writerId: "mb090-test",
                    recoveryRoot: recovery,
                    pendingRoot: pending);
                var state = store.LoadOrImport();
                var project = new ProjectRecord
                {
                    Name = "NAS layout audit",
                    Layout = new ProjectLayout
                    {
                        Tab = "tasks",
                        AiDock = "right",
                        TasksCollapsed = true,
                        NotesCollapsed = true
                    }
                };
                state.Notes.Add(new NoteRecord
                {
                    Title = "Projects",
                    NoteKind = "project-hub",
                    Projects = [project]
                });

                store.Save(state);

                var projectFile = Directory.EnumerateFiles(
                        Path.Combine(root, "projects"),
                        "*.h2project.json",
                        SearchOption.TopDirectoryOnly)
                    .Single();
                var json = File.ReadAllText(projectFile);

                Check(json.Contains("\"AiDock\"", StringComparison.Ordinal),
                    "Portable AI dock preference did not survive NAS project serialization.");
                foreach (var property in new[] { "NotesFraction", "HasCustomSplit", "AiWidth", "AiHeight", "AiX", "AiY" })
                    Check(!json.Contains("\"" + property + "\"", StringComparison.Ordinal),
                        property + " was synchronized through the NAS project file.");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        });

        test("H2M-090 responsive UI reads and writes geometry only through LocalConfiguration", () =>
        {
            var repo = FindRepoRoot();
            var responsive = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.Responsive.cs"));

            Check(responsive.Contains("LocalSettings.GetProjectLayout", StringComparison.Ordinal),
                "Responsive layout does not use the machine-local project layout store.");
            Check(responsive.Contains("LocalSettings.Save()", StringComparison.Ordinal),
                "Machine-local geometry mutations are not persisted locally.");

            foreach (var forbidden in new[]
            {
                ".Layout.NotesFraction",
                ".Layout.HasCustomSplit",
                ".Layout.AiWidth",
                ".Layout.AiHeight",
                ".Layout.AiX",
                ".Layout.AiY"
            })
                Check(!responsive.Contains(forbidden, StringComparison.Ordinal),
                    "Responsive UI still writes machine geometry into ProjectRecord: " + forbidden);
        });
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}");
    }

    private static void Equal<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception(
                "Expected [" + string.Join(", ", expected) + "]; actual [" + string.Join(", ", actual) + "]");
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
