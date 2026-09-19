using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

// Schema 5 keeps the existing project/note JSON layout but changes the write protocol:
// multiple devices may open the same NAS workspace, every commit takes only a short
// workspace transaction lock, and remote changes are merged by stable entity IDs.
public sealed class ProjectWorkspaceStore : INoteStorage
{
    public const int SchemaVersion = 5;
    private int _sourceVersion = SchemaVersion;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private Dictionary<Guid, FileEntry> _entries = [];
    private readonly Action<int>? _checkpoint;
    private readonly Action<int>? _consistencyCheckpoint;
    private readonly string _recoveryRoot;
    private SheetState? _baseState;
    private bool _recoveryFallbackActive;
    private const int ConsistencyReadAttempts = 3;

    public string Root { get; }
    public string FilePath => Path.Combine(Root, "workspace.h2index.json");
    public string WriterId { get; }
    public string RecoveryCacheRoot => _recoveryRoot;
    public WorkspaceSyncDiagnostic? LastSyncDiagnostic { get; private set; }
    public bool IsRecoveryFallbackActive => _recoveryFallbackActive;
    public IReadOnlyList<WorkspaceMergeConflict> LastMergeConflicts { get; private set; } = Array.Empty<WorkspaceMergeConflict>();

    private string JournalPath => Path.Combine(Root, ".h2-transaction.json");
    private string RecoveryPointerPath => Path.Combine(_recoveryRoot, "last-good.txt");
    private string SyncDiagnosticPath => Path.Combine(_recoveryRoot, "last-sync-diagnostic.json");
    private string CommitLockPath => Path.Combine(Root, ".h2-commit.lock");

