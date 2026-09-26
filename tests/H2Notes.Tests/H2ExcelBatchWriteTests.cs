using System.Reflection;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Tools;

internal static class H2ExcelBatchWriteTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-022 E1 whole-batch validator rejects duplicate overflow conflict and no-op",()=>{
            Expect<ArgumentException>(()=>ExcelPatchMutationRules.ValidateAndNormalize([new("A1",Value:"x"),new("$A$1",Value:"y")]));
            Expect<ArgumentException>(()=>ExcelPatchMutationRules.ValidateAndNormalize([new("XFE1",Value:"x")]));
            Expect<ArgumentException>(()=>ExcelPatchMutationRules.ValidateAndNormalize([new("A1",Value:"x",Formula:"=1")]));
            Expect<ArgumentException>(()=>ExcelPatchMutationRules.ValidateAndNormalize([new("A1")]));
        });
        test("AR-022 E2 late invalid fixture cell is rejected before the first write",()=>{
            var backend=new FixtureOfficeBackend();var session=backend.DiscoverExcel().ActiveSessionId!;var before=backend.SnapshotExcel(session);
            OfficeHostFaultException? error=null;try{_=backend.PatchExcel(new(session,before.StateToken,true,"Data",
                [new("A1",Value:"PREFIX-MUST-NOT-WRITE"),new("C99",Value:"MISSING")]){ContentToken=ExcelPatchMutationRules.ContentToken(before)});}
            catch(OfficeHostFaultException ex){error=ex;}var after=backend.SnapshotExcel(session);
            Check(error?.Code=="cell_not_found"&&error.NoEffect,"Late target did not fail as no-effect preflight.");
            Check(Cell(before,"A1").Value==Cell(after,"A1").Value,"Prefix cell changed before full preflight.");
        });
        test("AR-022 E2 merged-non-anchor and protected targets fail with no effect",()=>{
            var normal=new FixtureOfficeBackend();var s=normal.DiscoverExcel().ActiveSessionId!;var before=normal.SnapshotExcel(s);
            OfficeHostFaultException? merged=null;try{_=normal.PatchExcel(new(s,before.StateToken,true,"Data",[new("B1",Value:"NO")])
                {ContentToken=ExcelPatchMutationRules.ContentToken(before)});}catch(OfficeHostFaultException ex){merged=ex;}
            Check(merged?.Code=="merged_cell_non_anchor"&&merged.NoEffect,"Merged non-anchor was not blocked.");
            var locked=new FixtureOfficeBackend(excelSheetProtected:true);s=locked.DiscoverExcel().ActiveSessionId!;before=locked.SnapshotExcel(s);
            OfficeHostFaultException? pe=null;try{_=locked.PatchExcel(new(s,before.StateToken,true,"Data",[new("A1",Value:"NO")])
                {ContentToken=ExcelPatchMutationRules.ContentToken(before)});}catch(OfficeHostFaultException ex){pe=ex;}
            Check(pe?.Code=="protected_cell"&&pe.NoEffect,"Protected mutation was not blocked before effect.");
        });
        test("AR-022 E2 injected mid-batch fault is PartiallyApplied and repair excludes applied cells",()=>{
            var backend=new FixtureOfficeBackend(excelPatchFaultAfterWrites:1);var s=backend.DiscoverExcel().ActiveSessionId!;var before=backend.SnapshotExcel(s);
            ExcelCellPatch[] requested=[new("A1",Value:"FIRST"),new("A2",Value:"SECOND")];
            var result=backend.PatchExcel(new(s,before.StateToken,true,"Data",requested){ContentToken=ExcelPatchMutationRules.ContentToken(before),
                LogicalOperationId="op-partial",BatchId="batch-partial",ChunkId="chunk-partial"});
            Check(result.MutationStatus==ExcelPatchMutationStatus.PartiallyApplied&&result.MutationEffect==ExcelPatchMutationEffect.PartiallyApplied
                &&result.ReadbackComplete&&result.AppliedCells.SequenceEqual(["A1"])&&result.UnappliedCells.SequenceEqual(["A2"])
                &&result.UnknownCells.Count==0,"Partial evidence is wrong.");
            var repair=ExcelPatchMutationRules.RepairCandidates(result,requested);Check(repair.Count==1&&repair[0].Address=="A2","Repair would replay applied cell.");
        });
        test("AR-022 E2 unavailable post-write readback is OutcomeUnknown and cannot auto-repair",()=>{
            var backend=new FixtureOfficeBackend(excelPatchFaultAfterWrites:1,excelPatchReadbackFailsAfterFault:true);
            var s=backend.DiscoverExcel().ActiveSessionId!;var before=backend.SnapshotExcel(s);
            ExcelCellPatch[] requested=[new("A1",Value:"FIRST"),new("A2",Value:"SECOND")];
            var result=backend.PatchExcel(new(s,before.StateToken,true,"Data",requested){ContentToken=ExcelPatchMutationRules.ContentToken(before)});
            Check(result.MutationStatus==ExcelPatchMutationStatus.OutcomeUnknown&&result.MutationEffect==ExcelPatchMutationEffect.Unknown
                &&!result.ReadbackComplete&&result.UnknownCells.SequenceEqual(["A1","A2"]),"Lost readback falsely classified.");
            Expect<InvalidOperationException>(()=>ExcelPatchMutationRules.RepairCandidates(result,requested));
        });
        test("AR-022 E2 content token ignores selection but rejects actual content changes",()=>{
            var backend=new FixtureOfficeBackend();var s=backend.DiscoverExcel().ActiveSessionId!;var before=backend.SnapshotExcel(s);
            backend.MoveExcelSelectionForFixture("A2");var ui=backend.SnapshotExcel(s);
            Check(before.StateToken!=ui.StateToken,"State token missed selection.");Check(ExcelPatchMutationRules.ContentToken(before)==ExcelPatchMutationRules.ContentToken(ui),"Selection invalidated content token.");
            var applied=backend.PatchExcel(new(s,before.StateToken,true,"Data",[new("A2",Value:"PINNED")]){ContentToken=ExcelPatchMutationRules.ContentToken(before)});
            Check(applied.MutationStatus==ExcelPatchMutationStatus.Applied,"Pinned content write failed after selection-only change.");
            var fresh=backend.SnapshotExcel(s);backend.SetExcelValueForFixture("A1","EXTERNAL-CONTENT");
            OfficeHostFaultException? stale=null;try{_=backend.PatchExcel(new(s,fresh.StateToken,true,"Data",[new("A2",Value:"STALE")])
                {ContentToken=ExcelPatchMutationRules.ContentToken(fresh)});}catch(OfficeHostFaultException ex){stale=ex;}
            Check(stale?.Code=="stale_content"&&stale.NoEffect,"Content change did not stale mutation.");
        });
        test("AR-022 E2 recalculation obeys workbook sheet and range scope",()=>{
            var backend=new FixtureOfficeBackend();var s=backend.DiscoverExcel().ActiveSessionId!;var before=backend.SnapshotExcel(s);
            var patch=backend.PatchExcel(new(s,before.StateToken,true,"Data",[new("A1",Value:"RECALC-SCOPE")]){ContentToken=ExcelPatchMutationRules.ContentToken(before)});
            Check(Cell(patch.After,"B1").Value!="RECALC-SCOPE","Patch unexpectedly recalculated formula.");
            var narrow=backend.RecalculateExcel(new(s,patch.After.StateToken,true){ContentToken=ExcelPatchMutationRules.ContentToken(patch.After),SheetName="Data",Range="A1"});
            Check(Cell(narrow,"B1").Value!="RECALC-SCOPE","A1 recalc changed B1.");
            var targeted=backend.RecalculateExcel(new(s,narrow.StateToken,true){ContentToken=ExcelPatchMutationRules.ContentToken(narrow),SheetName="Data",Range="B1"});
            Check(Cell(targeted,"B1").Value=="RECALC-SCOPE","B1 recalc did not return value.");
        });
        test("AR-022 E2 production runtime emits typed PartiallyApplied outcome and content-token schema",()=>{
            var root=Path.Combine(Path.GetTempPath(),"h2-ar022-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{
                var client=new BatchClient();var type=typeof(H2ProductionAgentAdapter).Assembly.GetType("H2AgentLab.Integration.H2OfficeRuntimeTools",true)!;
                using var office=(IDisposable)Activator.CreateInstance(type,[new Func<bool>(()=>true),root,null,null])!;
                type.GetField("_client",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(office,client);
                var registry=new ToolRegistry();type.GetMethod("Register")!.Invoke(office,[registry]);Check(registry.TryGet("excel.write_range",out var descriptor),"write tool missing");
                var p=descriptor.CallableSchema.GetProperty("function").GetProperty("parameters");var req=p.GetProperty("required").EnumerateArray().Select(x=>x.GetString()).ToArray();
                Check(req.Contains("content_token")&&!req.Contains("state_token"),"Mutation schema still binds UI state.");
                var before=client.Backend.SnapshotExcel(client.SessionId);var call=new global::H2AgentLab.ToolCall("ar022","excel.write_range",
                    JsonSerializer.SerializeToElement(new{session_id=client.SessionId,content_token=ExcelPatchMutationRules.ContentToken(before),sheet_name="Data",
                        cells=new[]{new{address="A1",value="FIRST"},new{address="A2",value="SECOND"}}}));
                var output=ToolOutcomeBridge.ExecuteAsync(descriptor,call,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Check(output.Outcome.Status==ToolOutcomeStatus.PartiallyApplied&&output.Outcome.Effect==ToolMutationEffect.PartiallyApplied
                    &&output.Outcome.Error?.Code=="partially_applied"&&output.Outcome.NeedsReconciliation,"Typed outcome hid partial write.");
                using var json=JsonDocument.Parse(output.DomainPayload);Check(json.RootElement.GetProperty("AppliedCells")[0].GetString()=="A1"
                    &&json.RootElement.GetProperty("UnappliedCells")[0].GetString()=="A2","Payload lost partial evidence.");
            }finally{try{Directory.Delete(root,true);}catch{}}
        });
    }
    private static ExcelCellState Cell(ExcelLiveSnapshot s,string a)=>s.Sheets.Single(x=>x.Name=="Data").Cells.Single(x=>x.Address==a);
    private static void Check(bool v,string m){if(!v)throw new InvalidOperationException(m);}
    private static void Expect<T>(Action a)where T:Exception{try{a();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    private sealed class BatchClient:IOfficeSessionClient
    {
        public FixtureOfficeBackend Backend{get;}=new(excelPatchFaultAfterWrites:1);public string SessionId=>Backend.DiscoverExcel().ActiveSessionId!;
        public string InstanceIdentity=>"ar022-client";
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken c=default)=>Task.FromResult(Backend.DiscoverExcel());
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string s,CancellationToken c=default)=>Task.FromResult(Backend.SnapshotExcel(s));
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest r,CancellationToken c=default)=>Task.FromResult(Backend.PatchExcel(r));
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r,CancellationToken c=default)=>Task.FromResult(Backend.RecalculateExcel(r));
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r,CancellationToken c=default)=>Task.FromResult(Backend.SaveExcelCopy(r));
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken c=default)=>throw new NotSupportedException();
        public Task<WordLiveSnapshot> SnapshotWordAsync(string s,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}
