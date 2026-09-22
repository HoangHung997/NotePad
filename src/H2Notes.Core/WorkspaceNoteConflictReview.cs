using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

public sealed record WorkspaceNoteConflictReview(string Id, Guid ProjectId, string Title, RichDocument Local, RichDocument Remote, string ExpectedNotes)
{
    public const string ResolvedKey = "H2ResolvedNoteConflicts";
    public static WorkspaceNoteConflictReview? Create(WorkspaceMergeConflict conflict, ProjectRecord project)
    {
        var parts=conflict.Path.Split('/');
        if(parts.Length!=3 || parts[0]!="project" || !Guid.TryParse(parts[1],out var id) || id!=project.Id || parts[2] is not ("notes" or "notes-rich"))return null;
        RichDocument Read(string json)=>parts[2]=="notes-rich" ? JsonSerializer.Deserialize<RichDocument>(json) ?? RichDocument.Plain("") : RichDocument.FromLegacy(JsonSerializer.Deserialize<string>(json)??"");
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflict.Path+conflict.CreatedAtUtc.ToString("O")+conflict.LocalJson+conflict.RemoteJson)));
        if(project.Extra?.TryGetValue(ResolvedKey,out var resolved)==true && resolved.ValueKind==JsonValueKind.Array && resolved.EnumerateArray().Any(v=>v.GetString()==key))return null;
        return new(key,id,project.DisplayName,Read(conflict.LocalJson),Read(conflict.RemoteJson),Snapshot(project));
    }
    public static string Snapshot(ProjectRecord project)=>JsonSerializer.Serialize(project.ReadNotes());
    public bool Apply(ProjectRecord project,ConflictResolution choice)
    {
        if(!Enum.IsDefined(choice) || project.Id!=ProjectId || Snapshot(project)!=ExpectedNotes)return false;
        if(project.Extra?.TryGetValue(ResolvedKey,out var done)==true && done.ValueKind==JsonValueKind.Array && done.EnumerateArray().Any(v=>v.ValueKind==JsonValueKind.String && v.GetString()==Id))return false;
        var result=choice==ConflictResolution.KeepDestination ? Remote.Clone() : Local.Clone();
        if(choice==ConflictResolution.KeepBoth)
        {
            result.Runs.Add(new("\n\n— Bản từ máy khác —\n",new()));result.Runs.AddRange(Remote.Clone().Runs);
        }
        project.NotesRich=result;project.Notes=result.Text;project.UpdatedAtUtc=DateTime.UtcNow;
        project.Extra??=[];
        var resolved=project.Extra.TryGetValue(ResolvedKey,out var existing) && existing.ValueKind==JsonValueKind.Array
            ? existing.EnumerateArray().Select(v=>v.GetString()!).Where(v=>v is not null).ToList() : new List<string>();
        if(!resolved.Contains(Id))resolved.Add(Id);
        project.Extra[ResolvedKey]=JsonSerializer.SerializeToElement(resolved);return true;
    }
}
