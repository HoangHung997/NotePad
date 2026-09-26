using H2AgentLab.Runtime;
using H2AgentLab.Tasking;

/// <summary>AR-030 host-policy promotion only. Evidence-resolution repair is still pending;
/// these unit tests do not certify Office, a model or native UI.</summary>
internal static class H2AgentMutationPolicyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-030 policy first mutation retains every host-required verifier", () =>
        {
            var before = Contract();
            var after = before.WithExecutedMutation();
            Check(after.VerificationPolicy.RequiredVerifierIds.Contains("host-layout-verifier")
                && after.VerificationPolicy.RequiredVerifierIds.Contains(AgentRuntimeDomainVerifierRouter.VerifierId),
                "Mutation promotion dropped a host-required verifier.");
            Check(after.VerificationPolicy.RequireVerification && !after.VerificationPolicy.AllowNotMechanicallyVerifiable,
                "Mutation promotion weakened mechanical verification.");
            Check(ReferenceEquals(before.Goals, after.Goals) && before.Scope == after.Scope
                && before.MutationAllowed == after.MutationAllowed
                && before.PreserveConstraints.SequenceEqual(after.PreserveConstraints)
                && before.OutputRequirements.SequenceEqual(after.OutputRequirements),
                "Mutation promotion changed user source, scope or host preservation criteria.");
            Check(ReferenceEquals(after, after.WithExecutedMutation()), "Mutation promotion is not idempotent.");
        });
        test("AR-030 policy declared mutation criterion retains host meaning and evidence", () =>
        {
            var proof = Proof("prior-host-evidence");
            var criterion = new AgentAcceptanceCriterion(AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                "Host-specific preserved document structure", [proof]);
            var before = Contract().ExpandAcceptanceCriteria([criterion]);
            AgentTaskContract after;
            try { after = before.WithExecutedMutation(); }
            catch (ArgumentException e) { throw new InvalidOperationException("Declared mutation criterion was duplicated.", e); }
            var kept = after.AcceptanceCriteria.Single(x => x.CriterionId == criterion.CriterionId);
            Check(kept.Requirement == criterion.Requirement && kept.Evidence.SequenceEqual(criterion.Evidence),
                "Existing host criterion was redefined or its evidence was lost.");
        });
        test("AR-030 policy orchestrator accepts strengthened mutation without losing host policy", () =>
        {
            var before = Contract();
            var host = new AgentOrchestrator();
            var session = host.Receive(before);
            try { host.UpdateContract(session, before.WithExecutedMutation()); }
            catch (InvalidOperationException e) { throw new InvalidOperationException("Valid mutation promotion cannot pass the host policy guard.", e); }
            Check(session.Contract.IsMutating && session.Contract.VerificationPolicy.RequiredVerifierIds.Contains("host-layout-verifier"),
                "Orchestrator dropped a required verifier.");
        });
    }
    private static AgentTaskContract Contract() => new AgentTaskContract(Guid.NewGuid(),
        "Write first output; Export second output", "workspace:proof-fixture", null, null,
        ["Preserve untouched content"], ["Keep exact format"], [], AgentTaskRiskClass.ReadOnly,
        new AgentVerificationPolicy(requiredVerifierIds: ["host-layout-verifier"]), mutationAllowed: true)
        .WithUserInput(new(Guid.NewGuid(), "Write first output; Export second output"));
    private static AgentEvidenceReference Proof(string id, char value = 'a')
        => new(AgentEvidenceKind.ArtifactHash, id, new string(value, 64), "Controlled fixture proof");
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
