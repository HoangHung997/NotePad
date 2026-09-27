using H2AgentLab.Tasking;

namespace H2AgentLab.Verification;

public static class VerificationCompletionGate
{
    public static AgentVerificationOutcome Evaluate(
        AgentTaskContract contract,
        IEnumerable<VerificationReport> reports)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(reports);

        var snapshot = reports.ToArray();
        if (snapshot.Any(x => x is null))
            throw new ArgumentException("Verification reports cannot contain null entries.", nameof(reports));

        var resultByCriterion = new Dictionary<string, VerificationCriterionResult>(StringComparer.Ordinal);
        foreach (var report in snapshot)
        {
            foreach (var result in report.Criteria)
            {
                if (!resultByCriterion.TryAdd(result.CriterionId, result))
                    throw new InvalidOperationException(
                        $"Criterion '{result.CriterionId}' was reported more than once. Aggregate domain evidence before completion.");
            }
        }

        var requiredCriteria = contract.AcceptanceCriteria.Select(x => x.CriterionId).ToArray();
        var missing = requiredCriteria.Where(x => !resultByCriterion.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
            return new AgentVerificationOutcome(
                passed: false,
                verifierIds: snapshot.SelectMany(x => x.ContributingVerifierIds.Count == 0 ? [x.VerifierId] : x.ContributingVerifierIds));

        if (requiredCriteria.Any(id =>
            resultByCriterion[id].Status != VerificationCriterionStatus.Passed))
            return new AgentVerificationOutcome(
                passed: false,
                verifierIds: snapshot.SelectMany(x => x.ContributingVerifierIds.Count == 0 ? [x.VerifierId] : x.ContributingVerifierIds));

        var allReportsPass = snapshot.Length > 0 && snapshot.All(x => x.Passed);
        return new AgentVerificationOutcome(
            passed: allReportsPass,
            verifierIds: snapshot.SelectMany(x => x.ContributingVerifierIds.Count == 0 ? [x.VerifierId] : x.ContributingVerifierIds));
    }
}
