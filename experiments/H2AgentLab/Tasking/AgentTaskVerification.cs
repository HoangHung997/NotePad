namespace H2AgentLab.Tasking;

public sealed record AgentVerificationOutcome
{
    public AgentVerificationOutcome(
        bool passed,
        bool notMechanicallyVerifiable = false,
        IEnumerable<string>? verifierIds = null,
        IEnumerable<AgentEvidenceReference>? nonMechanicalEvidence = null)
    {
        if (passed && notMechanicallyVerifiable)
            throw new ArgumentException("Verification outcome cannot be both passed and not mechanically verifiable.");

        Passed = passed;
        NotMechanicallyVerifiable = notMechanicallyVerifiable;
        VerifierIds = Array.AsReadOnly((verifierIds ?? Array.Empty<string>())
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
        NonMechanicalEvidence = Array.AsReadOnly((nonMechanicalEvidence ?? Array.Empty<AgentEvidenceReference>())
            .Where(x => x is not null)
            .Distinct()
            .ToArray());

        if (VerifierIds.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new ArgumentException("Verifier IDs must be at most 128 characters and contain no control characters.", nameof(verifierIds));
        if (NonMechanicalEvidence.Count > 0 && !NotMechanicallyVerifiable)
            throw new ArgumentException(
                "Non-mechanical evidence is only valid for an explicitly not-mechanically-verifiable outcome.",
                nameof(nonMechanicalEvidence));
    }

    public bool Passed { get; }
    public bool NotMechanicallyVerifiable { get; }
    public IReadOnlyList<string> VerifierIds { get; }
    public IReadOnlyList<AgentEvidenceReference> NonMechanicalEvidence { get; }
}

public static class AgentTaskCompletionGate
{
    public static void EnsureCanComplete(AgentTaskContract contract, AgentVerificationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(outcome);

        var policy = contract.VerificationPolicy;
        var requiresVerification =
            contract.IsMutating
            || policy.RequireVerification
            || policy.RequiredVerifierIds.Count > 0;

        if (!requiresVerification)
            return;

        if (outcome.Passed)
        {
            var missing = policy.RequiredVerifierIds
                .Where(required => !outcome.VerifierIds.Contains(required, StringComparer.Ordinal))
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "Completion verification is missing required verifier(s): " + string.Join(", ", missing) + ".");
            return;
        }

        if (outcome.NotMechanicallyVerifiable)
        {
            if (!policy.AllowNotMechanicallyVerifiable)
                throw new InvalidOperationException(
                    "Task policy does not allow not-mechanically-verifiable completion.");
            if (policy.RequiredVerifierIds.Count > 0)
                throw new InvalidOperationException(
                    "Not-mechanically-verifiable completion cannot bypass explicitly required verifier(s).");
            if (!outcome.NonMechanicalEvidence.Any(x => x.Kind == AgentEvidenceKind.HostClassification))
                throw new InvalidOperationException(
                    "Not-mechanically-verifiable completion requires explicit host classification evidence.");
            return;
        }

        if (contract.IsMutating)
            throw new InvalidOperationException("Mutating task cannot complete without successful verification.");

        throw new InvalidOperationException("Task verification policy requires successful verification before completion.");
    }
}
