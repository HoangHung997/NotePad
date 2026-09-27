namespace H2Notes.Core;

/// <summary>Read-only projection from Agent-owned completion evidence, not another source of state.
/// A tool's exit code or a model's final text cannot assign these values.</summary>
public sealed record H2AgentCompletionAssessment(string State, int RequiredOutcomes, int VerifiedOutcomes,
    int OpenOutcomes, int UnverifiedMutations, int UnresolvedAttempts, int PendingOperations,
    IReadOnlyList<string> VerifierIds, IReadOnlyList<H2AgentAlternateResolution> Resolutions)
{
    public string VerificationLevel => State == "CompletedVerified" ? "Mechanical" : "NotCertified";
    public bool LayoutAccepted => false;
    public bool UserAccepted => false;
}
public sealed record H2AgentAlternateResolution(Guid FailedInvocationId, Guid ReplacementInvocationId,
    string CriterionId, string TargetId, string PostconditionId, string Reason, IReadOnlyList<string> EvidenceIds);
