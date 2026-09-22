using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Verification;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

/// <summary>Host-only observation supplied to a verifier, never accepted from tool JSON/model text.
/// The IDs are scoped to this invocation. No arguments, document contents or credentials are copied.</summary>
public sealed record AgentVerificationAttempt(Guid InvocationId, string ToolCallId, string ToolName,
    string GoalRevisionId, string TargetId, bool Mutation, ToolOutcomeStatus Status, ToolMutationEffect Effect);

/// <summary>A per-call claim produced by a host verifier after readback. Target and postcondition
/// are verifier-owned identities, not evidence of authorization or a reason to repeat a write.</summary>
public sealed record VerificationCallCoverage(Guid InvocationId, string CriterionId, string TargetId,
    string PostconditionId, VerificationCriterionStatus Status, IReadOnlyList<string> EvidenceIds);

/// <summary>Explicit alternate-approach resolution. Both calls must have verifier coverage for the
/// same active criterion, exact target and postcondition. Unknown/running effects cannot use this.</summary>
public sealed record VerificationAlternateResolution(Guid FailedInvocationId, Guid ReplacementInvocationId,
    string CriterionId, string TargetId, string PostconditionId);

/// <summary>Run-local assessment of the existing execution/report history, not another engine/store.
/// Raw reports/receipts remain in the existing Agent journal. It never schedules/replays operations.</summary>
internal sealed class AgentCompletionAssessment(bool requireObservedProof)
{
    private sealed class Attempt(AgentVerificationAttempt value, JsonElement arguments)
    {
        public AgentVerificationAttempt Value { get; } = value;
        public JsonElement Arguments { get; } = arguments;
        public Dictionary<string, VerificationCallCoverage> Coverage { get; } = new(StringComparer.Ordinal);
        public string? VerifierId { get; set; }
        public bool Verified { get; set; }
        public Guid? ResolvedBy { get; set; }
    }
    private readonly Dictionary<Guid, Attempt> _attempts = [];
    private readonly Dictionary<(string Verifier, string Criterion), VerificationCriterionResult> _criteria = [];
    private readonly List<H2AgentAlternateResolution> _resolutions = [];
    private readonly HashSet<string> _proofIds = new(StringComparer.Ordinal);
    public int UnverifiedMutations => _attempts.Values.Count(a => a.Value.Mutation && !a.Verified && a.ResolvedBy is null);
    public int OutstandingProofs => _attempts.Values.Count(a => !a.Verified && a.ResolvedBy is null
        && a.Coverage.Values.Any(c => c.Status != VerificationCriterionStatus.Passed));
    public IReadOnlyList<AgentVerificationAttempt> Attempts => _attempts.Values.Select(a => a.Value).ToArray();
    public IReadOnlyCollection<string> ProofIds => _proofIds;

    public void Register(AgentRuntimeVerificationContext context)
    {
        foreach (var call in context.Calls)
        {
            var outcome = context.Results.Single(r => r.ToolCallId == call.Id).Outcome;
            if (call.Invocation is null || outcome is null) continue;
            if (_attempts.Count >= 4096) throw new AgentVerificationRequiredException("Completion attempt budget reached; history is retained.");
            var value = new AgentVerificationAttempt(call.Invocation.InvocationId, call.Id, call.Name,
                context.Contract.Goals!.RevisionId, Target(call, outcome.Resource?.Id),
                context.MutationCallIds.Contains(call.Id), outcome.Status, outcome.Effect);
            if (!_attempts.TryAdd(value.InvocationId, new(value, call.Arguments.Clone())))
                throw new AgentVerificationRequiredException("Duplicate host invocation identity.");
        }
    }

