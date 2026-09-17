using System.Text.Json;

namespace H2Notes.Core;

public enum WorkspaceTransferMode { Merge, Overwrite, UseExisting }
public enum ConflictResolution { KeepCurrent, KeepDestination, KeepBoth }
public sealed record WorkspaceConflict(Guid Id, string Title, string Kind);

public sealed class WorkspaceTransfer
{
    private readonly SheetState _source;
    private readonly SheetState _destination;
    public IReadOnlyList<WorkspaceConflict> Conflicts { get; }
    public int SourceProjects => _source.Notes.Sum(n => n.Projects.Count);
    public int DestinationProjects => _destination.Notes.Sum(n => n.Projects.Count);
    public int SourceNotes => _source.Notes.Count(n => !n.IsBoard);
    public int DestinationNotes => _destination.Notes.Count(n => !n.IsBoard);
    public bool MatchesSource(SheetState current) => Same(_source, current);

    public WorkspaceTransfer(SheetState source, SheetState destination)
    {
        ProjectWorkspaceStore.ValidateState(source); ProjectWorkspaceStore.ValidateState(destination);
        _source = ProjectWorkspaceStore.Clone(source); _destination = ProjectWorkspaceStore.Clone(destination);
        List<WorkspaceConflict> conflicts = [];
        var existingProjects = _destination.Notes.SelectMany(n => n.Projects).ToDictionary(p => p.Id);
        foreach (var board in _source.Notes.Where(n => n.IsBoard))
        {
            var other = _destination.Notes.FirstOrDefault(n => n.Id == board.Id && n.IsBoard);
            if (other is null) continue;
            var common = board.Projects.Select(p => p.Id).Intersect(other.Projects.Select(p => p.Id)).ToHashSet();
            if (board.Title != other.Title || !board.Projects.Where(p => common.Contains(p.Id)).Select(p => p.Id).SequenceEqual(other.Projects.Where(p => common.Contains(p.Id)).Select(p => p.Id)))
                conflicts.Add(new(board.Id, "Tên / thứ tự bảng: " + board.Title, "board"));
        }
        foreach (var p in _source.Notes.SelectMany(n => n.Projects))
            if (existingProjects.TryGetValue(p.Id, out var other) && !Same(p, other)) conflicts.Add(new(p.Id, p.DisplayName, "project"));
        foreach (var n in _source.Notes.Where(n => !n.IsBoard))
        {
            var other = _destination.Notes.FirstOrDefault(x => x.Id == n.Id);
            if (other is not null && !Same(n, other)) conflicts.Add(new(n.Id, n.Title, "note"));
        }
        Conflicts = conflicts;
    }

