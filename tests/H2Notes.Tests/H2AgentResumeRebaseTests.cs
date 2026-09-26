using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2Notes.Core;

internal static class H2AgentResumeRebaseTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-052 E1 canonical goal snapshot restores exact outcome and evidence state",()=>WithRoot(root=>
        {
            var task=Guid.NewGuid();var seeded=Seed(root,task,withUnknown:false);
            var restored=AgentGoalState.Restore(task,seeded.Scope,seeded.Summary.GoalState!,seeded.Summary.Evidence);
            Check(restored.RevisionId==seeded.Summary.GoalState!.RevisionId
                && restored.MutationRevisions.SequenceEqual(seeded.Summary.GoalState.MutationRevisions)
                && restored.Obligations.Count==seeded.Summary.GoalState.Outcomes.Count
                && restored.Obligations.All(o=>o.Status==AgentObligationStatus.Verified)
                && restored.Obligations.SelectMany(o=>o.Evidence).All(e=>e.ReferenceId==seeded.EvidenceId),
                "Canonical outcome/evidence state changed during rehydration.");
        }));

        foreach(var protocol in new[]{AiProtocol.Ollama,AiProtocol.OpenAiChat,AiProtocol.OpenAiResponses})
        {
            var captured=protocol;
            test("AR-052 E2 resumes same TaskId on fresh "+captured+" turn without opaque continuation",()=>WithRoot(root=>
            {
                var task=Guid.NewGuid();var seed=Seed(root,task,withUnknown:false);
                var profile=Profile(captured,"resume-"+captured);
                var factory=new RealProtocolFactory(captured);
                var selected=Guid.NewGuid();var resolverCalls=0;
                using var adapter=new H2ProductionAgentAdapter(root,()=>throw new InvalidOperationException("global resolver must not be used"),
                    transportFactory:factory,requestModelResolver:(id,effort)=>
                    {
                        resolverCalls++;Check(id==selected&&effort=="low","Selected resume model/effort was not honored.");
                        return new(profile,"fixture-key");
                    });
                var interrupted=adapter.GetTaskSummary(task);
                Check(interrupted.Recovery is {Interrupted:true,ReconcileRequired:false},
                    "Seed task was not projected as safely interrupted.");

                var turn=Guid.NewGuid();
                var context=new H2AgentTaskContext(seed.Workspace,"resume-host-summary",
                    ModelProfileId:selected,ReasoningEffort:"low",ThreadId:seed.ThreadId,TurnId:turn);
                var returned=adapter.ResumeTaskAsync(task,context,true).GetAwaiter().GetResult();
                var done=Wait(adapter,returned);
                Check(returned==task&&done.TaskId==task&&done.TurnId==turn&&done.ThreadId==seed.ThreadId,
                    "Resume changed task/thread identity or failed to bind the new turn.");
                Check(done.Status==H2AgentTaskStatus.Completed&&done.GoalState is not null
                    && done.GoalState.RevisionId==seed.Summary.GoalState!.RevisionId
                    && done.GoalState.MutationRevisions.SequenceEqual(seed.Summary.GoalState.MutationRevisions)
                    && done.GoalState.Outcomes.All(o=>o.Status=="Verified")
                    && done.Evidence.Any(e=>e.EvidenceId==seed.EvidenceId),
                    "Resume lost verified work state/evidence.");
                Check(resolverCalls==1&&factory.CreatedProfiles.Count==1&&factory.CreatedProfiles[0].Protocol==captured,
                    "Resume did not allocate exactly one explicitly selected transport.");
                Check(factory.Bodies.Count==1
                    && factory.Bodies[0].Contains("HOST_RESUME_STATE_CANONICAL_NOT_PROVIDER_CONTINUATION",StringComparison.Ordinal)
                    && factory.Bodies[0].Contains(seed.Summary.GoalState.RevisionId,StringComparison.Ordinal)
                    && !factory.Bodies[0].Contains(seed.OldTranscriptMarker,StringComparison.Ordinal)
                    && !factory.Bodies[0].Contains("previous_response_id",StringComparison.OrdinalIgnoreCase),
                    "Fresh transport request replayed old transcript/provider continuation or lost canonical state.");
                Check(done.GoalState.MutationRevisions.Count==1,
                    "Resume without tool calls duplicated or discarded historical mutation revision.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));
        }

        test("AR-052 E2 unresolved mutation blocks before model or transport allocation",()=>WithRoot(root=>
        {
            var task=Guid.NewGuid();var seed=Seed(root,task,withUnknown:true);
            var created=0;var resolver=0;var factory=new CountingFactory(()=>created++);
            using var adapter=new H2ProductionAgentAdapter(root,()=>throw new InvalidOperationException("unused"),
                transportFactory:factory,requestModelResolver:(id,effort)=>
                {resolver++;return new(Profile(AiProtocol.Ollama,"blocked"),"");});
            var context=new H2AgentTaskContext(seed.Workspace,"blocked",ModelProfileId:Guid.NewGuid(),
                ThreadId:seed.ThreadId,TurnId:Guid.NewGuid());
            Expect<InvalidOperationException>(()=>adapter.ResumeTaskAsync(task,context,true).GetAwaiter().GetResult(),
                "Reconcile the exact resource");
            Check(created==0&&resolver==0,"Unreconciled effect reached model/provider allocation.");

            var recovery=adapter.GetTaskSummary(task).Recovery!;
            var operation=recovery.Operations.Single(o=>o.ReconciliationState is null);
            var result=adapter.ReconcileInterruptedTask(task,
            [
                new(operation.InvocationId,H2AgentReconcileDisposition.Verified,
                    seed.ResourceKey,"v-resolved","host verified exact effect",DateTime.UtcNow)
            ]);
            Check(!result.ReconcileRequired&&result.RequiresFreshPermission,
                "Exact reconciliation did not clear the restart blocker.");
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }));

        test("AR-052 E2 mutation resume requires a fresh active permission scope",()=>WithRoot(root=>
        {
            var task=Guid.NewGuid();var seed=Seed(root,task,withUnknown:false);
            var resolver=0;var factory=new RealProtocolFactory(AiProtocol.Ollama);
            using var adapter=new H2ProductionAgentAdapter(root,()=>throw new InvalidOperationException("unused"),
                transportFactory:factory,requestModelResolver:(id,effort)=>
                {resolver++;return new(Profile(AiProtocol.Ollama,"fresh-permission"),"");});
            var now=DateTime.UtcNow;
            var expired=new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess,H2AgentResourceScopeKind.Machine,
                H2AgentPermissionScope.CurrentMachineResourceKey,true,false,now.AddMinutes(-20),now.AddMinutes(-10));
            Expect<InvalidOperationException>(()=>adapter.ResumeTaskAsync(task,
                new(seed.Workspace,"expired",PermissionScope:expired,ModelProfileId:Guid.NewGuid(),
                    ThreadId:seed.ThreadId,TurnId:Guid.NewGuid()),false).GetAwaiter().GetResult(),
                "expired or revoked");
            Check(resolver==0&&factory.Bodies.Count==0,"Expired old grant reached model/provider allocation.");

            var fresh=new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess,H2AgentResourceScopeKind.Machine,
                H2AgentPermissionScope.CurrentMachineResourceKey,true,false,now,now.AddMinutes(20));
            var resumed=adapter.ResumeTaskAsync(task,
                new(seed.Workspace,"fresh",PermissionScope:fresh,ModelProfileId:Guid.NewGuid(),
                    ThreadId:seed.ThreadId,TurnId:Guid.NewGuid()),false).GetAwaiter().GetResult();
            Check(Wait(adapter,resumed).Status==H2AgentTaskStatus.Completed&&resolver==1,
                "Fresh permission did not admit explicit resume.");
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }));

        test("AR-052 E2 selected-provider failure never auto-falls back and can be explicitly rebased later",()=>WithRoot(root=>
        {
            var task=Guid.NewGuid();var seed=Seed(root,task,withUnknown:false);
            var selectedA=Guid.NewGuid();var selectedB=Guid.NewGuid();
            var factory=new SwitchFactory();
            using var adapter=new H2ProductionAgentAdapter(root,()=>throw new InvalidOperationException("global fallback forbidden"),
                transportFactory:factory,requestModelResolver:(id,effort)=>id==selectedA
                    ? new(Profile(AiProtocol.OpenAiChat,"provider-A"),"a")
                    : id==selectedB?new(Profile(AiProtocol.Ollama,"provider-B"),"b")
                    : throw new InvalidOperationException("unapproved-provider"));

            var firstTurn=Guid.NewGuid();
            var same=adapter.ResumeTaskAsync(task,new(seed.Workspace,"provider A",
                ModelProfileId:selectedA,ThreadId:seed.ThreadId,TurnId:firstTurn),true).GetAwaiter().GetResult();
            var failed=Wait(adapter,same);
            Check(failed.Status==H2AgentTaskStatus.Failed&&factory.Created.Count==1
                &&factory.Created[0].Model=="provider-A","Provider A failure silently fell back.");

            var secondTurn=Guid.NewGuid();Guid resumed=Guid.Empty;
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(DateTime.UtcNow<deadline)
            {
                try
                {
                    resumed=adapter.ResumeTaskAsync(task,new(seed.Workspace,"provider B",
                        ModelProfileId:selectedB,ThreadId:seed.ThreadId,TurnId:secondTurn),true)
                        .GetAwaiter().GetResult();
                    break;
                }
                catch(InvalidOperationException ex) when(ex.Message.Contains("still live",StringComparison.Ordinal))
                {Thread.Sleep(10);}
            }
            Check(resumed==task,"Explicit provider rebase did not retain TaskId.");
            var done=Wait(adapter,resumed);
            Check(done.Status==H2AgentTaskStatus.Completed&&done.TurnId==secondTurn
                &&factory.Created.Count==2&&factory.Created[1].Model=="provider-B",
                "Explicit second provider did not create a fresh successful turn.");
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }));

        test("AR-052 E2 durable steering receipt ACKs reconnect without reapplying old correction",()=>WithRoot(root=>
        {
            var task=Guid.NewGuid();var seed=Seed(root,task,withUnknown:false);
            var input=Guid.NewGuid();const string correction="Preserve the exact verified outcome.";
            using(var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration")))
                Check(archive.RecordSteeringInput(task,input,correction),"Could not seed steering receipt.");

            var script=new HoldingFactory();
            using var adapter=new H2ProductionAgentAdapter(root,()=>new(Profile(AiProtocol.Ollama,"steering"),""),
                transportFactory:script);
            var resumed=adapter.ResumeTaskAsync(task,new(seed.Workspace,"steering",
                ThreadId:seed.ThreadId,TurnId:Guid.NewGuid()),true).GetAwaiter().GetResult();
            Check(adapter.SupplementTask(resumed,input,correction),
                "Reconnect did not acknowledge the durable steering receipt.");
            Check(!adapter.SupplementTask(resumed,input,correction+" changed"),
                "Reused steering ID with different text was accepted.");
            script.Release.TrySetResult();
            var done=Wait(adapter,resumed);
            Check(done.Status==H2AgentTaskStatus.Completed&&script.Supplements.Count==0,
                "Durable steering input was applied twice after restart.");
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }));
    }

    private static SeedFixture Seed(string root,Guid task,bool withUnknown)
    {
        var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
        var scope="workspace:"+workspace;
        var thread=Guid.NewGuid();var oldTurn=Guid.NewGuid();var input=Guid.NewGuid();
        var goals=AgentGoalState.Create(task,scope,new(input,"Create report; preserve formulas"));
        var evidenceId="resume-evidence-"+Guid.NewGuid().ToString("N");
        var evidence=new H2AgentEvidence(evidenceId,AgentEvidenceKind.ToolResult.ToString(),
            new string('a',64),"verified prior effect",Provenance:"AR-052 seed",VerificationPassed:true);
        var goalSnapshot=new H2AgentGoalSnapshot(goals.RevisionId,
            goals.Revisions.Select(r=>new H2AgentGoalRevisionSnapshot(r.Id,r.ParentId,r.Sequence,r.SourceId,
                r.SourceText,r.Added,r.Retired)).ToArray(),
            goals.Obligations.Select(o=>new H2AgentOutcomeSnapshot(o.Id,o.Requirement,o.SourceId,o.RevisionId,
                o.TargetScope,AgentObligationStatus.Verified.ToString(),o.ReplacedBy,[evidenceId])).ToArray(),
            [goals.RevisionId],scope);
        var now=DateTime.UtcNow;
        var summary=new H2AgentTaskSummary(task,null,"Create report; preserve formulas",H2AgentTaskStatus.Running,
            null,[evidence],null,null,now,now,thread,oldTurn){GoalState=goalSnapshot};
        var marker="ARCHIVED_TRANSCRIPT_SHOULD_NOT_REPLAY_"+Guid.NewGuid().ToString("N");
        var resource="resume-resource-"+Guid.NewGuid().ToString("N");
        using(var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration")))
        {
            archive.Upsert(summary);
            archive.AppendProgress(task,new(0,now,"commentary","old-transcript",marker));
            var op=Operation(task,oldTurn,goals.RevisionId,resource);
            archive.RecordOperation(task,op);
            archive.RecordOperation(task,op with
            {
                State="Result",Status="Succeeded",Effect="Applied",OutputSha256=Hash("old-result"),
                ReconciliationState=withUnknown?null:"Verified",
                ReconciliationEvidenceId=withUnknown?null:evidenceId,
                ReconciledObservedVersion=withUnknown?null:"v-old",
                ReconciledUtc=withUnknown?null:now
            });
        }
        return new SeedFixture(summary,workspace,scope,thread,evidenceId,marker,resource);
    }

    private static H2AgentOperationRecord Operation(Guid task,Guid turn,string revision,string resource)
        => new(Guid.NewGuid(),"op-"+Guid.NewGuid().ToString("N"),turn,revision,
            "old-call","fixture.write","Dispatched","NotKnown","Unknown",
            Hash("{}"),Hash(resource),null,null);

    private static string Hash(string value)
        =>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AiProfile Profile(AiProtocol protocol,string model)=>new()
    {
        Protocol=protocol,
        Model=model,
        BaseUrl=protocol==AiProtocol.Ollama?"http://localhost:11434":"https://example.test/v1",
        RequestBudget=new(){ContextLimitTokens=32000,ContextLimitSource="ar052-fixture-v1",
            ReservedOutputTokens=256,SafetyMarginTokens=128}
    };

    private sealed record SeedFixture(H2AgentTaskSummary Summary,string Workspace,string Scope,Guid ThreadId,
        string EvidenceId,string OldTranscriptMarker,string ResourceKey);

    private sealed class RealProtocolFactory(AiProtocol protocol):IAgentTransportFactory
    {
        public List<AiProfile> CreatedProfiles{get;}=[];
        public List<string> Bodies{get;}=[];
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)
        {
            CreatedProfiles.Add(profile.Copy());
            var handler=new ResumeHandler(protocol,Bodies);
            return protocol switch
            {
                AiProtocol.Ollama=>new OllamaTransport(profile,handler),
                AiProtocol.OpenAiChat=>new ChatCompletionsTransport(profile,apiKey,handler),
                AiProtocol.OpenAiResponses=>new OpenAiResponsesTransport(profile,apiKey,handler),
                _=>throw new NotSupportedException()
            };
        }
    }

    private sealed class CountingFactory(Action created):IAgentTransportFactory
    {
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)
        {created();return new OllamaTransport(profile,new ResumeHandler(AiProtocol.Ollama,[]));}
    }

    private sealed class SwitchFactory:IAgentTransportFactory
    {
        public List<AiProfile> Created{get;}=[];
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)
        {
            Created.Add(profile.Copy());
            if(profile.Model=="provider-A")return new ThrowingTransport(profile);
            return new OllamaTransport(profile,new ResumeHandler(AiProtocol.Ollama,[]));
        }
    }

    private sealed class HoldingFactory:IAgentTransportFactory,IAgentTransport
    {
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Supplements=[];
        public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.OllamaNative;
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)=>this;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            await Release.Task.WaitAsync(cancellationToken);
            yield return AgentTransportEvent.TextDeltaEvent("resumed");
            yield return AgentTransportEvent.Complete();
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Supplements.AddRange(request.SupplementalUserMessages??[]);
            yield return AgentTransportEvent.TextDeltaEvent("continued");
            yield return AgentTransportEvent.Complete();
            await Task.CompletedTask;
        }
        public void Cancel(){Release.TrySetCanceled();}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class ThrowingTransport(AiProfile profile):IAgentTransport
    {
        public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.ChatCompletionsFallback;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            await Task.Yield();
            throw new IOException("provider-A unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken=default)
        {await Task.Yield();yield break;}
        public void Cancel(){}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class ResumeHandler(AiProtocol protocol,List<string> bodies):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var body=await request.Content!.ReadAsStringAsync(ct);bodies.Add(body);
            return protocol switch
            {
                AiProtocol.Ollama=>Ndjson(new
                {
                    message=new{role="assistant",content="resumed "+protocol},
                    done=true,done_reason="stop",prompt_eval_count=20,eval_count=3
                }),
                AiProtocol.OpenAiChat=>Sse(
                    """{"choices":[{"delta":{"content":"resumed chat"},"finish_reason":null}]}""",
                    """{"choices":[{"delta":{},"finish_reason":"stop"}]}"""),
                AiProtocol.OpenAiResponses=>Sse(
                    """{"type":"response.created","response":{"id":"resp_ar052","status":"in_progress"}}""",
                    """{"type":"response.output_text.delta","delta":"resumed responses"}""",
                    """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_ar052","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"resumed responses","annotations":[]}]}}""",
                    """{"type":"response.completed","response":{"id":"resp_ar052","status":"completed","output":[{"id":"msg_ar052","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"resumed responses","annotations":[]}]}],"usage":{"input_tokens":20,"output_tokens":3,"total_tokens":23}}}"""),
                _=>throw new NotSupportedException()
            };
        }
        private static HttpResponseMessage Ndjson(params object[] lines)
        {
            var body=string.Join("\n",lines.Select(item=>JsonSerializer.Serialize(item)))+"\n";
            var content=new StringContent(body);
            content.Headers.ContentType=new MediaTypeHeaderValue("application/x-ndjson");
            return new(HttpStatusCode.OK){Content=content};
        }
        private static HttpResponseMessage Sse(params string[] events)
        {
            var body=string.Join("\n\n",events.Select(x=>"data: "+x))+"\n\ndata: [DONE]\n\n";
            var content=new StringContent(body);
            content.Headers.ContentType=new MediaTypeHeaderValue("text/event-stream");
            return new(HttpStatusCode.OK){Content=content};
        }
    }

    private static H2AgentTaskSummary Wait(H2ProductionAgentAdapter adapter,Guid task)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline)
        {
            var summary=adapter.GetTaskSummary(task);
            if(summary.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Blocked
                or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Cancelled)return summary;
            Thread.Sleep(10);
        }
        var current=adapter.GetTaskSummary(task);
        var progress=adapter.ObserveTask(task,-1).Progress.TakeLast(8)
            .Select(p=>p.Kind+":"+p.Code).ToArray();
        adapter.CancelTask(task);
        throw new TimeoutException("AR-052 task did not finish. status="+current.Status
            +"; error="+current.Error+"; completion="+current.Completion?.State
            +"; progress="+string.Join(",",progress));
    }

    private static void Expect<T>(Action action,string contains)where T:Exception
    {
        try{action();}
        catch(T ex)when(ex.Message.Contains(contains,StringComparison.OrdinalIgnoreCase)){return;}
        throw new InvalidOperationException("Expected "+typeof(T).Name+" containing "+contains);
    }

    private static void Check(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}

    private static void WithRoot(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar052-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}finally{try{Directory.Delete(root,true);}catch{}}
    }
}
