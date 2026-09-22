using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using H2AgentLab.Desktop;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

/// <summary>E2 dispatch/UI projection with the concrete H2 adapter and controlled Office client.
/// Fixtures expose synthetic document snapshots; no Office installation or live model is exercised.</summary>
internal static class H2AgentLiveBindingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-012 RC-04 production Office generic-open selects only the project document", () => Temp(root =>
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var office = new OfficeFixture(root); office.AddExcel("inside", Path.Combine(work, "inside.xlsx"), "DOC-PROJECT");
            office.AddExcel("outside", Path.Combine(root, "outside.xlsx"), "DO-NOT-READ-EXTERNAL"); office.Active = "outside";
            var wire = new Wire(Search("excel.list_workbooks"), Call("list", "excel.list_workbooks"), Search("excel.get_active_workbook"), Call("read", "excel.get_active_workbook"));
            var result = Execute(work, Guid.NewGuid(), "Read the open project workbook", new(work, null), office, wire);
            Check(result.Summary.Status == H2AgentTaskStatus.Completed, result.Summary.Error ?? "Project read failed.");
            Check(office.Reads.SequenceEqual(["inside"]) && office.Writes == 0, "Unrelated active workbook was read.");
            Check(wire.Results.Any(r => r.Content.Contains("DOC-PROJECT")) && wire.Results.All(r => !r.Content.Contains("DO-NOT-READ-EXTERNAL")), "Wrong document result exposed.");
            Check(!wire.Results.Single(r => r.ToolCallId == "list").Content.Contains("outside.xlsx"), "Discovery leaked out-of-scope path.");
        }));
        test("AR-012 RC-03 production ambiguity is not resolved by a model session ID", () => Temp(root =>
        {
            var office = new OfficeFixture(root); office.AddExcel("a", Path.Combine(root,"A.xlsx"), "DOC-A"); office.AddExcel("b", Path.Combine(root,"A (1).xlsx"), "DOC-B");
            var wire = new Wire(Search("excel.read_range"), Call("read", "excel.read_range", new { session_id = "b" }));
            var result = Execute(root, Guid.NewGuid(), "Read the open workbook", new(root,null), office, wire);
            Rejected(result, "ambiguous_target"); Check(office.Reads.Count == 0 && office.Writes == 0, "Model chose among ambiguous native sessions.");
        }));
        test("AR-012 RC-04 explicit-active project request never substitutes inside for outside capture", () => Temp(root =>
        {
            var project = Directory.CreateDirectory(Path.Combine(root,"project")).FullName;
            var outside = Path.Combine(root,"outside.xlsx"); var office = new OfficeFixture(root);
            office.AddExcel("inside", Path.Combine(project,"inside.xlsx"), "INSIDE"); office.AddExcel("outside", outside, "OUTSIDE");
            var context = new H2AgentTaskContext(project, null, PermissionScope: Full(), ActiveWorkContext: Capture("outside", outside));
            var wire = new Wire(Search("excel.read_range"), Call("read","excel.read_range",new { session_id="inside" }));
            var result = Execute(project, Guid.NewGuid(), "Read the active workbook", context, office, wire, readOnly:false);
            Rejected(result,"target_not_grounded"); Check(office.Reads.Count == 0, "Full Access bypassed active-target grounding.");
        }));
        test("AR-012 RC-24 production Global capture does not follow later provider foreground", () => Temp(root =>
        {
            var office = new OfficeFixture(root); var a=Path.Combine(root,"A.xlsx"); office.AddExcel("a",a,"DOC-A"); office.AddExcel("b",Path.Combine(root,"B.xlsx"),"DOC-B"); office.Active="b";
            var wire=new Wire(Search("excel.get_active_workbook"),Call("read","excel.get_active_workbook"));
            var result=Execute(root,null,"Read the active workbook",new(root,null,ActiveWorkContext:Capture("a",a)),office,wire);
            Check(result.Summary.Status==H2AgentTaskStatus.Completed && office.Reads.SequenceEqual(["a"]),"Captured target moved to foreground B.");
        }));
        test("AR-012 production external target reaches real Global and Project activity chip and replay", () => Temp(root =>
        {
            var work=Directory.CreateDirectory(Path.Combine(root,"work")).FullName; var path=Path.Combine(root,"external.xlsx");
            var office=new OfficeFixture(root); office.AddExcel("outside",path,"EXTERNAL-ONLY");
            var wire=new Wire(Search("excel.read_range"),Call("read","excel.read_range",new{session_id="outside"}));
            var result=Execute(work,Guid.NewGuid(),"Read exactly \""+path+"\"",new(work,null),office,wire);
            Check(result.Summary.Status==H2AgentTaskStatus.Completed,"Explicit file was not usable.");
            var activity=result.Progress.Single(p=>p.TargetBinding is {IsExternal:true});
            Check(activity.TargetBinding!.Binding!.CanonicalPath==path && H2AgentActivity.Label(activity).StartsWith("External: "),"Host target label absent.");
            Check(WorkAssistantActivityText.FromProgress(activity)==H2AgentActivity.Label(activity),"Global ticker differs from project chat.");
            var view=new AgentTurnView(); view.Present(null,result);
            var rows=(StackPanel)view.Children.OfType<Expander>().Single(e=>e.Name=="AgentTurnActivity").Content!;
            var chip=rows.Children.OfType<Border>().Single(c=>c.Name=="AgentTargetChip");
            Check(((SelectableTextBlock)chip.Child!).Text==activity.TargetBinding.ScopeLabel,"Real turn renderer did not display external chip.");
            var local=AgentTurnView.CreateTargetChip(activity.TargetBinding with {IsExternal=false});
            Check(chip.Background!.ToString()!=local.Background!.ToString(),"External target is visually indistinguishable.");
            using var reopened=new H2ProductionAgentAdapter(Path.Combine(work,"state"),()=>new(Profile(),""));
            var replay=reopened.ObserveTask(result.Summary.TaskId).Progress.Single(p=>p.TargetBinding is {IsExternal:true});
            Check(replay.TargetBinding==activity.TargetBinding,"Existing activity replay lost bound identity.");
        }));
        test("AR-012 RC-05 closed explicit input cannot be replaced by another open workbook", () => Temp(root =>
        {
            var closed=Path.Combine(root,"closed.xlsx"); File.WriteAllText(closed,"SYNTHETIC IDENTITY FIXTURE NOT A REAL XLSX");
            var office=new OfficeFixture(root); office.AddExcel("other",Path.Combine(root,"other.xlsx"),"WRONG");
            var wire=new Wire(Search("excel.read_range"),Call("read","excel.read_range",new{session_id="other"}));
            var result=Execute(root,Guid.NewGuid(),"Read open workbook \""+closed+"\"",new(root,null),office,wire);
            Rejected(result,"resource_not_found"); Check(office.Reads.Count==0,"Closed input was replaced by a live lookalike.");
        }));
        test("AR-012 RC-24 provider connection change before native response invalidates binding", () => Temp(root =>
        {
            var office=new OfficeFixture(root); office.AddExcel("a",Path.Combine(root,"A.xlsx"),"MARKER-NOT-ACCEPTED");
            office.BeforeRead=()=>office.InstanceIdentity="fixture-reconnected";
            var wire=new Wire(Search("excel.read_range"),Call("read","excel.read_range",new{session_id="a"}));
            var result=Execute(root,null,"Read open workbook",new(root,null),office,wire);
            Rejected(result,"stale_resource"); Check(wire.Results.All(r=>!r.Content.Contains("MARKER-NOT-ACCEPTED")),"New provider instance reused old binding.");
        }));
        test("AR-012 production response for wrong session is not forwarded to model", () => Temp(root =>
        {
            var office=new OfficeFixture(root); var a=Path.Combine(root,"A.xlsx"); office.AddExcel("a",a,"DOC-A"); office.AddExcel("b",Path.Combine(root,"B.xlsx"),"SECRET-B-MARKER");
            office.WrongReadSession="b";
            var wire=new Wire(Search("excel.read_range"),Call("read","excel.read_range",new{session_id="a"}));
            var result=Execute(root,null,"Read \""+a+"\"",new(root,null),office,wire);
            Rejected(result,"stale_resource"); Check(wire.Results.All(r=>!r.Content.Contains("SECRET-B-MARKER")),"Wrong native readback was published.");
        }));
        test("AR-012 bound Save As before next write rejects before any effect", () => Temp(root =>
        {
            var office=new OfficeFixture(root); var a=Path.Combine(root,"A.xlsx");office.AddExcel("a",a,"BEFORE");
            var wire=new Wire(Search("excel.read_range"),Call("read","excel.read_range",new{session_id="a"}),Search("excel.write_range"),Write("write","a"));
            wire.AfterResult=r=>{if(r.ToolCallId=="read")office.Rename("a",Path.Combine(root,"Renamed.xlsx"));};
            var result=Execute(root,null,"Read and update \""+a+"\"",new(root,null,PermissionScope:Full()),office,wire,readOnly:false);
            Rejected(result,"stale_resource");Check(office.Writes==0,"Save As silently rebound a pending mutation.");
        }));
        test("AR-012 write readback identity mismatch records unknown effect not verified success", () => Temp(root =>
        {
            var office=new OfficeFixture(root);var path=Path.Combine(root,"A.xlsx");office.AddExcel("a",path,"BEFORE");office.RenameAfterWrite=Path.Combine(root,"Replaced.xlsx");
            var wire=new Wire(Search("excel.write_range"),Write("write","a"));
            var result=Execute(root,null,"Update \""+path+"\"",new(root,null,PermissionScope:Full()),office,wire,readOnly:false);
            Check(office.Writes==1 && File.ReadAllText(Path.Combine(root,"effects.log"))=="WRITE\n","Controlled effect did not occur.");
            Check(result.Summary.Status!=H2AgentTaskStatus.Completed,"Mismatched write readback completed task.");
            Check(result.Progress.Any(p=>p.ToolOutcome is {Effect:H2ToolMutationEffect.Unknown}),"Post-write change was mislabeled no effect.");
            Check(!result.Summary.Evidence.Any(e=>e.VerificationPassed==true),"Wrong readback awarded host verification.");
        }));
        test("AR-012 matching captured selection performs and verifies the exact permitted write", () => Temp(root =>
        {
            var office=new OfficeFixture(root);var path=Path.Combine(root,"A.xlsx");office.AddExcel("a",path,"BEFORE");
            var context=new H2AgentTaskContext(root,null,PermissionScope:Full(),ActiveWorkContext:Capture("a",path) with {Selection="Data!A1"});
            var wire=new Wire(Search("excel.write_range"),Write("write","a"));
            var result=Execute(root,null,"Update current selection",context,office,wire,readOnly:false);
            Check(office.Writes==1 && office.Books["a"].Sheets.Single().Cells.Single().Value=="AFTER","Valid bound selection did not execute exactly once.");
            Check(result.Summary.Status==H2AgentTaskStatus.Completed && result.Summary.Evidence.Any(e=>e.VerificationPassed==true),
                "Correctly bound and independently read-back fixture write was not verified: "+result.Summary.Error);
        }));
        test("AR-012 captured selection identity never exposes plaintext", () =>
        {
            const string privateText="SYNTHETIC-PRIVATE-SELECTION";
            var capture=Capture("unsaved","Document1") with {Selection=privateText,ApplicationKind=H2ApplicationKind.Word};
            var bound=H2AgentResourceBinding.FromCaptured(capture);
            Check(bound.UiStateToken==H2AgentResourceBinding.SelectionToken(privateText)
                && !JsonSerializer.Serialize(bound).Contains(privateText),"Selection plaintext leaked into identity metadata.");
            var other=H2AgentResourceBinding.FromCaptured(capture with {Selection=privateText+" changed"});
            Check(!bound.MatchesObservation(other,requireContentVersion:false,requireUiState:true),"Different selections share a state token.");
        });
        test("AR-012 RC-07 changed captured selection cannot redirect a production write", () => Temp(root =>
        {
            var office=new OfficeFixture(root);var path=Path.Combine(root,"A.xlsx");office.AddExcel("a",path,"BEFORE");
            office.Books["a"]=office.Books["a"] with {SelectionAddress="Z99"};
            var context=new H2AgentTaskContext(root,null,PermissionScope:Full(),ActiveWorkContext:Capture("a",path) with {Selection="Data!A1"});
            var wire=new Wire(Search("excel.write_range"),Write("write","a"));
            var result=Execute(root,null,"Update current selection",context,office,wire,readOnly:false);
            Rejected(result,"stale_resource");Check(office.Writes==0,"Selection moved write target.");
        }));
        test("AR-012 RC-05 actual production Word route binds an unsaved session without disk fallback", () => Temp(root =>
        {
            var office=new OfficeFixture(root);office.AddWord("unsaved","Document1","UNSAVED-ONLY");
            var capture=Capture("unsaved","Document1") with {ApplicationKind=H2ApplicationKind.Word, ProcessName="WINWORD",Selection=null};
            var wire=new Wire(Search("word.read_paragraphs"),Call("read","word.read_paragraphs",new{session_id="unsaved"}));
            var result=Execute(root,null,"Read the active document",new(root,null,ActiveWorkContext:capture),office,wire);
            Check(result.Summary.Status==H2AgentTaskStatus.Completed && office.Reads.SequenceEqual(["unsaved"]),"Unsaved Word route failed.");
            Check(wire.Results.Any(r=>r.Content.Contains("UNSAVED-ONLY")),"Live Word marker missing.");
            Check(result.Progress.Any(p=>p.TargetBinding?.Binding is {CanonicalPath:null,Provenance:"LiveDocument"}),"Unsaved identity became a disk file.");
        }));
        test("AR-012 language evidence from a different Word session is not exposed", () => Temp(root =>
        {
            var office=new OfficeFixture(root);var path=Path.Combine(root,"A.docx");office.AddWord("a",path,"DOC-A");office.WrongLanguageSession="b";
            var wire=new Wire(Search("word.extract_legal_citations"),Call("language","word.extract_legal_citations",new{session_id="a",state_token="v1"}));
            var result=Execute(root,null,"Read citations from the open document",new(root,null),office,wire);
            Rejected(result,"stale_resource");Check(wire.Results.All(r=>!r.Content.Contains("CONTROLLED-LANGUAGE-MARKER")),"Wrong-session language evidence was exposed.");
        }));
        test("AR-012 grounding rejection remains distinct from permission denial in UI metadata", () =>
        {
            var call=new H2AgentLab.ToolCall("call","fixture",JsonSerializer.SerializeToElement(new{}));
            var output=H2AgentLab.Tools.ToolOutcomeBridge.Failure(call,null,"target_not_grounded",H2AgentLab.Tools.ToolErrorPhase.Preflight,H2AgentLab.Tools.ToolMutationEffect.None);
            var projected=H2ToolOutcomeProjection.ToProduct(output.Outcome);
            Check(projected.ErrorCode=="target_not_grounded" && projected.Effect==H2ToolMutationEffect.None,
                "Grounding was conflated with permission or mutation.");
            Check(output.Outcome.Error!.RetryClass==H2AgentLab.Tools.ToolRetryClass.Reobserve,"Grounding advice retries without selecting target.");
        });
        test("AR-012 stale captured native process blocks production reads despite Full Access", () => Temp(root =>
        {
            var office=new OfficeFixture(root);var path=Path.Combine(root,"A.xlsx");office.AddExcel("a",path,"NO-READ");
            var wire=new Wire(Search("excel.get_active_workbook"),Call("read","excel.get_active_workbook"));
            var result=Execute(root,null,"Read the active workbook",new(root,null,PermissionScope:Full(),ActiveWorkContext:Capture("a",path)),office,wire,false,captureValid:false);
            Rejected(result,"stale_resource");Check(office.Reads.Count==0,"Stale process identity was accepted.");
        }));
        foreach (var name in new[] { "word.replace_range", "word.apply_format", "word.insert_text" })
            test("AR-012 Word revision stale preflight has proven no effect for " + name, () => Temp(root =>
            {
                var office = new OfficeFixture(root);
                var path = Path.Combine(root, "Revision.docx");
                office.AddWord("word", path, "BEFORE");
                object args = name == "word.insert_text"
                    ? new { session_id = "word", state_token = "old-v0", paragraph_index = 0, offset = 0, text = "AFTER" }
                    : new { session_id = "word", state_token = "old-v0", paragraphs = new[] { new { paragraphIndex = 0, text = "AFTER", bold = true } } };
                var wire = new Wire(Search(name), Call("stale-word", name, args));
                var result = Execute(root, null, "Update the open Word document", new(root, null, PermissionScope: Full()), office, wire, false);
                Rejected(result, "stale_resource");
                var outcome = wire.Results.Single(r => r.ToolCallId == "stale-word").Outcome!;
                Check(outcome.Status == H2AgentLab.Tools.ToolOutcomeStatus.Rejected
                    && outcome.Error is { Phase: H2AgentLab.Tools.ToolErrorPhase.Preflight, RetryClass: H2AgentLab.Tools.ToolRetryClass.Reobserve }
                    && !outcome.NeedsReconciliation, "Stale preflight was mislabeled an uncertain mutation.");
                Check(office.Writes == 0 && office.Documents["word"].Paragraphs[0].Text == "BEFORE"
                    && !File.Exists(Path.Combine(root, "effects.log")), "Stale token reached the mutating executor.");
            }));
        test("AR-012 Word revision valid write is read back and preserves the guard paragraph", () => Temp(root =>
        {
            var office = WordRevisionFixture(root);
            var wire = new Wire(Search("word.replace_range"), WordRevisionWrite("valid-word"));
            var result = Execute(root, null, "Update the first paragraph of the open Word document", new(root, null, PermissionScope: Full()), office, wire, false);
            Check(office.Writes == 1 && File.ReadAllText(Path.Combine(root, "word-content.txt")) == "AFTER\nGUARD",
                "Valid Word fixture mutation or guard preservation failed.");
            Check(result.Summary.Status == H2AgentTaskStatus.Completed
                && result.Summary.Evidence.Any(e => e.VerificationPassed == true),
                "Valid Word write did not pass the existing independent readback verifier: " + result.Summary.Error);
        }));
        test("AR-012 Word revision lost response after actual write remains unknown and cannot repeat", () => Temp(root =>
        {
            var office = WordRevisionFixture(root); office.LoseWordResponse = true;
            var wire = new Wire(Search("word.replace_range"), WordRevisionWrite("lost-word"), WordRevisionWrite("repeat-word"));
            var result = Execute(root, null, "Update the first paragraph of the open Word document", new(root, null, PermissionScope: Full()), office, wire, false);
            Check(office.Writes == 1 && File.ReadAllText(Path.Combine(root, "word-content.txt")) == "AFTER\nGUARD",
                "Lost-response fixture did not apply exactly one write.");
            Check(wire.Results.Single(r => r.ToolCallId == "lost-word").Outcome is
                { Effect: H2AgentLab.Tools.ToolMutationEffect.Unknown, Error.RetryClass: H2AgentLab.Tools.ToolRetryClass.ReconcileRequired },
                "After-dispatch failure falsely became a no-effect preflight rejection.");
            Check(result.Summary.Status != H2AgentTaskStatus.Completed
                && !result.Summary.Evidence.Any(e => e.VerificationPassed == true), "Unknown Word effect was declared verified.");
        }));
        test("AR-012 selected desktop controller rejects process window and session replacement", () =>
        {
            var bound=new DesktopWindowInfo("s",101,123,456,"EXCEL","Fixture",new(0,0,100,100),96,true);
            Check(SelectedDesktopWindowController.SameTarget(bound,bound with {Foreground=false,Title="Changed title"}),"Foreground/title movement invalidated identity.");
            foreach(var changed in new[]{bound with {Handle=102},bound with {ProcessId=124},bound with{ProcessStartedUtcTicks=789},bound with{SessionId="other"}})
                Check(!SelectedDesktopWindowController.SameTarget(bound,changed),"Native window identity drift accepted.");
        });
        test("AR-012 selected desktop controller exercises exact identity through helper IPC", () =>
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var directory=new DirectoryInfo(AppContext.BaseDirectory);
            while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"H2Notes.Avalonia.slnx")))directory=directory.Parent;
            Check(directory is not null,"Full repository checkout is required for fixture helper evidence.");
            var helper=Path.Combine(directory!.FullName,"experiments","H2AgentLab.DesktopHost","bin","Release","net10.0-windows","H2AgentLab.DesktopHost.exe");
            using var client=new DesktopHostClient(helper,fixtureMode:true,defaultTimeout:TimeSpan.FromSeconds(10));
            var target=client.ListWindowsAsync(timeout.Token).GetAwaiter().GetResult().Single();
            using var correct=new SelectedDesktopWindowController(client,target);
            var observed=JsonSerializer.SerializeToElement(correct.Inspect((_,_)=>Task.FromResult(true),timeout.Token).GetAwaiter().GetResult());
            var token=observed.GetProperty("controls").EnumerateArray().Single(c=>c.GetProperty("name").GetString()=="Fixture text").GetProperty("Token").GetString()!;
            var acted=JsonSerializer.SerializeToElement(correct.Act("type_control",token,"BOUND-FIXTURE",(_,_)=>Task.FromResult(true),timeout.Token).GetAwaiter().GetResult());
            Check(acted.GetProperty("valueObserved").GetBoolean(),"Valid selected helper window failed its actual readback.");
            using var wrong=new SelectedDesktopWindowController(client,target with {Handle=target.Handle+1});
            try{wrong.Inspect((_,_)=>Task.FromResult(true),timeout.Token).GetAwaiter().GetResult();throw new InvalidOperationException("Wrong HWND accepted through actual IPC.");}
            catch(H2AgentLab.Tools.ToolPreflightException ex){Check(ex.Code=="stale_resource","Wrong identity classification.");}
            var after=client.ObserveAsync(target.SessionId,timeout.Token).GetAwaiter().GetResult();
            Check(after.Elements.Single(e=>e.Name=="Fixture text").Value=="BOUND-FIXTURE","Rejected target changed the fixture window.");
        });
        test("AR-012 Windows junction escape rejected before file effects", () => Temp(root =>
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("This registered acceptance case requires Windows.");
            var work=Directory.CreateDirectory(Path.Combine(root,"work")).FullName;var outside=Directory.CreateDirectory(Path.Combine(root,"outside")).FullName;
            var secret=Path.Combine(outside,"marker.txt");File.WriteAllText(secret,"PRESERVE");var link=Path.Combine(work,"junction");
            using var process=Process.Start(new ProcessStartInfo("cmd.exe",$"/d /c mklink /J \"{link}\" \"{outside}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
            if(!process.WaitForExit(15000)){process.Kill(true);process.WaitForExit();throw new TimeoutException("Fixture junction creation timed out.");}
            Check(process.ExitCode==0,"Cannot create the dedicated junction fixture: "+process.StandardError.ReadToEnd());
            try
            {
                var policy=new H2AgentTargetBindingPolicy(null,work);
                Check(!policy.AllowsPath(Path.Combine(link,"marker.txt")),"Junction path was grounded.");
                var workspace=new H2AgentLab.SafeWorkspace(work,()=>true,groundedTarget:policy.AllowsPath);
                try{workspace.Resolve(Path.Combine(link,"marker.txt"));throw new InvalidOperationException("Junction escaped FullAccess grounding.");}catch(IOException){}
                Check(File.ReadAllText(secret)=="PRESERVE","Outside file changed.");
            }
            finally{Directory.Delete(link,false);}
        }));
    }

    private static H2AgentTaskObservation Execute(string root,Guid? project,string goal,H2AgentTaskContext context,OfficeFixture office,Wire wire,bool readOnly=true,bool captureValid=true)
    {
        using var adapter=new H2ProductionAgentAdapter(Path.Combine(root,"state"),()=>new(Profile(),""),new Factory(wire),
            officeClientFactory:()=>office,captureValidator:_=>captureValid);
        try
        {
            var id=adapter.StartTaskAsync(project,goal,context,readOnly).GetAwaiter().GetResult();
            var end=Environment.TickCount64+20000;
            while(Environment.TickCount64<end){var result=adapter.ObserveTask(id);if(H2AgentActivity.IsTerminal(result.Summary.Status))return result;Thread.Sleep(10);}
            throw new TimeoutException("Binding fixture task exceeded its finite budget.");
        }
        finally{adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();}
    }
    private static void Rejected(H2AgentTaskObservation result,string code)
    {
        Check(result.Summary.Status!=H2AgentTaskStatus.Completed,"Rejected binding completed task.");
        Check(result.Progress.Any(p=>p.ToolOutcome is {Effect:H2ToolMutationEffect.None} o && o.ErrorCode==code),"Expected no-effect rejection "+code+"; observed "+JsonSerializer.Serialize(result.Progress.Select(p=>p.ToolOutcome).Where(o=>o!=null)));
    }
    private static OfficeFixture WordRevisionFixture(string root)
    {
        var office = new OfficeFixture(root);
        office.AddWord("word", Path.Combine(root, "Revision.docx"), "BEFORE");
        office.Documents["word"] = office.Documents["word"] with { Paragraphs = [
            new(0, "BEFORE", "Normal", [new(0, "BEFORE", "Normal", false, false, false)]),
            new(1, "GUARD", "Normal", [new(0, "GUARD", "Normal", true, false, false)])] };
        return office;
    }
    private static AgentTransportToolCall WordRevisionWrite(string id)
        => Call(id, "word.replace_range", new { session_id = "word", state_token = "v1",
            paragraphs = new[] { new { paragraphIndex = 0, text = "AFTER" } } });
    private static H2ActiveWorkContext Capture(string session,string path)=>new(123,456,"EXCEL",H2ApplicationKind.Excel,101,"win32:65:123:456","Synthetic",session,path,null,"office-host",DateTime.UtcNow);
    private static H2AgentPermissionScope Full(){var now=DateTime.UtcNow;return new(H2AgentPermissionMode.FullAccess,H2AgentResourceScopeKind.Machine,H2AgentPermissionScope.CurrentMachineResourceKey,true,false,now,now.AddMinutes(10));}
    private static AiProfile Profile()=>new(){Model="fixture-no-network",Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1"};
    private static AgentTransportToolCall Search(string name)=>Call("search-"+name,"tool_search",new{query=name});
    private static AgentTransportToolCall Call(string id,string name,object? args=null)=>new(id,name,JsonSerializer.Serialize(args??new{}));
    private static AgentTransportToolCall Write(string id,string session)=>Call(id,"excel.write_range",new{session_id=session,state_token="v1",sheet_name="Data",cells=new[]{new{address="A1",value="AFTER"}}});
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Temp(Action<string> body){var root=Path.Combine(Path.GetTempPath(),"h2-ar012-live-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{body(root);}finally{Directory.Delete(root,true);}}
    private sealed class Factory(Wire wire):IAgentTransportFactory{public IAgentTransport Create(AiProfile p,string key,AgentRunTelemetry telemetry)=>wire;}
    private sealed class Wire(params AgentTransportToolCall[] calls):IAgentTransport
    {
        private int index; public List<AgentToolResult> Results{get;}=[];public Action<AgentToolResult>? AfterResult;
        public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request,CancellationToken ct=default)=>Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request,CancellationToken ct=default){Results.AddRange(request.ToolResults);foreach(var item in request.ToolResults)AfterResult?.Invoke(item);return Round(ct);}
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation]CancellationToken ct){ct.ThrowIfCancellationRequested();await Task.Delay(1,ct).ConfigureAwait(false);if(index<calls.Length)yield return AgentTransportEvent.Tool(calls[index++]);else yield return AgentTransportEvent.TextDeltaEvent("Synthetic final candidate; host gates decide.");yield return AgentTransportEvent.Complete();}
        public void Cancel(){} public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class OfficeFixture(string root):IOfficeSessionClient
    {
        public string InstanceIdentity{get;set;}="fixture-office-connection-1";
        public Dictionary<string,ExcelLiveSnapshot> Books{get;}=[];public Dictionary<string,WordLiveSnapshot> Documents{get;}=[];
        public List<string> Reads{get;}=[];public int Writes;public string? Active;public Action? BeforeRead;public string? WrongReadSession;public string? WrongLanguageSession;public string? RenameAfterWrite;
        public void AddExcel(string id,string path,string marker)=>Books.Add(id,new(id,Path.GetFileName(path),path,true,"Data","A1",[new("Data","visible",[new("A1",marker,"",false,false,null,"General","general","bottom")],[],[],[])],"v1"));
        public void AddWord(string id,string path,string marker)=>Documents.Add(id,new(id,Path.GetFileName(path),path,false,0,0,"",[new(0,marker,"Normal",[])],[],[],[],[],"v1"));
        public void Rename(string id,string path)=>Books[id]=Books[id] with {Name=Path.GetFileName(path),FullName=path};
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new ExcelDiscovery(Books.Values.Select(x=>new ExcelWorkbookInfo(x.SessionId,x.Name,x.FullName,x.Saved,"","","")).ToArray(),Active));}
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new WordDiscovery(Documents.Values.Select(x=>new WordDocumentInfo(x.SessionId,x.Name,x.FullName,x.Saved,0,0,"","")).ToArray(),Active));}
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string id,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Reads.Add(id);BeforeRead?.Invoke();return Task.FromResult(Books[WrongReadSession??id]);}
        public Task<WordLiveSnapshot> SnapshotWordAsync(string id,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Reads.Add(id);return Task.FromResult(Documents[id]);}
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest request,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();Check(request.PermissionGranted,"Native mutation lacked permission.");var before=Books[request.SessionId];Check(before.StateToken==request.StateToken,"Native state precondition missing.");
            Writes++;File.AppendAllText(Path.Combine(root,"effects.log"),"WRITE\n");
            var after=before with{StateToken="v2",Saved=false,Sheets=before.Sheets.Select(s=>s with{Cells=s.Cells.Select(c=>c with{Value=request.Cells.Single(p=>p.Address==c.Address).Value??c.Value}).ToArray()}).ToArray()};
            Books[request.SessionId]=after;if(RenameAfterWrite is{} renamed)Rename(request.SessionId,renamed);
            return Task.FromResult(new ExcelPatchResult(before,after,request.Cells.Select(c=>c.Address).ToArray()));
        }
        public bool LoseWordResponse;
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var before = Documents[request.SessionId];
            Check(request.PermissionGranted && request.StateToken == before.StateToken, "Fixture precondition failed.");
            Check(WordPatchRules.ValidationError(before, request.Paragraphs) is null, "Invalid fixture Word patch.");
            // This fixture intentionally supports only single-line paragraph edits, not native Word layout.
            Check(request.Paragraphs.All(p => p.Text is null || WordPatchRules.Lines(p.Text).Length == 1), "Unsupported fixture multiline edit.");
            var edits = request.Paragraphs.ToDictionary(p => p.ParagraphIndex);
            var after = before with { Saved = false, StateToken = "v2", Paragraphs = before.Paragraphs.Select(p =>
            {
                if (!edits.TryGetValue(p.Index, out var edit)) return p;
                return p with { Text = edit.Text ?? p.Text, Runs = p.Runs.Select(r => r with {
                    Text = edit.Text ?? r.Text, Bold = edit.Bold ?? r.Bold,
                    Italic = edit.Italic ?? r.Italic, Underline = edit.Underline ?? r.Underline }).ToArray() };
            }).ToArray() };
            Writes++; Documents[request.SessionId] = after;
            File.AppendAllText(Path.Combine(root, "effects.log"), "WRITE\n");
            File.WriteAllText(Path.Combine(root, "word-content.txt"), string.Join("\n", after.Paragraphs.Select(p => p.Text)));
            if (LoseWordResponse) throw new IOException("Controlled response loss AFTER the disposable fixture write.");
            return Task.FromResult(new WordPatchResult(before, after, request.Paragraphs.Select(p => p.ParagraphIndex).ToArray()));
        }
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r,CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();Reads.Add(r.SessionId);return Task.FromResult(new WordLanguageEvidenceResult(WrongLanguageSession??r.SessionId,"v1",[],[],[],"CONTROLLED-LANGUAGE-MARKER"));}
        public void Dispose(){}
    }
}
