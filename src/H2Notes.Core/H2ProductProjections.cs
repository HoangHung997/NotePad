namespace H2Notes.Core;

public enum H2WorkspaceSyncState
{
    Healthy = 0,
    Busy = 1,
    PendingLocal = 2,
    Warning = 3,
    RecoveryRequired = 4,
    Offline = 5
}

public sealed record H2WorkspaceHealthSnapshot(
    H2WorkspaceSyncState State,
    string? Code = null,
    string? Message = null,
    DateTime? ObservedUtc = null,
    bool HasPendingChanges = false,
    bool IsRecoveryFallback = false)
{
    public static H2WorkspaceHealthSnapshot Healthy { get; } =
        new(H2WorkspaceSyncState.Healthy);
}

public sealed record ProjectOverviewProjection(
    Guid ProjectId,
    string Name,
    int CompletedTasks,
    int TotalTasks,
    Guid? NextTaskId,
    string? NextTask,
    H2AgentTaskStatus? AgentStatus,
    int AttentionCount,
    DateTime? LatestVerifiedActivityUtc,
    H2WorkspaceSyncState SyncState);

public sealed record NeedsAttentionProjection(
    Guid? ProjectId,
    Guid? AgentTaskId,
    string Kind,
    string Code,
    string Title,
    DateTime? AtUtc);

public sealed record ProjectActivityProjection(
    Guid ProjectId,
    DateTime AtUtc,
    string Kind,
    string Summary,
    Guid? AgentTaskId = null,
    string? EvidenceId = null);

/// <summary>
/// Rebuildable product queries over authoritative H2 project/task data, the H2 Agent adapter
/// and current workspace health. This service owns no durable state and writes to none of them.
/// </summary>
public sealed class H2ProductProjectionService
{
    private readonly IH2AgentAdapter _agent;

    public H2ProductProjectionService(IH2AgentAdapter agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public static H2WorkspaceHealthSnapshot CaptureWorkspaceHealth(ProjectWorkspaceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var diagnostic = store.LastSyncDiagnostic;
        var hasPending = store.HasPendingChanges;

        if (store.IsRecoveryFallbackActive)
            return new(
                H2WorkspaceSyncState.RecoveryRequired,
                diagnostic?.Code ?? "recovery-fallback",
                diagnostic?.Message ?? "Workspace is using a write-blocked last-known-good generation.",
                DateTime.UtcNow,
                hasPending,
                IsRecoveryFallback: true);

        if (diagnostic is not null)
        {
            if (diagnostic.Recovered || diagnostic.Code == "transient_generation_converged")
                return new(
                    hasPending ? H2WorkspaceSyncState.PendingLocal : H2WorkspaceSyncState.Healthy,
                    diagnostic.Code,
                    diagnostic.Message,
                    DateTime.UtcNow,
                    hasPending,
                    IsRecoveryFallback: false);

            if (diagnostic.IsPersistent)
                return new(
                    H2WorkspaceSyncState.RecoveryRequired,
                    diagnostic.Code,
                    diagnostic.Message,
                    DateTime.UtcNow,
                    hasPending,
                    IsRecoveryFallback: false);

            if (diagnostic.Code is "io_error" or "access_denied")
                return new(
                    H2WorkspaceSyncState.Offline,
                    diagnostic.Code,
                    diagnostic.Message,
                    DateTime.UtcNow,
                    hasPending,
                    IsRecoveryFallback: false);

            return new(
                H2WorkspaceSyncState.Warning,
                diagnostic.Code,
                diagnostic.Message,
                DateTime.UtcNow,
                hasPending,
                IsRecoveryFallback: false);
        }

        return hasPending
            ? new H2WorkspaceHealthSnapshot(
                H2WorkspaceSyncState.PendingLocal,
                "pending-local",
                "Local changes are durably pending shared sync.",
                DateTime.UtcNow,
                HasPendingChanges: true)
            : H2WorkspaceHealthSnapshot.Healthy;
    }

    public ProjectOverviewProjection BuildProjectOverview(
        ProjectRecord project,
        H2WorkspaceHealthSnapshot? workspaceHealth = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var health = workspaceHealth ?? H2WorkspaceHealthSnapshot.Healthy;
        var recent = Recent(project.Id);
        var latestAgent = recent.FirstOrDefault();
        var attention = AttentionForProject(project.Id, recent, health).Count;
        var latestVerified = LatestVerifiedActivity(project, recent);

        var total = project.ChecklistItems.Count;
        var completed = project.ChecklistItems.Count(task => task.IsCompleted);
        var next = project.ChecklistItems.FirstOrDefault(task =>
            !task.IsCompleted && !string.IsNullOrWhiteSpace(TaskText(task)));

        return new(
            project.Id,
            ProjectName(project),
            completed,
            total,
            next?.Id,
            next is null ? null : TaskText(next),
            latestAgent?.Status,
            attention,
            latestVerified,
            health.State);
    }

    public IReadOnlyList<NeedsAttentionProjection> BuildNeedsAttention(
        IEnumerable<ProjectRecord> projects,
        H2WorkspaceHealthSnapshot? workspaceHealth = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var health = workspaceHealth ?? H2WorkspaceHealthSnapshot.Healthy;
        var items = new List<NeedsAttentionProjection>();

        foreach (var project in projects)
            items.AddRange(AttentionForProject(project.Id, Recent(project.Id), health: null));

        if (health.State != H2WorkspaceSyncState.Healthy)
        {
            items.Add(new(
                ProjectId: null,
                AgentTaskId: null,
                Kind: "workspace",
                Code: health.Code ?? health.State.ToString().ToLowerInvariant(),
                Title: string.IsNullOrWhiteSpace(health.Message)
                    ? WorkspaceAttentionTitle(health.State)
                    : health.Message!,
                AtUtc: health.ObservedUtc));
        }

        return items
            .OrderByDescending(item => AttentionPriority(item.Code))
            .ThenByDescending(item => item.AtUtc ?? DateTime.MinValue)
            .ThenBy(item => item.ProjectId)
            .ToArray();
    }

    public IReadOnlyList<ProjectActivityProjection> BuildProjectActivity(
        ProjectRecord project,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));

