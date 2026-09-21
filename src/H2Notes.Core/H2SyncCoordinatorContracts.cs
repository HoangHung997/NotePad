using System.Security.Cryptography;
using System.Text;

namespace H2Notes.Core;

public enum H2ProjectEntityKind
{
    Workspace = 1,
    Project = 2,
    Task = 3,
    Note = 4,
    Link = 5,
    Conversation = 6,
    Message = 7
}

public enum H2ProjectEventKind
{
    CreateEntity = 1,
    SetField = 2,
    DeleteEntity = 3,
    ReorderEntity = 4,
    AppendMessage = 5,
    ResolveConflict = 6,
    MigrationBaseline = 7
}

public enum H2ProjectConflictState
{
    Open = 1,
    Resolved = 2
}

public enum H2ProjectEventDisposition
{
    Applied = 1,
    Conflict = 2
}

public enum H2ConflictResolutionAction
{
    SetField = 1,
    DeleteEntity = 2,
    RestoreEntity = 3
}

public sealed record H2ConflictResolutionPayload(
    H2ConflictResolutionAction Action,
    string? ValueJson);

public sealed record H2ProjectRevisionEntry
{
    public H2ProjectRevisionEntry(
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey,
        long revision,
        bool isDeleted = false)
    {
        if (!Enum.IsDefined(entityKind)) throw new ArgumentOutOfRangeException(nameof(entityKind));
        EntityKind = entityKind;
        EntityId = H2CoordinatorContractGuard.NonEmpty(entityId, nameof(entityId));
        FieldKey = H2CoordinatorContractGuard.BoundOrNull(fieldKey, nameof(fieldKey), 200);
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        IsDeleted = isDeleted;
    }

    public H2ProjectEntityKind EntityKind { get; }
    public Guid EntityId { get; }
    public string? FieldKey { get; }
    public long Revision { get; }
    public bool IsDeleted { get; }

    public string StableKey
        => $"{(int)EntityKind}:{EntityId:N}:{FieldKey ?? "$entity"}";
}

public enum H2ProjectAiQueueState
{
    Waiting = 1,
    WaitingForSync = 2,
    Running = 3,
    WaitingForRepair = 4,
    NeedsUserReview = 5,
    Completed = 6,
    Abandoned = 7,
    Failed = 8,
    Cancelled = 9
}

/// <summary>
/// Stable machine installation identity used by Coordinator protocol. This is intentionally
/// independent from Windows computer name, drive mapping, username, IP address and NAS path.
/// </summary>
public sealed record H2CoordinatorDeviceIdentity
{
    public H2CoordinatorDeviceIdentity(Guid deviceId, string displayName)
    {
        DeviceId = H2CoordinatorContractGuard.NonEmpty(deviceId, nameof(deviceId));
        DisplayName = H2CoordinatorContractGuard.Bound(displayName, nameof(displayName), 1, 200);
    }

    public Guid DeviceId { get; }
    public string DisplayName { get; }

    public static H2CoordinatorDeviceIdentity CreateNew(string displayName)
        => new(Guid.NewGuid(), displayName);
}

public sealed record H2ProjectMutationTarget
{
    public H2ProjectMutationTarget(
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey = null,
        long? expectedRevision = null)
    {
        if (!Enum.IsDefined(entityKind)) throw new ArgumentOutOfRangeException(nameof(entityKind));
        EntityKind = entityKind;
        EntityId = H2CoordinatorContractGuard.NonEmpty(entityId, nameof(entityId));
        FieldKey = H2CoordinatorContractGuard.BoundOrNull(fieldKey, nameof(fieldKey), 200);
        if (expectedRevision is < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ExpectedRevision = expectedRevision;
    }

    public H2ProjectEntityKind EntityKind { get; }
    public Guid EntityId { get; }
    public string? FieldKey { get; }
    public long? ExpectedRevision { get; }
}

/// <summary>
/// Immutable client-created project mutation before Coordinator acceptance/ordering.
/// ClientCreatedUtc is audit metadata only; it never decides accepted shared order.
/// </summary>
public sealed record H2ProjectEventDraft
{
    public const int MaxPayloadChars = 8 * 1024 * 1024;

