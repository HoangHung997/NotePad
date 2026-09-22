using System.Text.Json;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class App
{
    public IReadOnlyList<WorkspaceNoteConflictReview> GetNoteConflictReviews()
    {
        if(_storage is not ProjectWorkspaceStore store)return [];
        var all=store.LastMergeConflicts.ToList();var folder=Path.Combine(store.Root,"conflicts");
        if(Directory.Exists(folder))
            foreach(var path in Directory.EnumerateFiles(folder,"*.json").OrderByDescending(p=>p).Take(100))
                try { if(new FileInfo(path).Length<=4*1024*1024)all.AddRange(JsonSerializer.Deserialize<List<WorkspaceMergeConflict>>(File.ReadAllText(path))??[]); }
                catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { }
        var projects=State.Notes.SelectMany(n=>n.Projects).ToArray();var result=new List<WorkspaceNoteConflictReview>();
        foreach(var conflict in all)
            foreach(var project in projects)
                try { if(WorkspaceNoteConflictReview.Create(conflict,project) is {} item)result.Add(item); }
                catch(JsonException) { }
        return result.DistinctBy(r=>r.Id).OrderBy(r=>r.Title).ToArray();
    }
    public bool ResolveNoteConflict(WorkspaceNoteConflictReview review,ConflictResolution choice)
    {
        if(_saving || !_storageReady || _restoring || IsExiting)return false;
        _main?.FlushNotes();
        var project=State.Notes.SelectMany(n=>n.Projects).FirstOrDefault(p=>p.Id==review.ProjectId);
        if(project is null || !review.Apply(project,choice))return false;
        MarkProjectDirty(project.Id);_main?.RefreshAfterExternalSync();SaveNow();
        return LastSaveError is null;
    }
}
