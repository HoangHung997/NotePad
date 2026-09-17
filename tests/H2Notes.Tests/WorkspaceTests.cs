using H2Notes.Core;
using System.Text.Json;

internal static class WorkspaceTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Workspace assertion failed"); }
    private static string Folder() { var p = Path.Combine(Path.GetTempPath(), "H2Notes-workspace-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static void Fails(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Expected safe failure"); }
    public static void Run(Action<string, Action> test)
    {
        test("Workspace stores each project with rich text, chat, paths and ordinary notes while drafts stay local", () =>
        {
            var dir = Folder(); var store = new ProjectWorkspaceStore(dir); store.LoadOrImport(); var state = SheetStorage.Demo();
            var p = state.Notes[0].Projects[0]; p.NotesRich = RichDocument.Plain("Vietnamese rich note"); p.NotesRich.Format(0, 4, s => s with { Bold = true });
            p.Conversations.Add(new() { Draft = "draft", Messages = [new() { Content = "question" }, new() { Role = "assistant", Content = "answer", Model = "local-model" }] });
            p.Links.Add(new(Guid.NewGuid(), "drawing", "D:/drawing.dwg"));
            state.Notes.Add(new() { NoteKind = "general", Title = "Ordinary", Content = "keep" }); store.Save(state);
            Check(Directory.GetFiles(Path.Combine(dir, "projects"), "*.h2project.json").Length == 5);
            Check(Directory.GetFiles(Path.Combine(dir, "notes"), "*.h2note.json").Length == 1);
            var read = new ProjectWorkspaceStore(dir).LoadOrImport(); var loaded = read.Notes[0].Projects[0];
            Check(loaded.Id == p.Id && loaded.ReadNotes().StyleAt(0).Bold && loaded.Conversations[0].Messages.Count == 2 && loaded.Conversations[0].Draft == "");
            Check(loaded.Links[0].Target == "D:/drawing.dwg" && read.Notes.Count == 2);
        });
        test("Workspace migration preserves source bytes and immutable backup", () =>
        {
            var root = Folder(); var legacy = Path.Combine(root, "legacy.json"); new SheetStorage(legacy).Save(SheetStorage.Demo()); var bytes = File.ReadAllBytes(legacy);
            var store = new ProjectWorkspaceStore(Path.Combine(root, "new")); var state = store.LoadOrImport(legacy);
            Check(bytes.SequenceEqual(File.ReadAllBytes(legacy)) && state.Notes[0].Projects.Count == 5);
            Check(Directory.GetFiles(Path.Combine(store.Root, "backups"), "migration-*.json").Any());
        });
        test("Workspace unchanged projects are not rewritten; rename keeps stable GUID path", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var files = Directory.GetFiles(Path.Combine(store.Root, "projects")); var path = files.Single(f => f.Contains(state.Notes[0].Projects[0].Id.ToString("N")));
            var unchanged = files.First(f => f != path); var date = File.GetLastWriteTimeUtc(unchanged);
            state.Notes[0].Projects[0].NameRich = RichDocument.Plain("Renamed / safely"); store.Save(state);
            Check(File.Exists(path) && Directory.GetFiles(Path.Combine(store.Root, "projects")).Length == 5 && File.GetLastWriteTimeUtc(unchanged) == date);
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
        test("Workspace instance lock prevents two copies on one PC", () =>
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
        test("Two NAS devices append AI runs to the same conversation without losing either run", () =>
        {
            var root = Folder(); var seedStore = new ProjectWorkspaceStore(root, writerId: "seed"); seedStore.LoadOrImport();
            var seed = SheetStorage.Demo(); var projectId = seed.Notes[0].Projects[0].Id; var conversation = new AiConversation { Title = "Shared" };
            seed.Notes[0].Projects[0].Conversations.Add(conversation); seedStore.Save(seed);

            var storeA = new ProjectWorkspaceStore(root, writerId: "PC-A"); var stateA = storeA.LoadOrImport();
            var storeB = new ProjectWorkspaceStore(root, writerId: "PC-B"); var stateB = storeB.LoadOrImport();
            var convA = stateA.Notes[0].Projects[0].Conversations.Single();
            var convB = stateB.Notes[0].Projects[0].Conversations.Single();
            var runA = Guid.NewGuid(); var userA = new AiMessage { Role = "user", Content = "from A", CreatedAt = DateTime.UtcNow, AiRunId = runA, DeviceId = "PC-A" };
            convA.Messages.Add(userA); convA.Messages.Add(new AiMessage { Role = "assistant", Content = "reply A", ParentId = userA.Id, CreatedAt = DateTime.UtcNow, AiRunId = runA, DeviceId = "PC-A" });
            var runB = Guid.NewGuid(); var userB = new AiMessage { Role = "user", Content = "from B", CreatedAt = DateTime.UtcNow.AddMilliseconds(1), AiRunId = runB, DeviceId = "PC-B" };
            convB.Messages.Add(userB); convB.Messages.Add(new AiMessage { Role = "assistant", Content = "reply B", ParentId = userB.Id, CreatedAt = DateTime.UtcNow.AddMilliseconds(2), AiRunId = runB, DeviceId = "PC-B" });

            storeA.SaveIncremental(stateA, new HashSet<Guid> { projectId });
            storeB.SaveIncremental(stateB, new HashSet<Guid> { projectId });
            var final = new ProjectWorkspaceStore(root, writerId: "reader").LoadOrImport().Notes[0].Projects[0].Conversations.Single();
            Check(final.Messages.Count == 4 && final.Messages.Select(m => m.Id).Distinct().Count() == 4);
            Check(final.Messages.All(m => m.Sequence > 0) && final.Messages.Select(m => m.Sequence).Distinct().Count() == 4);
            Check(final.Messages.Count(m => m.AiRunId == runA) == 2 && final.Messages.Count(m => m.AiRunId == runB) == 2);
            Check(final.Messages.Single(m => m.Content == "reply A").ParentId == userA.Id && final.Messages.Single(m => m.Content == "reply B").ParentId == userB.Id);
        });
        test("Two NAS devices merge edits to different tasks and different fields of one task", () =>
        {
            var root = Folder(); var seedStore = new ProjectWorkspaceStore(root, writerId: "seed"); seedStore.LoadOrImport(); var seed = SheetStorage.Demo(); seedStore.Save(seed);
            var projectId = seed.Notes[0].Projects[0].Id;
            var storeA = new ProjectWorkspaceStore(root, writerId: "A"); var a = storeA.LoadOrImport();
            var storeB = new ProjectWorkspaceStore(root, writerId: "B"); var b = storeB.LoadOrImport();
            a.Notes[0].Projects[0].ChecklistItems[0].Comment = "comment from A";
            b.Notes[0].Projects[0].ChecklistItems[1].IsCompleted = true;
            b.Notes[0].Projects[0].ChecklistItems[0].IsCompleted = true;
            storeA.SaveIncremental(a, new HashSet<Guid> { projectId });
            storeB.SaveIncremental(b, new HashSet<Guid> { projectId });
            var read = new ProjectWorkspaceStore(root).LoadOrImport().Notes[0].Projects[0];
            Check(read.ChecklistItems[0].Comment == "comment from A" && read.ChecklistItems[0].IsCompleted);
            Check(read.ChecklistItems[1].IsCompleted);
        });
        test("Same-field NAS conflict keeps current writer, records audit, and does not block the workspace", () =>
        {
            var root = Folder(); var seedStore = new ProjectWorkspaceStore(root, writerId: "seed"); seedStore.LoadOrImport(); var seed = SheetStorage.Demo(); seedStore.Save(seed);
            var projectId = seed.Notes[0].Projects[0].Id;
            var storeA = new ProjectWorkspaceStore(root, writerId: "A"); var a = storeA.LoadOrImport();
            var storeB = new ProjectWorkspaceStore(root, writerId: "B"); var b = storeB.LoadOrImport();
            a.Notes[0].Projects[0].ChecklistItems[0].Text = "writer A";
            b.Notes[0].Projects[0].ChecklistItems[0].Text = "writer B";
            storeA.SaveIncremental(a, new HashSet<Guid> { projectId });
            storeB.SaveIncremental(b, new HashSet<Guid> { projectId });
            var read = new ProjectWorkspaceStore(root).LoadOrImport().Notes[0].Projects[0];
            Check(read.ChecklistItems[0].Text == "writer B");
            Check(storeB.LastMergeConflicts.Count > 0 && Directory.Exists(Path.Combine(root, "conflicts")) && Directory.GetFiles(Path.Combine(root, "conflicts"), "*.json").Length > 0);
        });
        test("Remote refresh brings NAS changes into an open device without discarding local edits", () =>
        {
            var root = Folder(); var seedStore = new ProjectWorkspaceStore(root, writerId: "seed"); seedStore.LoadOrImport(); var seed = SheetStorage.Demo(); seedStore.Save(seed);
            var projectId = seed.Notes[0].Projects[0].Id;
            var storeA = new ProjectWorkspaceStore(root, writerId: "A"); var a = storeA.LoadOrImport();
            var storeB = new ProjectWorkspaceStore(root, writerId: "B"); var b = storeB.LoadOrImport();
            a.Notes[0].Projects[0].ChecklistItems[0].Comment = "local unsaved";
            b.Notes[0].Projects[0].ChecklistItems.Add(new TaskRecord { Text = "remote new task" });
            storeB.SaveIncremental(b, new HashSet<Guid> { projectId });
            Check(storeA.RefreshFromDisk(a, new HashSet<Guid> { projectId }));
            Check(a.Notes[0].Projects[0].ChecklistItems[0].Comment == "local unsaved");
            Check(a.Notes[0].Projects[0].ChecklistItems.Any(t => t.Text == "remote new task"));
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
