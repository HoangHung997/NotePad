using System.Text;
using H2AgentLab.Tasking;

namespace H2AgentLab.Verification;

public sealed record AgentRepairContext(
    IReadOnlyList<string> FailedCriterionIds,
    IReadOnlyList<string> PassedCriterionIds,
    string PromptContext);

/// <summary>
/// Converts semantic verifier failures into small task-local repair context. Already-passed criteria
/// are explicitly protected so repair work targets only failed requirements instead of replaying the
/// complete task transcript.
/// </summary>
public sealed class AgentRepairController
{
    public const int MaxRepairContextCharacters = 8_000;

    public AgentRepairContext Build(
        AgentTaskContract contract,
        VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(report);

        var criteriaById = contract.AcceptanceCriteria
            .ToDictionary(x => x.CriterionId, StringComparer.Ordinal);
        var unknown = report.Criteria
            .Select(x => x.CriterionId)
            .Where(x => !criteriaById.ContainsKey(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException(
                "Verification report contains criterion(s) outside the task contract: "
                + string.Join(", ", unknown) + ".");

        var failed = report.Criteria
            .Where(x => x.Status == VerificationCriterionStatus.Failed)
            .OrderBy(x => x.CriterionId, StringComparer.Ordinal)
            .ToArray();
        if (failed.Length == 0)
            throw new InvalidOperationException("Repair context requires at least one failed criterion.");

        var passed = report.Criteria
            .Where(x => x.Status == VerificationCriterionStatus.Passed)
            .Select(x => x.CriterionId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("Repair only the verifier failures below.");
        builder.AppendLine("Do not regress already-passed criteria or unrelated task requirements.");
        builder.AppendLine();

        foreach (var result in failed)
        {
            var requirement = criteriaById[result.CriterionId].Requirement;
            builder.Append("FAILED ")
                .Append(result.CriterionId)
                .Append(": ")
                .AppendLine(requirement);
            builder.Append("Reason: ")
                .AppendLine(result.Failure!.Message);

            var evidence = result.EvidenceIds
                .Concat(result.Failure.EvidenceIds)
                .Distinct(StringComparer.Ordinal)
                .Take(12)
                .ToArray();
            if (evidence.Length > 0)
                builder.Append("Evidence: ").AppendLine(string.Join(", ", evidence));
            builder.AppendLine();
        }

        if (passed.Length > 0)
        {
            builder.Append("Preserve passed criteria: ")
                .AppendLine(string.Join(", ", passed));
        }

        var context = builder.ToString().Trim();
        if (context.Length > MaxRepairContextCharacters)
            context = context[..(MaxRepairContextCharacters - 14)] + "…[truncated]";

        return new AgentRepairContext(
            failed.Select(x => x.CriterionId).ToArray(),
            passed,
            context);
    }
}
