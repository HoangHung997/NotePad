using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace H2AgentLab.OfficeProtocol;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExcelPatchMutationStatus { Applied, Failed, PartiallyApplied, OutcomeUnknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExcelPatchMutationEffect { None, Applied, PartiallyApplied, Unknown }

public sealed record ExcelPatchOperationIdentity(string LogicalOperationId,string BatchId,string ChunkId,int ChunkIndex,int ChunkCount);

public static class ExcelPatchMutationRules
{
    public static IReadOnlyList<ExcelCellPatch> ValidateAndNormalize(IReadOnlyList<ExcelCellPatch>? cells)
    {
        var countError=ExcelPatchLimits.ValidationError(cells?.Count??-1);
        if(cells is null||countError is not null)throw new ArgumentException(countError??"Excel patch cells are required.",nameof(cells));
        var result=new List<ExcelCellPatch>(cells.Count);var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var patch in cells)
        {
            if(patch is null)throw new ArgumentException("Excel patch contains a null cell.",nameof(cells));
            ExcelRangeBounds bounds;
            try{bounds=ExcelRangeReadRules.ParseRange(patch.Address);}
            catch(Exception ex)when(ex is ArgumentException or ArgumentOutOfRangeException or OverflowException)
            {throw new ArgumentException("Excel patch contains an invalid single-cell address.",nameof(cells),ex);}
            if(bounds.CellCount!=1)throw new ArgumentException("Excel patch addresses must identify exactly one cell.",nameof(cells));
            var address=bounds.Address;
            if(!seen.Add(address))throw new ArgumentException($"Excel patch contains duplicate target '{address}'.",nameof(cells));
            if(patch.Value is not null&&patch.Formula is not null)throw new ArgumentException($"Excel patch '{address}' cannot set both value and formula.",nameof(cells));
            if(patch.ClearValue&&(patch.Value is not null||patch.Formula is not null))throw new ArgumentException($"Excel patch '{address}' cannot clear and set content in one operation.",nameof(cells));
            if(!patch.ClearValue&&patch.Value is null&&patch.Formula is null&&patch.Bold is null&&patch.Italic is null&&patch.FillColor is null&&patch.NumberFormat is null)
                throw new ArgumentException($"Excel patch '{address}' has no requested change.",nameof(cells));
            result.Add(patch with{Address=address});
        }
        return result;
    }
    public static ExcelPatchOperationIdentity Identity(ExcelPatchRequest request,IReadOnlyList<ExcelCellPatch> normalized)
    {
        var fingerprint=Hash(JsonSerializer.Serialize(new{request.SessionId,request.SheetName,contentToken=request.ContentToken??request.StateToken,cells=normalized}));
        var logical=Safe(request.LogicalOperationId,"op-"+fingerprint);var batch=Safe(request.BatchId,"batch-"+fingerprint);
        var index=request.ChunkIndex<0?0:request.ChunkIndex;var count=request.ChunkCount<1?1:request.ChunkCount;
        if(index>=count)throw new ArgumentException("Excel patch chunk index must be within chunk count.",nameof(request));
        return new(logical,batch,Safe(request.ChunkId,$"chunk-{fingerprint}-{index+1}-of-{count}"),index,count);
    }
    public static string ContentToken(ExcelLiveSnapshot snapshot)=>!string.IsNullOrWhiteSpace(snapshot.ContentToken)?snapshot.ContentToken:snapshot.StateToken;
    public static ExcelPatchResult Classify(ExcelPatchRequest request,ExcelLiveSnapshot before,ExcelLiveSnapshot? after,
        IReadOnlyList<ExcelCellPatch> normalized,string? errorCode=null,string? safeMessage=null)
    {
        var id=Identity(request,normalized);var bs=before.Sheets.SingleOrDefault(s=>s.Name==request.SheetName);
        var @as=after?.Sheets.SingleOrDefault(s=>s.Name==request.SheetName);var applied=new List<string>();var unapplied=new List<string>();
        var unknown=new List<string>();var changed=new List<string>();
        foreach(var patch in normalized)
        {
            var prior=bs?.Cells.SingleOrDefault(c=>c.Address==patch.Address);var current=@as?.Cells.SingleOrDefault(c=>c.Address==patch.Address);
            if(after is null||prior is null||current is null){unknown.Add(patch.Address);continue;}
            if(!SameCell(prior,current))changed.Add(patch.Address);
            if(MatchesRequested(patch,current))applied.Add(patch.Address);else if(SameCell(prior,current))unapplied.Add(patch.Address);else unknown.Add(patch.Address);
        }
        ExcelPatchMutationStatus status;ExcelPatchMutationEffect effect;
        if(unknown.Count>0){status=ExcelPatchMutationStatus.OutcomeUnknown;effect=ExcelPatchMutationEffect.Unknown;errorCode??="outcome_unknown";safeMessage??="Excel state could not be reconciled after dispatch. Read the same target before retrying.";}
        else if(applied.Count==normalized.Count){status=ExcelPatchMutationStatus.Applied;effect=ExcelPatchMutationEffect.Applied;errorCode=null;safeMessage=null;}
        else if(applied.Count>0){status=ExcelPatchMutationStatus.PartiallyApplied;effect=ExcelPatchMutationEffect.PartiallyApplied;errorCode??="partially_applied";safeMessage??="Only part of the Excel batch was applied. Repair only cells proven unapplied.";}
        else{status=ExcelPatchMutationStatus.Failed;effect=ExcelPatchMutationEffect.None;errorCode??="excel_patch_failed";safeMessage??="Excel did not apply the requested batch.";}
        return new ExcelPatchResult(before,after??before,changed.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray()){
            LogicalOperationId=id.LogicalOperationId,BatchId=id.BatchId,ChunkId=id.ChunkId,ChunkIndex=id.ChunkIndex,ChunkCount=id.ChunkCount,
            MutationStatus=status,MutationEffect=effect,ReadbackComplete=after is not null&&unknown.Count==0,ErrorCode=errorCode,ErrorMessage=safeMessage,
            AppliedCells=applied.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),UnappliedCells=unapplied.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            UnknownCells=unknown.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),ContentTokenBefore=ContentToken(before),ContentTokenAfter=after is null?null:ContentToken(after)};
    }
    public static IReadOnlyList<ExcelCellPatch> RepairCandidates(ExcelPatchResult result,IReadOnlyList<ExcelCellPatch> original)
    {
        var normalized=ValidateAndNormalize(original);
        if(!result.ReadbackComplete||result.UnknownCells.Count!=0||result.MutationStatus==ExcelPatchMutationStatus.OutcomeUnknown)throw new InvalidOperationException("Excel mutation must be reconciled before repair.");
        if(result.MutationStatus==ExcelPatchMutationStatus.Applied)return [];
        var pending=result.UnappliedCells.ToHashSet(StringComparer.Ordinal);return normalized.Where(p=>pending.Contains(p.Address)).ToArray();
    }
    public static bool MatchesRequested(ExcelCellPatch patch,ExcelCellState actual)
        =>(patch.Formula is null||patch.Formula==actual.Formula)&&(patch.Value is null||patch.Value==actual.Value&&string.IsNullOrEmpty(actual.Formula))
        &&(!patch.ClearValue||string.IsNullOrEmpty(actual.Value)&&string.IsNullOrEmpty(actual.Formula))
        &&(patch.Bold is null||patch.Bold==actual.Bold)&&(patch.Italic is null||patch.Italic==actual.Italic)
        &&(patch.FillColor is null||patch.FillColor==actual.FillColor)&&(patch.NumberFormat is null||patch.NumberFormat==actual.NumberFormat);
    public static bool SameCell(ExcelCellState left,ExcelCellState right)=>JsonSerializer.Serialize(left)==JsonSerializer.Serialize(right);
    private static string Safe(string? value,string fallback){if(string.IsNullOrWhiteSpace(value))return fallback;var v=value.Trim();if(v.Length>160||v.Any(char.IsControl))throw new ArgumentException("Excel operation identity is invalid.");return v;}
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
