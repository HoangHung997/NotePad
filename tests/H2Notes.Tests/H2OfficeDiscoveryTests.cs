using System.Runtime.InteropServices;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>E1 catalog/probe seam and E2 concrete production capture/UI projection. No native
/// Office is substituted as passed. Controlled native-object leases never touch user documents.</summary>
internal static class H2OfficeDiscoveryTests
{
    public static void Run(Action<string,Action> test)
    {
        H2OfficeEnumerationFailureTests.Run(test);
        test("AR-020 discovery distinguishes two processes with near-identical names",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");p.Add(12,102,2001,"A.xlsx");
            using var b=new ComOfficeBackend(p);var d=b.DiscoverExcel();
            Check(d.Workbooks.Count==2 && d.Workbooks.Select(x=>x.SessionId).Distinct().Count()==2,"Processes collapsed.");
            Check(d.Workbooks.All(x=>x.NativeIdentity is {ProcessStartUtcTicks: >0,ViewWindowHandle: >0}),"Native identity missing.");
        });
        test("AR-020 two views share document identity but never share selection identity",()=>{
            var p=new Probe();var doc=new object();p.Add(11,101,1001,"A.xlsx",doc);p.Add(11,101,1002,"A.xlsx",doc);
            using var b=new ComOfficeBackend(p);var d=b.DiscoverExcel();
            Check(d.Workbooks.Select(x=>x.NativeIdentity!.DocumentId).Distinct().Count()==1,"Views became two documents.");
            Check(d.Workbooks.Select(x=>x.NativeIdentity!.ViewIdentity).Distinct().Count()==2,"Two views collapsed.");
            Check(d.Workbooks.Select(x=>x.SessionId).Distinct().Count()==2,"View sessions collapsed.");
        });
        test("AR-020 discovery is metadata only and never reads any selection or document body",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");
            using var b=new ComOfficeBackend(p);var d=b.DiscoverExcel();
            Check(p.SelectionReads==0 && d.Workbooks[0].StateToken=="" && d.Workbooks[0].SelectionAddress=="","Discovery read content or forged a token.");
            Check(d.Report is {Complete:true,ViewsProbed:1},"Missing coverage metrics.");
        });
        test("AR-020 targeted capture uses frozen window even when foreground changed",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");p.Add(12,102,2001,"B.xlsx");p.ForegroundRoot=2001;
            using var b=new ComOfficeBackend(p);var c=b.Capture(new("excel",1001,11,101));
            Check(c.Status=="Ready" && c.FullName=="A.xlsx" && c.Selection=="Sheet1!A1","Capture followed foreground.");
            Check(p.Opens.SequenceEqual([1001L,1001L]) && p.SelectionReads==1,"Capture probed unrelated documents.");
        });
        test("AR-020 wrong PID or process start cannot recover a captured view",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");using var b=new ComOfficeBackend(p);
            Check(b.Capture(new("excel",1001,99,101)).Code=="stale_resource","Wrong PID accepted.");
            Check(b.Capture(new("excel",1001,11,102)).Code=="stale_resource","Reused PID accepted.");
            Check(p.SelectionReads==0,"Invalid capture read selection.");
        });
        test("AR-020 capture rejects a document switched while selection was being read",()=>{
            var p=new Probe();var item=p.Add(11,101,1001,"A.xlsx");
            item.OnSelection=()=>item.Document=new object();using var b=new ComOfficeBackend(p);
            var result=b.Capture(new("excel",1001,11,101));
            Check(result.Code=="stale_resource" && result.SessionId is null,"Mixed document/selection snapshot escaped revalidation.");
        });
        test("AR-020 post-effect native identity failure never claims no effect",()=>{
            var p=new Probe();var item=p.Add(11,101,1001,"A.xlsx");using var c=new OfficeWindowCatalog(p);
            var bound=c.Refresh("excel").Single();item.Document=new object();
            try { c.ValidateCurrent(bound,false);throw new InvalidOperationException("Expected stale native identity"); }
            catch(OfficeHostFaultException e){Check(e.Code=="stale_resource" && !e.NoEffect,"Post-effect stale result became no-effect proof.");}
            Fault(()=>c.ValidateCurrent(bound,true),"stale_resource");
        });
        test("AR-020 Save As retires old session before any body read or mutation",()=>{
            var p=new Probe();var v=p.Add(11,101,1001,"A.xlsx");using var b=new ComOfficeBackend(p);var id=b.DiscoverExcel().Workbooks[0].SessionId;
            v.Name="B.xlsx";Fault(()=>b.SnapshotExcel(id),"stale_resource");
            var next=b.DiscoverExcel().Workbooks.Single();Check(next.SessionId!=id && next.Name=="B.xlsx","Save As silently reused old binding.");
        });
        test("AR-020 replacement document under same HWND and path invalidates old session",()=>{
            var p=new Probe();var v=p.Add(11,101,1001,"A.xlsx");using var b=new ComOfficeBackend(p);var id=b.DiscoverExcel().Workbooks[0].SessionId;
            v.Document=new object();Fault(()=>b.SnapshotExcel(id),"stale_resource");
            Check(b.DiscoverExcel().Workbooks.Single().SessionId!=id,"Reopened lookalike resurrected a handle.");
        });
        test("AR-020 close and reopen cannot resurrect a retired session",()=>{
            var p=new Probe();var v=p.Add(11,101,1001,"A.xlsx");using var b=new ComOfficeBackend(p);var id=b.DiscoverExcel().Workbooks[0].SessionId;
            p.Views.Clear();Check(b.DiscoverExcel().Workbooks.Count==0,"Closed view remained discoverable.");p.Views.Add(v);
            Check(b.DiscoverExcel().Workbooks.Single().SessionId!=id,"Retired session resurrected.");Fault(()=>b.SnapshotExcel(id),"stale_resource");
        });
        test("AR-020 repeated metadata probes preserve live session without caching stale results",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");using var b=new ComOfficeBackend(p);
            var first=b.DiscoverExcel().Workbooks.Single();var second=b.DiscoverExcel().Workbooks.Single();
            Check(first.SessionId==second.SessionId && first.NativeIdentity==second.NativeIdentity && p.Opens.Count==2,"Live identity drifted or no fresh probe.");
            Check(p.Releases==1,"Superseded native references leaked.");
        });
        test("AR-020 per-window busy issue retains other metadata but never claims a complete catalog",()=>{
            var p=new Probe();p.Add(11,101,1001,"A.xlsx");p.Add(12,102,2001,"B.xlsx").Fault="modal_blocked";
            using var b=new ComOfficeBackend(p);var d=b.DiscoverExcel();
            Check(d.Workbooks.Count==1 && d.Report is {Complete:false} && d.Report.Issues.Single().Code=="modal_blocked","Busy view disappeared as successful complete discovery.");
            Check(b.Capture(new("excel",2001,12,102)).Code=="modal_blocked","Typed capture failure lost.");
        });
        test("AR-020 view cap is explicit and all owned probe leases are released",()=>{
            var p=new Probe();for(var i=1;i<=OfficeDiscoveryLimits.MaxViews+1;i++)p.Add(11,101,1000+i,"A"+i+".xlsx");
            using(var b=new ComOfficeBackend(p)){var d=b.DiscoverExcel();Check(d.Workbooks.Count==OfficeDiscoveryLimits.MaxViews && d.Report is {Complete:false},"Unbounded/complete capped catalog.");}
            Check(p.Releases==p.Opens.Count,"Native probe ownership leak.");
        });
        test("AR-020 retained view cap spans targeted roots and both Office kinds without evicting live identity",()=>{
            var p=new Probe();
            using(var catalog=new OfficeWindowCatalog(p))
            {
                OfficeViewLease? last=null; Probe.Item? lastItem=null;
                for(var i=1;i<=OfficeDiscoveryLimits.MaxRetainedViews;i++)
                {
                    p.Views.Clear();
                    lastItem=p.Add(11,101,1000+i,"Document"+i,application:i%2==0?"word":"excel");
                    last=catalog.Refresh(lastItem.Candidate.Application,lastItem.Candidate.RootHandle).Single();
                }
                var lastId=last!.SessionId;
                // Replacing an existing root at capacity must not count its old lease twice.
                last=catalog.Refresh("word",lastItem!.Candidate.RootHandle).Single();
                Check(last.SessionId==lastId,"Replacing a live root exhausted the budget or changed its session.");
                var overflow=p.Add(12,102,9000,"Overflow",application:"word");
                Fault(()=>catalog.Refresh("word",overflow.Candidate.RootHandle),"session_capacity");
                Check(catalog.LastReport is {Complete:false} && catalog.LastReport.Issues.Single().Code=="session_capacity",
                    "Capacity rejection left a complete discovery report.");
                catalog.ValidateCurrent(last,true);
                Check(p.Opens.Count-p.Releases==OfficeDiscoveryLimits.MaxRetainedViews,"Rejected probe leaked a lease or evicted an accepted binding.");
                // Explicit full refresh, not quota eviction, retires the closed old roots.
                p.Views.Clear();p.Views.Add(lastItem);
                Check(catalog.Refresh("excel").Count==0,"Full refresh retained closed Excel roots.");
                Check(catalog.Refresh("word").Single().SessionId==lastId,"Reclaiming closed views changed the surviving session.");
                p.Views.Add(overflow);
                Check(catalog.Refresh("word",overflow.Candidate.RootHandle).Single().Name=="Overflow","Quota never recovered after safe full refresh.");
            }
            Check(p.Releases==p.Opens.Count && p.SelectionReads==0,"Catalog quota control leaked leases or read content.");
        });
        test("AR-020 native COM failure classes distinguish busy and stale",()=>{
            Check(OfficeNativeWindowProbe.FaultCode(new COMException("sensitive",unchecked((int)0x8001010A)))=="provider_busy","Busy flattened.");
            Check(OfficeNativeWindowProbe.FaultCode(new COMException("sensitive",unchecked((int)0x80010108)))=="stale_resource","Disconnected object accepted.");
            Check(OfficeNativeWindowProbe.FaultCode(new COMException("sensitive",unchecked((int)0x80004002)))=="native_object_unavailable","Unsupported object overclaimed.");
        });
        test("AR-020 Word capture contains range positions not selected private plaintext",()=>{
            var p=new Probe();p.Add(11,101,1001,"Document1",application:"word");using var b=new ComOfficeBackend(p);
            var c=b.Capture(new("word",1001,11,101));Check(c.Selection=="word-range:3:9" && c.FullName=="Document1","Word capture lost unsaved/range identity.");
        });
        test("AR-020 RPC no-effect proof survives additive protocol serialization",()=>{
            var response=new OfficeRpcResponse("call",false,null,new("stale_resource","No dispatch"){NoEffect=true});
            var roundtrip=JsonSerializer.Deserialize<OfficeRpcResponse>(JsonSerializer.Serialize(response))!;
            Check(roundtrip.Error!.NoEffect && roundtrip.Id=="call","No-effect proof lost.");
            var old=JsonSerializer.Deserialize<OfficeRpcResponse>("{\"Id\":\"old\",\"Ok\":false,\"Error\":{\"Code\":\"busy\",\"Message\":\"busy\"}}")!;
            Check(!old.Error!.NoEffect,"Legacy error was invented as preflight.");
        });
        test("AR-020 opt-in marker aid rejects unconsented native access before probing",()=>{
            try {OfficeNativeMarkerProbe.Run(["--native-catalog","not-used.json"]);throw new InvalidOperationException("Consent was bypassed.");}
            catch(ArgumentException e){Check(e.Message.Contains("allow-native-office"),"Wrong preflight rejection.");}
        });
        foreach(var kind in new[]{"excel","word"})
            test("AR-020 marker aid requires exact process view and observed body for "+kind,()=>{
                var b=new MarkerBackend();var c=new OfficeMarkerCase("FIXTURE",kind,11,101,1001,1001,"Document1","DOC-A");
                var good=OfficeNativeMarkerProbe.Observe(b,c);
                Check(good.Passed && good.Identity==b.Identity && b.BodyReads==1,"Exact marker fixture was not read.");
                var wrong=OfficeNativeMarkerProbe.Observe(b,c with {ProcessStartUtcTicks=102});
                Check(!wrong.Passed && wrong.Code=="stale_resource" && b.BodyReads==1,"Wrong identity read a body.");
                b.Text="DOC-B";
                Check(!OfficeNativeMarkerProbe.Observe(b,c).Passed,"Missing marker was accepted.");
                b.WrongReadback=true;
                Check(OfficeNativeMarkerProbe.Observe(b,c).Code=="stale_resource","Wrong-view readback certified a marker.");
                b.Complete=false;var reads=b.BodyReads;
                Check(OfficeNativeMarkerProbe.Observe(b,c).Code=="incomplete_discovery" && b.BodyReads==reads,"Partial catalog read a guessed document.");
            });
        test("AR-020 production capture reuses owned helper but not stale capture output",()=>Temp(root=>{
            var client=new CaptureClient();var created=0;
            var adapter=new H2ProductionAgentAdapter(root,()=>new(new AiProfile(),""),officeClientFactory:()=>{created++;return client;});
            var a=adapter.CaptureActiveWorkContext(Context());client.Session="changed";var b=adapter.CaptureActiveWorkContext(Context());
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Check(created==1 && client.Captures==2 && a!.DocumentSessionId!=b!.DocumentSessionId && client.Disposed,"Capture cache stale or helper leaked.");
        }));
        test("AR-020 production capture failure preserves foreground and typed status in actual UI projection",()=>Temp(root=>{
            var client=new CaptureClient{Error="modal_blocked"};using var a=new H2ProductionAgentAdapter(root,()=>new(new AiProfile(),""),officeClientFactory:()=>client);
            var capture=new WorkAssistantActiveContextCapture(new WindowBackend(),()=>a);var c=capture.Capture()!;
            Check(c.ProcessId==11 && c.NativeWindowHandle==1001 && c.WindowTitle=="Synthetic Office" && c.DocumentSessionId is null,"Failure erased or invented the known window.");
            Check(c.EnrichmentErrorCode=="modal_blocked" && c.ToBoundedSummary().Contains("modal_blocked"),"UI lost diagnostic reason.");
        }));
        test("AR-020 production capture rejects a response for another native process",()=>Temp(root=>{
            var client=new CaptureClient{WrongProcess=true};using var a=new H2ProductionAgentAdapter(root,()=>new(new AiProfile(),""),officeClientFactory:()=>client);
            var c=a.CaptureActiveWorkContext(Context());Check(c!.ErrorCode=="stale_resource" && c.DocumentSessionId is null,"Wrong-process response became active target.");
        }));
        test("AR-020 exhausted capture cache retires only its helper without retrying old identity",()=>Temp(root=>{
            var exhausted=new CaptureClient{Error="session_capacity"};var fresh=new CaptureClient{Session="new-helper-session"};var created=0;
            using var adapter=new H2ProductionAgentAdapter(root,()=>new(new AiProfile(),""),officeClientFactory:()=>++created==1?exhausted:fresh);
            var first=adapter.CaptureActiveWorkContext(Context());
            Check(first!.ErrorCode=="session_capacity" && exhausted.Disposed && created==1 && exhausted.Captures==1,
                "Capacity error leaked its owned helper or retried automatically.");
            var second=adapter.CaptureActiveWorkContext(Context());
            Check(created==2 && second!.DocumentSessionId=="new-helper-session" && fresh.Captures==1,
                "A later explicit capture reused an exhausted helper or old session.");
        }));
        test("AR-020 capture timeout retires only the owned helper and reports deadline",()=>Temp(root=>{
            var client=new CaptureClient{Timeout=true};using var a=new H2ProductionAgentAdapter(root,()=>new(new AiProfile(),""),officeClientFactory:()=>client);
            var c=a.CaptureActiveWorkContext(Context());Check(c!.ErrorCode=="deadline_exceeded" && client.Disposed,"Deadline became silent null or leaked helper.");
        }));
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Fault(Action action,string code)
    {try{action();throw new InvalidOperationException("Expected preflight "+code);}catch(OfficeHostFaultException e){Check(e.Code==code && e.NoEffect,"Incorrect no-effect classification.");}}
    private static void Temp(Action<string> action)
    {var root=Path.Combine(Path.GetTempPath(),"H2-AR020-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{action(root);}finally{Directory.Delete(root,true);}}
    private static H2ActiveWorkContext Context()=>new(11,101,"EXCEL",H2ApplicationKind.Excel,1001,"win32:3e9:11:101","Synthetic Office",null,null,null,null,DateTime.UtcNow);
    private sealed class WindowBackend:IWorkAssistantWindowContextBackend
    {public WorkAssistantWindowSnapshot? CaptureForeground()=>new(1001,11,101,"EXCEL","Synthetic Office");public WorkAssistantWindowSnapshot? InspectWindow(long handle)=>CaptureForeground();}
    private sealed class Probe:IOfficeWindowProbe
    {
        public sealed class Item {public required OfficeWindowCandidate Candidate;public required string Name;public object Document=new();public string? Fault;public Action? OnSelection;}
        public List<Item> Views=[];public List<long> Opens=[];public int Releases,SelectionReads;
        public long ForegroundRoot{get;set;}
        public Item Add(int pid,long start,long hwnd,string name,object? document=null,string application="excel")
        {var item=new Item{Candidate=new(application,pid,start,1,hwnd,hwnd+10000),Name=name,Document=document??new object()};Views.Add(item);return item;}
        public OfficeWindowScan Enumerate(string application,long? rootHandle=null)=>new(Views.Where(x=>x.Candidate.Application==application && (rootHandle is null||x.Candidate.RootHandle==rootHandle)).Select(x=>x.Candidate).ToArray(),[],true,Views.Count);
        public OfficeViewLease Open(OfficeWindowCandidate candidate)
        {
            Opens.Add(candidate.RootHandle);var i=Views.First(x=>x.Candidate==candidate);
            if(i.Fault is{} code)throw new OfficeHostFaultException(code,"Controlled failure",true);
            return new(candidate,candidate.RootHandle,new object(),i.Document,new object(),i.Name,i.Name,false,"FIXTURE-NOT-OFFICE",
                ()=>{SelectionReads++;i.OnSelection?.Invoke();return candidate.Application=="word"?"word-range:3:9":"Sheet1!A1";},()=>Releases++);
        }
    }
    // Test-only seam for the manual native marker report. These tests are never E3 evidence.
    private sealed class MarkerBackend:IOfficeBackend
    {
        public OfficeNativeIdentity Identity=new(11,101,1,1001,1001,11001,"controlled-document","FIXTURE");
        public bool Complete=true,WrongReadback;public int BodyReads;public string Text="DOC-A";
        private OfficeDiscoveryReport Report=>new(Complete,"CONTROLLED_FIXTURE",0,1,1,[]);
        public ExcelDiscovery DiscoverExcel()=>new([new("session","Document1","Document1",false,"","",""){NativeIdentity=Identity}],null){Report=Report};
        public WordDiscovery DiscoverWord()=>new([new("session","Document1","Document1",false,0,0,"",""){NativeIdentity=Identity}],null){Report=Report};
        public ExcelLiveSnapshot SnapshotExcel(string id)
        { BodyReads++;return new(id,"Document1","Document1",false,"Sheet1","A1",
            [new("Sheet1","Visible",[new("A1",Text,"",false,false,null,"General","left","top")],[],[],[])],"state")
            {NativeIdentity=WrongReadback?Identity with{ViewWindowHandle=2001}:Identity}; }
        public WordLiveSnapshot SnapshotWord(string id)
        { BodyReads++;return new(id,"Document1","Document1",false,0,0,"",[new(0,Text,"Normal",[])],[],[],[],[],"state")
            {NativeIdentity=WrongReadback?Identity with{ViewWindowHandle=2001}:Identity}; }
        public ExcelPatchResult PatchExcel(ExcelPatchRequest r)=>throw new InvalidOperationException("Marker probe must not mutate.");
        public ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest r)=>throw new InvalidOperationException("Marker probe must not mutate.");
        public OfficeSaveCopyResult SaveExcelCopy(OfficeSaveCopyRequest r)=>throw new InvalidOperationException("Marker probe must not mutate.");
        public WordPatchResult PatchWord(WordPatchRequest r)=>throw new InvalidOperationException("Marker probe must not mutate.");
        public WordLanguageEvidenceResult InspectWordLanguage(WordLanguageEvidenceRequest r)=>throw new InvalidOperationException("Marker probe must not inspect language.");
        public OfficeSaveCopyResult SaveWordCopy(OfficeSaveCopyRequest r)=>throw new InvalidOperationException("Marker probe must not mutate.");
    }
    private sealed class CaptureClient:IOfficeSessionClient,IOfficeCaptureClient
    {
        public string InstanceIdentity=>"controlled-capture";
        public string Session="captured";public string? Error;public bool WrongProcess,Timeout,Disposed;public int Captures;
        public Task<OfficeCaptureResult> CaptureAsync(OfficeCaptureRequest r,CancellationToken ct=default)
        {Captures++;if(Timeout)throw new TimeoutException("controlled");return Task.FromResult(Error is{} error
            ? new OfficeCaptureResult("Unavailable",error,null,null,null,null,4)
            : new OfficeCaptureResult("Ready",null,Session,"Book1","Sheet1!A1",new(WrongProcess?99:r.ProcessId,r.ProcessStartUtcTicks,1,r.WindowHandle,r.WindowHandle,11001,"doc","FIXTURE"),4));}
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken ct=default)=>throw new InvalidOperationException("Capture must not enumerate every workbook.");
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken ct=default)=>throw new InvalidOperationException("Capture must not enumerate every document.");
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string id,CancellationToken ct=default)=>throw new InvalidOperationException("Capture must not snapshot document bodies.");
        public Task<WordLiveSnapshot> SnapshotWordAsync(string id,CancellationToken ct=default)=>throw new InvalidOperationException("Capture must not snapshot document bodies.");
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public void Dispose()=>Disposed=true;
    }
}