    public SheetState Prepare(WorkspaceTransferMode mode, IReadOnlyDictionary<Guid, ConflictResolution>? resolutions = null)
    {
        if (mode == WorkspaceTransferMode.UseExisting) return ProjectWorkspaceStore.Clone(_destination);
        if (mode == WorkspaceTransferMode.Overwrite) return ProjectWorkspaceStore.Clone(_source);
        foreach (var conflict in Conflicts)
            if (resolutions is null || !resolutions.ContainsKey(conflict.Id)) throw new InvalidOperationException("Chưa xử lý xung đột: " + conflict.Title);
        var result = ProjectWorkspaceStore.Clone(_destination);
        foreach (var incoming in _source.Notes)
        {
            if (!incoming.IsBoard)
            {
                var existing = result.Notes.FindIndex(n => n.Id == incoming.Id);
                if (existing < 0) result.Notes.Add(ProjectWorkspaceStore.Clone(incoming));
                else if (!Same(incoming, result.Notes[existing]))
                {
                    var choice = resolutions![incoming.Id];
                    if (choice == ConflictResolution.KeepCurrent) result.Notes[existing] = ProjectWorkspaceStore.Clone(incoming);
                    else if (choice == ConflictResolution.KeepBoth && !HasImportedCopy(result.Notes, incoming))
                    {
                        var copy = ProjectWorkspaceStore.Clone(incoming); copy.Id = Guid.NewGuid(); copy.Title += " (bản giữ lại)";
                        copy.SelectedAiConversationId = AiHistory.RenewIds(copy.AiConversations, copy.SelectedAiConversationId);
                        copy.Extra = Stamp(copy.Extra, incoming); result.Notes.Add(copy);
                    }
                }
                continue;
            }
            var board = result.Notes.FirstOrDefault(n => n.Id == incoming.Id && n.IsBoard);
            if (board is not null && Conflicts.Any(c => c.Id == incoming.Id && c.Kind == "board"))
            {
                var decision = resolutions![incoming.Id];
                if (decision == ConflictResolution.KeepBoth)
                {
                    if (!HasImportedCopy(result.Notes, incoming))
                    {
                        var copy = incoming.IndexShell(); copy.Id = Guid.NewGuid(); copy.Title += " (bản giữ lại)";
                        copy.Projects = incoming.Projects.Select(Duplicate).ToList(); copy.SelectedProjectId = copy.Projects.FirstOrDefault()?.Id;
                        copy.Extra = Stamp(copy.Extra is null ? null : new(copy.Extra), incoming); result.Notes.Add(copy);
                    }
                    continue;
                }
                if (decision == ConflictResolution.KeepCurrent)
                {
                    board.Title = incoming.Title;
                    var order = incoming.Projects.Select((p, i) => (p.Id, i)).ToDictionary(p => p.Id, p => p.i);
                    board.Projects = board.Projects.OrderBy(p => order.GetValueOrDefault(p.Id, int.MaxValue)).ToList();
                }
            }
            if (board is null)
            {
                board = ProjectWorkspaceStore.Clone(incoming); board.Projects.Clear();
                if (result.Notes.Any(n => n.Id == board.Id)) throw new InvalidDataException("ID bảng trùng với ghi chú khác loại.");
                result.Notes.Add(board);
            }
            foreach (var project in incoming.Projects)
            {
                var owner = result.Notes.FirstOrDefault(n => n.Projects.Any(p => p.Id == project.Id));
                if (owner is null) { board.Projects.Add(ProjectWorkspaceStore.Clone(project)); continue; }
                var position = owner.Projects.FindIndex(p => p.Id == project.Id);
                if (Same(project, owner.Projects[position])) continue;
                var choice = resolutions![project.Id];
                if (choice == ConflictResolution.KeepCurrent) owner.Projects[position] = ProjectWorkspaceStore.Clone(project);
                else if (choice == ConflictResolution.KeepBoth && !HasImportedProjectCopy(result, project))
                {
                    var copy = Duplicate(project); copy.Extra = Stamp(copy.Extra, project); board.Projects.Add(copy);
                }
            }
        }
        result.ImportHistory = result.ImportHistory.Concat(_source.ImportHistory).DistinctBy(r => r.Sha256).ToList();
        ProjectWorkspaceStore.ValidateState(result);
        return result;
    }

    private static string Fingerprint<T>(T value) => ProjectWorkspaceStore.Hash(ProjectWorkspaceStore.Encode(value));
    private static bool Same<T>(T a, T b) => Fingerprint(a) == Fingerprint(b);
    private static Dictionary<string, JsonElement> Stamp<T>(Dictionary<string, JsonElement>? extra, T original)
    {
        extra ??= []; extra["H2MergeSourceHash"] = JsonSerializer.SerializeToElement(Fingerprint(original)); return extra;
    }
    private static bool HasImportedCopy(IEnumerable<NoteRecord> notes, NoteRecord incoming) => notes.Any(n =>
        n.Extra?.TryGetValue("H2MergeSourceHash", out var hash) == true && hash.GetString() == Fingerprint(incoming));
    private static bool HasImportedProjectCopy(SheetState state, ProjectRecord incoming) => state.Notes.SelectMany(n => n.Projects).Any(p =>
        p.Extra?.TryGetValue("H2MergeSourceHash", out var hash) == true && hash.GetString() == Fingerprint(incoming));
    private static ProjectRecord Duplicate(ProjectRecord original)
    {
        var copy = ProjectWorkspaceStore.Clone(original);
        copy.Id = Guid.NewGuid(); copy.NameRich = copy.ReadName(); copy.NameRich.Replace(copy.NameRich.Text.Length, 0, " (bản giữ lại)");
        foreach (var task in copy.ChecklistItems) task.Id = Guid.NewGuid();
        copy.SelectedAiConversationId = AiHistory.RenewIds(copy.Conversations, copy.SelectedAiConversationId);
        copy.Links = copy.Links.Select(l => l with { Id = Guid.NewGuid() }).ToList();
        return copy;
    }
}
