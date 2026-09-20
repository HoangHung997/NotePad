using H2Notes.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

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
        test("AI time context exposes real timestamps and never invents legacy dates", () =>
        {
            var now = DateTimeOffset.Now;
            var board = new NoteRecord { Title = "Board", NoteKind = "project-hub" };
            var project = new ProjectRecord
            {
                Name = "Timed project", CreatedAtUtc = now.UtcDateTime.AddHours(-3), UpdatedAtUtc = now.UtcDateTime.AddHours(-1)
            };
            project.ChecklistItems.Add(new TaskRecord
            {
                Text = "Timed task", IsCompleted = true,
                CreatedAtUtc = now.UtcDateTime.AddHours(-2.5), UpdatedAtUtc = now.UtcDateTime.AddHours(-1.5), CompletedAtUtc = now.UtcDateTime.AddHours(-1.5)
            });
            var message = new AiMessage { Content = "Recorded turn", CreatedAt = now.UtcDateTime.AddMinutes(-30) };
            project.Conversations.Add(new AiConversation { Title = "Timed chat", CreatedAtUtc = now.UtcDateTime.AddHours(-2), Messages = [message] });
            board.Projects.Add(project);
            var known = new NoteRecord { Title = "Known note", NoteKind = "general", Content = "known", CreatedAtUtc = now.UtcDateTime.AddHours(-2), UpdatedAtUtc = now.UtcDateTime.AddHours(-1) };
            var legacy = new NoteRecord { Title = "Legacy note", NoteKind = "general", Content = "legacy" };
            var state = new SheetState { Notes = [board, known, legacy] };

            using var context = JsonDocument.Parse(AiLegacyRequestContext.Build(state, project, null, true));
            var root = context.RootElement;
            Check(root.GetProperty("timeContext").GetProperty("currentLocalTime").GetString()!.Length > 10);
            Check(root.GetProperty("timeContext").GetProperty("dayParts").GetProperty("morning").GetString() == "05:00-11:59");
            Check(root.GetProperty("tasks")[0].GetProperty("completedLocal").ValueKind == JsonValueKind.String);
            Check(root.GetProperty("conversations")[0].GetProperty("messages")[0].GetProperty("createdLocal").ValueKind == JsonValueKind.String);
            var notes = root.GetProperty("workspaceSources").GetProperty("notes").EnumerateArray().ToArray();
            Check(notes.Single(n => n.GetProperty("title").GetString() == "Known note").GetProperty("createdLocal").ValueKind == JsonValueKind.String);
            Check(notes.Single(n => n.GetProperty("title").GetString() == "Legacy note").GetProperty("createdLocal").ValueKind == JsonValueKind.Null);

            var timedTurn = AiHistory.RequestTurns(project.Conversations[0]).Single();
            Check(timedTurn.Content.Contains("createdLocal=") && timedTurn.Content.Contains("Recorded turn"));
            var untimed = new AiConversation { Messages = [new AiMessage { Content = "Legacy turn" }] };
            Check(AiHistory.RequestTurns(untimed).Single().Content == "Legacy turn");
        });
        test("Workspace location resolves mapped network and UNC aliases to one canonical endpoint", () =>
        {
            var mapped = WorkspaceLocation.Inspect(
                @"X:\Dữ liệu Hưng\.Note",
                _ => DriveType.Network,
                _ => @"\\NAS-SERVER\Share");
            var unc = WorkspaceLocation.Inspect(
                @"\\NAS-SERVER\Share\Dữ liệu Hưng\.Note",
                _ => DriveType.Network,
                _ => null);

            Check(mapped.Kind == WorkspaceLocationKind.MappedNetwork);
            Check(unc.Kind == WorkspaceLocationKind.UncNetwork);
            Check(mapped.ResolvedNetworkPath is not null);
            Check(mapped.CanonicalPath.Equals(unc.CanonicalPath, StringComparison.OrdinalIgnoreCase));
        });

        test("Workspace endpoint fallback switches only to reachable alias with matching WorkspaceId", () =>
        {
            var id = Guid.NewGuid();
            var profile = new WorkspaceLocationProfile(
                id,
                WorkspaceLocationKind.MappedNetwork,
                @"X:\Dữ liệu Hưng\.Note",
                @"\\NAS-SERVER\Share\Dữ liệu Hưng\.Note",
                @"\\NAS-SERVER\Share\Dữ liệu Hưng\.Note",
                DateTime.UtcNow);

            var selected = WorkspaceEndpointSelector.Select(
                profile,
                path => path.StartsWith(@"\\NAS-SERVER", StringComparison.OrdinalIgnoreCase),
                path => path.StartsWith(@"\\NAS-SERVER", StringComparison.OrdinalIgnoreCase) ? id : null);
            Check(selected == profile.ResolvedNetworkPath);

            var wrong = WorkspaceEndpointSelector.Select(
                profile,
                _ => true,
                _ => Guid.NewGuid());
            Check(wrong is null);

            var preferred = WorkspaceEndpointSelector.Select(
                profile,
                _ => true,
                _ => id);
            Check(preferred == profile.DisplayPath);
        });

        test("Schema 5 workspace migrates atomically to schema 6 stable WorkspaceId", () =>
        {
            var root = Folder();
            var seed = new ProjectWorkspaceStore(root); seed.LoadOrImport(); var original = SheetStorage.Demo(); seed.Save(original);
            var indexPath = Path.Combine(root, "workspace.h2index.json");
            var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            index["SchemaVersion"] = 5;
            index.Remove("WorkspaceId");

            foreach (var entryNode in index["Files"]!.AsArray())
            {
                var entry = entryNode!.AsObject();
                var relative = entry["File"]!.GetValue<string>();
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                document["SchemaVersion"] = 5;
                File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                entry["Hash"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            File.WriteAllText(indexPath, index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var legacy = new ProjectWorkspaceStore(root);
            var loaded = legacy.LoadOrImport();
            Check(legacy.WorkspaceId != Guid.Empty);
            Check(loaded.Notes[0].Projects.Count == original.Notes[0].Projects.Count);
            legacy.Save(loaded);

            using var migrated = JsonDocument.Parse(File.ReadAllBytes(indexPath));
            Check(migrated.RootElement.GetProperty("SchemaVersion").GetInt32() == ProjectWorkspaceStore.SchemaVersion);
            Check(migrated.RootElement.GetProperty("WorkspaceId").GetGuid() == legacy.WorkspaceId);
            foreach (var entry in migrated.RootElement.GetProperty("Files").EnumerateArray())
            {
                var relative = entry.GetProperty("File").GetString()!;
                using var child = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
                Check(child.RootElement.GetProperty("SchemaVersion").GetInt32() == ProjectWorkspaceStore.SchemaVersion);
            }
        });

        test("Logical WorkspaceId prevents same-machine duplicate aliases and self-transfer", () =>
        {
            var source = Folder();
            var first = new ProjectWorkspaceStore(source); first.LoadOrImport(); first.Save(SheetStorage.Demo());
            Check(first.WorkspaceId != Guid.Empty);

            var alias = Folder();
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(source, alias));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = file.Replace(source, alias);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }

            var a = new ProjectWorkspaceStore(source);
            var b = new ProjectWorkspaceStore(alias);
            Check(a.WorkspaceId == b.WorkspaceId && a.WorkspaceId != Guid.Empty);
            using var held = a.AcquireLock();
            Fails(() => { using var duplicate = b.AcquireLock(); });
            Fails(() => ProjectWorkspaceStore.ValidateDestination(source, alias));

            var otherRoot = Folder();
            var other = new ProjectWorkspaceStore(otherRoot); other.LoadOrImport(); other.Save(SheetStorage.Demo());
            Check(other.WorkspaceId != a.WorkspaceId);
            ProjectWorkspaceStore.ValidateDestination(source, otherRoot);
        });

        test("Offline pending workspace snapshot survives restart and merges remote changes before replay", () =>
        {
            var root = Folder();
            var pendingRoot = Folder();
            var seed = new ProjectWorkspaceStore(root); seed.LoadOrImport(); var initial = SheetStorage.Demo(); seed.Save(initial);

            var localStore = new ProjectWorkspaceStore(root, writerId: "OFFLINE-PC", pendingRoot: pendingRoot);
            var local = localStore.LoadOrImport();
            var baseGeneration = localStore.CurrentGenerationId;
            var localProject = local.Notes[0].Projects[0];
            localProject.ChecklistItems[0].Comment = "offline local comment";
            localProject.UpdatedAtUtc = DateTime.UtcNow;
            var pending = localStore.PersistPendingChanges(local, new HashSet<Guid> { localProject.Id });
            Check(localStore.HasPendingChanges && File.Exists(pending.FilePath));
            Check(pending.BaseGenerationId == baseGeneration && pending.WorkspaceId == localStore.WorkspaceId);

            // Another device advances the remote generation while this machine is offline.
            var remoteStore = new ProjectWorkspaceStore(root, writerId: "REMOTE-PC");
            var remote = remoteStore.LoadOrImport();
            var remoteProject = remote.Notes[0].Projects[1];
            remoteProject.ChecklistItems.Add(new TaskRecord { Text = "remote while offline", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
            remoteProject.UpdatedAtUtc = DateTime.UtcNow;
            remoteStore.SaveIncremental(remote, new HashSet<Guid> { remoteProject.Id });

            // A fresh process observes remote first, then restores/merges the durable pending snapshot.
            var restarted = new ProjectWorkspaceStore(root, writerId: "OFFLINE-PC", pendingRoot: pendingRoot);
            var observedRemote = restarted.LoadOrImport();
            Check(restarted.TryRestoreLatestPending(observedRemote, out var restored));
            Check(restored.BaseGenerationId == baseGeneration && restored.ObservedRemoteGenerationId == restarted.CurrentGenerationId);
            Check(restored.State.Notes[0].Projects[0].ChecklistItems[0].Comment == "offline local comment");
            Check(restored.State.Notes[0].Projects[1].ChecklistItems.Any(t => t.Text == "remote while offline"));

            // Re-reading the same append-only pending operation is idempotent.
            Check(restarted.TryRestoreLatestPending(observedRemote, out var replayed));
            Check(replayed.State.Notes[0].Projects[0].ChecklistItems[0].Comment == "offline local comment");
            Check(replayed.State.Notes[0].Projects[1].ChecklistItems.Count(t => t.Text == "remote while offline") == 1);

            restarted.SaveIncremental(restored.State, restored.DirtyProjectIds);
            restarted.AcknowledgePendingChanges();
            Check(!restarted.HasPendingChanges);
            var final = new ProjectWorkspaceStore(root).LoadOrImport();
            Check(final.Notes[0].Projects[0].ChecklistItems[0].Comment == "offline local comment");
            Check(final.Notes[0].Projects[1].ChecklistItems.Count(t => t.Text == "remote while offline") == 1);
        });

        test("Offline pending replay keeps local same-field edit and audits remote conflict", () =>
        {
            var root = Folder();
            var pendingRoot = Folder();
            var seed = new ProjectWorkspaceStore(root); seed.LoadOrImport(); var initial = SheetStorage.Demo(); seed.Save(initial);

            var offline = new ProjectWorkspaceStore(root, writerId: "OFFLINE-PC", pendingRoot: pendingRoot);
            var local = offline.LoadOrImport();
            var projectId = local.Notes[0].Projects[0].Id;
            local.Notes[0].Projects[0].NameRich = RichDocument.Plain("offline local name");
            local.Notes[0].Projects[0].UpdatedAtUtc = DateTime.UtcNow;
            offline.PersistPendingChanges(local, new HashSet<Guid> { projectId });

            var other = new ProjectWorkspaceStore(root, writerId: "REMOTE-PC");
            var remote = other.LoadOrImport();
            remote.Notes[0].Projects[0].NameRich = RichDocument.Plain("remote online name");
            remote.Notes[0].Projects[0].UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
            other.SaveIncremental(remote, new HashSet<Guid> { projectId });

            var restarted = new ProjectWorkspaceStore(root, writerId: "OFFLINE-PC", pendingRoot: pendingRoot);
            var current = restarted.LoadOrImport();
            Check(restarted.TryRestoreLatestPending(current, out var restored));
            Check(restored.State.Notes[0].Projects[0].DisplayName == "offline local name");
            Check(restored.Conflicts.Any(c => c.Path.Contains(projectId.ToString(), StringComparison.OrdinalIgnoreCase)
                && c.Resolution.Contains("kept local", StringComparison.OrdinalIgnoreCase)));

            restarted.SaveIncremental(restored.State, restored.DirtyProjectIds);
            Check(restarted.LastMergeConflicts.Count > 0);
            restarted.AcknowledgePendingChanges();
            Check(Directory.Exists(Path.Combine(root, "conflicts")));
            var final = new ProjectWorkspaceStore(root).LoadOrImport();
            Check(final.Notes[0].Projects[0].DisplayName == "offline local name");
        });

        test("Pending workspace snapshots stay append-only but bounded", () =>
        {
            var root = Folder();
            var pendingRoot = Folder();
            var store = new ProjectWorkspaceStore(root, writerId: "PC", pendingRoot: pendingRoot);
            store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            for (var i = 0; i < 12; i++)
            {
                state.Notes[0].Projects[0].ChecklistItems[0].Comment = "pending-" + i;
                store.PersistPendingChanges(state, new HashSet<Guid> { state.Notes[0].Projects[0].Id });
            }
            var files = Directory.GetFiles(pendingRoot, "*.pending.json");
            Check(files.Length == 8);
            Check(files.Select(Path.GetFileName).Distinct().Count() == files.Length);
        });

        test("Workspace rename keeps a stable GUID file path and project identity", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var path = Directory.GetFiles(Path.Combine(store.Root, "projects")).Single(f => f.Contains(state.Notes[0].Projects[0].Id.ToString("N")));
            state.Notes[0].Projects[0].NameRich = RichDocument.Plain("Renamed / safely"); store.Save(state);
            Check(File.Exists(path) && Directory.GetFiles(Path.Combine(store.Root, "projects")).Length == 5);
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
        test("Workspace final validation failure retains recovery journal until rollback", () =>
        {
            var root = Folder();
            var seed = new ProjectWorkspaceStore(root); seed.LoadOrImport(); var original = SheetStorage.Demo(); seed.Save(original);
            var projectId = original.Notes[0].Projects[0].Id;
            var projectPath = Path.Combine(root, "projects", projectId.ToString("N") + ".h2project.json");
            var journalPath = Path.Combine(root, ".h2-transaction.json");
            var journalSeenAtFault = false;

            var broken = new ProjectWorkspaceStore(root, n =>
            {
                if (n != 2) return; // changed project then workspace index: corrupt only after publication is complete
                journalSeenAtFault = File.Exists(journalPath);
                File.AppendAllText(projectPath, " ");
            });
            var state = broken.LoadOrImport();
            var previousName = state.Notes[0].Projects[0].DisplayName;
            state.Notes[0].Projects[0].NameRich = RichDocument.Plain("new generation must roll back");

            Fails(() => broken.SaveIncremental(state, new HashSet<Guid> { projectId }));
            Check(journalSeenAtFault);
            Check(!File.Exists(journalPath));

            var recovered = new ProjectWorkspaceStore(root).LoadOrImport();
            Check(recovered.Notes[0].Projects[0].DisplayName == previousName);
        });

        test("Workspace transient mixed generation converges on bounded reread", () =>
        {
            var root = Folder(); var recoveryRoot = root + "-recovery";
            var seed = new ProjectWorkspaceStore(root, recoveryRoot: recoveryRoot); seed.LoadOrImport();
            var original = SheetStorage.Demo(); seed.Save(original);
            var projectId = original.Notes[0].Projects[0].Id;
            var projectPath = Path.Combine(root, "projects", projectId.ToString("N") + ".h2project.json");
            var valid = File.ReadAllBytes(projectPath);
            File.AppendAllText(projectPath, " ");
            var attempts = 0;

            var reader = new ProjectWorkspaceStore(root, recoveryRoot: recoveryRoot, consistencyCheckpoint: attempt =>
            {
                attempts = attempt;
                if (attempt == 1) File.WriteAllBytes(projectPath, valid);
            });
            var loaded = reader.LoadOrImport();
            Check(loaded.Notes[0].Projects[0].Id == projectId);
            Check(attempts == 1);
            Check(reader.LastSyncDiagnostic is { Code: "transient_generation_converged", IsPersistent: false, Recovered: false, Attempt: 2 });
            Check(valid.SequenceEqual(File.ReadAllBytes(projectPath)));
        });

        test("Workspace persistent mixed generation offers guided last-known-good recovery with quarantine", () =>
        {
            var root = Folder(); var recoveryRoot = root + "-recovery";
            var seed = new ProjectWorkspaceStore(root, recoveryRoot: recoveryRoot); seed.LoadOrImport();
            var original = SheetStorage.Demo(); seed.Save(original);
            var project = original.Notes[0].Projects[0];
            var projectPath = Path.Combine(root, "projects", project.Id.ToString("N") + ".h2project.json");
            var valid = File.ReadAllBytes(projectPath);
            File.AppendAllText(projectPath, " ");
            var corrupt = File.ReadAllBytes(projectPath);

            var reader = new ProjectWorkspaceStore(root, writerId: "PC-2", recoveryRoot: recoveryRoot);
            var fallback = reader.LoadOrImport();
            Check(fallback.Notes[0].Projects[0].Id == project.Id);
            Check(reader.IsRecoveryFallbackActive);
            Check(reader.LastSyncDiagnostic is { Code: "persistent_generation_using_last_good", IsPersistent: true, RecoveryAvailable: true, Recovered: false, RelativeFile: not null });
            Check(corrupt.SequenceEqual(File.ReadAllBytes(projectPath))); // fallback read must not overwrite NAS
            fallback.Notes[0].Projects[0].NameRich = RichDocument.Plain("local draft while NAS invalid");
            Fails(() => reader.Save(fallback));
            Check(corrupt.SequenceEqual(File.ReadAllBytes(projectPath))); // fallback mode is strictly write-blocked

            Check(reader.TryRecoverLastKnownGood(out var recoveredDiagnostic));
            Check(recoveredDiagnostic is { Code: "persistent_generation_recovered", IsPersistent: true, RecoveryAvailable: true, Recovered: true });
            Check(recoveredDiagnostic.QuarantinePath is not null && Directory.Exists(recoveredDiagnostic.QuarantinePath));
            var quarantinedProject = Path.Combine(recoveredDiagnostic.QuarantinePath!, "projects", project.Id.ToString("N") + ".h2project.json");
            Check(File.Exists(quarantinedProject) && corrupt.SequenceEqual(File.ReadAllBytes(quarantinedProject)));
            Check(valid.SequenceEqual(File.ReadAllBytes(projectPath)));

            var reopened = new ProjectWorkspaceStore(root, recoveryRoot: recoveryRoot).LoadOrImport();
            Check(reopened.Notes[0].Projects[0].Id == project.Id);
        });

        test("Workspace persistent mismatch without local recovery fails closed and persists bounded diagnostics", () =>
        {
            var root = Folder(); var seedRecovery = root + "-seed-recovery"; var emptyRecovery = root + "-empty-recovery";
            var seed = new ProjectWorkspaceStore(root, recoveryRoot: seedRecovery); seed.LoadOrImport();
            var original = SheetStorage.Demo(); seed.Save(original);
            var project = original.Notes[0].Projects[0];
            var projectPath = Path.Combine(root, "projects", project.Id.ToString("N") + ".h2project.json");
            File.AppendAllText(projectPath, " ");
            var corrupt = File.ReadAllBytes(projectPath);

            var reader = new ProjectWorkspaceStore(root, writerId: "PC-NO-CACHE", recoveryRoot: emptyRecovery);
            Fails(() => reader.LoadOrImport());
            var diagnostic = reader.LastSyncDiagnostic;
            Check(diagnostic is { Code: "hash_mismatch", IsPersistent: true, RecoveryAvailable: false, Recovered: false, Attempt: 3 });
            Check(diagnostic!.WriterId == "PC-NO-CACHE" && diagnostic.RelativeFile!.StartsWith("projects/", StringComparison.Ordinal));
            Check(diagnostic.ExpectedHash is { Length: 64 } && diagnostic.ActualHash is { Length: 64 } && diagnostic.IndexHash is { Length: 64 });
            Check(corrupt.SequenceEqual(File.ReadAllBytes(projectPath)));

            Check(!reader.TryRecoverLastKnownGood(out var unavailable));
            Check(unavailable.Code == "recovery_unavailable" && !unavailable.RecoveryAvailable && !unavailable.Recovered);
            Check(corrupt.SequenceEqual(File.ReadAllBytes(projectPath)));
            var diagnosticFile = Path.Combine(reader.RecoveryCacheRoot, "last-sync-diagnostic.json");
            Check(File.Exists(diagnosticFile));
            var json = File.ReadAllText(diagnosticFile);
            Check(json.Contains("PC-NO-CACHE") && json.Contains(project.Id.ToString("N")) && !json.Contains(project.DisplayName));
        });

        test("Workspace rejects external modification, preserves bytes and opens only validated last-good read-only", () =>
        {
            var store = new ProjectWorkspaceStore(Folder()); store.LoadOrImport(); var state = SheetStorage.Demo(); store.Save(state);
            var path = Directory.GetFiles(Path.Combine(store.Root, "projects"))[0]; File.AppendAllText(path, " "); var altered = File.ReadAllBytes(path);
            Fails(() => store.Save(state)); Check(altered.SequenceEqual(File.ReadAllBytes(path)));

            var reader = new ProjectWorkspaceStore(store.Root);
            var fallback = reader.LoadOrImport();
            Check(reader.IsRecoveryFallbackActive);
            Check(reader.LastSyncDiagnostic is { Code: "persistent_generation_using_last_good", IsPersistent: true, RecoveryAvailable: true });
            Check(fallback.Notes[0].Projects.Count == state.Notes[0].Projects.Count);
            Check(altered.SequenceEqual(File.ReadAllBytes(path)));
            Fails(() => reader.Save(fallback));
            Check(altered.SequenceEqual(File.ReadAllBytes(path)));
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