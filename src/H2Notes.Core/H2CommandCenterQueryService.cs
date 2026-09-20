namespace H2Notes.Core;

public enum H2CommandCenterGroup
{
    NeedsAttention = 0,
    Working = 1,
    Waiting = 2,
    Normal = 3,
    Completed = 4
}

/// <summary>
/// Query facade for the future Command Center. It returns rebuildable projections only.
/// It is intentionally not a durable ProjectState or dashboard database.
/// </summary>
public sealed class H2CommandCenterQueryService
{
    private readonly H2ProductProjectionService _projections;

    public H2CommandCenterQueryService(H2ProductProjectionService projections)
        => _projections = projections ?? throw new ArgumentNullException(nameof(projections));

    public IReadOnlyList<ProjectOverviewProjection> GetProjects(
        IEnumerable<ProjectRecord> projects,
        H2WorkspaceHealthSnapshot? workspaceHealth = null)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects
            .Select(project => _projections.BuildProjectOverview(project, workspaceHealth))
            .OrderBy(project => GroupFor(project))
            .ThenByDescending(project => project.LatestVerifiedActivityUtc ?? DateTime.MinValue)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ProjectId)
            .ToArray();
    }

    public static H2CommandCenterGroup GroupFor(ProjectOverviewProjection project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.AttentionCount > 0)
            return H2CommandCenterGroup.NeedsAttention;

        if (project.AgentStatus == H2AgentTaskStatus.Running)
            return H2CommandCenterGroup.Working;

        if (project.AgentStatus == H2AgentTaskStatus.Queued)
            return H2CommandCenterGroup.Waiting;

        if (project.TotalTasks > 0
            && project.CompletedTasks == project.TotalTasks
            && project.AgentStatus is not H2AgentTaskStatus.Running
            && project.AgentStatus is not H2AgentTaskStatus.Queued)
            return H2CommandCenterGroup.Completed;

        return H2CommandCenterGroup.Normal;
    }

    public IReadOnlyList<NeedsAttentionProjection> GetNeedsAttention(
        IEnumerable<ProjectRecord> projects,
        H2WorkspaceHealthSnapshot? workspaceHealth = null)
        => _projections.BuildNeedsAttention(projects, workspaceHealth);

    public IReadOnlyList<ProjectActivityProjection> GetProjectActivity(
        ProjectRecord project,
        int limit = 50)
        => _projections.BuildProjectActivity(project, limit);
}
