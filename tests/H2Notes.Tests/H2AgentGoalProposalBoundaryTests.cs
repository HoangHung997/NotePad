using H2AgentLab.Tasking;

internal static class H2AgentGoalProposalBoundaryTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-030 proposal numeric quote cannot collide with extracted obligation ID",()=>{
            var s=AgentGoalState.Create(Guid.NewGuid(),"scope",new(Guid.NewGuid(),"Write item 0; Export PDF"));
            var n=s.AddProposal(s.Revisions[0].SourceId,"0");
            if(n.Active.Count!=3 || n.Active.Select(x=>x.Id).Distinct().Count()!=3)
                throw new InvalidOperationException("Proposal ID collided with a source-extracted outcome.");
        });
        test("AR-030 stale proposal cannot reactivate user-waived outcome",()=>{
            var s=AgentGoalState.Create(Guid.NewGuid(),"scope",new(Guid.NewGuid(),"Write item one; Export PDF"));
            var source=s.Revisions[0].SourceId;
            var n=s.Apply(new(Guid.NewGuid(),"Cancel outcome 2"));
            try { n.AddProposal(source,"Export PDF"); throw new Exception("Expected stale proposal rejection."); }
            catch(InvalidOperationException e) when(e.Message.Contains("current user revision")) { }
            if(n.Active.Count!=1 || n.Obligations[1].Status!=AgentObligationStatus.WaivedByUser)
                throw new InvalidOperationException("Waived outcome was revived.");
        });
        test("AR-030 current-source proposal remains pending without altering user lineage",()=>{
            var s=AgentGoalState.Create(Guid.NewGuid(),"scope",new(Guid.NewGuid(),"Write item one; Export PDF"));
            s=s.Apply(new(Guid.NewGuid(),"Replace \"Export PDF\" with \"Export DOCX\""));
            var n=s.AddProposal(s.Revisions[^1].SourceId,"DOCX");
            if(n.RevisionId!=s.RevisionId || n.Revisions.Count!=2 || n.Active.Last().Status!=AgentObligationStatus.Pending)
                throw new InvalidOperationException("A proposal changed trusted lineage or invented verification.");
        });
        test("AR-030 host criterion with goal prefix survives all user revisions",()=>{
            var c=Contract().ExpandAcceptanceCriteria([new("goal:host-layout", "Keep native layout")]);
            c=c.WithUserInput(new(Guid.NewGuid(),"Write item one; Export PDF"));
            c=c.WithUserInput(new(Guid.NewGuid(),"Cancel outcome 2"));
            if(!c.AcceptanceCriteria.Any(x=>x.CriterionId=="goal:host-layout" && x.Requirement=="Keep native layout"))
                throw new InvalidOperationException("User revision deleted a host-owned goal-prefixed criterion.");
        });
        test("AR-030 prior same-revision snapshot cannot drop an accepted proposal",()=>{
            var c=Contract().WithUserInput(new(Guid.NewGuid(),"Write item one; Export PDF"));
            var before=c.Goals!;c=c.WithGoals(before.AddProposal(before.Revisions[0].SourceId,"item one"));
            Reject(()=>c.WithGoals(before),"discard accepted obligations");
        });
        test("AR-030 history update cannot erase a recorded mutation or observed proof",()=>{
            var c=Contract().WithUserInput(new(Guid.NewGuid(),"Write item one; Export PDF"));var before=c.Goals!;
            var mutation=c.WithGoals(before.RecordMutation(before.RevisionId));
            Reject(()=>mutation.WithGoals(before),"mutation history");
            var evidence=new AgentEvidenceReference(AgentEvidenceKind.ArtifactHash,"proof",new string('a',64),"Dedicated fixture");
            var report=new H2AgentLab.Verification.VerificationReport("test-host",
                [new(before.Active[0].Id,H2AgentLab.Verification.VerificationCriterionStatus.Passed,[evidence.ReferenceId])]);
            var observed=c.WithGoals(before.Observe(before.RevisionId,report,[evidence]));
            Reject(()=>observed.WithGoals(before),"evidence");
        });
        test("AR-030 orchestrator cannot discard goal state while copying its criteria",()=>{
            var c=Contract().WithUserInput(new(Guid.NewGuid(),"Write item one; Export PDF"));
            var host=new AgentOrchestrator();var session=host.Receive(c);
            var untracked=new AgentTaskContract(c.TaskId,c.UserGoal,c.Scope,c.Inputs,c.RequiredChanges,c.PreserveConstraints,
                c.OutputRequirements,c.AcceptanceCriteria,c.RiskClass,c.VerificationPolicy,c.MutationAllowed);
            Reject(()=>host.UpdateContract(session,untracked),"remove accepted goal state");
            if(!ReferenceEquals(session.Contract,c))throw new InvalidOperationException("Rejected update changed current contract.");
        });
        test("AR-030 host accepts sourced supersession without losing unrelated criteria",()=>{
            var c=Contract().ExpandAcceptanceCriteria([new("goal:host-layout","Keep native layout")])
                .WithUserInput(new(Guid.NewGuid(),"Write item one; Export PDF"));
            var host=new AgentOrchestrator();var session=host.Receive(c);
            var revised=c.WithUserInput(new(Guid.NewGuid(),"Replace \"Export PDF\" with \"Export DOCX\""));
            host.UpdateContract(session,revised);
            if(session.Contract.Goals!.Active.Last().Requirement!="Export DOCX" || !session.Contract.AcceptanceCriteria.Any(x=>x.CriterionId=="goal:host-layout"))
                throw new InvalidOperationException("Source-backed update failed to preserve host criteria.");
        });
    }
    private static AgentTaskContract Contract()=>new(Guid.NewGuid(),"Task","scope",null,null,null,null,[],AgentTaskRiskClass.ReadOnly,new(false));
    private static void Reject(Action action,string contains)
    {
        try {action();throw new Exception("Expected host contract rejection: "+contains);}
        catch(InvalidOperationException e) when(e.Message.Contains(contains,StringComparison.Ordinal)) { }
    }
}
