using H2Notes.Core;
using System.Text.Json;

internal static class WorkspaceTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Workspace assertion failed"); }
    private static string Folder() { var p = Path.Combine(Path.GetTempPath(), "H2Notes-workspace-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static void Fails(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Expected safe failure"); }
    public static void Run(Action<string, Action> test)
    {
        test("Workspace stores each project with rich text, chat, paths and ordinary notes", () =>
        {
            var dir = Folder(); var store = new ProjectWorkspaceStore(dir); store.LoadOrImport(); var state = SheetStorage.Demo();
            var p = state.Notes[0].Projects[0]; p.NotesRich = RichDocument.Plain("Vietnamese rich note"); p.NotesRich.Format(0, 4, s => s with { Bold = true });
            p.Conversations.Add(new() { Draft = "draft", Messages = [new() { Content = "question" }, new() { Role = "assistant", Content = "answer", Model = "local-model" }] });
            p.Links.Add(new(Guid.NewGuid(), "drawing", "D:/drawing.dwg"));
            state.Notes.Add(new() { NoteKind = "general", Title = "Ordinary", Content = "keep" }); store.Save(state);
            Check(Directory.GetFiles(Path.Combine(dir, "projects"), "*.h2project.json").Length == 5);
            Check(Directory.GetFiles(Path.Combine(dir, "notes"), "*.h2note.json").Length == 1);
            var read = new ProjectWorkspaceStore(dir).LoadOrImport(); var loaded = read.Notes[0].Projects[0];
            Check(loaded.Id == p.Id && loaded.ReadNotes().StyleAt(0).Bold && loaded.Conversations[0].Messages.Count == 2 && loaded.Conversations[0].Draft == "draft");
            Check(loaded.Links[0].Target == "D:/drawing.dwg" && read.Notes.Count == 2);
        });
        test("Workspace migration preserves source bytes and immutable backup", () =>
        {
            var root = Folder(); var legacy = Path.Combine(root, "legacy.json"); new SheetStorage(legacy).Save(SheetStorage.Demo()); var bytes = File.ReadAllBytes(legacy);
            var store = new ProjectWorkspaceStore(Path.Combine(root, "new")); var state = store.LoadOrImport(legacy);
            Check(bytes.SequenceEqual(File.ReadAllBytes(legacy)) && state.Notes[0].Projects.Count == 5);
            Check(Directory.GetFiles(Path.Combine(store.Root, "backups"), "migration-*.json").Any());
        });
        test("Workspace unchanged projects are not rewritten; rename retains identity", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var files = Directory.GetFiles(Path.Combine(store.Root, "projects")); var old = files.Single(f => f.Contains(state.Notes[0].Projects[0].Id.ToString("N")));
            var unchanged = files.First(f => f != old); var date = File.GetLastWriteTimeUtc(unchanged);
            state.Notes[0].Projects[0].NameRich = RichDocument.Plain("Renamed / safely"); store.Save(state);
            Check(!File.Exists(old) && File.GetLastWriteTimeUtc(unchanged) == date);
            Check(store.Read().Notes[0].Projects[0].DisplayName == "Renamed / safely");
        });
        test("Workspace transaction rollback restores previous complete generation", () =>
        {
            var root = Folder(); var first = new ProjectWorkspaceStore(root); first.LoadOrImport(); first.Save(SheetStorage.Demo());
            var broken = new ProjectWorkspaceStore(root, n => { if (n == 2) throw new IOException("Injected disk failure"); }); var state = broken.LoadOrImport();
            var originals = state.Notes[0].Projects.Select(p => p.DisplayName).ToArray();
            state.Notes[0].Projects[0].NameRich = RichDocument.Plain("changed one"); state.Notes[0].Projects[1].NameRich = RichDocument.Plain("changed two");
            Fails(() => broken.Save(state)); var read = new ProjectWorkspaceStore(root).LoadOrImport();
            Check(read.Notes[0].Projects.Select(p => p.DisplayName).SequenceEqual(originals));
        });
        test("Workspace rejects external modification without overwriting it", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var path = Directory.GetFiles(Path.Combine(store.Root, "projects"))[0]; File.AppendAllText(path, " "); var altered = File.ReadAllBytes(path);
            Fails(() => store.Save(state)); Check(altered.SequenceEqual(File.ReadAllBytes(path))); Fails(() => new ProjectWorkspaceStore(store.Root).LoadOrImport());
        });
        test("Workspace prevents nested roots, foreign overwrite and duplicate IDs", () =>
        {
            var root = Folder(); Fails(() => ProjectWorkspaceStore.ValidateDestination(root, Path.Combine(root, "inside")));
            var store = new ProjectWorkspaceStore(root); store.LoadOrImport(); var state = SheetStorage.Demo();
            state.Notes[0].Projects[1].Id = state.Notes[0].Projects[0].Id; Fails(() => store.Save(state));
            Check(!File.Exists(store.FilePath));
        });
        test("Workspace lock prevents two writers", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); using var locked = store.AcquireLock(); Fails(() => { using var second = store.AcquireLock(); });
        });
        test("Incremental save persists only dirty projects while index order and selection update", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var board = state.Notes[0]; var project = board.Projects[2]; var clean = board.Projects[0];
            var cleanPath = Directory.GetFiles(Path.Combine(store.Root, "projects")).Single(p => p.Contains(clean.Id.ToString("N")));
            var cleanTime = File.GetLastWriteTimeUtc(cleanPath);
            project.NotesRich = RichDocument.Plain("Only this project changed"); board.SelectedProjectId = project.Id;
            SheetOperations.MoveProject(board, project.Id, clean.Id, false);
            store.SaveIncremental(state, new HashSet<Guid> { project.Id });
            var loaded = new ProjectWorkspaceStore(store.Root).LoadOrImport();
            Check(loaded.Notes[0].Projects[0].Id == project.Id && loaded.Notes[0].SelectedProjectId == project.Id);
            Check(loaded.Notes[0].Projects[0].NotesText == "Only this project changed" && File.GetLastWriteTimeUtc(cleanPath) == cleanTime);
        });
        test("Workspace merge requires explicit conflict decisions and keep-both does not duplicate on repeat", () =>
        {
            var source = SheetStorage.Demo(); var destination = ProjectWorkspaceStore.Clone(source);
            source.Notes[0].Projects[0].NameRich = RichDocument.Plain("Changed current");
            var plan = new WorkspaceTransfer(source, destination); Check(plan.Conflicts.Count == 1); Fails(() => plan.Prepare(WorkspaceTransferMode.Merge));
            var choices = new Dictionary<Guid, ConflictResolution> { [plan.Conflicts[0].Id] = ConflictResolution.KeepBoth };
            var merged = plan.Prepare(WorkspaceTransferMode.Merge, choices); Check(merged.Notes[0].Projects.Count == 6);
            var repeated = new WorkspaceTransfer(source, merged).Prepare(WorkspaceTransferMode.Merge, choices); Check(repeated.Notes[0].Projects.Count == 6);
            Check(destination.Notes[0].Projects.Count == 5 && source.Notes[0].Projects.Count == 5);
        });
        test("Workspace merge asks before choosing project order and preserves rich names in duplicate", () =>
        {
            var source = SheetStorage.Demo(); var dest = ProjectWorkspaceStore.Clone(source); var board = source.Notes[0];
            board.Projects.Reverse(); var rich = RichDocument.Plain("Rich title"); rich.Format(0, 4, s => s with { Bold = true }); board.Projects[0].NameRich = rich;
            var plan = new WorkspaceTransfer(source, dest); Check(plan.Conflicts.Any(c => c.Kind == "board")); Fails(() => plan.Prepare(WorkspaceTransferMode.Merge));
            var choices = plan.Conflicts.ToDictionary(c => c.Id, c => c.Kind == "board" ? ConflictResolution.KeepCurrent : ConflictResolution.KeepBoth);
            var merged = plan.Prepare(WorkspaceTransferMode.Merge, choices); Check(merged.Notes[0].Projects[0].Id == board.Projects[0].Id);
            Check(merged.Notes[0].Projects.Last().NameRich!.StyleAt(0).Bold);
        });
    }
}
