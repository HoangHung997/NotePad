using H2Notes.Core;

namespace H2Notes.Coordinator;

/// <summary>
/// In-process Coordinator service boundary over the single-writer durable store.
/// HTTP/authentication transport is added at H2M-133G; H2 clients depend on IH2SyncCoordinator.
/// </summary>
public sealed class H2SyncCoordinatorService : IH2SyncCoordinator
{
    private readonly H2CoordinatorSqliteStore _store;

    public H2SyncCoordinatorService(H2CoordinatorSqliteStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task RegisterDeviceAsync(
        Guid workspaceId,
        H2CoordinatorDeviceIdentity device,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.RegisterDevice(workspaceId, device);
        return Task.CompletedTask;
    }

    public Task<H2ProjectHead> GetProjectHeadAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.GetProjectHead(workspaceId, projectId));
    }

    public Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(
        Guid workspaceId,
        Guid projectId,
        long? atOrBeforeSequence = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _store.GetLatestSnapshot(workspaceId, projectId);
        if (snapshot is not null
            && atOrBeforeSequence.HasValue
            && snapshot.ThroughServerSequence > atOrBeforeSequence.Value)
            snapshot = null;
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(
        Guid workspaceId,
        Guid projectId,
        long afterServerSequence,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.GetProjectEvents(
                workspaceId,
                projectId,
                afterServerSequence,
                limit));
    }

    public Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(
        Guid workspaceId,
        Guid deviceId,
        IReadOnlyList<H2ProjectEventDraft> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.SubmitProjectEvents(workspaceId, deviceId, events));
    }

    public Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.GetConflicts(workspaceId, projectId));
    }

    public Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(
        Guid workspaceId,
        Guid conflictId,
        H2ProjectEventDraft resolutionEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.ResolveConflict(workspaceId, conflictId, resolutionEvent));
    }

    public Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(
        H2ProjectAiRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.EnqueueProjectAi(request));
    }

    public Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(
        Guid workspaceId,
        Guid projectId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.TryAcquireProjectAiLease(
                workspaceId,
                projectId,
                deviceId));
    }

    public Task<bool> HeartbeatProjectAiLeaseAsync(
        Guid leaseId,
        Guid deviceId,
        DateTimeOffset heartbeatUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.HeartbeatProjectAiLease(
                leaseId,
                deviceId,
                heartbeatUtc));
    }

    public Task<bool> ConfirmProjectAiBarrierAsync(
        Guid leaseId,
        Guid deviceId,
        long observedProjectSequence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.ConfirmProjectAiBarrier(
                leaseId,
                deviceId,
                observedProjectSequence));
    }

    public Task<bool> ResolveInterruptedProjectAiAsync(
        Guid leaseId,
        Guid deviceId,
        H2ProjectAiQueueState terminalState,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.ResolveInterruptedProjectAi(
                leaseId,
                deviceId,
                terminalState));
    }

    public Task CompleteProjectAiAsync(
        H2ProjectAiCompletion completion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.CompleteProjectAi(completion);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(
        Guid workspaceId,
        Guid projectId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _store.GetProjectAiQueue(workspaceId, projectId, limit));
    }
}
