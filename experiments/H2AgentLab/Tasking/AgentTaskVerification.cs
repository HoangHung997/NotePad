namespace H2AgentLab.Tasking;

public sealed record AgentVerificationOutcome
{
    public AgentVerificationOutcome(
        bool passed,
        bool notMechanicallyVerifiable = false,
        IEnumerable<string>? verifierIds = null)
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

        if (VerifierIds.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new ArgumentException("Verifier IDs must be at most 128 characters and contain no control characters.", nameof(verifierIds));
    }

    public bool Passed { get; }
    public bool NotMechanicallyVerifiable { get; }
    public IReadOnlyList<string> VerifierIds { get; }
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

        if (outcome.NotMechanicallyVerifiable && policy.AllowNotMechanicallyVerifiable)
            return;

        if (contract.IsMutating)
            throw new InvalidOperationException("Mutating task cannot complete without successful verification.");

        throw new InvalidOperationException("Task verification policy requires successful verification before completion.");
    }
}
