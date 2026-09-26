using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

internal static class H2AgentSteeringConcurrencyTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-042 E1 two runtime schedulers serialize same resource without globally blocking distinct resources", () =>
        {
            var coordinator=new HostResourceMutationCoordinator();
            using var first=new ToolExecutionScheduler(coordinator);
            using var second=new ToolExecutionScheduler(coordinator);
            var active=0;var max=0;
            var descriptor=MutationDescriptor("ar042-shared",async (call,ct)=>{
                var now=Interlocked.Increment(ref active);RaiseMax(ref max,now);
                await Task.Delay(80,ct).ConfigureAwait(false);
                Interlocked.Decrement(ref active);
                return Success();
            });
            var args=JsonSerializer.SerializeToElement(new{});
            Task.WhenAll(
                first.ExecuteBatchAsync([new(descriptor,new("same-1",descriptor.Name,args),"file:/same")],CancellationToken.None),
                second.ExecuteBatchAsync([new(descriptor,new("same-2",descriptor.Name,args),"file:/same")],CancellationToken.None))
                .GetAwaiter().GetResult();
            Check(max==1,"Same-resource mutations from separate runtimes overlapped.");

            active=0;max=0;
            var both=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parallel=MutationDescriptor("ar042-distinct",async (call,ct)=>{
                var now=Interlocked.Increment(ref active);RaiseMax(ref max,now);
                if(now>=2)both.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Decrement(ref active);
                return Success();
            });
            var a=first.ExecuteBatchAsync([new(parallel,new("a",parallel.Name,args),"file:/a")],CancellationToken.None);
            var b=second.ExecuteBatchAsync([new(parallel,new("b",parallel.Name,args),"file:/b")],CancellationToken.None);
            Check(both.Task.Wait(TimeSpan.FromSeconds(3)),"Distinct resources were serialized by a global mutation lock.");
            release.TrySetResult();Task.WhenAll(a,b).GetAwaiter().GetResult();
            Check(max>=2,"Distinct-resource mutations never overlapped.");
        });

        test("AR-042 E1 uncertain resource fence is shared across separate runtime schedulers", () =>
        {
            var coordinator=new HostResourceMutationCoordinator();
            using var first=new ToolExecutionScheduler(coordinator);
            using var second=new ToolExecutionScheduler(coordinator);
            var calls=0;
            var descriptor=MutationDescriptor("ar042-uncertain",(call,ct)=>{
                Interlocked.Increment(ref calls);
                if(call.Id=="unknown")throw new TimeoutException("controlled ambiguous mutation");
                return ValueTask.FromResult(Success());
            });
            var args=JsonSerializer.SerializeToElement(new{});
            var unknown=first.ExecuteBatchAsync(
                [new(descriptor,new("unknown",descriptor.Name,args),"excel:session:one")],CancellationToken.None)
                .GetAwaiter().GetResult().Single();
            Check(unknown.Outcome?.Effect==ToolMutationEffect.Unknown
                && unknown.Outcome.Status==ToolOutcomeStatus.OutcomeUnknown,
                "Ambiguous mutation did not establish an uncertainty fence.");

            var blocked=second.ExecuteBatchAsync(
                [new(descriptor,new("retry",descriptor.Name,args),"excel:session:one")],CancellationToken.None)
                .GetAwaiter().GetResult().Single();
            Check(calls==1
                && blocked.Outcome?.Error?.Code=="outcome_unknown"
                && blocked.Outcome.Effect==ToolMutationEffect.None,
                "Another task replayed a mutation on a resource fenced as uncertain.");
        });

        test("AR-042 E1 permission is revalidated after resource wait and before durable dispatch", () =>
        {
            var coordinator=new HostResourceMutationCoordinator();
            using var first=new ToolExecutionScheduler(coordinator);
            using var second=new ToolExecutionScheduler(coordinator);
            var holderEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseHolder=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedEffects=0;var queuedDispatches=0;var rechecks=0;var allowed=true;
            var descriptor=MutationDescriptor("ar042-expiry",async (call,ct)=>{
                if(call.Id=="holder")
                {
                    holderEntered.TrySetResult();
                    await releaseHolder.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else Interlocked.Increment(ref queuedEffects);
                return Success();
            });
            var args=JsonSerializer.SerializeToElement(new{});
            var holder=first.ExecuteBatchAsync(
                [new(descriptor,new("holder",descriptor.Name,args),"file:/lease")],CancellationToken.None);
            Check(holderEntered.Task.Wait(TimeSpan.FromSeconds(3)),"Holder mutation did not acquire the resource.");

            var queued=second.ExecuteBatchAsync(
                [new ToolExecutionRequest(descriptor,new("queued",descriptor.Name,args),"file:/lease")
                {
                    BeforeDispatchAsync=(call,ct)=>{
                        Interlocked.Increment(ref rechecks);
                        return ValueTask.FromResult<ToolExecutionOutput?>(allowed?null:
                            ToolOutcomeBridge.Failure(call,descriptor,"expired_permission",
                                ToolErrorPhase.Preflight,ToolMutationEffect.None));
                    },
                    BeforeExecute=_=>Interlocked.Increment(ref queuedDispatches)
                }],CancellationToken.None);
            Thread.Sleep(80);
            Check(rechecks==0,"Permission revalidation ran before the queued task acquired the resource.");
            allowed=false;releaseHolder.TrySetResult();
            Task.WhenAll(holder,queued).GetAwaiter().GetResult();
            var result=queued.Result.Single();
            Check(rechecks==1&&queuedEffects==0&&queuedDispatches==0
                && result.Outcome?.Error?.Code=="expired_permission"
                && result.Outcome.Effect==ToolMutationEffect.None,
                "Expired permission crossed the post-wait pre-dispatch boundary.");
        });

        test("AR-042 E1 steering revision cannot rewrite the revision of an already dispatched mutation", () =>
        {
            var task=Guid.NewGuid();
            var firstInput=new AgentGoalInput(Guid.NewGuid(),"create output A; preserve output B");
            var goals=AgentGoalState.Create(task,"fixture:ar042",firstInput);
            var dispatched=goals.RevisionId;
            goals=goals.RecordMutation(dispatched);
            var steerId=Guid.NewGuid();
            var revised=goals.Apply(new(steerId,"also update output C"));
            Check(revised.RevisionId!=dispatched
                && revised.MutationRevisions.SequenceEqual([dispatched]),
                "Steering rewrote or reassigned an already-dispatched mutation revision.");
            var replay=revised.Apply(new(steerId,"also update output C"));
            Check(replay.RevisionId==revised.RevisionId&&replay.Revisions.Count==revised.Revisions.Count,
                "Duplicate steering input created another goal revision.");
            Expect<InvalidOperationException>(()=>revised.Apply(new(steerId,"different steering payload")));
        });

        test("AR-042 E2 production adapter deduplicates concurrent TurnId reconnect and journals steering receipt without raw text", () =>
            WithRoot(root =>
            {
                var state=Path.Combine(root,"state");
                var factory=new HoldTransportFactory();
                var thread=Guid.NewGuid();var turn=Guid.NewGuid();
                var context=new H2AgentTaskContext(root,null,ThreadId:thread,TurnId:turn);
                Guid task;
                using(var adapter=Adapter(state,factory))
                {
                    var starts=Enumerable.Range(0,8)
                        .Select(_=>Task.Run(async()=>await adapter.StartTaskAsync(null,"AR-042 reconnect",context).ConfigureAwait(false)))
                        .ToArray();
                    Task.WaitAll(starts);
                    var ids=starts.Select(x=>x.Result).Distinct().ToArray();
                    Check(ids.Length==1,"Concurrent reconnect created duplicate TaskIds.");
                    task=ids[0];
                    Expect<InvalidOperationException>(()=>
                        adapter.StartTaskAsync(null,"different goal",context).GetAwaiter().GetResult());

                    var input=Guid.NewGuid();const string steering="AR042 steering payload must not persist raw";
                    Check(adapter.SupplementTask(task,input,steering),"Steering input was not accepted.");
                    Check(adapter.SupplementTask(task,input,steering),"Duplicate steering ACK was not idempotent.");
                    Check(!adapter.SupplementTask(task,input,"different payload"),"Same steering ID accepted different text.");
                    factory.Release.TrySetResult();
                    var done=Wait(adapter,task);
                    Check(done.Status==H2AgentTaskStatus.Completed
                        && factory.Starts==1
                        && factory.Inputs.SequenceEqual([steering]),
                        done.Error??"Steering/reconnect production path failed.");

                    var journal=string.Join("\n",Directory.EnumerateFiles(
                        Path.Combine(state,"integration","journal-v2"),"event-*.json")
                        .Order(StringComparer.Ordinal).Select(File.ReadAllText));
                    var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(steering))).ToLowerInvariant();
                    Check(journal.Contains("steering-input",StringComparison.Ordinal)
                        && journal.Contains(hash,StringComparison.Ordinal)
                        && !journal.Contains(steering,StringComparison.Ordinal),
                        "Durable steering receipt is missing or persisted raw steering text.");
                }

                var restartedFactory=new HoldTransportFactory();
                using var restarted=Adapter(state,restartedFactory);
                var same=restarted.StartTaskAsync(null,"AR-042 reconnect",context).GetAwaiter().GetResult();
                Check(same==task&&restartedFactory.Starts==0,
                    "Restart/reconnect duplicated a durable turn instead of returning the existing task.");
            }));

        test("AR-042 E2 cancelling a queued turn never cancels or starts the running owner", () =>
            WithRoot(root =>
            {
                var factory=new HoldTransportFactory();
                using var adapter=Adapter(Path.Combine(root,"state"),factory);
                var thread=Guid.NewGuid();
                var first=adapter.StartTaskAsync(null,"first",new(root,null,ThreadId:thread,TurnId:Guid.NewGuid())).Result;
                var second=adapter.StartTaskAsync(null,"second",new(root,null,ThreadId:thread,TurnId:Guid.NewGuid(),AfterTaskId:first)).Result;
                var cancelled=adapter.StartTaskAsync(null,"cancelled",new(root,null,ThreadId:thread,TurnId:Guid.NewGuid(),AfterTaskId:second)).Result;
                Check(adapter.GetTaskSummary(second).Status==H2AgentTaskStatus.Queued&&factory.Starts==1,
                    "Queued task ran concurrently with its predecessor.");
                adapter.CancelTask(cancelled);
                factory.Release.TrySetResult();
                Check(Wait(adapter,first).Status==H2AgentTaskStatus.Completed,"Queued cancellation cancelled running predecessor.");
                Check(Wait(adapter,second).Status==H2AgentTaskStatus.Completed,"Queue failed to continue after predecessor.");
                Check(Wait(adapter,cancelled).Status==H2AgentTaskStatus.Cancelled&&factory.Starts==2,
                    "Cancelled queued task executed or affected another owner.");
            }));
    }

    private static ToolDescriptor MutationDescriptor(
        string executorId,
        Func<global::H2AgentLab.ToolCall,CancellationToken,ValueTask<string>> execute)
        => new(
            "fixture_mutate",
            new("fixture","AR-042 deterministic mutation fixture."),
            "Mutating fixture.",
            AgentToolRisk.Medium,
            AgentToolAccess.Mutating,
            supportsParallel:true,
            "v1",
            JsonSerializer.SerializeToElement(new{type="object",properties=new{}}),
            new DelegatingToolExecutor(executorId,execute),
            resourceScope:new("fixture:mutation","fixture:*"),
            serializationKey:"fixture:mutation",
            resultFormat:ToolResultFormat.Json);

    private static string Success()
        => JsonSerializer.Serialize(new{ok=true,mutationApplied=true});

    private static void RaiseMax(ref int target,int value)
    {
        while(true)
        {
            var current=Volatile.Read(ref target);
            if(value<=current||Interlocked.CompareExchange(ref target,value,current)==current)return;
        }
    }

    private static H2ProductionAgentAdapter Adapter(string state,HoldTransportFactory factory)
        => new(state,()=>new(new AiProfile
        {
            Protocol=AiProtocol.OpenAiChat,
            BaseUrl="https://example.test/v1",
            Model="ar042-ci"
        },""),factory);

    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter,Guid id)
    {
        var until=DateTime.UtcNow.AddSeconds(12);
        while(DateTime.UtcNow<until)
        {
            var item=adapter.GetTaskSummary(id);
            if(H2AgentActivity.IsTerminal(item.Status))return item;
            Thread.Sleep(10);
        }
        throw new TimeoutException("AR-042 task did not finish.");
    }

    private static void WithRoot(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar042-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}
        finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action)where T:Exception
    {
        try{action();}
        catch(T){return;}
        throw new InvalidOperationException("Expected "+typeof(T).Name);
    }

    private sealed class HoldTransportFactory:IAgentTransportFactory
    {
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Inputs=[];
        public int Starts;

        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)
            =>new Transport(this);

        private sealed class Transport(HoldTransportFactory owner):IAgentTransport
        {
            public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.ChatCompletionsFallback;

            public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
                AgentTransportStartRequest request,
                [EnumeratorCancellation]CancellationToken cancellationToken=default)
            {
                Interlocked.Increment(ref owner.Starts);
                await owner.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                yield return AgentTransportEvent.TextDeltaEvent("initial complete");
                yield return AgentTransportEvent.Complete("ar042-start","stop");
            }

            public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
                AgentTransportContinuationRequest request,
                [EnumeratorCancellation]CancellationToken cancellationToken=default)
            {
                lock(owner.Inputs)owner.Inputs.AddRange(request.SupplementalUserMessages??[]);
                await Task.Yield();
                yield return AgentTransportEvent.TextDeltaEvent("steering complete");
                yield return AgentTransportEvent.Complete("ar042-cont","stop");
            }

            public void Cancel(){}
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
}
