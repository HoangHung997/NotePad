using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed record OfficeMarkerCase(string CaseId, string Application, int ProcessId,
    long ProcessStartUtcTicks, long RootWindowHandle, long ViewWindowHandle, string FullName, string Marker);
public sealed record OfficeMarkerObservation(string CaseId, bool Passed, string Code, string PathSha256,
    OfficeNativeIdentity? Identity, string? StateToken, long ElapsedMilliseconds);

/// <summary>Opt-in, read-only acceptance aid. It never starts/closes Office or writes a document.
/// Its report contains marker matches and hashes, not document bodies. A passing marker subset
/// is not automatic certification of the entire AR-020 E3 matrix.</summary>
public static class OfficeNativeMarkerProbe
{
    public static int Run(string[] args)
    {
        if (!args.Contains("--allow-native-office",StringComparer.Ordinal))
            throw new ArgumentException("Native probes require --allow-native-office and dedicated test documents.");
        var catalog=args.Length==3 && args[0]=="--native-catalog" && args[2]=="--allow-native-office";
        var markers=args.Length==4 && args[0]=="--native-markers" && args[3]=="--allow-native-office";
        if(!catalog && !markers) throw new ArgumentException("Unsupported native probe arguments.");
        var output=Path.GetFullPath(args[catalog?1:2]);
        if(File.Exists(output) || Directory.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output)))
            throw new ArgumentException("Output must be a new file in an existing dedicated evidence folder.");
        using var backend=new ComOfficeBackend();
        object report; bool passed;
        if(catalog)
        {
            var excel=backend.DiscoverExcel();var word=backend.DiscoverWord();
            passed=excel.Report?.Complete==true && word.Report?.Complete==true;
            report=new { Mode="NativeMetadataOnly",Utc=DateTime.UtcNow,Excel=excel,Word=word,
                NativeMarkerAcceptance="NOT_RUN",NoDocumentBodiesRead=true,NoDocumentMutations=true };
        }
        else if(markers)
        {
            var file=new FileInfo(args[1]);
            if(!file.Exists || file.Length>32_000) throw new ArgumentException("A bounded explicit test-resource manifest is required.");
            var cases=JsonSerializer.Deserialize<OfficeMarkerCase[]>(File.ReadAllText(file.FullName),new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
            if(cases is null || cases.Length is <1 or >8 || cases.Any(c=>c is null) || cases.Select(c=>c.CaseId).Distinct().Count()!=cases.Length)
                throw new ArgumentException("Provide one to eight uniquely identified explicit fixture cases.");
            var observations=cases.Select(c=>Observe(backend,c)).ToArray();passed=observations.All(c=>c.Passed);
            report=new { Mode="NativeReadOnlyMarkerSubset",Utc=DateTime.UtcNow,Results=observations,
                Overall=passed?"OBSERVED_MARKERS_MATCH":"FAILED_OR_INCOMPLETE",FullE3Gate="NOT_AWARDED_AUTOMATICALLY",
                NoDocumentMutations=true,ModelCalls=0 };
        }
        else throw new ArgumentException("Use --native-catalog <new-output.json> --allow-native-office, or --native-markers <cases.json> <new-output.json> --allow-native-office.");
        // Create-only: never overwrite an existing user artifact.
        using var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(stream,report,report.GetType(),new JsonSerializerOptions{WriteIndented=true});
        return passed?0:3;
    }
    public static OfficeMarkerObservation Observe(IOfficeBackend backend,OfficeMarkerCase c)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(c);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.FullName??""))).ToLowerInvariant();
        OfficeMarkerObservation Fail(string code)=>new(c.CaseId,false,code,hash,null,null,clock.ElapsedMilliseconds);
        if(c.CaseId is not {Length:>0 and <=64} || c.CaseId.Any(char.IsControl) || c.Application is not ("excel" or "word")
            || c.ProcessId<=0 || c.ProcessStartUtcTicks<=0 || c.RootWindowHandle<=0 || c.ViewWindowHandle<=0
            || c.FullName is not {Length:>0 and <=4096} || c.FullName.Any(char.IsControl) || c.Marker is not ("DOC-A" or "DOC-B" or "UNSAVED-ONLY"))
            return Fail("invalid_arguments");
        try
        {
            bool Match(string path,OfficeNativeIdentity? identity)=>string.Equals(path,c.FullName,StringComparison.OrdinalIgnoreCase)
                && identity is not null && identity.ProcessId==c.ProcessId && identity.ProcessStartUtcTicks==c.ProcessStartUtcTicks
                && identity.RootWindowHandle==c.RootWindowHandle && identity.ViewWindowHandle==c.ViewWindowHandle;
            if(c.Application=="excel")
            {
                var list=backend.DiscoverExcel();if(list.Report?.Complete!=true)return Fail("incomplete_discovery");
                var matches=list.Workbooks.Where(x=>Match(x.FullName,x.NativeIdentity)).ToArray();
                if(matches.Length!=1)return Fail(matches.Length>1?"ambiguous_target":"stale_resource");
                var selected=matches[0];var snapshot=backend.SnapshotExcel(selected.SessionId);
                if(snapshot.SessionId!=selected.SessionId || !Match(snapshot.FullName,snapshot.NativeIdentity)
                    || snapshot.NativeIdentity!=selected.NativeIdentity)return Fail("stale_resource");
                var hit=snapshot.Sheets.SelectMany(x=>x.Cells).Any(x=>x.Value==c.Marker);
                return new(c.CaseId,hit,hit?"marker_match":"marker_missing",hash,snapshot.NativeIdentity,snapshot.StateToken,clock.ElapsedMilliseconds);
            }
            else
            {
                var list=backend.DiscoverWord();if(list.Report?.Complete!=true)return Fail("incomplete_discovery");
                var matches=list.Documents.Where(x=>Match(x.FullName,x.NativeIdentity)).ToArray();
                if(matches.Length!=1)return Fail(matches.Length>1?"ambiguous_target":"stale_resource");
                var selected=matches[0];var snapshot=backend.SnapshotWord(selected.SessionId);
                if(snapshot.SessionId!=selected.SessionId || !Match(snapshot.FullName,snapshot.NativeIdentity)
                    || snapshot.NativeIdentity!=selected.NativeIdentity)return Fail("stale_resource");
                var hit=snapshot.Paragraphs.Any(x=>x.Text.Trim()==c.Marker);
                return new(c.CaseId,hit,hit?"marker_match":"marker_missing",hash,snapshot.NativeIdentity,snapshot.StateToken,clock.ElapsedMilliseconds);
            }
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex)) {return Fail(OfficeNativeWindowProbe.FaultCode(ex));}
    }
}
