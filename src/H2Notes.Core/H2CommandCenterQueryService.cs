namespace H2Notes.Core;

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
            .OrderByDescending(project => project.AttentionCount)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ProjectId)
            .ToArray();
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
