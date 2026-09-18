namespace H2AgentLab.Tasking;

/// <summary>
/// Coarse host-owned risk classification for an Agent Lab task. The class is deliberately
/// independent from provider/model behavior so later permission gates can reason about it
/// deterministically.
/// </summary>
public enum AgentTaskRiskClass
{
    ReadOnly = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// Host verification requirements attached to a task before orchestration begins.
/// </summary>
public sealed record AgentVerificationPolicy
{
    public AgentVerificationPolicy(
        bool requireVerification = true,
        bool allowNotMechanicallyVerifiable = false,
        IEnumerable<string>? requiredVerifierIds = null)
    {
        RequireVerification = requireVerification;
        AllowNotMechanicallyVerifiable = allowNotMechanicallyVerifiable;
        RequiredVerifierIds = NormalizeItems(requiredVerifierIds, nameof(requiredVerifierIds));
    }

    public bool RequireVerification { get; }
    public bool AllowNotMechanicallyVerifiable { get; }
    public IReadOnlyList<string> RequiredVerifierIds { get; }

    private static IReadOnlyList<string> NormalizeItems(IEnumerable<string>? values, string parameterName)
    {
        if (values is null) return Array.Empty<string>();
        var result = values
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (result.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new ArgumentException("Verifier identifiers must be at most 128 characters and contain no control characters.", parameterName);
        return Array.AsReadOnly(result);
    }
}

/// <summary>
/// Immutable host-owned statement of what the task is allowed and required to accomplish.
/// Collection inputs are copied at construction time so later caller mutation cannot silently
/// rewrite the accepted task contract.
/// </summary>
public sealed record AgentTaskContract
{
    public AgentTaskContract(
        Guid taskId,
        string userGoal,
        string scope,
        IEnumerable<string>? inputs,
        IEnumerable<string>? requiredChanges,
        IEnumerable<string>? preserveConstraints,
        IEnumerable<string>? outputRequirements,
        IEnumerable<string>? acceptanceCriteria,
        AgentTaskRiskClass riskClass,
        AgentVerificationPolicy verificationPolicy)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task ID cannot be empty.", nameof(taskId));

        TaskId = taskId;
        UserGoal = NormalizeRequired(userGoal, nameof(userGoal), 8_000);
        Scope = NormalizeRequired(scope, nameof(scope), 8_000);
        Inputs = NormalizeItems(inputs, nameof(inputs));
        RequiredChanges = NormalizeItems(requiredChanges, nameof(requiredChanges));
        PreserveConstraints = NormalizeItems(preserveConstraints, nameof(preserveConstraints));
        OutputRequirements = NormalizeItems(outputRequirements, nameof(outputRequirements));
        AcceptanceCriteria = NormalizeItems(acceptanceCriteria, nameof(acceptanceCriteria));
        RiskClass = riskClass;
        VerificationPolicy = verificationPolicy ?? throw new ArgumentNullException(nameof(verificationPolicy));

        if (!Enum.IsDefined(riskClass))
            throw new ArgumentOutOfRangeException(nameof(riskClass), "Unknown task risk class.");
    }

    public Guid TaskId { get; }
    public string UserGoal { get; }
    public string Scope { get; }
    public IReadOnlyList<string> Inputs { get; }
    public IReadOnlyList<string> RequiredChanges { get; }
    public IReadOnlyList<string> PreserveConstraints { get; }
    public IReadOnlyList<string> OutputRequirements { get; }
    public IReadOnlyList<string> AcceptanceCriteria { get; }
    public AgentTaskRiskClass RiskClass { get; }
    public AgentVerificationPolicy VerificationPolicy { get; }

    public bool IsMutating => RequiredChanges.Count > 0 || RiskClass != AgentTaskRiskClass.ReadOnly;

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"Value exceeds {maxLength} characters.", parameterName);
        return normalized;
    }

    private static IReadOnlyList<string> NormalizeItems(IEnumerable<string>? values, string parameterName)
    {
        if (values is null) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var normalized = value.Trim();
            if (normalized.Length > 8_000)
                throw new ArgumentException("Task contract item exceeds 8,000 characters.", parameterName);
            if (!result.Contains(normalized, StringComparer.Ordinal))
                result.Add(normalized);
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