    public H2ProjectEventDraft(
        Guid eventId,
        Guid workspaceId,
        Guid projectId,
        Guid deviceId,
        long deviceSequence,
        Guid clientOperationId,
        H2ProjectEventKind kind,
        H2ProjectMutationTarget target,
        string payloadJson,
        string payloadSha256,
        DateTimeOffset clientCreatedUtc)
    {
        EventId = H2CoordinatorContractGuard.NonEmpty(eventId, nameof(eventId));
        WorkspaceId = H2CoordinatorContractGuard.NonEmpty(workspaceId, nameof(workspaceId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        DeviceId = H2CoordinatorContractGuard.NonEmpty(deviceId, nameof(deviceId));
        if (deviceSequence <= 0) throw new ArgumentOutOfRangeException(nameof(deviceSequence));
        DeviceSequence = deviceSequence;
        ClientOperationId = H2CoordinatorContractGuard.NonEmpty(clientOperationId, nameof(clientOperationId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Target = target ?? throw new ArgumentNullException(nameof(target));

        if (payloadJson is null) throw new ArgumentNullException(nameof(payloadJson));
        if (payloadJson.Length > MaxPayloadChars)
            throw new ArgumentException("Project event payload is too large; external bytes belong in resources/artifacts.", nameof(payloadJson));
        PayloadJson = payloadJson;
        PayloadSha256 = H2CoordinatorContractGuard.Sha256(payloadSha256, nameof(payloadSha256));
        var computed = ComputePayloadSha256(payloadJson);
        if (!string.Equals(computed, PayloadSha256, StringComparison.Ordinal))
            throw new ArgumentException("Project event payload SHA-256 does not match payload bytes.", nameof(payloadSha256));

        if (kind == H2ProjectEventKind.SetField && string.IsNullOrWhiteSpace(target.FieldKey))
            throw new ArgumentException("SetField events require a stable FieldKey.", nameof(target));

        ClientCreatedUtc = clientCreatedUtc;
    }

    public Guid EventId { get; }
    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public Guid DeviceId { get; }
    public long DeviceSequence { get; }
    public Guid ClientOperationId { get; }
    public H2ProjectEventKind Kind { get; }
    public H2ProjectMutationTarget Target { get; }
    public string PayloadJson { get; }
    public string PayloadSha256 { get; }
    public DateTimeOffset ClientCreatedUtc { get; }

    public static string ComputePayloadSha256(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
    }
}

public sealed record H2AcceptedProjectEvent
{
    public H2AcceptedProjectEvent(
        H2ProjectEventDraft draft,
        long serverSequence,
        DateTimeOffset acceptedUtc,
        H2ProjectEventDisposition disposition = H2ProjectEventDisposition.Applied,
        long? resultingRevision = null,
        long? resultingEntityRevision = null,
        Guid? conflictId = null)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        if (serverSequence <= 0) throw new ArgumentOutOfRangeException(nameof(serverSequence));
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        if (resultingRevision is < 0) throw new ArgumentOutOfRangeException(nameof(resultingRevision));
        if (resultingEntityRevision is < 0) throw new ArgumentOutOfRangeException(nameof(resultingEntityRevision));
        if (disposition == H2ProjectEventDisposition.Conflict
            && (!conflictId.HasValue || conflictId.Value == Guid.Empty))
            throw new ArgumentException("Conflict event requires a non-empty ConflictId.", nameof(conflictId));
        if (disposition == H2ProjectEventDisposition.Applied && conflictId is not null)
            throw new ArgumentException("Applied event cannot carry ConflictId.", nameof(conflictId));

        ServerSequence = serverSequence;
        AcceptedUtc = acceptedUtc;
        Disposition = disposition;
        ResultingRevision = resultingRevision;
        ResultingEntityRevision = resultingEntityRevision;
        ConflictId = conflictId;
    }

    public H2ProjectEventDraft Draft { get; }
    public long ServerSequence { get; }
    public DateTimeOffset AcceptedUtc { get; }
    public H2ProjectEventDisposition Disposition { get; }
    public long? ResultingRevision { get; }
    public long? ResultingEntityRevision { get; }
    public Guid? ConflictId { get; }
}

public sealed record H2ProjectSnapshot
{
    public const int MaxStateChars = 32 * 1024 * 1024;

    public H2ProjectSnapshot(
        Guid workspaceId,
        Guid projectId,
        long throughServerSequence,
        string stateJson,
        string stateSha256,
        DateTimeOffset createdUtc,
        IEnumerable<H2ProjectRevisionEntry>? revisions = null)
    {
        WorkspaceId = H2CoordinatorContractGuard.NonEmpty(workspaceId, nameof(workspaceId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        if (throughServerSequence < 0) throw new ArgumentOutOfRangeException(nameof(throughServerSequence));
        ThroughServerSequence = throughServerSequence;
        if (stateJson is null) throw new ArgumentNullException(nameof(stateJson));
        if (stateJson.Length > MaxStateChars) throw new ArgumentException("Project snapshot is too large.", nameof(stateJson));
        StateJson = stateJson;
        StateSha256 = H2CoordinatorContractGuard.Sha256(stateSha256, nameof(stateSha256));
        var computed = H2ProjectEventDraft.ComputePayloadSha256(stateJson);
        if (!string.Equals(computed, StateSha256, StringComparison.Ordinal))
            throw new ArgumentException("Project snapshot SHA-256 does not match state bytes.", nameof(stateSha256));
        CreatedUtc = createdUtc;

        var revisionArray = (revisions ?? Array.Empty<H2ProjectRevisionEntry>()).ToArray();
        if (revisionArray.GroupBy(item => item.StableKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Snapshot contains duplicate revision identity.", nameof(revisions));
        Revisions = revisionArray;
    }

    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public long ThroughServerSequence { get; }
    public string StateJson { get; }
    public string StateSha256 { get; }
    public DateTimeOffset CreatedUtc { get; }
    public IReadOnlyList<H2ProjectRevisionEntry> Revisions { get; }
}

public sealed record H2ProjectConflict
{
    public H2ProjectConflict(
        Guid conflictId,
        Guid workspaceId,
        Guid projectId,
        H2ProjectMutationTarget target,
        IEnumerable<Guid> eventIds,
        DateTimeOffset createdUtc,
        H2ProjectConflictState state = H2ProjectConflictState.Open,
        Guid? resolvedByEventId = null)
    {
        ConflictId = H2CoordinatorContractGuard.NonEmpty(conflictId, nameof(conflictId));
        WorkspaceId = H2CoordinatorContractGuard.NonEmpty(workspaceId, nameof(workspaceId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(eventIds);
        var ids = eventIds.ToArray();
        if (ids.Length < 2 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Conflict must reference at least two distinct non-empty event IDs.", nameof(eventIds));
        EventIds = ids;
        CreatedUtc = createdUtc;
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        if (state == H2ProjectConflictState.Resolved
            && (!resolvedByEventId.HasValue || resolvedByEventId.Value == Guid.Empty))
            throw new ArgumentException("Resolved conflict requires a non-empty resolution event.", nameof(resolvedByEventId));
        if (state == H2ProjectConflictState.Open && resolvedByEventId is not null)
            throw new ArgumentException("Open conflict cannot already reference a resolution event.", nameof(resolvedByEventId));
        ResolvedByEventId = resolvedByEventId;
    }

    public Guid ConflictId { get; }
    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public H2ProjectMutationTarget Target { get; }
    public IReadOnlyList<Guid> EventIds { get; }
    public DateTimeOffset CreatedUtc { get; }
    public H2ProjectConflictState State { get; }
    public Guid? ResolvedByEventId { get; }
}

public sealed record H2LocalOutboxItem
{
    public H2LocalOutboxItem(H2ProjectEventDraft draft, DateTimeOffset enqueuedUtc, int attemptCount = 0)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        EnqueuedUtc = enqueuedUtc;
        if (attemptCount < 0) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        AttemptCount = attemptCount;
    }

    public H2ProjectEventDraft Draft { get; }
    public DateTimeOffset EnqueuedUtc { get; }
    public int AttemptCount { get; }
}

public sealed record H2ProjectEventAcknowledgement
{
    public H2ProjectEventAcknowledgement(
        Guid eventId,
        Guid clientOperationId,
        long serverSequence,
        DateTimeOffset acceptedUtc)
    {
        EventId = H2CoordinatorContractGuard.NonEmpty(eventId, nameof(eventId));
        ClientOperationId = H2CoordinatorContractGuard.NonEmpty(clientOperationId, nameof(clientOperationId));
        if (serverSequence <= 0) throw new ArgumentOutOfRangeException(nameof(serverSequence));
        ServerSequence = serverSequence;
        AcceptedUtc = acceptedUtc;
    }

    public Guid EventId { get; }
    public Guid ClientOperationId { get; }
    public long ServerSequence { get; }
    public DateTimeOffset AcceptedUtc { get; }
}

public sealed record H2ProjectHead
{
    public H2ProjectHead(
        Guid workspaceId,
        Guid projectId,
        long serverSequence,
        long snapshotThroughSequence,
        int openConflictCount)
    {
        WorkspaceId = H2CoordinatorContractGuard.NonEmpty(workspaceId, nameof(workspaceId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        if (serverSequence < 0) throw new ArgumentOutOfRangeException(nameof(serverSequence));
        if (snapshotThroughSequence < 0 || snapshotThroughSequence > serverSequence)
            throw new ArgumentOutOfRangeException(nameof(snapshotThroughSequence));
        if (openConflictCount < 0) throw new ArgumentOutOfRangeException(nameof(openConflictCount));
        ServerSequence = serverSequence;
        SnapshotThroughSequence = snapshotThroughSequence;
        OpenConflictCount = openConflictCount;
    }

    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public long ServerSequence { get; }
    public long SnapshotThroughSequence { get; }
    public int OpenConflictCount { get; }
}

public sealed record H2ProjectEventSubmissionResult
{
    public H2ProjectEventSubmissionResult(
        IEnumerable<H2ProjectEventAcknowledgement> acknowledgements,
        IEnumerable<H2ProjectConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(acknowledgements);
        ArgumentNullException.ThrowIfNull(conflicts);
        Acknowledgements = acknowledgements.ToArray();
        Conflicts = conflicts.ToArray();
    }

    public IReadOnlyList<H2ProjectEventAcknowledgement> Acknowledgements { get; }
    public IReadOnlyList<H2ProjectConflict> Conflicts { get; }
}

public sealed record H2ProjectAiRequest
{
    public H2ProjectAiRequest(
        Guid requestId,
        Guid workspaceId,
        Guid projectId,
        Guid ownerDeviceId,
        Guid clientRequestId,
        Guid? conversationId,
        Guid? userMessageId,
        DateTimeOffset clientCreatedUtc)
    {
        RequestId = H2CoordinatorContractGuard.NonEmpty(requestId, nameof(requestId));
        WorkspaceId = H2CoordinatorContractGuard.NonEmpty(workspaceId, nameof(workspaceId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        OwnerDeviceId = H2CoordinatorContractGuard.NonEmpty(ownerDeviceId, nameof(ownerDeviceId));
        ClientRequestId = H2CoordinatorContractGuard.NonEmpty(clientRequestId, nameof(clientRequestId));
        if (conversationId == Guid.Empty) throw new ArgumentException("ConversationId cannot be empty.", nameof(conversationId));
        if (userMessageId == Guid.Empty) throw new ArgumentException("UserMessageId cannot be empty.", nameof(userMessageId));
        ConversationId = conversationId;
        UserMessageId = userMessageId;
        ClientCreatedUtc = clientCreatedUtc;
    }

    public Guid RequestId { get; }
    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public Guid OwnerDeviceId { get; }
    public Guid ClientRequestId { get; }
    public Guid? ConversationId { get; }
    public Guid? UserMessageId { get; }
    public DateTimeOffset ClientCreatedUtc { get; }
}

public sealed record H2QueuedProjectAiRequest
{
    public H2QueuedProjectAiRequest(
        H2ProjectAiRequest request,
        long queueSequence,
        H2ProjectAiQueueState state,
        DateTimeOffset acceptedUtc)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (queueSequence <= 0) throw new ArgumentOutOfRangeException(nameof(queueSequence));
        QueueSequence = queueSequence;
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        AcceptedUtc = acceptedUtc;
    }

    public H2ProjectAiRequest Request { get; }
    public long QueueSequence { get; }
    public H2ProjectAiQueueState State { get; }
    public DateTimeOffset AcceptedUtc { get; }
}

public sealed record H2ProjectSyncBarrier
{
    public H2ProjectSyncBarrier(Guid projectId, long requiredProjectSequence)
    {
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        if (requiredProjectSequence < 0) throw new ArgumentOutOfRangeException(nameof(requiredProjectSequence));
        RequiredProjectSequence = requiredProjectSequence;
    }

    public Guid ProjectId { get; }
    public long RequiredProjectSequence { get; }
}

public sealed record H2ProjectAiLease
{
    public H2ProjectAiLease(
        Guid leaseId,
        Guid requestId,
        Guid projectId,
        Guid ownerDeviceId,
        long queueSequence,
        H2ProjectSyncBarrier barrier,
        DateTimeOffset grantedUtc,
        DateTimeOffset lastHeartbeatUtc,
        DateTimeOffset expiresUtc)
    {
        LeaseId = H2CoordinatorContractGuard.NonEmpty(leaseId, nameof(leaseId));
        RequestId = H2CoordinatorContractGuard.NonEmpty(requestId, nameof(requestId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        OwnerDeviceId = H2CoordinatorContractGuard.NonEmpty(ownerDeviceId, nameof(ownerDeviceId));
        if (queueSequence <= 0) throw new ArgumentOutOfRangeException(nameof(queueSequence));
        QueueSequence = queueSequence;
        Barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        if (barrier.ProjectId != projectId)
            throw new ArgumentException("Lease barrier must belong to the same ProjectId.", nameof(barrier));
        if (lastHeartbeatUtc < grantedUtc || expiresUtc <= lastHeartbeatUtc)
            throw new ArgumentException("Lease heartbeat/expiry timeline is invalid.", nameof(expiresUtc));
        GrantedUtc = grantedUtc;
        LastHeartbeatUtc = lastHeartbeatUtc;
        ExpiresUtc = expiresUtc;
    }

    public Guid LeaseId { get; }
    public Guid RequestId { get; }
    public Guid ProjectId { get; }
    public Guid OwnerDeviceId { get; }
    public long QueueSequence { get; }
    public H2ProjectSyncBarrier Barrier { get; }
    public DateTimeOffset GrantedUtc { get; }
    public DateTimeOffset LastHeartbeatUtc { get; }
    public DateTimeOffset ExpiresUtc { get; }
}

public sealed record H2ProjectAiCompletion
{
    public H2ProjectAiCompletion(
        Guid leaseId,
        Guid requestId,
        Guid projectId,
        H2ProjectAiQueueState terminalState,
        long committedThroughProjectSequence,
        Guid? assistantMessageEventId,
        DateTimeOffset completedUtc)
    {
        LeaseId = H2CoordinatorContractGuard.NonEmpty(leaseId, nameof(leaseId));
        RequestId = H2CoordinatorContractGuard.NonEmpty(requestId, nameof(requestId));
        ProjectId = H2CoordinatorContractGuard.NonEmpty(projectId, nameof(projectId));
        if (terminalState != H2ProjectAiQueueState.Completed
            && terminalState != H2ProjectAiQueueState.Abandoned
            && terminalState != H2ProjectAiQueueState.Failed
            && terminalState != H2ProjectAiQueueState.Cancelled
            && terminalState != H2ProjectAiQueueState.NeedsUserReview)
            throw new ArgumentException("AI completion requires a resolved terminal state.", nameof(terminalState));
        if (committedThroughProjectSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(committedThroughProjectSequence));
        if (assistantMessageEventId == Guid.Empty)
            throw new ArgumentException("Assistant message event ID cannot be empty.", nameof(assistantMessageEventId));
        TerminalState = terminalState;
        CommittedThroughProjectSequence = committedThroughProjectSequence;
        AssistantMessageEventId = assistantMessageEventId;
        CompletedUtc = completedUtc;
    }

    public Guid LeaseId { get; }
    public Guid RequestId { get; }
    public Guid ProjectId { get; }
    public H2ProjectAiQueueState TerminalState { get; }
    public long CommittedThroughProjectSequence { get; }
    public Guid? AssistantMessageEventId { get; }
    public DateTimeOffset CompletedUtc { get; }
}

/// <summary>
/// Product-facing shared-project coordination boundary. Implementations may use HTTP/SQLite,
/// but callers depend only on these protocol/domain contracts.
/// </summary>
public interface IH2SyncCoordinator
{
    Task RegisterDeviceAsync(
        Guid workspaceId,
        H2CoordinatorDeviceIdentity device,
        CancellationToken cancellationToken = default);

    Task<H2ProjectHead> GetProjectHeadAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(
        Guid workspaceId,
        Guid projectId,
        long? atOrBeforeSequence = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(
        Guid workspaceId,
        Guid projectId,
        long afterServerSequence,
        int limit = 500,
        CancellationToken cancellationToken = default);

    Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(
        Guid workspaceId,
        Guid deviceId,
        IReadOnlyList<H2ProjectEventDraft> events,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(
        Guid workspaceId,
        Guid conflictId,
        H2ProjectEventDraft resolutionEvent,
        CancellationToken cancellationToken = default);

    Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(
        H2ProjectAiRequest request,
        CancellationToken cancellationToken = default);

    Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(
        Guid workspaceId,
        Guid projectId,
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task<bool> HeartbeatProjectAiLeaseAsync(
        Guid leaseId,
        Guid deviceId,
        DateTimeOffset heartbeatUtc,
        CancellationToken cancellationToken = default);

    Task CompleteProjectAiAsync(
        H2ProjectAiCompletion completion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(
        Guid workspaceId,
        Guid projectId,
        int limit = 100,
        CancellationToken cancellationToken = default);
}

internal static class H2CoordinatorContractGuard
{
    public static Guid NonEmpty(Guid value, string name)
        => value != Guid.Empty ? value : throw new ArgumentException(name + " is required.", name);

    public static string Bound(string? value, string name, int min, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length < min || value.Length > max)
            throw new ArgumentException($"{name} must be {min}..{max} characters.", name);
        return value;
    }

    public static string? BoundOrNull(string? value, string name, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return null;
        if (value.Length > max) throw new ArgumentException($"{name} exceeds {max} characters.", name);
        return value;
    }

    public static string Sha256(string? value, string name)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException(name + " must be a 64-character SHA-256 hex digest.", name);
        return value;
    }
}