        var activity = new List<ProjectActivityProjection>();

        if (project.CreatedAtUtc is { } created)
            activity.Add(new(project.Id, created, "project", "Project created."));

        if (project.UpdatedAtUtc is { } updated)
            activity.Add(new(project.Id, updated, "project", "Project updated."));

        foreach (var task in project.ChecklistItems)
        {
            if (task.CreatedAtUtc is { } taskCreated)
                activity.Add(new(project.Id, taskCreated, "project-task",
                    "Task created: " + Bound(TaskText(task), 240)));

            if (task.CompletedAtUtc is { } completed)
                activity.Add(new(project.Id, completed, "project-task",
                    "Task completed: " + Bound(TaskText(task), 240)));
            else if (task.UpdatedAtUtc is { } taskUpdated && taskUpdated != task.CreatedAtUtc)
                activity.Add(new(project.Id, taskUpdated, "project-task",
                    "Task updated: " + Bound(TaskText(task), 240)));
        }

        foreach (var run in Recent(project.Id, limit))
        {
            activity.Add(new(
                project.Id,
                run.UpdatedUtc,
                "agent-task",
                AgentActivitySummary(run),
                run.TaskId));

            foreach (var evidence in run.Evidence)
            {
                activity.Add(new(
                    project.Id,
                    run.UpdatedUtc,
                    "agent-evidence",
                    string.IsNullOrWhiteSpace(evidence.Summary)
                        ? "Verified evidence: " + evidence.Kind
                        : "Verified evidence: " + Bound(evidence.Summary!, 240),
                    run.TaskId,
                    evidence.EvidenceId));
            }
        }

        return activity
            .OrderByDescending(item => item.AtUtc)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private IReadOnlyList<H2AgentTaskSummary> Recent(Guid projectId, int limit = 50)
        => _agent.GetRecentTasks(projectId, limit)
            .Where(task => task.ProjectId == projectId)
            .OrderByDescending(task => task.UpdatedUtc)
            .ThenBy(task => task.TaskId)
            .ToArray();

