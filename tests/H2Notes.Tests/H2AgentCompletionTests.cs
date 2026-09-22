using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Context;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>AR-033 E1 plus E2 actual production/runtime/file readback with a scripted transport.
/// Native Office/provider/model acceptance is NOT established by these fixtures.</summary>
internal static class H2AgentCompletionTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var mutation in new[] { false, true })
        foreach (var status in new[] { VerificationCriterionStatus.Failed, VerificationCriterionStatus.NotVerified })
            test("AR-033 verdict consistency rejects " + status + " with successful detail mutation=" + mutation, () =>
            {
                var c = Context(Contract(), "A", "one", mutation: mutation);
                var a = new AgentCompletionAssessment(true); a.Register(c);
                var detail = Covered("verify-a", "criterion-a", true, c, "A", "desired");
                var report = new VerificationReport(detail.VerifierId,
                    [new("criterion-a", status, c.Evidence.Select(e => e.ReferenceId),
                        status == VerificationCriterionStatus.Failed ? new("criterion-a", "Whole criterion failed") : null)])
                    { CallCoverage = detail.CallCoverage };
                Reject(() => a.Observe(c, report), "summary contradicts");
                Check(!a.ProofIds.Any() && a.UnverifiedMutations == (mutation ? 1 : 0),
                    "Contradictory summary changed accepted proof or mutation state.");
            });
        test("AR-033 verdict consistency preserves a valid mixed-target batch and later exact correction", () =>
        {
            var contract = Contract(); var a = new AgentCompletionAssessment(true);
            var first = Context(contract, "A", "first", mutation: true);
            var second = Context(contract, "B", "second", mutation: true);
            var batch = first with { Calls = first.Calls.Concat(second.Calls).ToArray(),
                Results = first.Results.Concat(second.Results).ToArray(),
                Evidence = first.Evidence.Concat(second.Evidence).ToArray(),
                MutationCallIds = first.MutationCallIds.Concat(second.MutationCallIds).ToArray() };
            a.Register(batch);
            var report = new VerificationReport("verify-a",
                [new("criterion-a", VerificationCriterionStatus.Failed, batch.Evidence.Select(e => e.ReferenceId),
                    new("criterion-a", "B still needs correction"))])
                { CallCoverage = Covered("verify-a", "criterion-a", true, first, "A", "desired").CallCoverage
                    .Concat(Covered("verify-a", "criterion-a", false, second, "B", "desired").CallCoverage).ToArray() };
            Check(!a.Observe(batch, report).Passed && a.UnverifiedMutations == 1,
                "Mixed verdict lost its failed target or rejected its successful target.");
            var correction = Context(contract, "B", "second", mutation: true); a.Register(correction);
            Check(a.Observe(correction, Covered("verify-a", "criterion-a", true, correction, "B", "desired")).Passed
                && a.UnverifiedMutations == 0, "Valid same-target correction remained blocked.");
        });
        test("AR-033 native verifier receipt is retained without manufacturing completion proof", () =>
        {
            var c=Context(Contract(),"A","one",mutation:true);var a=new AgentCompletionAssessment(true);a.Register(c);
            var input=Covered("verify-a","criterion-a",true,c,"A","desired");
            var report=a.Observe(c,new VerificationReport(input.VerifierId,input.Criteria,["native-run:readback-01"])
                {CallCoverage=input.CallCoverage});
            Check(report.ReportEvidenceIds.Contains("native-run:readback-01") && !a.ProofIds.Contains("native-run:readback-01"),
                "Native receipt was dropped or promoted to independent completion proof.");
        });
        test("AR-033 generic read-only verification still needs observed proof", () =>
        {
            var c = Context(Contract(), "A", "one"); var a = new AgentCompletionAssessment(true); a.Register(c);
            var r = new VerificationReport("fixture", [new(AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                VerificationCriterionStatus.Passed, ["missing-proof"])]);
            Check(!a.Observe(c,r).Passed, "Generic criterion bypassed actual proof resolution.");
        });
        test("AR-033 corrective write must recheck previously passed same-target preservation", () =>
        {
            var contract=Contract(); var a=new AgentCompletionAssessment(true);
            var before=Context(contract,"A","before",mutation:true); a.Register(before);
            var first=Covered("verify-a","criterion-a",true,before,"A","preserve");
            var second=Covered("verify-a","criterion-b",false,before,"A","fix");
            _=a.Observe(before,new VerificationReport("verify-a",first.Criteria.Concat(second.Criteria))
                { CallCoverage=first.CallCoverage.Concat(second.CallCoverage).ToArray() });
            var after=Context(contract,"A","after",mutation:true); a.Register(after);
            var r=Covered("verify-b","criterion-b",true,after,"A","fix") with
                { AlternateResolutions=[new(before.Calls[0].Invocation!.InvocationId,after.Calls[0].Invocation!.InvocationId,"criterion-b","A","fix")] };
            Reject(()=>a.Observe(after,r),"Alternate recovery");
        });
        test("AR-033 same verifier cannot erase a nonmutating failure on another resource", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var before = Context(contract, "A", "first"); a.Register(before);
            _ = a.Observe(before, Report("verify-a", "criterion-a", false, before));
            var after = Context(contract, "B", "second"); a.Register(after);
            var report = a.Observe(after, Report("verify-a", "criterion-a", true, after));
            Check(!report.Passed, "Same verifier erased a different resource failure.");
        });
        test("AR-033 same-resource readback correction retains an exact active proof", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var before = Context(contract, "A", "first"); a.Register(before);
            _ = a.Observe(before, Report("verify-a", "criterion-a", false, before));
            var after = Context(contract, "A", "second"); a.Register(after);
            var report = a.Observe(after, Report("verify-a", "criterion-a", true, after));
            Check(report.Passed && a.ProofIds.Contains("proof-second"), "Read-only completion proof was omitted.");
        });
        foreach (var uncertain in new[] { false, true })
            test("AR-033 trusted user waiver preserves uncertain effects=" + uncertain, () =>
            {
                var contract = Contract().WithUserInput(new(Guid.NewGuid(), "Write A; Write B"));
                var goal = contract.Goals!.Active.First().Id;
                var c = Context(contract, "A", "failed", error:true, effect:uncertain ? ToolMutationEffect.Unknown : ToolMutationEffect.None);
                var a = new AgentCompletionAssessment(true); a.Register(c);
                _ = a.Observe(c, Covered("verify-a", goal, false, c, "A", "desired"));
                var revised = contract.WithUserInput(new(Guid.NewGuid(), "Cancel outcome 1"));
                _ = a.ApplyRevision(revised);
                Check(a.IsResolved(c.Calls[0].Invocation!.InvocationId) == !uncertain,
                    "Trusted waiver either failed to retire unapplied work or erased an uncertain effect.");
                Check(revised.Goals!.Obligations.Single(o => o.Id == goal).Status == AgentObligationStatus.WaivedByUser,
                    "Waiver source history disappeared.");
            });
        test("AR-033 duplicate per-call proof claims are not silently overwritten", () =>
        {
            var c = Context(Contract(), "A", "one", mutation:true); var a = new AgentCompletionAssessment(true); a.Register(c);
            var r = Covered("verify-a", "criterion-a", true, c, "A", "desired");
            Reject(() => a.Observe(c,r with { CallCoverage = r.CallCoverage.Concat(r.CallCoverage).ToArray() }), "Duplicate");
        });
        test("AR-033 corrupt history cannot present a previous completion projection as current", () => Fixture(root =>
        {
            var now = DateTime.UtcNow; var id = Guid.NewGuid();
            var summary = new H2AgentTaskSummary(id,null,"Fixture",H2AgentTaskStatus.Completed,null,[],"old",null,now,now)
                { Completion = new("CompletedVerified",1,1,0,0,0,0,["fixture"],[]) };
            using (var archive = new AgentIntegrationTaskArchive(root))
            { archive.Upsert(summary); archive.Upsert(summary with { Status=H2AgentTaskStatus.Running }); }
            File.WriteAllText(Directory.GetFiles(Path.Combine(root,"journal-v2"),"event-*.json").Order().Last(),"{torn");
            using var reopened = new AgentIntegrationTaskArchive(root); var value=reopened.Get(id)!;
            Check(value.Status==H2AgentTaskStatus.Blocked && value.Completion?.State=="Interrupted",
                "Recovered prefix falsely retained a current CompletedVerified label.");
        }));
        test("AR-033 different verifier cannot erase another criterion failure", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var c1 = Context(contract, "A", "first"); a.Register(c1);
            _ = a.Observe(c1, Report("verify-a", "criterion-a", false, c1));
            var c2 = Context(contract, "B", "second"); a.Register(c2);
            var report = a.Observe(c2, Report("verify-b", "criterion-b", true, c2));
            Check(!report.Passed && report.Criteria.Single(c => c.CriterionId == "criterion-a").Status == VerificationCriterionStatus.Failed,
                "Different verifier erased failure.");
            Check(report.ContributingVerifierIds.Order().SequenceEqual(new[] { "verify-a", "verify-b" }), "Verifier identities lost.");
        });
        test("AR-033 all required verifier identities survive successful aggregation", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var c1 = Context(contract, "A", "first"); a.Register(c1); _ = a.Observe(c1, Report("verify-a", "criterion-a", true, c1));
            var c2 = Context(contract, "B", "second"); a.Register(c2); var report = a.Observe(c2, Report("verify-b", "criterion-b", true, c2));
            var outcome = VerificationCompletionGate.Evaluate(contract, [report]);
            AgentTaskCompletionGate.EnsureCanComplete(contract, outcome);
            Check(outcome.Passed && outcome.VerifierIds.Count == 2, "Required verifier provenance was not preserved.");
        });
        test("AR-033 generic latest pass cannot verify a different earlier mutation", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var c1 = Context(contract, "A", "first", mutation: true); a.Register(c1);
            _ = a.Observe(c1, Covered("verify-a", "criterion-a", false, c1, "A", "same"));
            var c2 = Context(contract, "B", "second", mutation: true); a.Register(c2);
            _ = a.Observe(c2, Covered("verify-a", "criterion-a", true, c2, "B", "same"));
            Check(a.UnverifiedMutations == 1, "Latest unrelated mutation erased earlier verification debt.");
        });
        test("AR-033 missing referenced observation cannot discharge a mutation", () =>
        {
            var a = new AgentCompletionAssessment(true); var c = Context(Contract(), "A", "one", mutation: true); a.Register(c);
            var r = Covered("verify-a", "criterion-a", true, c, "A", "same");
            r = r with { CallCoverage = [r.CallCoverage[0] with { EvidenceIds = ["proof-one", "missing"] }] };
            _ = a.Observe(c, r); Check(a.UnverifiedMutations == 1, "Missing proof was accepted.");
        });
        test("AR-033 conflicting observed hashes cannot discharge a mutation", () =>
        {
            var a = new AgentCompletionAssessment(true); var c = Context(Contract(), "A", "one", mutation: true);
            c = c with { Evidence = c.Evidence.Concat([new AgentEvidenceReference(AgentEvidenceKind.ToolResult, "proof-one", new string('b', 64))]).ToArray() };
            a.Register(c); _ = a.Observe(c, Covered("verify-a", "criterion-a", true, c, "A", "same"));
            Check(a.UnverifiedMutations == 1, "Ambiguous proof was accepted.");
        });
        test("AR-033 identical observed proof delivery remains valid", () =>
        {
            var a = new AgentCompletionAssessment(true); var c = Context(Contract(), "A", "one", mutation: true);
            c = c with { Evidence = c.Evidence.Concat(c.Evidence).ToArray() }; a.Register(c);
            _ = a.Observe(c, Covered("verify-a", "criterion-a", true, c, "A", "same"));
            Check(a.UnverifiedMutations == 0, "Identical redelivery was treated as a conflict.");
        });
        foreach (var mismatch in new[] { "target", "postcondition", "criterion", "foreign", "unknown", "running", "revision" })
            test("AR-033 alternate recovery rejects " + mismatch, () =>
            {
                var a = new AgentCompletionAssessment(true); var contract = Contract();
                var before = Context(contract, "A", "before", error: true, effect: mismatch == "unknown" ? ToolMutationEffect.Unknown : ToolMutationEffect.None,
                    running: mismatch == "running"); a.Register(before);
                _ = a.Observe(before, Covered("verify-a", "criterion-a", false, before, "A", "goal"));
                var nextContract = mismatch == "revision" ? contract.WithUserInput(new(Guid.NewGuid(), "Include a greeting")) : contract;
                var after = Context(nextContract, "A", "after", mutation: true); a.Register(after);
                var report = Covered("verify-b", "criterion-a", true, after, mismatch == "target" ? "B" : "A",
                    mismatch == "postcondition" ? "other" : "goal");
                report = report with { AlternateResolutions = [new(mismatch == "foreign" ? Guid.NewGuid() : before.Calls[0].Invocation!.InvocationId,
                    after.Calls[0].Invocation!.InvocationId, mismatch == "criterion" ? "unknown-criterion" : "criterion-a", "A", "goal")] };
                Reject(() => a.Observe(after, report), "Alternate recovery");
            });
        test("AR-033 explicit verified alternate retains linked source identity", () =>
        {
            var a = new AgentCompletionAssessment(true); var contract = Contract();
            var before = Context(contract, "A", "before", error: true); a.Register(before);
            _ = a.Observe(before, Covered("verify-a", "criterion-a", false, before, "A", "goal"));
            var after = Context(contract, "A", "after", mutation: true); a.Register(after);
            var report = Covered("verify-b", "criterion-a", true, after, "A", "goal") with
            { AlternateResolutions = [new(before.Calls[0].Invocation!.InvocationId, after.Calls[0].Invocation!.InvocationId, "criterion-a", "A", "goal")] };
            report = a.Observe(after, report);
            Check(a.IsResolved(before.Calls[0].Invocation!.InvocationId), "Verified alternate did not resolve its original attempt.");
            var projection = a.Snapshot(contract, 0, 0, report);
            Check(projection.Resolutions.Count == 1 && projection.Resolutions[0].EvidenceIds.Single() == "proof-after", "Resolution source proof missing.");
            // A replacement verifier does not magically satisfy a separately required verifier.
            Reject(() => AgentTaskCompletionGate.EnsureCanComplete(contract, VerificationCompletionGate.Evaluate(contract, [report])), "verification");
        });
        foreach (var project in new[] { false, true })
        {
            test("AR-033 RC-13 concrete " + (project ? "Project" : "Global") + " verified alternate completes exact file outcome", () =>
                Fixture(root => RunProduction(root, project, Mode.Alternate)));
            test("AR-033 RC-14 concrete " + (project ? "Project" : "Global") + " unrelated file cannot erase earlier failure", () =>
                Fixture(root => RunProduction(root, project, Mode.Unrelated)));
        }
        foreach (var mode in new[] { Mode.WrongTarget, Mode.MissingOutput, Mode.MissingVerifier, Mode.ModelClaims, Mode.LostProof, Mode.Unverified, Mode.Contradictory, Mode.FailedSummary, Mode.PendingSummary })
            test("AR-033 concrete completion remains blocked for " + mode, () => Fixture(root => RunProduction(root, false, mode)));
        test("AR-033 simple conversation is completed unverified not fabricated content verification", () => Fixture(root =>
        {
            var wire = new Wire([]); using var adapter = new H2ProductionAgentAdapter(Path.Combine(root,"state"), () => new(Profile(),""), transportFactory:wire);
            var id = adapter.StartTaskAsync(null,"Hello", ContextFor(root),true).Result;var result=Wait(adapter,id);Drain(adapter);
            Check(result.Status == H2AgentTaskStatus.Completed && result.Completion?.State == "CompletedUnverified", "Read-only text was advertised as verified.");
        }));
    }

    private enum Mode { Alternate, Unrelated, WrongTarget, MissingOutput, MissingVerifier, ModelClaims, LostProof, Unverified, Contradictory, FailedSummary, PendingSummary }
    private static void RunProduction(string root, bool project, Mode mode)
    {
        var failureFirst = mode is Mode.Alternate or Mode.Unrelated or Mode.WrongTarget or Mode.ModelClaims;
        var calls = new List<AgentTransportToolCall>();
        if(failureFirst) { calls.Add(Discover("fixture.method_a"));calls.Add(new("fail","fixture.method_a", "{\"path\":\"A.txt\"}")); }
        calls.Add(Discover("write_text"));calls.Add(Write(mode is Mode.Unrelated or Mode.WrongTarget or Mode.ModelClaims ? "B.txt":"A.txt"));
        if(mode == Mode.MissingVerifier) calls.Add(new("write-second","write_text",JsonSerializer.Serialize(new{path="B.txt",text="ONE",expectedHash=""})));
        var wire = new Wire(calls.ToArray()); var factory = new Factory(wire, mode);
        using var adapter = new H2ProductionAgentAdapter(Path.Combine(root,"state"), () => new(Profile(),""), runtimeFactory:factory);
        if(mode==Mode.LostProof) wire.BeforeFinal=()=>
        {
            foreach(var path in Directory.GetFiles(Path.Combine(factory.StateRoot!,"artifacts","context"),"*.txt")) File.Delete(path);
        };
        var goal=mode==Mode.MissingOutput ? "Write A.txt; Write B.txt; Export PDF" : mode==Mode.MissingVerifier ? "Write A.txt; Write B.txt" : "Write A.txt; Confirm A.txt";
        var id = adapter.StartTaskAsync(project ? Guid.NewGuid():null, goal, ContextFor(root), false).Result;
        var result=Wait(adapter,id);Drain(adapter);
        if(mode==Mode.Alternate)
        {
            Check(result.Status==H2AgentTaskStatus.Completed && result.Completion?.State=="CompletedVerified", result.Error??"Verified alternate remained blocked.");
            Check(File.ReadAllText(Path.Combine(root,"A.txt"))=="ONE" && !File.Exists(Path.Combine(root,"B.txt")),"Alternate changed the wrong file.");
            Check(result.Completion!.Resolutions.Any(r=>r.Reason=="verified-alternate"),"No linked alternate proof.");
        }
        else
        {
            Check(result.Status==H2AgentTaskStatus.Blocked, "False completion: "+mode+" / "+result.Status+" / "+result.Error);
            Check(result.Completion is null || result.Completion.State!="CompletedVerified", "Blocked result has a verified completion label.");
            if(mode is Mode.Contradictory or Mode.FailedSummary or Mode.PendingSummary)
                Check(result.GoalState!.Outcomes.First().Status != "Verified",
                    "Contradictory raw report marked an outcome verified before target-aware validation.");
            if(mode is Mode.FailedSummary or Mode.PendingSummary)
                Check(File.ReadAllText(Path.Combine(root, "A.txt")) == "ONE"
                    && result.Error?.Contains("summary contradicts", StringComparison.Ordinal) == true,
                    "Contradiction fixture did not execute the real write and then block its proof.");
            if(mode==Mode.MissingOutput)Check(result.GoalState!.Outcomes.Count(o=>o.Status=="Verified")==1 && result.Completion!.OpenOutcomes==2,"Partial goal count lost.");
        }
        using var reopened=new H2ProductionAgentAdapter(Path.Combine(root,"state"),()=>new(Profile(),""),transportFactory:new Wire([]));
        var reloaded=reopened.GetTaskSummary(id);Drain(reopened);
        Check(JsonSerializer.Serialize(result.Completion)==JsonSerializer.Serialize(reloaded.Completion),"Completion assessment lost in the existing archive.");
        var outDir=Environment.GetEnvironmentVariable("H2_AR033_EVIDENCE_DIR");
        if(!string.IsNullOrWhiteSpace(outDir)) { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir,mode+"-"+(project?"project":"global")+".json"),
            JsonSerializer.Serialize(new { summary=result, calls=wire.Results.Select(r=>new{r.ToolName,r.IsError}), model="scripted-no-network", native="NOT_RUN" },new JsonSerializerOptions{WriteIndented=true})); }
    }
    private sealed class Factory(Wire wire, Mode mode):IAgentRuntimeFactory
    {
        public string? StateRoot;
        public AgentRuntime Create(AiProfile p,string key,AgentTools tools,AgentContextManager context,AgentRunTelemetry telemetry)
        {
            StateRoot=tools.StateRoot;
            var registry=NormalRuntimeToolRegistry.Create(tools);var domains=new List<IAgentRuntimeDomainVerifier>{new FileRuntimeDomainVerifier(tools.Workspace)};
            registry.Register(new("fixture.method_a",new("fixture","Controlled unavailable route"),"Controlled unavailable file method",AgentToolRisk.Low,AgentToolAccess.ReadOnly,true,"1",
                JsonSerializer.SerializeToElement(new{type="function",function=new{name="fixture.method_a",description="Controlled fixture",parameters=new{type="object",properties=new{path=new{type="string"}}}}}),
                new DelegatingOutcomeToolExecutor("fixture",(call,ct)=>ValueTask.FromResult(ToolOutcomeBridge.Failure(call,null,"needs_configuration",ToolErrorPhase.Preflight,ToolMutationEffect.None))),
                canProvideVerificationEvidence:true,resultFormat:ToolResultFormat.Json));
            var session=typeof(AgentTools).GetProperty("ProductionSession",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tools)!;
            registry=(ToolRegistry)session.GetType().GetMethod("Configure")!.Invoke(session,[tools,registry,domains])!;
            return new(wire,context,registry,verifier:new HostVerifier(new(domains),tools.Workspace,mode),permissionPolicy:(IAgentRuntimePermissionPolicy)session,
                evidenceProjector:new(new ArtifactStore(tools.StateRoot)),hooks:new AgentRuntimeHooks(telemetry));
        }
    }
    private sealed class HostVerifier(AgentRuntimeDomainVerifierRouter inner,SafeWorkspace workspace,Mode mode):IAgentRuntimeVerifier
    {
        private Guid? failed;
        public async Task<VerificationReport?> VerifyAsync(AgentRuntimeVerificationContext c,CancellationToken ct)
        {
            var call=c.Calls.Single();var criterion=c.Contract.Goals!.Active.First().Id;var target="file:"+workspace.Resolve("A.txt");
            if(call.Name=="fixture.method_a")
            {
                failed=call.Invocation!.InvocationId;
                return new VerificationReport(AgentRuntimeDomainVerifierRouter.VerifierId,[new(criterion,VerificationCriterionStatus.Failed,[],new(criterion,"First route unavailable"))])
                {CallCoverage=[new(failed.Value,criterion,target,"contents=ONE",VerificationCriterionStatus.Failed,[])]};
            }
            if(call.Name!="write_text")return null;
            var path=call.Arguments.GetProperty("path").GetString()!;
            if(mode==Mode.MissingVerifier && path=="B.txt")return null;
            var report=await inner.VerifyAsync(c,ct);if(report is null)return null;
            if(mode==Mode.Unverified)return new(report.VerifierId,[new(criterion,VerificationCriterionStatus.NotVerified)]);
            if(mode is Mode.Unrelated or Mode.ModelClaims)return report;
            Check(File.ReadAllText(workspace.Resolve(path))=="ONE","Fixture readback differs.");
            var proof=c.Evidence.Select(e=>e.ReferenceId).ToArray();
            var matched=c.Contract.Goals!.Active.Where(o=>o.Requirement=="Write "+path || o.Requirement=="Confirm "+path).ToArray();
            if(mode==Mode.WrongTarget)matched=[c.Contract.Goals.Active.First()];
            var extra=matched.Select(o=>new VerificationCriterionResult(o.Id,VerificationCriterionStatus.Passed,proof)).ToArray();
            var binding=matched.Select(o=>new VerificationCallCoverage(call.Invocation!.InvocationId,o.Id,"file:"+workspace.Resolve(path),"contents=ONE",VerificationCriterionStatus.Passed,proof));
            var coverage=report.CallCoverage.Concat(binding).ToArray();
            if(mode==Mode.Contradictory) coverage=coverage.Select(x=>x.CriterionId==criterion
                ? x with {Status=VerificationCriterionStatus.Failed} : x).ToArray();
            if (mode is Mode.FailedSummary or Mode.PendingSummary)
                extra = extra.Select(x => new VerificationCriterionResult(x.CriterionId,
                    mode == Mode.FailedSummary ? VerificationCriterionStatus.Failed : VerificationCriterionStatus.NotVerified,
                    x.EvidenceIds, mode == Mode.FailedSummary ? new(x.CriterionId, "Controlled summary failure") : null)).ToArray();
            return new(report.VerifierId,report.Criteria.Concat(extra),report.ReportEvidenceIds)
            {CallCoverage=coverage,AlternateResolutions= failed is {} old && mode is Mode.Alternate or Mode.WrongTarget
                ? [new(old,call.Invocation!.InvocationId,criterion,target,"contents=ONE")] : []};
        }
    }
    private static AgentTaskContract Contract() => new AgentTaskContract(Guid.NewGuid(),"Fixture conversation","workspace:fixture", [], [], [], [],
        [new AgentAcceptanceCriterion("criterion-a","A"),new AgentAcceptanceCriterion("criterion-b","B")], AgentTaskRiskClass.ReadOnly,
        new AgentVerificationPolicy(true,requiredVerifierIds:["verify-a","verify-b"]), mutationAllowed:true)
        .WithUserInput(new(Guid.NewGuid(),"Hello"));
    private static AgentRuntimeVerificationContext Context(AgentTaskContract contract,string path,string id,bool mutation=false,bool error=false,ToolMutationEffect effect=ToolMutationEffect.None,bool running=false)
    {
        var args=JsonSerializer.SerializeToElement(new{path});var call=new ToolCall(id,"fixture."+id,args){Invocation=ToolInvocation.Create(contract.TaskId,"fixture."+id,args)};
        var outcome=new ToolOutcome(call.Invocation!,running?ToolOutcomeStatus.Running:error?ToolOutcomeStatus.Failed:ToolOutcomeStatus.Succeeded,
            mutation?ToolMutationEffect.Applied:effect,new(true),new(ToolVerificationStatus.NotRun,[]));
        return new(contract,1,[call],[new AgentToolResult(id,call.Name,"{}",error){Outcome=outcome}])
        {Evidence=[new(AgentEvidenceKind.ToolResult,"proof-"+id,new string('a',64))],MutationCallIds=mutation?[id]:[]};
    }
    private static VerificationReport Report(string verifier,string criterion,bool passed,AgentRuntimeVerificationContext c) =>
        new(verifier,[new(criterion,passed?VerificationCriterionStatus.Passed:VerificationCriterionStatus.Failed,
            c.Evidence.Select(e=>e.ReferenceId),passed?null:new(criterion,"Controlled unmet postcondition"))]);
    private static VerificationReport Covered(string verifier,string criterion,bool passed,AgentRuntimeVerificationContext c,string target,string condition)=>
        Report(verifier,criterion,passed,c) with {CallCoverage=[new(c.Calls[0].Invocation!.InvocationId,criterion,target,condition,
            passed?VerificationCriterionStatus.Passed:VerificationCriterionStatus.Failed,c.Evidence.Select(e=>e.ReferenceId).ToArray())]};
    private static void Reject(Action action,string text){try{action();}catch(InvalidOperationException e){Check(e.Message.Contains(text,StringComparison.OrdinalIgnoreCase),e.Message);return;}throw new InvalidOperationException("Expected rejection: "+text);}
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private static AiProfile Profile()=>new(){Model="scripted-no-network",Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1"};
    private static H2AgentTaskContext ContextFor(string root)=>new(root,"Disposable AR-033 fixture",PermissionScope:WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess,root,DateTime.UtcNow).PermissionScope);
    private static AgentTransportToolCall Discover(string name)=>new("discover-"+name,"tool_search",JsonSerializer.Serialize(new{query=name}));
    private static AgentTransportToolCall Write(string path)=>new("write-first","write_text",JsonSerializer.Serialize(new{path,text="ONE",expectedHash=""}));
    private static H2AgentTaskSummary Wait(H2ProductionAgentAdapter adapter,Guid task){var end=Environment.TickCount64+20000;while(Environment.TickCount64<end){var s=adapter.GetTaskSummary(task);if(H2AgentActivity.IsTerminal(s.Status))return s;Thread.Sleep(10);}throw new TimeoutException("AR-033 fixture timeout");}
    private static void Drain(H2ProductionAgentAdapter a)=>a.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    private static void Fixture(Action<string> body){var root=Path.Combine(Path.GetTempPath(),"h2-ar033-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{body(root);}finally{Directory.Delete(root,true);}}
    private sealed class Wire(AgentTransportToolCall[] calls):IAgentTransport,IAgentTransportFactory
    {
        private int next;public Action? BeforeFinal;public List<AgentToolResult> Results=[];
        public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.ChatCompletionsFallback;
        public IAgentTransport Create(AiProfile p,string key,AgentRunTelemetry telemetry)=>this;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r,[EnumeratorCancellation]CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();foreach(var e in Round())yield return e;await Task.CompletedTask;}
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r,[EnumeratorCancellation]CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();Results.AddRange(r.ToolResults);foreach(var e in Round())yield return e;await Task.CompletedTask;}
        private IEnumerable<AgentTransportEvent> Round(){if(next<calls.Length)yield return AgentTransportEvent.Tool(calls[next++]);else{BeforeFinal?.Invoke();BeforeFinal=null;yield return AgentTransportEvent.TextDeltaEvent("Everything is Verified (untrusted model claim).");}yield return AgentTransportEvent.Complete();}
        public void Cancel(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
