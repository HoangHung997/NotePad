using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public enum AgentRuntimeEntryPoint { Lab, Global, Project }
public enum AgentRuntimeHookKind { BeforeModelRequest, AfterToolObservation, BeforeCompletion, Checkpoint }
public enum AgentRuntimeCheckpointKind { ContextPrepared, ToolBatchObserved, CompletionValidated }

/// <summary>Immutable invocation identity, not a permission grant or a second task store.</summary>
public sealed record AgentRuntimeInvocation(AgentRuntimeEntryPoint EntryPoint, Guid? ProjectId = null)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(EntryPoint) || (EntryPoint == AgentRuntimeEntryPoint.Project) != ProjectId.HasValue
            || ProjectId == Guid.Empty)
            throw new ArgumentException("Runtime entry point and project identity do not agree.");
    }
}

public sealed record AgentRuntimeHookScope(Guid TaskId, Guid TurnId, AgentRuntimeInvocation Invocation);

/// <summary>The exact provider-neutral request at the engine/transport boundary. This is NOT
/// provider wire-token accounting; that belongs next to serialization in AR-050.</summary>
public sealed record AgentRuntimeModelRequestBoundary(
    AgentRuntimeHookScope Scope, int RequestIndex, AgentContextSnapshot Context,
    IReadOnlyList<string> RegisteredToolNames,
    AgentTransportStartRequest? Start = null, AgentTransportContinuationRequest? Continuation = null);

public sealed record AgentRuntimeObservationBoundary(
    AgentRuntimeHookScope Scope, AgentRuntimeVerificationContext Observation);

public sealed record AgentRuntimeCompletionBoundary(
    AgentRuntimeHookScope Scope, AgentTaskContract Contract, string FinalText,
    VerificationReport? LatestVerification, int UnresolvedCalls, bool MutationAwaitingVerification);

/// <summary>A safe engine checkpoint boundary. Only ContextCheckpoint identifies a previously
/// persisted CompactionManager checkpoint. Other boundaries are explicitly in-memory; they do
/// not certify journal persistence, resume, semantic recall, or a completed user outcome.</summary>
public sealed record AgentRuntimeCheckpointBoundary(
    AgentRuntimeHookScope Scope, AgentRuntimeCheckpointKind Kind, AgentContextSnapshot Context,
    int ToolRound = 0, RuntimeCompactionResult? ContextCheckpoint = null);

/// <summary>Host-only hooks. They are awaited, cannot be selected by the model, and cannot grant
/// permissions or replace the final host verification gate. Hook failure stops the current run.</summary>
public interface IAgentRuntimeHooks
{
    ValueTask BeforeModelRequestAsync(AgentRuntimeModelRequestBoundary boundary, CancellationToken cancellationToken);
    ValueTask AfterToolObservationAsync(AgentRuntimeObservationBoundary boundary, CancellationToken cancellationToken);
    ValueTask BeforeCompletionAsync(AgentRuntimeCompletionBoundary boundary, CancellationToken cancellationToken);
    ValueTask OnCheckpointAsync(AgentRuntimeCheckpointBoundary boundary, CancellationToken cancellationToken);
}

/// <summary>Small, content-free trace for the existing telemetry channel. No prompts, arguments,
/// outputs, paths, credentials or final prose are copied into this record.</summary>
public sealed record AgentRuntimeHookEvent(
    long Sequence, AgentRuntimeHookKind Kind, AgentRuntimeHookScope Scope,
    int RequestIndex = 0, int ToolRound = 0, int ContextCharacters = 0,
    int RegisteredToolCount = 0, int ObservationCount = 0,
    AgentRuntimeCheckpointKind? CheckpointKind = null, string? DurableCheckpointId = null);

