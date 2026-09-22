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
    }
}