    private static List<NeedsAttentionProjection> AttentionForProject(
        Guid projectId,
        IReadOnlyList<H2AgentTaskSummary> recent,
        H2WorkspaceHealthSnapshot? health)
    {
        var items = new List<NeedsAttentionProjection>();
        foreach (var task in recent)
        {
            var code = task.Status switch
            {
                H2AgentTaskStatus.WaitingForApproval => "agent-waiting-approval",
                H2AgentTaskStatus.Blocked => "agent-blocked",
                H2AgentTaskStatus.Failed => "agent-failed",
                _ => null
            };
            if (code is null) continue;

            var title = task.Status switch
            {
                H2AgentTaskStatus.WaitingForApproval =>
                    task.PendingApproval?.Title ?? "Agent needs approval.",
                H2AgentTaskStatus.Blocked =>
                    string.IsNullOrWhiteSpace(task.Error) ? "Agent task is blocked." : task.Error!,
                _ =>
                    string.IsNullOrWhiteSpace(task.Error) ? "Agent task failed." : task.Error!
            };

            items.Add(new(
                projectId,
                task.TaskId,
                "agent",
                code,
                Bound(title, 300),
                task.UpdatedUtc));
        }

        if (health is { State: not H2WorkspaceSyncState.Healthy })
        {
            items.Add(new(
                projectId,
                null,
                "workspace",
                health.Code ?? health.State.ToString().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(health.Message)
                    ? WorkspaceAttentionTitle(health.State)
                    : health.Message!,
                health.ObservedUtc));
        }

        return items;
    }

    private static DateTime? LatestVerifiedActivity(
        ProjectRecord project,
        IReadOnlyList<H2AgentTaskSummary> recent)
    {
        var candidates = new List<DateTime>();
        if (project.UpdatedAtUtc is { } projectUpdated) candidates.Add(projectUpdated);
        candidates.AddRange(project.ChecklistItems
            .SelectMany(task => new[] { task.UpdatedAtUtc, task.CompletedAtUtc })
            .Where(value => value.HasValue)
            .Select(value => value!.Value));

        candidates.AddRange(recent
            .Where(task => task.Status == H2AgentTaskStatus.Completed || task.Evidence.Count > 0)
            .Select(task => task.UpdatedUtc));

        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static string ProjectName(ProjectRecord project)
        => project.NameRich?.Text ?? RichDocument.FromLegacy(project.Name ?? "").Text;

    private static string TaskText(TaskRecord task)
        => task.TextRich?.Text ?? RichDocument.FromLegacy(task.Text ?? "").Text;

    private static string AgentActivitySummary(H2AgentTaskSummary task)
        => task.Status switch
        {
            H2AgentTaskStatus.Completed when !string.IsNullOrWhiteSpace(task.FinalText) =>
                "Agent completed: " + Bound(task.FinalText!, 240),
            H2AgentTaskStatus.Completed => "Agent task completed.",
            H2AgentTaskStatus.WaitingForApproval => "Agent is waiting for approval.",
            H2AgentTaskStatus.Blocked => "Agent task blocked.",
            H2AgentTaskStatus.Cancelled => "Agent task cancelled.",
            H2AgentTaskStatus.Failed => "Agent task failed.",
            H2AgentTaskStatus.Running => "Agent task running.",
            _ => "Agent task queued."
        };

    private static string WorkspaceAttentionTitle(H2WorkspaceSyncState state)
        => state switch
        {
            H2WorkspaceSyncState.Busy => "Shared workspace is busy.",
            H2WorkspaceSyncState.PendingLocal => "Local changes are pending shared sync.",
            H2WorkspaceSyncState.Warning => "Workspace sync needs attention.",
            H2WorkspaceSyncState.RecoveryRequired => "Workspace recovery is required.",
            H2WorkspaceSyncState.Offline => "Shared workspace is offline.",
            _ => "Workspace needs attention."
        };

    private static int AttentionPriority(string code)
        => code switch
        {
            "agent-failed" => 100,
            "agent-blocked" => 90,
            "agent-waiting-approval" => 80,
            _ => 50
        };

    private static string Bound(string value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }
}