/// <summary>The same concrete implementation is used by Lab, Global and Project. It validates
/// boundary identities and emits actual hook calls into AgentRunTelemetry, without owning a
/// parallel journal or making product acceptance decisions.</summary>
public class AgentRuntimeHooks(AgentRunTelemetry? telemetry = null, Action<AgentRuntimeHookEvent>? observer = null)
    : IAgentRuntimeHooks
{
    private long _sequence;

    public virtual ValueTask BeforeModelRequestAsync(AgentRuntimeModelRequestBoundary boundary, CancellationToken cancellationToken)
    {
        Validate(boundary.Scope, cancellationToken);
        if ((boundary.Start is null) == (boundary.Continuation is null) || boundary.RequestIndex < 1)
            throw new InvalidOperationException("Exactly one runtime request is required at the model boundary.");
        var task = boundary.Start?.TaskId ?? boundary.Continuation!.TaskId;
        var turn = boundary.Start?.TurnId ?? boundary.Continuation!.TurnId;
        if (task != boundary.Scope.TaskId || turn != boundary.Scope.TurnId)
            throw new InvalidOperationException("Model request crossed runtime task/turn identity.");
        Emit(new(Next(), AgentRuntimeHookKind.BeforeModelRequest, boundary.Scope,
            RequestIndex: boundary.RequestIndex, ContextCharacters: boundary.Context.Usage.TotalCharacters,
            RegisteredToolCount: boundary.RegisteredToolNames.Count));
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask AfterToolObservationAsync(AgentRuntimeObservationBoundary boundary, CancellationToken cancellationToken)
    {
        Validate(boundary.Scope, cancellationToken);
        var observation = boundary.Observation;
        if (observation.Contract.TaskId != boundary.Scope.TaskId || observation.Calls.Count != observation.Results.Count
            || !observation.Calls.Select(c => c.Id).SequenceEqual(observation.Results.Select(r => r.ToolCallId)))
            throw new InvalidOperationException("Tool observation crossed runtime identity or lost call/result pairing.");
        Emit(new(Next(), AgentRuntimeHookKind.AfterToolObservation, boundary.Scope,
            ToolRound: observation.ToolRound, ObservationCount: observation.Results.Count));
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask BeforeCompletionAsync(AgentRuntimeCompletionBoundary boundary, CancellationToken cancellationToken)
    {
        Validate(boundary.Scope, cancellationToken);
        if (boundary.Contract.TaskId != boundary.Scope.TaskId)
            throw new InvalidOperationException("Completion candidate belongs to a different task.");
        // This is a candidate, not a PASS: AgentRuntime still enforces every existing gate.
        Emit(new(Next(), AgentRuntimeHookKind.BeforeCompletion, boundary.Scope));
        return ValueTask.CompletedTask;
    }

    public virtual ValueTask OnCheckpointAsync(AgentRuntimeCheckpointBoundary boundary, CancellationToken cancellationToken)
    {
        Validate(boundary.Scope, cancellationToken);
        if (!Enum.IsDefined(boundary.Kind) || boundary.ToolRound < 0
            || boundary.ContextCheckpoint is not null && boundary.Kind != AgentRuntimeCheckpointKind.ContextPrepared)
            throw new InvalidOperationException("Invalid runtime checkpoint boundary.");
        Emit(new(Next(), AgentRuntimeHookKind.Checkpoint, boundary.Scope,
            ToolRound: boundary.ToolRound, ContextCharacters: boundary.Context.Usage.TotalCharacters,
            CheckpointKind: boundary.Kind, DurableCheckpointId: boundary.ContextCheckpoint?.CheckpointId));
        return ValueTask.CompletedTask;
    }

    private long Next() => Interlocked.Increment(ref _sequence);
    private void Emit(AgentRuntimeHookEvent item)
    {
        telemetry?.Trace.Mark(AgentTraceKind.RuntimeHook, "runtime-hook-" + item.Kind, JsonSerializer.Serialize(item));
        observer?.Invoke(item);
    }
    private static void Validate(AgentRuntimeHookScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.TaskId == Guid.Empty || scope.TurnId == Guid.Empty)
            throw new InvalidOperationException("Runtime hooks require nonempty task/turn identity.");
        scope.Invocation.Validate();
    }
}
