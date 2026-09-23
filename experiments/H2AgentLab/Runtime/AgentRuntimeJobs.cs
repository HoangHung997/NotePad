using System.Security.Cryptography;
using System.Text;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

/// <summary>Host-only observation of an existing invocation, not a new tool call, a verifier
/// verdict, or a value parsed from model/provider JSON. The owning service observes its handles.</summary>
public sealed record AgentRuntimeJobObservation(Guid InvocationId, string JobId,
    string GoalRevisionId, ToolExecutionOutput Output);

/// <summary>Bounded, run-local index over the existing invocation journal. Durable truth stays
/// in the Agent archive. It never starts a process, adopts a PID or replays a mutation.</summary>
internal sealed class AgentRuntimeJobs(Action<H2AgentOperationRecord>? journal)
{
    private sealed record Pending(ToolCall Call, AgentTaskContract Contract, ToolOutcome Outcome,
        H2AgentOperationRecord? Record);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, H2AgentOperationRecord> _batchRecords = [];
    private readonly Dictionary<Guid, Pending> _pending = [];

    public void Record(H2AgentOperationRecord record)
    {
        journal?.Invoke(record);
        lock (_gate)
        {
            if (_batchRecords.Count >= 4096 && !_batchRecords.ContainsKey(record.InvocationId))
                throw new AgentVerificationRequiredException("Job journal index capacity reached.");
            _batchRecords[record.InvocationId] = record;
        }
    }

    public void Register(AgentRuntimeVerificationContext context)
    {
        foreach (var call in context.Calls)
        {
            var outcome = context.Results.Single(x => x.ToolCallId == call.Id).Outcome;
            if (outcome is not { Status: ToolOutcomeStatus.Running or ToolOutcomeStatus.Succeeded, Job: not null } || call.Invocation is null) continue;
            if (_pending.Count >= 128) throw new AgentVerificationRequiredException("Pending job index capacity reached.");
            if (!_pending.TryAdd(call.Invocation.InvocationId,
                new(call, context.Contract, outcome, _batchRecords.GetValueOrDefault(call.Invocation.InvocationId))))
                throw new AgentVerificationRequiredException("Duplicate running job invocation.");
        }
        _batchRecords.Clear();
    }

    public IReadOnlyList<(ToolCall Call, ToolExecutionOutput Output)> Observe(
        IReadOnlyList<AgentRuntimeJobObservation> updates, AgentTaskContract current, ToolRegistry registry)
    {
        if (updates.Count > 128 || updates.Select(x => x.InvocationId).Distinct().Count() != updates.Count)
            throw new AgentVerificationRequiredException("Invalid host job observation batch.");
        var accepted = new List<(ToolCall, ToolExecutionOutput)>();
        foreach (var update in updates)
        {
            if (!_pending.TryGetValue(update.InvocationId, out var pending)
                || pending.Outcome.Job!.JobId != update.JobId
                || pending.Contract.Goals!.RevisionId != update.GoalRevisionId
                || update.Output.Outcome.Invocation != pending.Call.Invocation
                || update.Output.Outcome.Job?.JobId != update.JobId
                || pending.Outcome.Status == ToolOutcomeStatus.Succeeded && update.Output.Outcome.Status != ToolOutcomeStatus.Succeeded
                || update.Output.Outcome.Status is ToolOutcomeStatus.Running or ToolOutcomeStatus.Rejected
                || update.Output.Outcome.Effect == ToolMutationEffect.None && pending.Outcome.Effect != ToolMutationEffect.None
                || !registry.TryGet(pending.Call.Name, out var descriptor))
                throw new AgentVerificationRequiredException("Job observation does not match an owned running invocation.");
            var output = ToolOutcomeBridge.Validate(update.Output with
            { Outcome = update.Output.Outcome with { Resource = pending.Outcome.Resource } }, pending.Call, descriptor);
            if (output.Outcome.Error?.Code == "invalid_result")
                throw new AgentVerificationRequiredException("Invalid terminal job observation.");
            if (journal is not null)
            {
                var original = pending.Record ?? throw new AgentVerificationRequiredException("Job dispatch journal is missing.");
                journal(original with { State = "Result", Status = output.Outcome.Status.ToString(),
                    Effect = output.Outcome.Effect.ToString(), ErrorCode = output.Outcome.Error?.Code,
                    OutputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.DomainPayload))).ToLowerInvariant() });
            }
            // Observe the old operation, but never verify a different user revision with it.
            if (current.Goals!.RevisionId != update.GoalRevisionId)
                throw new AgentVerificationRequiredException("Job ended under an earlier goal revision; reconcile before completing current work.");
            _pending.Remove(update.InvocationId);
            accepted.Add((pending.Call, output));
        }
        return accepted;
    }
}