    // No semantic inference: exact resource selectors if present, otherwise the complete argument
    // object. A host verifier may use a stronger native identity in its explicit coverage.
    internal static string Target(global::H2AgentLab.ToolCall call, string? scope = null)
    {
        string[] selectors = ["path", "destination", "url", "session_id", "resource_id", "window_id", "document_id", "sheet", "sheet_name", "range", "address"];
        var fields = call.Arguments.EnumerateObject().Where(p => selectors.Contains(p.Name, StringComparer.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        var material = fields.Length == 0 ? call.Arguments.GetRawText()
            : string.Join("\n", fields.Select(p => p.Name + "=" + p.Value.GetRawText()));
        return "target-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((scope ?? "") + "\n" + material))).ToLowerInvariant();
    }

    public VerificationReport Observe(AgentRuntimeVerificationContext context, VerificationReport report)
    {
        var current = context.Calls.Where(c => c.Invocation is not null).ToDictionary(c => c.Invocation!.InvocationId);
        foreach (var result in report.Criteria)
        {
            var accepted = result;
            if (requireObservedProof && result.Status == VerificationCriterionStatus.Passed
                && result.CriterionId != AgentRuntimeDomainVerifierRouter.MutationCriterionId
                && !Proof(context.Evidence, result.EvidenceIds))
                accepted = new(result.CriterionId, VerificationCriterionStatus.NotVerified, result.EvidenceIds);
            _criteria[(report.VerifierId, result.CriterionId)] = accepted;
        }
        var coverage = report.CallCoverage.ToArray();
        if (coverage.Length == 0 && context.MutationCallIds.Count == 1)
        {
            // Compatibility for existing single-operation host verifiers. A generic/non-matching
            // report cannot discharge a write. Production supplies observed ArtifactStore proof.
            var call = context.Calls.Single(c => c.Id == context.MutationCallIds[0]);
            var item = _attempts[call.Invocation!.InvocationId];
            coverage = report.Criteria.Where(c => context.Contract.AcceptanceCriteria.Any(a => a.CriterionId == c.CriterionId))
                .Select(c => new VerificationCallCoverage(item.Value.InvocationId, c.CriterionId, item.Value.TargetId,
                    c.CriterionId, c.Status, c.EvidenceIds)).ToArray();
        }
        foreach (var c in coverage)
        {
            if (!current.ContainsKey(c.InvocationId) || !_attempts.TryGetValue(c.InvocationId, out var attempt)
                || string.IsNullOrWhiteSpace(c.TargetId) || string.IsNullOrWhiteSpace(c.PostconditionId)
                || !report.Criteria.Any(r => r.CriterionId == c.CriterionId))
                throw new AgentVerificationRequiredException("Verifier coverage is not bound to this observed invocation.");
            var passed = c.Status == VerificationCriterionStatus.Passed && Proof(context.Evidence, c.EvidenceIds);
            attempt.Coverage[c.CriterionId] = passed ? c : c with { Status = c.Status == VerificationCriterionStatus.Failed
                ? VerificationCriterionStatus.Failed : VerificationCriterionStatus.NotVerified };
            attempt.VerifierId = report.VerifierId;
            if (passed) foreach (var id in c.EvidenceIds) _proofIds.Add(id);
        }
        foreach (var id in current.Keys)
        {
            var a = _attempts[id];
            a.Verified = a.Value.Status == ToolOutcomeStatus.Succeeded && a.Value.Effect is not (ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied)
                && a.Coverage.Count > 0 && a.Coverage.Values.All(c => c.Status == VerificationCriterionStatus.Passed);
            if (!a.Verified) continue;
            // A verified corrective call may supersede its own failed target/postcondition only.
            // It must not clear another target just because a generic router criterion is reused.
            foreach (var old in _attempts.Values.Where(o => o != a && !o.Verified && o.ResolvedBy is null).ToArray())
                if (old.Value.ToolName == a.Value.ToolName && SameCoverage(old, a) && SafeToResolve(old, a)) Resolve(old, a, "verified-retry");
        }
        foreach (var resolution in report.AlternateResolutions)
        {
            if (!_attempts.TryGetValue(resolution.FailedInvocationId, out var old)
                || !current.ContainsKey(resolution.ReplacementInvocationId)
                || !_attempts.TryGetValue(resolution.ReplacementInvocationId, out var replacement)
                || !replacement.Verified || old.ResolvedBy is not null || !SafeToResolve(old, replacement)
                || !context.Contract.AcceptanceCriteria.Any(c => c.CriterionId == resolution.CriterionId)
                || !old.Coverage.TryGetValue(resolution.CriterionId, out var before)
                || !replacement.Coverage.TryGetValue(resolution.CriterionId, out var after)
                || after.Status != VerificationCriterionStatus.Passed
                || before.TargetId != resolution.TargetId || after.TargetId != resolution.TargetId
                || before.PostconditionId != resolution.PostconditionId || after.PostconditionId != resolution.PostconditionId
                || !SameCoverage(old, replacement))
                throw new AgentVerificationRequiredException("Alternate recovery lacks matching obligation, target or verified postcondition.");
            Resolve(old, replacement, "verified-alternate");
        }
        var retired = context.Contract.Goals!.Obligations.Where(o => !o.Active).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _criteria.Keys.Where(k => retired.Contains(k.Criterion)).ToArray()) _criteria.Remove(key);
        return Aggregate(report.VerifierId) with { CallCoverage = coverage, AlternateResolutions = report.AlternateResolutions };
    }

    private bool Proof(IReadOnlyList<AgentEvidenceReference> observed, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return false;
        if (!requireObservedProof) return true; // Legacy unit fixture hosts without an ArtifactStore projector.
        return ids.Distinct(StringComparer.Ordinal).All(id => observed.Where(e => e.ReferenceId == id)
            .Select(e => (e.Kind, e.Sha256)).Distinct().Count() == 1);
    }
    private static bool SafeToResolve(Attempt old, Attempt current) => old != current
        && old.Value.GoalRevisionId == current.Value.GoalRevisionId
        && old.Value.Status != ToolOutcomeStatus.Running
        && old.Value.Effect is not (ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied);
    private static bool SameCoverage(Attempt old, Attempt current) => old.Coverage.Count > 0
        && old.Coverage.All(p => current.Coverage.TryGetValue(p.Key, out var next)
            && next.Status == VerificationCriterionStatus.Passed && next.TargetId == p.Value.TargetId
            && next.PostconditionId == p.Value.PostconditionId);
    private void Resolve(Attempt old, Attempt current, string reason)
    {
        old.ResolvedBy = current.Value.InvocationId;
        var c = current.Coverage.Values.First();
        _resolutions.Add(new(old.Value.InvocationId, current.Value.InvocationId, c.CriterionId, c.TargetId,
            c.PostconditionId, reason, c.EvidenceIds.ToArray()));
        // Remove only the resolved predecessor's active report; immutable history remains.
        if (old.VerifierId != current.VerifierId && old.VerifierId is not null)
            foreach (var id in old.Coverage.Keys) _criteria.Remove((old.VerifierId, id));
    }
    public bool IsResolved(Guid failed) => _attempts.TryGetValue(failed, out var a) && a.ResolvedBy is not null;
    public bool CanResolveExactRetry(Guid failed, Guid successful)
    {
        if (!_attempts.TryGetValue(failed, out var old) || !_attempts.TryGetValue(successful, out var next)) return false;
        if (old.ResolvedBy == successful) return true;
        if (!SafeToResolve(old, next) || next.Value.Status != ToolOutcomeStatus.Succeeded) return false;
        if (!old.Value.Mutation && old.Value.Effect == ToolMutationEffect.None)
            return !next.Value.Mutation || next.Verified;
        return next.Verified && SameCoverage(old, next);
    }
    private VerificationReport Aggregate(string lastVerifier)
    {
        var criteria = _criteria.GroupBy(p => p.Key.Criterion, StringComparer.Ordinal).Select(group =>
        {
            var values = group.Select(p => p.Value).ToArray();
            var fail = values.FirstOrDefault(c => c.Status == VerificationCriterionStatus.Failed);
            if (fail is not null) return fail;
            var pending = values.FirstOrDefault(c => c.Status == VerificationCriterionStatus.NotVerified);
            if (pending is not null) return pending;
            return new VerificationCriterionResult(group.Key, VerificationCriterionStatus.Passed,
                values.SelectMany(c => c.EvidenceIds));
        }).OrderBy(c => c.CriterionId, StringComparer.Ordinal).ToArray();
        return new(lastVerifier, criteria, criteria.SelectMany(c => c.EvidenceIds))
        { ContributingVerifierIds = _criteria.Keys.Select(k => k.Verifier).Distinct(StringComparer.Ordinal).ToArray() };
    }
    public H2AgentCompletionAssessment Snapshot(AgentTaskContract contract, int unresolved, int pending, VerificationReport? aggregate)
    {
        var goals = contract.Goals!.Active;
        var verified = goals.Count(o => o.Status == AgentObligationStatus.Verified);
        var open = goals.Count - verified;
        var missingCriteria = contract.AcceptanceCriteria.Count(a => aggregate?.Criteria.Any(c => c.CriterionId == a.CriterionId
            && c.Status == VerificationCriterionStatus.Passed) != true);
        var blocked = open > 0 || unresolved > 0 || pending > 0 || UnverifiedMutations > 0
            || OutstandingProofs > 0 || aggregate is { Passed: false } || contract.VerificationPolicy.RequireVerification && missingCriteria > 0;
        var state = blocked ? verified > 0 ? "PartiallyCompleted" : "Blocked"
            : aggregate?.Passed == true ? "CompletedVerified" : "CompletedUnverified";
        return new(state, goals.Count, verified, open, UnverifiedMutations, unresolved, pending,
            aggregate?.ContributingVerifierIds ?? [], _resolutions.ToArray());
    }
}
