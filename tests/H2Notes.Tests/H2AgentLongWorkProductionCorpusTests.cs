using System.Net;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using H2AgentLab.Context;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class H2AgentLongWorkProductionCorpusTests
{
    private const string OldRequirement = "Update DOC-A title to REV-A";
    private const string NewRequirement = "Update DOC-A title to REV-B";
    private const string PreserveRequirement = "preserve DOC-B unchanged";
    private const string PdfRequirement = "export DOC-A as PDF";

    public static void Run(Action<string,Action> test)
    {
        test("AR-080 E2 RC-33 golden restart boundary repeats three times without duplicate side effects", () =>
        {
            for(var iteration=1;iteration<=3;iteration++)
                WithRoot("golden-"+iteration, root => GoldenRestart(root,iteration));
        });

        test("AR-080 E2 changed requirement and exact compaction source recall repeat three times", () =>
        {
            for(var iteration=1;iteration<=3;iteration++)
                WithRoot("recall-"+iteration, root => RevisionRecall(root,iteration));
        });

        test("AR-080 E2 missing PDF cannot become Completed even when model claims done three times", () =>
        {
            for(var iteration=1;iteration<=3;iteration++)
                WithRoot("pdf-"+iteration, root => MissingPdfBlocked(root,iteration));
        });

        test("AR-080 E2 UNSAVED-only resume keeps current revision/session and near-name control file three times", () =>
        {
            for(var iteration=1;iteration<=3;iteration++)
                WithRoot("unsaved-"+iteration, root => UnsavedControl(root,iteration));
        });
    }

    private static void GoldenRestart(string root,int iteration)
    {
        var seed=Seed(root,iteration,includePdf:true,unknownOperation:true);
        var factory=new FinalOnlyChatFactory();
        using(var adapter=Adapter(seed.StateRoot,factory))
        {
            var before=adapter.GetTaskSummary(seed.TaskId);
            Check(before.Recovery is {Interrupted:true,ReconcileRequired:true},
                "Crash-after-write operation was not projected as ReconcileRequired.");
            Expect<InvalidOperationException>(()=>adapter.ResumeTaskAsync(seed.TaskId,
                ResumeContext(seed,iteration),true).GetAwaiter().GetResult(),"Reconcile the exact resource");

            var reconciled=adapter.ReconcileInterruptedTask(seed.TaskId,
            [
                new(seed.Operation!.InvocationId,H2AgentReconcileDisposition.Verified,
                    seed.ResourceKey,seed.DocAHash,"host reobserved synthetic DOC-A bytes",DateTime.UtcNow)
            ]);
            Check(!reconciled.ReconcileRequired&&reconciled.RequiresFreshPermission,
                "Exact postcondition reconciliation did not clear replay blocker.");

            var resumed=adapter.ResumeTaskAsync(seed.TaskId,ResumeContext(seed,iteration),true)
                .GetAwaiter().GetResult();
            Check(resumed==seed.TaskId,"Restart changed TaskId.");
            var done=Wait(adapter,resumed);
            Check(done.Status==H2AgentTaskStatus.Blocked,
                "Missing PDF outcome was reported complete after restart: "+done.Status+" / "+done.Error);
            AssertGoalState(done,expectPdfPending:true);
            Check(factory.Bodies.Count==1&&factory.Budgets.Count==1&&factory.Budgets[0].Allowed,
                "Resume did not emit exactly one budgeted fresh provider request.");
            Check(factory.Bodies[0].Contains("HOST_RESUME_STATE_CANONICAL_NOT_PROVIDER_CONTINUATION",StringComparison.Ordinal)
                && factory.Bodies[0].Contains(seed.Goals.RevisionId,StringComparison.Ordinal)
                && factory.Bodies[0].Contains(NewRequirement,StringComparison.Ordinal)
                && factory.Bodies[0].Contains(PdfRequirement,StringComparison.Ordinal),
                "Fresh request lost canonical revision/current outcomes.");
            Check(!File.Exists(seed.PdfPath),"Fixture unexpectedly created the missing PDF.");
            Check(ReadDocx(seed.DocAPath).CountText("REV-B")==1,
                "Already-applied DOC-A effect was duplicated after restart.");
            Check(Hash(File.ReadAllBytes(seed.DocAPath))==seed.DocAHash,
                "DOC-A bytes changed during reconcile/resume.");
            Check(Hash(File.ReadAllBytes(seed.DocBPath))==seed.DocBHash,
                "Near-name DOC-B control changed during DOC-A recovery.");
        }
        VerifyDurableRecall(seed,iteration);
        Save("golden-"+iteration,new{
            iteration,seed.TaskId,seed.Goals.RevisionId,
            docA=seed.DocAHash,docB=seed.DocBHash,
            request=factory.Budgets.Single(),
            expectedStatus="BlockedMissingPdf",
            duplicateSideEffects=0,unintendedSideEffects=0,
            componentReality=new{
                productionAdapter="real",
                journal="real",
                compaction="real",
                requestSerializer="real ChatCompletions serializer with intercepted HTTP",
                wordMutation="synthetic DOCX + seeded crash boundary fixture",
                model="scripted final-only fixture",
                nativeOffice="NOT_RUN"
            }
        });
    }

    private static void RevisionRecall(string root,int iteration)
    {
        var seed=Seed(root,iteration,includePdf:true,unknownOperation:false);
        using var archive=new AgentIntegrationTaskArchive(Path.Combine(seed.StateRoot,"integration"));
        var summary=archive.Get(seed.TaskId)!;
        AssertGoalState(summary,expectPdfPending:true);
        var journal=archive.ReadJournal(seed.TaskId);
        var compact=journal.Single(x=>x.Kind=="context-compaction")
            .Payload.Deserialize<AgentContextCompactionRecord>()!;
        var exact=new ArtifactStore(seed.TaskRoot).ReadText(compact.Source.Id);
        Check(exact.Contains(seed.EarlyMarker,StringComparison.Ordinal),
            "Exact early fact disappeared from compaction source.");
        Check(journal.Where(x=>x.Kind is "task-state" or "revision")
            .Any(x=>x.Payload.GetRawText().Contains(seed.Correction,StringComparison.Ordinal)),
            "Current user correction is not retrievable from durable source.");
        Check(summary.GoalState!.Outcomes.Single(x=>x.Requirement==OldRequirement).Status=="Superseded"
            && summary.GoalState.Outcomes.Single(x=>x.Requirement==NewRequirement).Status=="Verified",
            "Latest revision did not supersede the old requirement deterministically.");
        Check(seed.Compaction.Budgets.Count==5
            && seed.Compaction.Budgets.All(x=>x.Allowed&&x.EstimatedInputTokens>0&&x.SerializedBytes>0)
            && seed.Compaction.Compactions==1,
            "Compaction/request budget measurements are incomplete.");
        Save("recall-"+iteration,new{
            iteration,revision=summary.GoalState.RevisionId,
            exactEarlySha=Hash(Encoding.UTF8.GetBytes(seed.EarlyMarker)),
            seed.Compaction.Compactions,
            requests=seed.Compaction.Budgets.Count,
            estimatedInput=seed.Compaction.Budgets.Select(x=>x.EstimatedInputTokens).ToArray(),
            serializedBytes=seed.Compaction.Budgets.Select(x=>x.SerializedBytes).ToArray()
        });
    }

    private static void MissingPdfBlocked(string root,int iteration)
    {
        var seed=Seed(root,iteration,includePdf:true,unknownOperation:false);
        var factory=new FinalOnlyChatFactory();
        using var adapter=Adapter(seed.StateRoot,factory);
        var resumed=adapter.ResumeTaskAsync(seed.TaskId,ResumeContext(seed,iteration),true)
            .GetAwaiter().GetResult();
        var done=Wait(adapter,resumed);
        Check(done.Status==H2AgentTaskStatus.Blocked&&done.Completion is {RequiredOutcomes:>0},
            "Model final text bypassed pending PDF outcome.");
        AssertGoalState(done,expectPdfPending:true);
        Check(!File.Exists(seed.PdfPath)&&Hash(File.ReadAllBytes(seed.DocBPath))==seed.DocBHash,
            "Blocked completion mutated PDF/control output.");
        Save("missing-pdf-"+iteration,new{
            iteration,status=done.Status.ToString(),done.Error,
            required=done.Completion?.RequiredOutcomes,verified=done.Completion?.VerifiedOutcomes,
            pdfExists=File.Exists(seed.PdfPath)
        });
    }

    private static void UnsavedControl(string root,int iteration)
    {
        var seed=Seed(root,iteration,includePdf:false,unknownOperation:false);
        var factory=new FinalOnlyChatFactory();
        using var adapter=Adapter(seed.StateRoot,factory);
        var context=ResumeContext(seed,iteration);
        var resumed=adapter.ResumeTaskAsync(seed.TaskId,context,true).GetAwaiter().GetResult();
        var done=Wait(adapter,resumed);
        Check(done.Status==H2AgentTaskStatus.Completed,
            "All-verified UNSAVED-only resume did not complete: "+done.Error);
        AssertGoalState(done,expectPdfPending:false);
        Check(factory.Bodies.Count==1
            && factory.Bodies[0].Contains(seed.UnsavedSession,StringComparison.Ordinal)
            && factory.Bodies[0].Contains(NewRequirement,StringComparison.Ordinal),
            "Fresh request lost UNSAVED-only session/current revision.");
        Check(Hash(File.ReadAllBytes(seed.DocBPath))==seed.DocBHash
            && Hash(File.ReadAllBytes(seed.DocAPath))==seed.DocAHash,
            "Read-only resume modified one of the near-name documents.");
        Save("unsaved-"+iteration,new{
            iteration,status=done.Status.ToString(),seed.UnsavedSession,
            request=factory.Budgets.Single(),docA=seed.DocAHash,docB=seed.DocBHash
        });
    }

    private static SeedFixture Seed(string root,int iteration,bool includePdf,bool unknownOperation)
    {
        var workspace=Path.Combine(root,"workspace");
        var state=Path.Combine(root,"agent-state");
        Directory.CreateDirectory(workspace);Directory.CreateDirectory(state);
        var docA=Path.Combine(workspace,"DOC-A.docx");
        var docB=Path.Combine(workspace,"DOC-A-control.docx");
        File.WriteAllBytes(docA,Docx("TITLE REV-B","BODY A "+iteration));
        File.WriteAllBytes(docB,Docx("CONTROL DOC-B","DO NOT TOUCH "+iteration));
        var docAHash=Hash(File.ReadAllBytes(docA));var docBHash=Hash(File.ReadAllBytes(docB));
        var pdf=Path.Combine(workspace,"DOC-A.pdf");
        var task=Guid.NewGuid();var thread=Guid.NewGuid();var oldTurn=Guid.NewGuid();
        var scope="workspace:"+workspace;
        var initial=OldRequirement+"; "+PreserveRequirement+(includePdf?"; "+PdfRequirement:"");
        var goals=AgentGoalState.Create(task,scope,new(Guid.NewGuid(),initial));
        var correction="replace \""+OldRequirement+"\" with \""+NewRequirement+"\".";
        goals=goals.Apply(new(Guid.NewGuid(),correction));

        var titleEvidence="ar080-title-"+Guid.NewGuid().ToString("N");
        var controlEvidence="ar080-control-"+Guid.NewGuid().ToString("N");
        var evidence=new[]{
            new H2AgentEvidence(titleEvidence,AgentEvidenceKind.ToolResult.ToString(),docAHash,
                "Synthetic DOC-A bytes already contain corrected title.",Provenance:"AR-080 synthetic crash fixture",VerificationPassed:true),
            new H2AgentEvidence(controlEvidence,AgentEvidenceKind.ToolResult.ToString(),docBHash,
                "Near-name DOC-B control hash.",Provenance:"AR-080 synthetic control",VerificationPassed:true)
        };
        var outcomes=goals.Obligations.Select(o=>{
            var status=o.Status.ToString();IReadOnlyList<string> ids=[];
            if(o.Requirement==NewRequirement){status=AgentObligationStatus.Verified.ToString();ids=[titleEvidence];}
            else if(o.Requirement==PreserveRequirement){status=AgentObligationStatus.Verified.ToString();ids=[controlEvidence];}
            else if(o.Requirement==PdfRequirement)status=AgentObligationStatus.Pending.ToString();
            return new H2AgentOutcomeSnapshot(o.Id,o.Requirement,o.SourceId,o.RevisionId,o.TargetScope,status,o.ReplacedBy,ids);
        }).ToArray();
        var snapshot=new H2AgentGoalSnapshot(goals.RevisionId,
            goals.Revisions.Select(r=>new H2AgentGoalRevisionSnapshot(r.Id,r.ParentId,r.Sequence,r.SourceId,r.SourceText,r.Added,r.Retired)).ToArray(),
            outcomes,[goals.RevisionId],scope);
        var now=DateTime.UtcNow;
        var summary=new H2AgentTaskSummary(task,null,initial,H2AgentTaskStatus.Running,null,evidence,null,null,
            now,now,thread,oldTurn){GoalState=snapshot};
        var taskRoot=Path.Combine(state,"tasks",task.ToString("N"));
        Directory.CreateDirectory(taskRoot);
        var early="AR080-EARLY-EXACT-"+iteration+"-"+Guid.NewGuid().ToString("N");
        var resource="word:session:unsaved-ar080-"+iteration;
        H2AgentOperationRecord? operation=null;CompactionMeasurement compaction;
        using(var archive=new AgentIntegrationTaskArchive(Path.Combine(state,"integration")))
        {
            archive.Upsert(summary);
            compaction=SeedCompaction(archive,taskRoot,task,oldTurn,initial,goals,early,evidence);
            for(var i=0;i<24;i++)
                archive.AppendProgress(task,new(i,DateTime.UtcNow,"commentary","distractor","noise-"+i+"-"+new string('x',80)));
            if(unknownOperation)
            {
                operation=new(Guid.NewGuid(),"op-"+Guid.NewGuid().ToString("N"),oldTurn,goals.RevisionId,
                    "word-edit-ar080","word.replace_range","Dispatched","NotKnown","Unknown",
                    Hash(Encoding.UTF8.GetBytes("{\"title\":\"REV-B\"}")),Hash(Encoding.UTF8.GetBytes(resource)),null,null);
                archive.RecordOperation(task,operation);
            }
        }
        return new(task,thread,oldTurn,workspace,state,taskRoot,docA,docB,pdf,docAHash,docBHash,
            goals,correction,early,resource,"unsaved-ar080-"+iteration,operation,compaction);
    }

    private static CompactionMeasurement SeedCompaction(
        AgentIntegrationTaskArchive archive,string taskRoot,Guid task,Guid turn,string goal,
        AgentGoalState goals,string early,IReadOnlyList<H2AgentEvidence> evidence)
        => Task.Run(async()=>{
            var profile=Profile("ar080-compaction");
            using var wire=new H2AgentRequestBudgetTests.WireFixture("chat",profile);
            await using var transport=wire.Create();
            var budgets=new List<AgentRequestBudgetReceipt>();
            ((IAgentRequestBudgetSource)transport).RequestBudgetEvaluated+=budgets.Add;
            var tool=new AgentToolDefinition("lookup","fixture lookup",
                JsonSerializer.SerializeToElement(new{type="object",properties=new{}}));
            var start=new AgentTransportStartRequest(task,turn,
                [new(AgentTransportMessageRole.System,"HOST-POLICY"),
                 new(AgentTransportMessageRole.User,goal+"\nEARLY="+early)],[tool]);
            var contract=new AgentTaskContract(task,goal,goals.Scope,[],[NewRequirement],
                [PreserveRequirement],goals.Active.Any(x=>x.Requirement==PdfRequirement)?[PdfRequirement]:[],
                [],AgentTaskRiskClass.Medium,new AgentVerificationPolicy(requireVerification:false),true,goals);
            var coordinator=new RuntimeCompactionCoordinator(taskRoot,new AgentContextManager());
            var activated=0;
            var work=coordinator.CreateWorkTurn(start,
                r=>{archive.RecordContextCompaction(r);activated++;},
                archive.RecordContextSource,new(4,4));
            var events=await Collect(transport.StartAsync(start));
            for(var i=1;i<=4;i++)
            {
                var call=events.Single(e=>e.Kind==AgentTransportEventKind.ToolCall).ToolCall!;
                var result=i==1?"EARLY-SOURCE "+early:new string((char)('a'+i),1200);
                var ack=new AgentTransportContinuationRequest(task,turn,
                    [new(call.Id,call.Name,result)],
                    SupplementalUserMessages:i==3?[goals.Revisions[^1].SourceText]:null);
                var anchors=JsonSerializer.SerializeToElement(new{
                    TaskId=task,TurnId=turn,RevisionId=goals.RevisionId,Contract=contract,
                    Invocation=new{EntryPoint="AR080",ProjectId=(Guid?)null},
                    PendingOperations=Array.Empty<object>(),UnresolvedCalls=Array.Empty<object>(),
                    UncertainResources=Array.Empty<string>(),
                    ObservedEvidence=evidence.Select(x=>new{x.EvidenceId,x.Sha256}).ToArray(),
                    Verification=(object?)null,Completion=new{State="Running"}
                });
                var plan=work.Prepare(transport,i+1,"observed-"+i,[call],ack,goals.RevisionId,anchors,default);
                events=plan is { } next
                    ? await Collect(((IAgentContextRebaseTransport)transport).RebaseContextAsync(next.Context,ack,next.BodySha256))
                    : await Collect(transport.ContinueAsync(ack));
            }
            Check(activated==1&&wire.Bodies.Count==5&&budgets.Count==5,
                "AR-080 compaction fixture did not execute one bounded cycle.");
            return new CompactionMeasurement(budgets.ToArray(),activated);
        }).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    private static H2AgentTaskContext ResumeContext(SeedFixture seed,int iteration)
        => new(seed.Workspace,"AR-080 resume "+iteration,
            ThreadId:seed.ThreadId,TurnId:Guid.NewGuid(),
            ActiveWorkContext:new H2ActiveWorkContext(
                4000+iteration,DateTime.UtcNow.AddMinutes(-1).Ticks,"WINWORD",H2ApplicationKind.Word,
                7000+iteration,"win32-ar080-"+iteration,"Unsaved DOC-A - Word",
                seed.UnsavedSession,null,"paragraph 1","ar080-fixture",DateTime.UtcNow));

    private static H2ProductionAgentAdapter Adapter(string state,FinalOnlyChatFactory factory)
        => new(state,()=>new(Profile("ar080-resume"),""),factory);

    private static AiProfile Profile(string model)=>new()
    {
        Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1",Model=model,
        RequestBudget=new(){ContextLimitTokens=200_000,ContextLimitSource="ar080-e2-fixture-budget-v1",
            ReservedOutputTokens=128,SafetyMarginTokens=64}
    };

    private static void VerifyDurableRecall(SeedFixture seed,int iteration)
    {
        using var archive=new AgentIntegrationTaskArchive(Path.Combine(seed.StateRoot,"integration"));
        var journal=archive.ReadJournal(seed.TaskId);
        var compact=journal.Single(x=>x.Kind=="context-compaction")
            .Payload.Deserialize<AgentContextCompactionRecord>()!;
        var exact=new ArtifactStore(seed.TaskRoot).ReadText(compact.Source.Id);
        Check(exact.Contains(seed.EarlyMarker,StringComparison.Ordinal),
            "Restart lost exact source backing the compaction.");
        Check(journal.Where(x=>x.Kind is "task-state" or "revision")
            .Any(x=>x.Payload.GetRawText().Contains(seed.Correction,StringComparison.Ordinal)),
            "Restart lost exact correction source.");
        var current=archive.Get(seed.TaskId)!;
        Check(current.GoalState?.RevisionId==seed.Goals.RevisionId,
            "Restart changed current goal revision.");
    }

    private static void AssertGoalState(H2AgentTaskSummary summary,bool expectPdfPending)
    {
        var state=summary.GoalState??throw new InvalidOperationException("GoalState missing.");
        Check(state.Outcomes.Single(x=>x.Requirement==OldRequirement).Status=="Superseded",
            "Old requirement was revived.");
        Check(state.Outcomes.Single(x=>x.Requirement==NewRequirement).Status=="Verified",
            "Current title requirement lost verified state.");
        Check(state.Outcomes.Single(x=>x.Requirement==PreserveRequirement).Status=="Verified",
            "Control-file preservation lost verified state.");
        var pdf=state.Outcomes.SingleOrDefault(x=>x.Requirement==PdfRequirement);
        Check(expectPdfPending?pdf?.Status=="Pending":pdf is null,
            "PDF outcome state is inconsistent with corpus mode.");
    }

    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter,Guid id)
    {
        var until=DateTime.UtcNow.AddSeconds(20);
        while(DateTime.UtcNow<until)
        {
            var s=adapter.GetTaskSummary(id);
            if(H2AgentActivity.IsTerminal(s.Status))return s;
            Thread.Sleep(10);
        }
        adapter.CancelTask(id);throw new TimeoutException("AR-080 task did not finish.");
    }

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> source)
    {
        var list=new List<AgentTransportEvent>();
        await foreach(var item in source)list.Add(item);
        return list;
    }

    private static byte[] Docx(string title,string body)
    {
        using var stream=new MemoryStream();
        using(var doc=WordprocessingDocument.Create(stream,DocumentFormat.OpenXml.WordprocessingDocumentType.Document,true))
        {
            var main=doc.AddMainDocumentPart();
            main.Document=new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text(title))),
                new W.Paragraph(new W.Run(new W.Text(body))),
                new W.SectionProperties()));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static string ReadDocx(string path)
    {
        using var doc=WordprocessingDocument.Open(path,false);
        return doc.MainDocumentPart?.Document?.InnerText??"";
    }

    private static int CountText(this string text,string value)
    {
        var count=0;var offset=0;
        while((offset=text.IndexOf(value,offset,StringComparison.Ordinal))>=0){count++;offset+=value.Length;}
        return count;
    }

    private static string Hash(byte[] bytes)
        =>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WithRoot(string name,Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar080-"+name+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private static T Expect<T>(Action action,string contains)where T:Exception
    {
        try{action();}
        catch(T ex)when(ex.Message.Contains(contains,StringComparison.OrdinalIgnoreCase)){return ex;}
        throw new InvalidOperationException("Expected "+typeof(T).Name+" containing "+contains);
    }

    private static void Save(string name,object value)
    {
        var root=Environment.GetEnvironmentVariable("H2_AR080_EVIDENCE_DIR");
        if(string.IsNullOrWhiteSpace(root))return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,name+".json"),
            JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
    }

    private sealed class FinalOnlyChatFactory:IAgentTransportFactory
    {
        public List<string> Bodies{get;}=[];
        public List<AgentRequestBudgetReceipt> Budgets{get;}=[];
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)
        {
            var transport=new ChatCompletionsTransport(profile,apiKey,new Handler(this));
            ((IAgentRequestBudgetSource)transport).RequestBudgetEvaluated+=Budgets.Add;
            return transport;
        }
        private sealed class Handler(FinalOnlyChatFactory owner):HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                owner.Bodies.Add(await request.Content!.ReadAsStringAsync(token));
                var frame=JsonSerializer.Serialize(new{
                    choices=new[]{new{index=0,delta=JsonSerializer.SerializeToElement(new{content="MODEL CLAIMED DONE"}),
                        finish_reason="stop"}}
                });
                var raw="data: "+frame+"\n\ndata: [DONE]\n\n";
                return new(HttpStatusCode.OK){Content=new StringContent(raw,Encoding.UTF8,"text/event-stream")};
            }
        }
    }

    private sealed record CompactionMeasurement(
        IReadOnlyList<AgentRequestBudgetReceipt> Budgets,int Compactions);

    private sealed record SeedFixture(
        Guid TaskId,Guid ThreadId,Guid OldTurn,string Workspace,string StateRoot,string TaskRoot,
        string DocAPath,string DocBPath,string PdfPath,string DocAHash,string DocBHash,
        AgentGoalState Goals,string Correction,string EarlyMarker,string ResourceKey,string UnsavedSession,
        H2AgentOperationRecord? Operation,CompactionMeasurement Compaction);
}