    public ProjectWorkspaceStore(
        string root,
        Action<int>? checkpoint = null,
        string? writerId = null,
        string? recoveryRoot = null,
        Action<int>? consistencyCheckpoint = null)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _checkpoint = checkpoint;
        _consistencyCheckpoint = consistencyCheckpoint;
        WriterId = string.IsNullOrWhiteSpace(writerId) ? Environment.MachineName : writerId.Trim();
        var recoveryKey = Hash(Encoding.UTF8.GetBytes(Root))[..32];
        _recoveryRoot = Path.GetFullPath(recoveryRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "H2Notes", "workspace-recovery", recoveryKey));
    }

    // This lock is intentionally local to one Windows profile. It prevents two copies
    // of H2 Notes on the same PC from editing the same workspace, while another PC may
    // open the same NAS folder. Cross-device serialization happens only in AcquireCommitLock().
    public FileStream AcquireLock()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes", "instance-locks");
        Directory.CreateDirectory(folder);
        var key = Hash(Encoding.UTF8.GetBytes(Root))[..32];
        return new FileStream(Path.Combine(folder, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private FileStream AcquireCommitLock()
    {
        Directory.CreateDirectory(Root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        IOException? last = null;
        do
        {
            try { return new FileStream(CommitLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { last = ex; Thread.Sleep(60); }
        }
        while (DateTime.UtcNow < deadline);
        throw new IOException("Kho dữ liệu đang được thiết bị khác ghi. H2 Notes sẽ thử lại ở lần tự lưu sau.", last);
    }

    public SheetState LoadOrImport(string? legacyPath = null)
    {
        Recover();
        if (File.Exists(FilePath)) return Read();
        if (FindDataFiles().Any()) throw new InvalidDataException("Có tệp dự án nhưng thiếu chỉ mục kho. Không tạo kho trống đè lên dữ liệu.");

        _loaded = true; _known.Clear(); _entries.Clear(); _sourceVersion = SchemaVersion;
        var empty = new SheetState(); _baseState = Clone(empty);
        if (legacyPath is null || !File.Exists(legacyPath)) return empty;

        var state = SheetStorage.Read(legacyPath);
        Directory.CreateDirectory(Path.Combine(Root, "backups"));
        File.Copy(legacyPath, Path.Combine(Root, "backups", "migration-" + Guid.NewGuid().ToString("N") + ".json"));
        Save(state);
        return state;
    }

    public SheetState Read()
    {
        using var commit = AcquireCommitLock();
        RecoverUnderLock();
        var snapshot = ReadStableSnapshotUnderLock();
        ApplySnapshot(snapshot);
        _baseState = Clone(snapshot.State);
        _loaded = true;
        return snapshot.State;
    }

    public bool RefreshFromDisk(SheetState liveState, IReadOnlySet<Guid> dirtyProjects)
    {
        if (!_loaded || !File.Exists(FilePath)) return false;
        using var commit = AcquireCommitLock();
        RecoverUnderLock();
        var snapshot = ReadStableSnapshotUnderLock();
        if (_known.TryGetValue("workspace.h2index.json", out var knownIndex)
            && snapshot.Fingerprints.TryGetValue("workspace.h2index.json", out var currentIndex)
            && knownIndex == currentIndex) return false;

        var baseline = _baseState is null ? Clone(snapshot.State) : Clone(_baseState);
        LastMergeConflicts = WorkspaceConcurrency.MergeIntoLocal(baseline, liveState, snapshot.State, dirtyProjects, WriterId);
        ApplySnapshot(snapshot);
        // The new baseline is what is currently on the NAS. Local dirty edits remain in liveState
        // and will be compared against this baseline on the next save.
        _baseState = Clone(snapshot.State);
        return true;
    }

    public void Save(SheetState state) => SaveIncremental(state, null);

    public void SaveIncremental(SheetState state, IReadOnlySet<Guid>? changedProjects)
    {
        if (!_loaded) throw new InvalidOperationException("Đọc kho trước khi lưu để kiểm tra xung đột.");
        if (_recoveryFallbackActive)
            throw new InvalidOperationException("Kho NAS đang dùng last-known-good cục bộ. Hãy phục hồi hoặc đổi kho trước khi lưu.");
        ValidateState(state);

        using var commit = AcquireCommitLock();
        RecoverUnderLock();
        var hasRemote = File.Exists(FilePath);
        var remote = hasRemote ? ReadStableSnapshotUnderLock() : WorkspaceSnapshot.Empty();
        if (_recoveryFallbackActive)
            throw new InvalidOperationException("Kho NAS vẫn lỗi generation. Chưa ghi đè dữ liệu; hãy phục hồi từ last-known-good trước.");
        var baseline = _baseState is null ? Clone(remote.State) : Clone(_baseState);

        LastMergeConflicts = hasRemote
            ? WorkspaceConcurrency.MergeIntoLocal(baseline, state, remote.State, changedProjects, WriterId)
            : Array.Empty<WorkspaceMergeConflict>();

        _sourceVersion = hasRemote ? remote.SchemaVersion : SchemaVersion;
        if (_sourceVersion != SchemaVersion) changedProjects = null; // one complete upgrade transaction

        var package = BuildPackage(state, changedProjects, remote);
        var changed = package.Data
            .Where(p => p.Value is not null && (!remote.Fingerprints.TryGetValue(p.Key, out var hash) || hash != Hash(p.Value)))
            .Select(p => p.Key).ToList();
        var removed = remote.Fingerprints.Keys.Where(k => !package.Data.ContainsKey(k)).ToList();
        if (changed.Count == 0 && removed.Count == 0)
        {
            ApplySnapshot(remote);
            _baseState = Clone(remote.State);
            return;
        }

        AssertSnapshotUnchanged(remote.Fingerprints);
        var transactionId = Guid.NewGuid().ToString("N");
        var backup = "backups/save-" + transactionId;
        var entries = changed.Concat(removed).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k == "workspace.h2index.json" ? 1 : 0)
            .Select((file, i) => new JournalEntry(file, File.Exists(Resolve(file)), backup + "/" + i + ".bak")).ToList();

        foreach (var entry in entries)
        {
            var target = Resolve(entry.File);
            if (!remote.Fingerprints.ContainsKey(entry.File) && File.Exists(target))
                throw new IOException("Không ghi đè tệp chưa thuộc chỉ mục: " + entry.File);
            if (!entry.Existed) continue;
            var saved = ResolveBackup(entry.Backup);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(target, saved, false);
        }

        AssertSnapshotUnchanged(remote.Fingerprints);
        AtomicWrite(JournalPath, Encode(new SaveJournal { Entries = entries }));
        try
        {
            var count = 0;
            foreach (var entry in entries)
            {
                var target = Resolve(entry.File);
                if (package.Data.TryGetValue(entry.File, out var bytes) && bytes is not null) AtomicWrite(target, bytes);
                else File.Delete(target);
                _checkpoint?.Invoke(++count);
            }
            if (LastMergeConflicts.Count > 0) WriteConflictAudit(LastMergeConflicts);

            // Keep the recovery journal until the newly published generation has passed
            // complete hash/schema/state validation. If validation fails, the catch path
            // still has the backups/journal needed to restore the previous generation.
            var final = ReadSnapshot();
            File.Delete(JournalPath);
            ApplySnapshot(final);
            _baseState = Clone(final.State);
            _sourceVersion = SchemaVersion;
            TryCaptureLastKnownGood(final);
        }
        catch
        {
            RecoverUnderLock();
            throw;
        }
    }

    private WorkspaceSnapshot ReadStableSnapshotUnderLock()
    {
        WorkspaceConsistencyException? last = null;
        for (var attempt = 1; attempt <= ConsistencyReadAttempts; attempt++)
        {
            try
            {
                var snapshot = ReadSnapshot();
                _recoveryFallbackActive = false;
                if (last is null) LastSyncDiagnostic = null;
                else
                {
                    SetSyncDiagnostic(new WorkspaceSyncDiagnostic
                    {
                        Code = "transient_generation_converged",
                        FailureCode = last.Code,
                        ExceptionType = last.GetType().Name,
                        Message = "NAS đã hội tụ về một generation hợp lệ sau khi đọc lại.",
                        WriterId = WriterId,
                        RelativeFile = last.RelativeFile,
                        ExpectedHash = last.ExpectedHash,
                        ActualHash = last.ActualHash,
                        IndexHash = last.IndexHash,
                        Attempt = attempt,
                        IsPersistent = false,
                        RecoveryAvailable = HasValidLastKnownGood(),
                        Recovered = false
                    });
                }
                TryCaptureLastKnownGood(snapshot);
                return snapshot;
            }
            catch (WorkspaceConsistencyException ex)
            {
                last = ex;
                var recoveryAvailable = HasValidLastKnownGood();
                SetSyncDiagnostic(DiagnosticFrom(ex, attempt, false, recoveryAvailable, false));
                _consistencyCheckpoint?.Invoke(attempt);
                if (attempt < ConsistencyReadAttempts) Thread.Sleep(75);
            }
        }

        if (last is null) throw new InvalidOperationException("Không xác định được lỗi generation.");
        if (TryLoadLastKnownGood(out var fallbackId, out _, out var fallbackSnapshot))
        {
            _recoveryFallbackActive = true;
            SetSyncDiagnostic(new WorkspaceSyncDiagnostic
            {
                Code = "persistent_generation_using_last_good",
                FailureCode = last.Code,
                ExceptionType = last.GetType().Name,
                Message = "NAS đang lỗi generation kéo dài. App mở bản last-known-good cục bộ và khóa ghi cho tới khi phục hồi được xác nhận.",
                WriterId = WriterId,
                RelativeFile = last.RelativeFile,
                ExpectedHash = last.ExpectedHash,
                ActualHash = last.ActualHash,
                IndexHash = last.IndexHash,
                Attempt = ConsistencyReadAttempts,
                IsPersistent = true,
                RecoveryAvailable = true,
                Recovered = false,
                RecoverySnapshotId = fallbackId
            });
            return fallbackSnapshot;
        }

        _recoveryFallbackActive = false;
        SetSyncDiagnostic(DiagnosticFrom(last, ConsistencyReadAttempts, true, false, false));
        throw last;
    }

    public bool TryRecoverLastKnownGood(out WorkspaceSyncDiagnostic diagnostic)
    {
        using var commit = AcquireCommitLock();
        RecoverUnderLock();

        WorkspaceConsistencyException failure;
        try
        {
            var current = ReadSnapshot();
            _recoveryFallbackActive = false;
            TryCaptureLastKnownGood(current);
            diagnostic = new WorkspaceSyncDiagnostic
            {
                Code = "recovery_not_needed",
                ExceptionType = "",
                Message = "Generation NAS hiện tại đã hợp lệ; không cần phục hồi.",
                WriterId = WriterId,
                Attempt = 1,
                IsPersistent = false,
                RecoveryAvailable = HasValidLastKnownGood(),
                Recovered = false
            };
            SetSyncDiagnostic(diagnostic);
            return false;
        }
        catch (WorkspaceConsistencyException ex)
        {
            failure = ex;
        }

        if (!TryLoadLastKnownGood(out var recoveryId, out var recoveryFolder, out var recoverySnapshot))
        {
            diagnostic = DiagnosticFrom(failure, ConsistencyReadAttempts, true, false, false) with
            {
                Code = "recovery_unavailable",
                Message = "Generation NAS vẫn lỗi nhưng máy này chưa có last-known-good hợp lệ để phục hồi."
            };
            SetSyncDiagnostic(diagnostic);
            return false;
        }

        try
        {
            var quarantine = QuarantineCurrentGeneration(failure);
            RestoreRecoveryGeneration(recoveryFolder, recoverySnapshot);
            var restored = ReadSnapshot();
            ApplySnapshot(restored);
            _baseState = Clone(restored.State);
            _recoveryFallbackActive = false;
            TryCaptureLastKnownGood(restored);
            diagnostic = new WorkspaceSyncDiagnostic
            {
                Code = "persistent_generation_recovered",
                FailureCode = failure.Code,
                ExceptionType = failure.GetType().Name,
                Message = "Đã phục hồi generation NAS từ last-known-good sau khi cách ly đầy đủ generation lỗi.",
                WriterId = WriterId,
                RelativeFile = failure.RelativeFile,
                ExpectedHash = failure.ExpectedHash,
                ActualHash = failure.ActualHash,
                IndexHash = failure.IndexHash,
                Attempt = ConsistencyReadAttempts,
                IsPersistent = true,
                RecoveryAvailable = true,
                Recovered = true,
                RecoverySnapshotId = recoveryId,
                QuarantinePath = quarantine
            };
            SetSyncDiagnostic(diagnostic);
            return true;
        }
        catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            diagnostic = new WorkspaceSyncDiagnostic
            {
                Code = "persistent_recovery_failed",
                FailureCode = failure.Code,
                ExceptionType = recoveryError.GetType().Name,
                Message = BoundDiagnosticMessage(recoveryError.Message),
                WriterId = WriterId,
                RelativeFile = failure.RelativeFile,
                ExpectedHash = failure.ExpectedHash,
                ActualHash = failure.ActualHash,
                IndexHash = failure.IndexHash,
                Attempt = ConsistencyReadAttempts,
                IsPersistent = true,
                RecoveryAvailable = true,
                Recovered = false,
                RecoverySnapshotId = recoveryId
            };
            SetSyncDiagnostic(diagnostic);
            return false;
        }
    }

    private WorkspaceSyncDiagnostic DiagnosticFrom(
        WorkspaceConsistencyException ex,
        int attempt,
        bool persistent,
        bool recoveryAvailable,
        bool recovered)
        => new()
        {
            Code = ex.Code,
            FailureCode = ex.Code,
            ExceptionType = ex.GetType().Name,
            Message = BoundDiagnosticMessage(ex.Message),
            WriterId = WriterId,
            RelativeFile = ex.RelativeFile,
            ExpectedHash = ex.ExpectedHash,
            ActualHash = ex.ActualHash,
            IndexHash = ex.IndexHash,
            Attempt = attempt,
            IsPersistent = persistent,
            RecoveryAvailable = recoveryAvailable,
            Recovered = recovered
        };

    public WorkspaceSyncDiagnostic RecordSyncFailure(Exception ex)
    {
        if (ex is WorkspaceConsistencyException && LastSyncDiagnostic is not null) return LastSyncDiagnostic;
        var code = ex switch
        {
            UnauthorizedAccessException => "access_denied",
            System.Text.Json.JsonException => "invalid_json",
            IOException => "io_error",
            _ => "sync_error"
        };
        var diagnostic = new WorkspaceSyncDiagnostic
        {
            Code = code,
            ExceptionType = ex.GetType().Name,
            Message = BoundDiagnosticMessage(ex.Message),
            WriterId = WriterId,
            Attempt = 1,
            IsPersistent = false,
            RecoveryAvailable = HasValidLastKnownGood(),
            Recovered = false
        };
        SetSyncDiagnostic(diagnostic);
        return diagnostic;
    }

    private void SetSyncDiagnostic(WorkspaceSyncDiagnostic diagnostic)
    {
        LastSyncDiagnostic = diagnostic;
        try { AtomicWrite(SyncDiagnosticPath, Encode(diagnostic)); }
        catch { /* diagnostics must never overwrite or block project data */ }
    }

    private static string BoundDiagnosticMessage(string value)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 600 ? value : value[..600];
    }

    private bool HasValidLastKnownGood()
        => TryLoadLastKnownGood(out _, out _, out _);

    private bool TryCaptureLastKnownGood(WorkspaceSnapshot snapshot)
    {
        try
        {
            if (!snapshot.Fingerprints.TryGetValue("workspace.h2index.json", out var generationId)) return false;
            var generationFolder = Path.Combine(_recoveryRoot, "generations", generationId);
            if (Directory.Exists(generationFolder))
            {
                try
                {
                    var existing = ReadRecoverySnapshot(generationFolder);
                    if (existing.Fingerprints.TryGetValue("workspace.h2index.json", out var existingId) && existingId == generationId)
                    {
                        AtomicWrite(RecoveryPointerPath, Encoding.UTF8.GetBytes(generationId));
                        return true;
                    }
                }
                catch { Directory.Delete(generationFolder, true); }
            }

            Directory.CreateDirectory(generationFolder);
            foreach (var pair in snapshot.Fingerprints)
            {
                var bytes = File.ReadAllBytes(Resolve(pair.Key));
                if (Hash(bytes) != pair.Value) throw new IOException("Generation đổi trong lúc tạo last-known-good: " + pair.Key);
                AtomicWrite(RecoveryDataPath(generationFolder, pair.Key), bytes);
            }

            var verified = ReadRecoverySnapshot(generationFolder);
            if (!verified.Fingerprints.TryGetValue("workspace.h2index.json", out var verifiedId) || verifiedId != generationId)
                throw new InvalidDataException("Last-known-good không vượt qua kiểm tra generation.");
            AtomicWrite(RecoveryPointerPath, Encoding.UTF8.GetBytes(generationId));
            CleanupRecoveryGenerations(generationId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadLastKnownGood(out string generationId, out string generationFolder, out WorkspaceSnapshot snapshot)
    {
        generationId = "";
        generationFolder = "";
        snapshot = WorkspaceSnapshot.Empty();
        try
        {
            if (!File.Exists(RecoveryPointerPath)) return false;
            generationId = File.ReadAllText(RecoveryPointerPath).Trim();
            if (generationId.Length != 64 || generationId.Any(c => !Uri.IsHexDigit(c))) return false;
            generationFolder = Path.Combine(_recoveryRoot, "generations", generationId);
            if (!Directory.Exists(generationFolder)) return false;
            snapshot = ReadRecoverySnapshot(generationFolder);
            return snapshot.Fingerprints.TryGetValue("workspace.h2index.json", out var actual) && actual == generationId;
        }
        catch
        {
            generationId = "";
            generationFolder = "";
            snapshot = WorkspaceSnapshot.Empty();
            return false;
        }
    }

    private WorkspaceSnapshot ReadRecoverySnapshot(string generationFolder)
        => ReadSnapshotFrom(relative => File.ReadAllBytes(RecoveryDataPath(generationFolder, relative)));

    private string QuarantineCurrentGeneration(WorkspaceConsistencyException cause)
    {
        var indexBytes = File.ReadAllBytes(FilePath);
        var index = Decode<WorkspaceIndex>(indexBytes);
        var quarantineBase = Path.Combine(_recoveryRoot, "quarantine");
        Directory.CreateDirectory(quarantineBase);
        var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
        var pending = Path.Combine(quarantineBase, name + ".pending");
        var final = Path.Combine(quarantineBase, name);
        Directory.CreateDirectory(pending);

        var files = new List<object>();
        AtomicWrite(RecoveryDataPath(pending, "workspace.h2index.json"), indexBytes);
        foreach (var entry in index.Files)
        {
            var source = Resolve(entry.File);
            if (!File.Exists(source))
            {
                files.Add(new { entry.File, Missing = true, entry.Hash, ActualHash = (string?)null });
                continue;
            }

            var bytes = File.ReadAllBytes(source);
            AtomicWrite(RecoveryDataPath(pending, entry.File), bytes);
            files.Add(new { entry.File, Missing = false, entry.Hash, ActualHash = Hash(bytes) });
        }

        AtomicWrite(Path.Combine(pending, "diagnostic.json"), Encode(new
        {
            CapturedUtc = DateTime.UtcNow,
            WriterId,
            Cause = cause.Code,
            cause.RelativeFile,
            cause.ExpectedHash,
            cause.ActualHash,
            cause.IndexHash,
            Files = files
        }));
        Directory.Move(pending, final);
        return final;
    }

    private void RestoreRecoveryGeneration(string generationFolder, WorkspaceSnapshot snapshot)
    {
        foreach (var pair in snapshot.Fingerprints.Where(p => p.Key != "workspace.h2index.json"))
        {
            var bytes = File.ReadAllBytes(RecoveryDataPath(generationFolder, pair.Key));
            if (Hash(bytes) != pair.Value) throw new InvalidDataException("Last-known-good bị thay đổi: " + pair.Key);
            AtomicWrite(Resolve(pair.Key), bytes);
        }

        var indexBytes = File.ReadAllBytes(RecoveryDataPath(generationFolder, "workspace.h2index.json"));
        if (Hash(indexBytes) != snapshot.Fingerprints["workspace.h2index.json"])
            throw new InvalidDataException("Last-known-good index bị thay đổi.");
        AtomicWrite(FilePath, indexBytes);
    }

    private string RecoveryDataPath(string root, string relative)
    {
        if (!IsWorkspaceDataFile(relative)) throw new InvalidDataException("Đường dẫn recovery không hợp lệ.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Đường dẫn recovery vượt phạm vi.");
        return path;
    }

    private static bool IsWorkspaceDataFile(string relative)
        => relative == "workspace.h2index.json"
           || (relative.StartsWith("projects/", StringComparison.Ordinal) && relative.EndsWith(".h2project.json", StringComparison.Ordinal))
           || (relative.StartsWith("notes/", StringComparison.Ordinal) && relative.EndsWith(".h2note.json", StringComparison.Ordinal));

    private void CleanupRecoveryGenerations(string current)
    {
        try
        {
            var root = Path.Combine(_recoveryRoot, "generations");
            if (!Directory.Exists(root)) return;
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories()
                         .OrderByDescending(d => d.LastWriteTimeUtc)
                         .Skip(3))
                if (!directory.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) directory.Delete(true);
        }
        catch { /* best-effort local cache cleanup only */ }
    }

    private WritePackage BuildPackage(SheetState state, IReadOnlySet<Guid>? changedProjects, WorkspaceSnapshot remote)
    {
        Directory.CreateDirectory(Root);
        var data = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var index = new WorkspaceIndex
        {
            State = new SheetState
            {
                SheetSchemaVersion = state.SheetSchemaVersion,
                SheetPreferences = state.SheetPreferences,
                ImportHistory = state.ImportHistory,
                // Desktop session is stored locally by the Avalonia app. Never let two PCs
                // overwrite each other's open-window state through the shared NAS index.
                DesktopSession = null,
                Extra = state.Extra
            }
        };

        foreach (var note in state.Notes)
        {
            if (note.IsBoard)
            {
                var shell = SharedNote(note, includeProjects: false);
                index.State.Notes.Add(shell);
                foreach (var project in note.Projects)
                {
                    if (changedProjects is not null && !changedProjects.Contains(project.Id)
                        && remote.Entries.TryGetValue(project.Id, out var cached)
                        && cached.BoardId == note.Id && cached.Kind == "project" && remote.SchemaVersion == SchemaVersion)
                    {
                        data.Add(cached.File, null); index.Files.Add(cached); continue;
                    }

                    var name = "projects/" + project.Id.ToString("N") + ".h2project.json";
                    var bytes = Encode(new ProjectFile { BoardId = note.Id, Project = SharedProject(project) });
                    data.Add(name, bytes);
                    index.Files.Add(new(project.Id, note.Id, "project", name, Hash(bytes)));
                }
            }
            else
            {
                var name = "notes/" + note.Id.ToString("N") + ".h2note.json";
                var bytes = Encode(new GeneralNoteFile { Note = SharedNote(note, includeProjects: true) });
                data.Add(name, bytes);
                index.Files.Add(new(note.Id, null, "note", name, Hash(bytes)));
            }
        }

        data.Add("workspace.h2index.json", Encode(index));
        return new(data, index);
    }

    private static ProjectRecord SharedProject(ProjectRecord project)
    {
        var clone = Clone(project);
        ClearDrafts(clone.Conversations);
        return clone;
    }

    private static NoteRecord SharedNote(NoteRecord note, bool includeProjects)
    {
        var clone = Clone(note);
        if (!includeProjects) clone.Projects = [];
        ClearDrafts(clone.AiConversations);
        foreach (var project in clone.Projects) ClearDrafts(project.Conversations);
        return clone;
    }

    private static void ClearDrafts(IEnumerable<AiConversation> conversations)
    {
        foreach (var conversation in conversations)
        {
            conversation.Draft = "";
            conversation.DraftAttachments.Clear();
        }
    }

    private WorkspaceSnapshot ReadSnapshot()
        => ReadSnapshotFrom(relative => File.ReadAllBytes(Resolve(relative)));

    private WorkspaceSnapshot ReadSnapshotFrom(Func<string, byte[]> read)
    {
        byte[] indexBytes;
        try { indexBytes = read("workspace.h2index.json"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WorkspaceConsistencyException("missing_index", "Thiếu chỉ mục kho dữ liệu.",
                "workspace.h2index.json", inner: ex);
        }

        var indexHash = Hash(indexBytes);
        WorkspaceIndex index;
        try { index = Decode<WorkspaceIndex>(indexBytes); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WorkspaceConsistencyException("invalid_index_json", "Chỉ mục kho không phải JSON hợp lệ.",
                "workspace.h2index.json", actualHash: indexHash, indexHash: indexHash, inner: ex);
        }

        if (index.SchemaVersion is not 2 and not 3 and not 4 and not SchemaVersion)
            throw new InvalidDataException("Kho thuộc phiên bản không được hỗ trợ. Không ghi đè.");
        if (index.State is null || index.Files is null) throw new InvalidDataException("Chỉ mục kho không đầy đủ.");

        var state = index.State;
        foreach (var board in state.Notes.Where(n => n.IsBoard)) board.Projects.Clear();
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspace.h2index.json"] = indexHash
        };
        var ids = new HashSet<Guid>();
        foreach (var entry in index.Files)
        {
            if (!ids.Add(entry.Id)) throw new InvalidDataException("ID trùng trong chỉ mục kho.");
            byte[] bytes;
            try { bytes = read(entry.File); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new WorkspaceConsistencyException("missing_file",
                    "Chỉ mục tham chiếu tệp chưa xuất hiện: " + entry.File,
                    entry.File, entry.Hash, indexHash: indexHash, inner: ex);
            }

            var actualHash = Hash(bytes);
            if (actualHash != entry.Hash)
                throw new WorkspaceConsistencyException("hash_mismatch",
                    "Tệp và chỉ mục đang thuộc hai generation khác nhau: " + entry.File,
                    entry.File, entry.Hash, actualHash, indexHash);

            fingerprints.Add(entry.File, entry.Hash);
            if (entry.Kind == "project")
            {
                ProjectFile doc;
                try { doc = Decode<ProjectFile>(bytes); }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new WorkspaceConsistencyException("invalid_project_json",
                        "Tệp dự án không phải JSON hợp lệ: " + entry.File,
                        entry.File, entry.Hash, actualHash, indexHash, ex);
                }
                if (doc.SchemaVersion != index.SchemaVersion || doc.Project?.Id != entry.Id || doc.BoardId != entry.BoardId)
                    throw new WorkspaceConsistencyException("project_index_mismatch",
                        "Tệp dự án không khớp chỉ mục: " + entry.File,
                        entry.File, entry.Hash, actualHash, indexHash);
                var board = state.Notes.SingleOrDefault(n => n.Id == entry.BoardId)
                    ?? throw new InvalidDataException("Không tìm thấy bảng sở hữu dự án.");
                board.Projects.Add(doc.Project);
            }
            else if (entry.Kind == "note")
            {
                GeneralNoteFile doc;
                try { doc = Decode<GeneralNoteFile>(bytes); }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new WorkspaceConsistencyException("invalid_note_json",
                        "Tệp ghi chú không phải JSON hợp lệ: " + entry.File,
                        entry.File, entry.Hash, actualHash, indexHash, ex);
                }
                if (doc.SchemaVersion != index.SchemaVersion || doc.Note?.Id != entry.Id || doc.Note.IsBoard)
                    throw new WorkspaceConsistencyException("note_index_mismatch",
                        "Tệp ghi chú không khớp chỉ mục: " + entry.File,
                        entry.File, entry.Hash, actualHash, indexHash);
                state.Notes.Add(doc.Note);
            }
            else throw new InvalidDataException("Loại tệp trong kho không được hỗ trợ.");
        }
        try { ValidateState(state); }
        catch (InvalidDataException ex)
        {
            throw new WorkspaceConsistencyException("invalid_snapshot_state",
                "Generation kho không vượt qua kiểm tra trạng thái.", indexHash: indexHash, inner: ex);
        }
        return new(state, fingerprints, index.Files.ToDictionary(e => e.Id), index.SchemaVersion);
    }

    private void ApplySnapshot(WorkspaceSnapshot snapshot)
    {
        _known = snapshot.Fingerprints;
        _entries = snapshot.Entries;
        _sourceVersion = snapshot.SchemaVersion;
        _loaded = true;
    }

    private void AssertSnapshotUnchanged(Dictionary<string, string> fingerprints)
    {
        foreach (var pair in fingerprints)
        {
            var path = Resolve(pair.Key);
            if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != pair.Value)
                throw new IOException("Dữ liệu bị thay đổi ngoài giao thức đồng bộ trong lúc lưu: " + pair.Key);
        }
        if (fingerprints.Count == 0 && File.Exists(FilePath))
            throw new IOException("Kho vừa được tạo bởi thiết bị khác. Sẽ đồng bộ ở lần lưu kế tiếp.");
    }

    public void Recover()
    {
        Directory.CreateDirectory(Root);
        using var commit = AcquireCommitLock();
        RecoverUnderLock();
    }

    private void RecoverUnderLock()
    {
        if (!File.Exists(JournalPath)) return;
        var journal = Decode<SaveJournal>(File.ReadAllBytes(JournalPath));
        foreach (var entry in journal.Entries)
        {
            _ = Resolve(entry.File);
            var backup = ResolveBackup(entry.Backup);
            if (entry.Existed && !File.Exists(backup)) throw new InvalidDataException("Thiếu bản sao phục hồi. Giữ nguyên kho để kiểm tra.");
        }
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if (entry.Existed) AtomicWrite(Resolve(entry.File), File.ReadAllBytes(ResolveBackup(entry.Backup)));
            else File.Delete(Resolve(entry.File));
        }
        File.Delete(JournalPath);
    }

    private void WriteConflictAudit(IReadOnlyList<WorkspaceMergeConflict> conflicts)
    {
        var folder = Path.Combine(Root, "conflicts");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(path, Encode(conflicts));
    }

    public LegacyImportRecord ImportCopies(SheetState current, LegacyImportPreview preview)
    {
        if (LegacyImport.AlreadyImported(current, preview)) throw new InvalidDataException("File này đã được nhập.");
        var copies = LegacyImport.CreateCopies(current, preview);
        var backup = Path.Combine(Root, "backups", "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        AtomicWrite(Path.Combine(backup, "before-import.json"), Encode(current));
        AtomicWrite(Path.Combine(backup, "source.json"), preview.SourceBytes);
        var record = new LegacyImportRecord(Path.GetFileName(preview.SourcePath), preview.Sha256, DateTime.UtcNow, copies.Select(n => n.Id).ToList(), backup);
        var next = Clone(current); next.Notes.AddRange(copies); next.ImportHistory.Add(record);
        Save(next);
        current.Notes.AddRange(copies); current.ImportHistory.Add(record);
        return record;
    }

    public string BackupSnapshot(SheetState state)
    {
        var target = Path.Combine(Root, "backups", "transfer-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(target, Encode(state)); return target;
    }

    private IEnumerable<string> FindDataFiles()
    {
        foreach (var folder in new[] { "projects", "notes" })
        {
            var path = Path.Combine(Root, folder);
            if (!Directory.Exists(path)) continue;
            RejectReparse(path);
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)) yield return file;
        }
    }

    private string Resolve(string relative)
    {
        if (!IsWorkspaceDataFile(relative))
            throw new InvalidDataException("Đường dẫn tệp kho không hợp lệ.");
        return ResolveSafe(relative);
    }

    private string ResolveBackup(string relative)
    {
        if (!relative.StartsWith("backups/", StringComparison.Ordinal)) throw new InvalidDataException("Đường dẫn backup không hợp lệ.");
        return ResolveSafe(relative);
    }

    private string ResolveSafe(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Đường dẫn vượt phạm vi kho.");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn vượt phạm vi kho.");
        for (var check = full; check is not null && check.Length >= Root.Length; check = Path.GetDirectoryName(check)) RejectReparse(check);
        return full;
    }

    public static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Không dùng liên kết thư mục/tệp làm kho dữ liệu: " + path);
    }

    public static void ValidateDestination(string source, string target)
    {
        var a = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        var b = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || (a + Path.DirectorySeparatorChar).StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (b + Path.DirectorySeparatorChar).StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Không chuyển vào chính kho đang dùng hoặc thư mục lồng nhau.");
        for (var path = b; path is not null; path = Path.GetDirectoryName(path)) RejectReparse(path);
    }

    // Retained for compatibility with old imports/tests. Schema 5 uses the GUID-only
    // file name above so renaming a project no longer becomes delete + create on NAS.
    public static string SafeName(string name, Guid id)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var value = new string(name.Normalize(NormalizationForm.FormC).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (value.Length > 72) value = value[..72].TrimEnd();
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        if (string.IsNullOrWhiteSpace(value)) value = "Du an";
        return value + "--" + id.ToString("N");
    }

    public static void ValidateState(SheetState state)
    {
        if (state.Notes is null || state.Notes.Any(n => n.Projects is null || n.Content is null)) throw new InvalidDataException("Dữ liệu ghi chú không đầy đủ.");
        var ids = new HashSet<Guid>();
        void ValidateConversations(List<AiConversation> conversations)
        {
            if (conversations is null) throw new InvalidDataException("Thiếu lịch sử AI.");
            foreach (var c in conversations)
            {
                if (c.Id == Guid.Empty || !ids.Add(c.Id) || c.Messages is null) throw new InvalidDataException("Hội thoại không hợp lệ.");
                foreach (var m in c.Messages) if (m.Id == Guid.Empty || !ids.Add(m.Id)) throw new InvalidDataException("ID tin nhắn bị trùng.");
                if (c.DraftAttachments is null || c.Messages.Any(m => m.Attachments is null || m.SavedFiles is null)) throw new InvalidDataException("Thiếu danh sách đính kèm.");
                foreach (var a in c.DraftAttachments.Concat(c.Messages.SelectMany(m => m.Attachments)))
                    if (a is null || a.Id == Guid.Empty || !ids.Add(a.Id) || a.Data is null || a.Text is null || a.Name is null || a.MimeType is null || a.Notice is null || a.Data.Length > AiDocuments.MaxFileBytes)
                        throw new InvalidDataException("Tệp đính kèm không hợp lệ hoặc quá lớn.");
            }
        }
        foreach (var n in state.Notes)
        {
            if (n.Id == Guid.Empty || !ids.Add(n.Id)) throw new InvalidDataException("ID ghi chú bị trùng hoặc rỗng.");
            ValidateConversations(n.AiConversations);
            foreach (var p in n.Projects)
            {
                if (p.Id == Guid.Empty || !ids.Add(p.Id) || p.ChecklistItems is null || p.Conversations is null || p.Links is null || p.Layout is null)
                    throw new InvalidDataException("Dữ liệu dự án hoặc ID không hợp lệ.");
                foreach (var t in p.ChecklistItems) if (t.Id == Guid.Empty || !ids.Add(t.Id)) throw new InvalidDataException("ID công việc bị trùng.");
                ValidateConversations(p.Conversations);
            }
        }
    }

    public static T Clone<T>(T value)
    {
        if (value is null) return value!;
        return Decode<T>(Encode(value));
    }

    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("File JSON rỗng.");
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void AtomicWrite(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class WorkspaceIndex
    {
        public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion;
        public SheetState State { get; set; } = new();
        public List<FileEntry> Files { get; set; } = [];
    }

    private sealed record FileEntry(Guid Id, Guid? BoardId, string Kind, string File, string Hash);
    private sealed class ProjectFile { public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion; public Guid BoardId { get; set; } public ProjectRecord Project { get; set; } = null!; }
    private sealed class GeneralNoteFile { public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion; public NoteRecord Note { get; set; } = null!; }
    private sealed class SaveJournal { public List<JournalEntry> Entries { get; set; } = []; }
    private sealed record JournalEntry(string File, bool Existed, string Backup);
    private sealed record WritePackage(Dictionary<string, byte[]?> Data, WorkspaceIndex Index);
    private sealed record WorkspaceSnapshot(SheetState State, Dictionary<string, string> Fingerprints, Dictionary<Guid, FileEntry> Entries, int SchemaVersion)
    {
        public static WorkspaceSnapshot Empty() => new(new SheetState(), new(StringComparer.OrdinalIgnoreCase), [], ProjectWorkspaceStore.SchemaVersion);
    }
}
