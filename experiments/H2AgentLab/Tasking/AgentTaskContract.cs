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
        IEnumerable<AgentAcceptanceCriterion>? acceptanceCriteria,
        AgentTaskRiskClass riskClass,
        AgentVerificationPolicy verificationPolicy,
        bool mutationAllowed = false)
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
        AcceptanceCriteria = NormalizeCriteria(acceptanceCriteria);
        RiskClass = riskClass;
        VerificationPolicy = verificationPolicy ?? throw new ArgumentNullException(nameof(verificationPolicy));
        MutationAllowed = mutationAllowed || IsMutating;

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
    public IReadOnlyList<AgentAcceptanceCriterion> AcceptanceCriteria { get; }
    public AgentTaskRiskClass RiskClass { get; }
    public AgentVerificationPolicy VerificationPolicy { get; }
    public bool MutationAllowed { get; }

    public bool IsMutating => RequiredChanges.Count > 0 || RiskClass != AgentTaskRiskClass.ReadOnly;

    // Permission is a capability, not evidence that the user's request requires a mutation.
    // The runtime promotes a conversational contract only after an authorized tool executes.
    public AgentTaskContract WithExecutedMutation()
        => IsMutating ? this : new(
            TaskId, UserGoal, Scope, Inputs, ["verify executed changes"], PreserveConstraints,
            OutputRequirements,
            AcceptanceCriteria.Concat([new AgentAcceptanceCriterion(
                Runtime.AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                "Executed changes are re-observed and deterministically verified.")]),
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requiredVerifierIds: [Runtime.AgentRuntimeDomainVerifierRouter.VerifierId]),
            MutationAllowed);

    /// <summary>
    /// Adds new acceptance requirements without permitting an existing criterion to disappear or
    /// be redefined. This is the only contract-level criterion expansion operation.
    /// </summary>
    public AgentTaskContract ExpandAcceptanceCriteria(IEnumerable<AgentAcceptanceCriterion> additions)
    {
        ArgumentNullException.ThrowIfNull(additions);
        var merged = AcceptanceCriteria.ToList();
        foreach (var addition in additions)
        {
            ArgumentNullException.ThrowIfNull(addition);
            var existing = merged.FirstOrDefault(x => x.CriterionId == addition.CriterionId);
            if (existing is null)
            {
                merged.Add(addition);
                continue;
            }

            if (!string.Equals(existing.Requirement, addition.Requirement, StringComparison.Ordinal))
                throw new InvalidOperationException($"Acceptance criterion '{addition.CriterionId}' cannot be silently redefined.");

            foreach (var evidence in addition.Evidence)
                existing = existing.WithEvidence(evidence);
            merged[merged.FindIndex(x => x.CriterionId == addition.CriterionId)] = existing;
        }

        return CopyWithCriteria(merged);
    }

    /// <summary>
    /// Appends evidence to one existing criterion while preserving the full accepted criterion set.
    /// </summary>
    public AgentTaskContract WithCriterionEvidence(string criterionId, AgentEvidenceReference evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterionId);
        ArgumentNullException.ThrowIfNull(evidence);
        var index = AcceptanceCriteria.ToList().FindIndex(x => x.CriterionId == criterionId.Trim());
        if (index < 0)
            throw new KeyNotFoundException($"Unknown acceptance criterion '{criterionId.Trim()}'.");

        var criteria = AcceptanceCriteria.ToArray();
        criteria[index] = criteria[index].WithEvidence(evidence);
        return CopyWithCriteria(criteria);
    }

    private AgentTaskContract CopyWithCriteria(IEnumerable<AgentAcceptanceCriterion> criteria)
        => new(
            TaskId,
            UserGoal,
            Scope,
            Inputs,
            RequiredChanges,
            PreserveConstraints,
            OutputRequirements,
            criteria,
            RiskClass,
            VerificationPolicy,
            MutationAllowed);

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

    private static IReadOnlyList<AgentAcceptanceCriterion> NormalizeCriteria(IEnumerable<AgentAcceptanceCriterion>? values)
    {
        if (values is null) return Array.Empty<AgentAcceptanceCriterion>();

        var result = new List<AgentAcceptanceCriterion>();
        foreach (var criterion in values)
        {
            ArgumentNullException.ThrowIfNull(criterion);
            var existing = result.FirstOrDefault(x => x.CriterionId == criterion.CriterionId);
            if (existing is not null)
                throw new ArgumentException($"Duplicate acceptance criterion ID '{criterion.CriterionId}'.", nameof(values));
            result.Add(criterion);
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
