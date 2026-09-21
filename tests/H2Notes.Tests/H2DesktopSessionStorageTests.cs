using System.Reflection;
using System.Text.Json;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2DesktopSessionStorageTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("H2M-091 DesktopSession and note placements serialize only in local configuration", () =>
        {
            var noteId = Guid.NewGuid();
            var config = new LocalConfiguration
            {
                DesktopSession = new DesktopSessionState
                {
                    SelectedBoardId = Guid.NewGuid(),
                    OpenWindowIds = [noteId],
                    NoteWindows =
                    {
                        [noteId] = new NoteWindowPlacementState
                        {
                            Left = -1480,
                            Top = 88,
                            Width = 640,
                            Height = 520,
                            IsPinned = true
                        }
                    },
                    ProjectAiWindow = new ProjectAiWindowState
                    {
                        Left = 333,
                        Top = 222,
                        Width = 515,
                        Height = 690,
                        IsVisible = true
                    }
                }
            };

            var json = JsonSerializer.Serialize(config);
            Check(json.Contains("\"DesktopSession\"", StringComparison.Ordinal)
                  && json.Contains("\"NoteWindows\"", StringComparison.Ordinal)
                  && json.Contains("-1480", StringComparison.Ordinal),
                "Local configuration did not retain machine-specific desktop placement.");
        });

        test("H2M-091 NAS snapshot strips DesktopSession and note window coordinates", () =>
        {
            var root = Temp("nas");
            try
            {
                var store = new ProjectWorkspaceStore(
                    root,
                    writerId: "pc-a",
                    recoveryRoot: Path.Combine(root, ".recovery"),
                    pendingRoot: Path.Combine(root, ".pending"));
                var state = store.LoadOrImport();
                var note = new NoteRecord
                {
                    NoteKind = "general",
                    Title = "PC A note",
                    Left = -2200.125,
                    Top = 777,
                    Width = 1555,
                    Height = 999,
                    IsPinned = true,
                    IsVisibleOnDesktop = true
                };
                var board = new NoteRecord
                {
                    NoteKind = "project-hub",
                    Title = "Projects",
                    SheetLeft = -1900.875,
                    SheetTop = 650,
                    SheetWidth = 1600,
                    SheetHeight = 1000,
                    IsPinned = true,
                    IsVisibleOnDesktop = true
                };
                state.Notes.Add(board);
                state.Notes.Add(note);
                state.DesktopSession = new DesktopSessionState
                {
                    SelectedBoardId = board.Id,
                    OpenWindowIds = [board.Id, note.Id],
                    NoteWindows =
                    {
                        [note.Id] = NoteWindowPlacementState.From(note)
                    }
                };
                store.Save(state);

                var index = File.ReadAllText(Path.Combine(root, "workspace.h2index.json"));
                Check(!index.Contains(note.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                      || !index.Contains("\"OpenWindowIds\":[", StringComparison.Ordinal),
                    "Shared index appears to contain live desktop session window IDs.");
                Check(!index.Contains("-2200.125", StringComparison.Ordinal)
                      && !index.Contains("-1900.875", StringComparison.Ordinal),
                    "Shared index leaked monitor-specific coordinates.");

                var noteFile = Directory.EnumerateFiles(Path.Combine(root, "notes"), "*.h2note.json").Single();
                var noteJson = File.ReadAllText(noteFile);
                Check(!noteJson.Contains("-2200.125", StringComparison.Ordinal)
                      && !noteJson.Contains("1555", StringComparison.Ordinal)
                      && !noteJson.Contains("\"IsPinned\": true", StringComparison.OrdinalIgnoreCase)
                      && !noteJson.Contains("\"IsVisibleOnDesktop\": true", StringComparison.OrdinalIgnoreCase),
                    "NAS note file retained PC A window/session state.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-091 local desktop session overrides foreign note geometry after NAS load", () =>
        {
            var root = Temp("apply");
            try
            {
                var app = new App();
                SetPrivate(app, "_storage", new ProjectWorkspaceStore(root, writerId: "pc-b"));
                var note = new NoteRecord
                {
                    NoteKind = "general",
                    Left = -2400,
                    Top = 901,
                    Width = 1700,
                    Height = 1000,
                    IsPinned = true,
                    IsVisibleOnDesktop = true
                };
                var board = new NoteRecord
                {
                    NoteKind = "project-hub",
                    SheetLeft = -2000,
                    SheetTop = 700,
                    SheetWidth = 1700,
                    SheetHeight = 1000,
                    IsPinned = true,
                    IsVisibleOnDesktop = true
                };
                var state = new SheetState { Notes = [board, note] };
                SetState(app, state);

                app.LocalSettings.DesktopSession = new DesktopSessionState
                {
                    SelectedBoardId = board.Id,
                    OpenWindowIds = [note.Id],
                    NoteWindows =
                    {
                        [note.Id] = new NoteWindowPlacementState
                        {
                            Left = 44,
                            Top = 55,
                            Width = 620,
                            Height = 510,
                            IsPinned = false
                        },
                        [board.Id] = new NoteWindowPlacementState
                        {
                            Left = 70,
                            Top = 80,
                            Width = 1180,
                            Height = 820,
                            IsPinned = false
                        }
                    }
                };

                CallPrivate(app, "ApplyLocalDesktopSession");

                Equal(44d, note.Left);
                Equal(55d, note.Top);
                Equal(620d, note.Width);
                Equal(510d, note.Height);
                Check(!note.IsPinned, "Foreign NAS pin state survived local placement restore.");
                Equal(70, board.SheetLeft);
                Equal(80, board.SheetTop);
                Equal(1180d, board.SheetWidth);
                Equal(820d, board.SheetHeight);
                Check(state.DesktopSession!.OpenWindowIds.SequenceEqual(new[] { note.Id }),
                    "Remote session windows replaced the local open-window list.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-091 new PC without local session starts from safe desktop defaults", () =>
        {
            var root = Temp("newpc");
            try
            {
                var app = new App();
                SetPrivate(app, "_storage", new ProjectWorkspaceStore(root, writerId: "new-pc"));
                var note = new NoteRecord
                {
                    NoteKind = "general",
                    Left = -2600,
                    Top = 1200,
                    Width = 1800,
                    Height = 1100,
                    IsPinned = true,
                    IsVisibleOnDesktop = true
                };
                var state = new SheetState
                {
                    Notes = [new NoteRecord { NoteKind = "project-hub" }, note],
                    DesktopSession = new DesktopSessionState
                    {
                        OpenWindowIds = [note.Id]
                    }
                };
                SetState(app, state);
                app.LocalSettings.DesktopSession = null;

                CallPrivate(app, "ApplyLocalDesktopSession");

                Equal(120d, note.Left);
                Equal(100d, note.Top);
                Equal(1100d, note.Width);
                Equal(740d, note.Height);
                Check(!note.IsPinned && !note.IsVisibleOnDesktop,
                    "New PC inherited machine-specific note state.");
                Check(state.DesktopSession is not null && state.DesktopSession.OpenWindowIds.Count == 0,
                    "New PC inherited remote open-window session IDs.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-091 ProjectWorkspaceStore source explicitly keeps DesktopSession local", () =>
        {
            var repo = FindRepoRoot();
            var store = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "ProjectWorkspaceStore.cs"));
            var app = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));

            Check(store.Contains("DesktopSession = null", StringComparison.Ordinal),
                "NAS workspace index no longer explicitly drops DesktopSession.");
            Check(store.Contains("clone.SheetLeft = null", StringComparison.Ordinal)
                  && store.Contains("clone.Left = 120", StringComparison.Ordinal),
                "Shared note serialization no longer neutralizes machine window placement.");
            Check(app.Contains("ApplyLocalDesktopSession();", StringComparison.Ordinal)
                  && app.Contains("_local.DesktopSession", StringComparison.Ordinal),
                "Avalonia app no longer restores project-workspace desktop state from local configuration.");
        });
    }

    private static void SetPrivate(object owner, string name, object? value)
        => owner.GetType().GetField(name, Private)!.SetValue(owner, value);

    private static void SetState(App app, SheetState state)
        => typeof(App).GetProperty(nameof(App.State))!.GetSetMethod(true)!.Invoke(app, [state]);

    private static object? CallPrivate(object owner, string name)
        => owner.GetType().GetMethod(name, Private)!.Invoke(owner, null);

    private static string Temp(string suffix)
        => Path.Combine(Path.GetTempPath(), "h2-mb091-" + suffix + "-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}");
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
