using H2AgentLab.Runtime;
using H2AgentLab.Tasking;
using H2AgentLab.Verification;

/// <summary>AR-030 remaining evidence-reference integrity. All proofs are labelled fixtures;
/// these unit tests do not certify Office, a model or native UI.</summary>
internal static class H2AgentGoalProofIntegrityTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-030 evidence missing referenced proof cannot produce Verified", () =>
        {
            var before = Contract().Goals!;
            var after = before.Observe(before.RevisionId,
                Report(before, "proof-a", "not-observed"), [Proof("proof-a")]);
            Check(after.Active[0].Status == AgentObligationStatus.AppliedUnverified,
                "Missing referenced evidence was accepted as Verified.");
            Check(after.Active[0].Evidence.Select(x => x.ReferenceId).SequenceEqual(["proof-a"]),
                "A missing evidence object was invented.");
            Check(before.Active[0].Status == AgentObligationStatus.Pending, "Observation changed an earlier immutable snapshot.");
        });
        test("AR-030 evidence conflicting evidence identity cannot produce Verified", () =>
        {
            var before = Contract().Goals!;
            var after = before.Observe(before.RevisionId, Report(before, "same-id"),
                [Proof("same-id", 'a'), Proof("same-id", 'b')]);
            Check(after.Active[0].Status == AgentObligationStatus.AppliedUnverified,
                "Conflicting evidence identities were accepted as Verified.");
            Check(after.Active[0].Evidence.Count == 0, "Ambiguous evidence was attached as resolved proof.");
        });
        test("AR-030 evidence all referenced proofs can verify only their own outcome", () =>
        {
            var before = Contract().Goals!;
            var a = Proof("proof-a"); var b = Proof("proof-b");
            var after = before.Observe(before.RevisionId, Report(before, a.ReferenceId, b.ReferenceId), [a, b]);
            Check(after.Active[0].Status == AgentObligationStatus.Verified && after.Active[0].Evidence.Count == 2
                && after.Active[1].Status == AgentObligationStatus.Pending, "Complete proof resolved the wrong coverage.");
        });
        test("AR-030 evidence identical repeated evidence is not an identity conflict", () =>
        {
            var before = Contract().Goals!; var proof = Proof("proof-a");
            var after = before.Observe(before.RevisionId, Report(before, proof.ReferenceId), [proof, proof]);
            Check(after.Active[0].Status == AgentObligationStatus.Verified && after.Active[0].Evidence.Count == 1,
                "A duplicate delivery of identical proof was treated as conflicting evidence.");
        });
        test("AR-030 evidence unrelated evidence cannot satisfy an empty reference list", () =>
        {
            var before = Contract().Goals!;
            var after = before.Observe(before.RevisionId, Report(before), [Proof("unrelated")]);
            Check(after.Active[0].Status == AgentObligationStatus.AppliedUnverified && after.Active[0].Evidence.Count == 0,
                "Unreferenced evidence turned a source-less verdict into Verified.");
        });
        test("AR-030 evidence late complete evidence repairs an unverified observation", () =>
        {
            var before = Contract().Goals!; var a = Proof("proof-a"); var b = Proof("proof-b");
            var partial = before.Observe(before.RevisionId, Report(before, a.ReferenceId, b.ReferenceId), [a]);
            var after = partial.Observe(partial.RevisionId, Report(partial, a.ReferenceId, b.ReferenceId), [a, b]);
            Check(after.Active[0].Status == AgentObligationStatus.Verified && after.Active[0].Evidence.Count == 2,
                "A complete later observation could not resolve the unverified result.");
        });
    }
    private static AgentTaskContract Contract() => new AgentTaskContract(Guid.NewGuid(),
        "Write first output; Export second output", "workspace:proof-fixture", null, null,
        ["Preserve untouched content"], ["Keep exact format"], [], AgentTaskRiskClass.ReadOnly,
        new AgentVerificationPolicy(requiredVerifierIds: ["host-layout-verifier"]), mutationAllowed: true)
        .WithUserInput(new(Guid.NewGuid(), "Write first output; Export second output"));
    private static AgentEvidenceReference Proof(string id, char value = 'a')
        => new(AgentEvidenceKind.ArtifactHash, id, new string(value, 64), "Controlled fixture proof");
    private static VerificationReport Report(AgentGoalState state, params string[] ids)
        => new("fixture-criterion-verifier", [new(state.Active[0].Id, VerificationCriterionStatus.Passed, ids)]);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
