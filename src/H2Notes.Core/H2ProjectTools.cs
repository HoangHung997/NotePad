namespace H2Notes.Core;

public sealed record H2ProjectToolAuthorization(
    AiPermissionMode Permission,
    bool Approved = false,
    Guid? AgentTaskId = null);

public sealed record H2ProjectTaskSummary(
    Guid TaskId,
    string Text,
    string Comment,
    bool IsCompleted,
    DateTime? CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record H2ProjectSummary(
    Guid ProjectId,
    long Version,
    string Name,
    string Notes,
    int CompletedTasks,
    int TotalTasks,
    Guid? NextTaskId,
    string? NextTask,
    IReadOnlyList<H2ProjectTaskSummary> Tasks);

public sealed record H2ProjectMutationReceipt(
    Guid ProjectId,
    long PreviousVersion,
    long Version,
    string Tool,
    string Audit,
    Guid? EntityId = null,
    Guid? AgentTaskId = null);

public sealed record H2AddProjectTaskRequest(
    Guid ProjectId,
    long ExpectedVersion,
    string Text,
    H2ProjectToolAuthorization Authorization);

public sealed record H2AppendProjectNoteRequest(
    Guid ProjectId,
    long ExpectedVersion,
    string Text,
    H2ProjectToolAuthorization Authorization);

public sealed record H2ReplaceProjectNoteRequest(
    Guid ProjectId,
    long ExpectedVersion,
    string Match,
    string Text,
    H2ProjectToolAuthorization Authorization);

/// <summary>
/// Minimal typed H2 project tool surface. It deliberately exposes no unrestricted ProjectRecord
/// mutation and no Agent runtime/provider types.
/// </summary>
public interface IH2ProjectToolHost
{
    H2ProjectSummary ReadProject(Guid projectId);
    H2ProjectMutationReceipt AddTask(H2AddProjectTaskRequest request);
    H2ProjectMutationReceipt AppendNote(H2AppendProjectNoteRequest request);
    H2ProjectMutationReceipt ReplaceNote(H2ReplaceProjectNoteRequest request);
}

/// <summary>
/// Optional adapter capability. A production Agent bridge can bind H2 typed project tools without
/// H2 Notes taking a dependency on AgentRuntime/ToolRegistry.
/// </summary>
public interface IH2ProjectToolHostConsumer
{
    void BindProjectToolHost(IH2ProjectToolHost host);
}

public sealed class H2ProjectToolHost : IH2ProjectToolHost
{
    private readonly object _gate = new();
    private readonly Func<Guid, ProjectRecord?> _resolve;
    private readonly Action<Guid>? _changed;

    public H2ProjectToolHost(
        Func<Guid, ProjectRecord?> resolve,
        Action<Guid>? changed = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _changed = changed;
    }

    public H2ProjectSummary ReadProject(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("ProjectId must not be empty.", nameof(projectId));

        lock (_gate)
        {
            var project = Resolve(projectId);
            var progress = ProjectProgressCalculator.Calculate(project);
            var next = ProjectProgressCalculator.NextTask(project);
            var tasks = project.ChecklistItems
                .Take(200)
                .Select(task => new H2ProjectTaskSummary(
                    task.Id,
                    Bound(ProjectProgressCalculator.TaskText(task), 4_000),
                    Bound(task.CommentText, 8_000),
                    task.IsCompleted,
                    task.CreatedAtUtc,
                    task.UpdatedAtUtc,
                    task.CompletedAtUtc))
                .ToArray();

            return new H2ProjectSummary(
                project.Id,
                Version(project),
                Bound(ProjectName(project), 500),
                Bound(ProjectNotes(project), 20_000),
                progress.Completed,
                progress.Total,
                next?.Id,
                next is null ? null : Bound(ProjectProgressCalculator.TaskText(next), 4_000),
                tasks);
        }
    }

    public H2ProjectMutationReceipt AddTask(H2AddProjectTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var project = PrepareMutation(
                request.ProjectId,
                request.ExpectedVersion,
                request.Authorization);

            var text = RequiredText(request.Text, 4_000, "Task text");
            if (project.ChecklistItems.Count >= 5_000)
                throw new InvalidOperationException("Project task limit reached.");

            var previous = Version(project);
            var now = NextTimestamp(project);
            var task = new TaskRecord
            {
                TextRich = RichDocument.Plain(text),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            project.ChecklistItems.Add(task);
            project.UpdatedAtUtc = now;
            Changed(project.Id);

            return Receipt(
                project,
                previous,
                "add_project_task",
                "Added project task: " + Audit(text),
                task.Id,
                request.Authorization.AgentTaskId);
        }
    }

    public H2ProjectMutationReceipt AppendNote(H2AppendProjectNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var project = PrepareMutation(
                request.ProjectId,
                request.ExpectedVersion,
                request.Authorization);

            var text = RequiredText(request.Text, 20_000, "Note text");
            var previous = Version(project);
            var notes = project.ReadNotes();
            var prefix = notes.Text.Length == 0 || notes.Text.EndsWith('\n') ? "" : "\n";
            if ((long)notes.Text.Length + prefix.Length + text.Length > 600_000)
                throw new InvalidOperationException("Project notes size limit reached.");

            notes.Replace(notes.Text.Length, 0, prefix + text);
            project.NotesRich = notes;
            project.UpdatedAtUtc = NextTimestamp(project);
            Changed(project.Id);

            return Receipt(
                project,
                previous,
                "append_project_note",
                "Appended project note: " + Audit(text),
                null,
                request.Authorization.AgentTaskId);
        }
    }

    public H2ProjectMutationReceipt ReplaceNote(H2ReplaceProjectNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var project = PrepareMutation(
                request.ProjectId,
                request.ExpectedVersion,
                request.Authorization);

            var match = RequiredMatch(request.Match);
            var replacement = OptionalText(request.Text, 20_000, "Replacement text");
            var previous = Version(project);
            var notes = project.ReadNotes();
            var index = UniqueIndex(notes.Text, match);
            var nextLength = (long)notes.Text.Length - match.Length + replacement.Length;
            if (nextLength > 600_000)
                throw new InvalidOperationException("Project notes size limit reached.");

            notes.Replace(index, match.Length, replacement);
            project.NotesRich = notes;
            project.UpdatedAtUtc = NextTimestamp(project);
            Changed(project.Id);

            return Receipt(
                project,
                previous,
                "replace_project_note",
                "Replaced project note segment: " + Audit(match),
                null,
                request.Authorization.AgentTaskId);
        }
    }

    private ProjectRecord PrepareMutation(
        Guid projectId,
        long expectedVersion,
        H2ProjectToolAuthorization authorization)
    {
        if (authorization is null)
            throw new InvalidOperationException("Project mutation authorization is required.");
        if (projectId == Guid.Empty)
            throw new ArgumentException("ProjectId must not be empty.", nameof(projectId));

        ValidatePermission(authorization);
        var project = Resolve(projectId);
        var current = Version(project);
        if (current != expectedVersion)
            throw new InvalidOperationException(
                $"Project changed since Agent context was prepared. expected={expectedVersion}; current={current}.");
        return project;
    }

    private static void ValidatePermission(H2ProjectToolAuthorization authorization)
    {
        if (!Enum.IsDefined(authorization.Permission)
            || authorization.Permission == AiPermissionMode.ReadOnly)
            throw new InvalidOperationException("Current Agent permission is read-only.");

        if (authorization.Permission == AiPermissionMode.ConfirmChanges
            && !authorization.Approved)
            throw new InvalidOperationException("Explicit approval is required for this project mutation.");
    }

    private ProjectRecord Resolve(Guid projectId)
        => _resolve(projectId)
           ?? throw new KeyNotFoundException("Project is not available in the current H2 workspace.");

    private static long Version(ProjectRecord project)
        => project.UpdatedAtUtc?.Ticks ?? 0;

    private static DateTime NextTimestamp(ProjectRecord project)
    {
        var now = DateTime.UtcNow;
        if (project.UpdatedAtUtc is { } previous && now <= previous)
            now = previous.AddTicks(1);
        return now;
    }

    private void Changed(Guid projectId)
        => _changed?.Invoke(projectId);

    private static H2ProjectMutationReceipt Receipt(
        ProjectRecord project,
        long previousVersion,
        string tool,
        string audit,
        Guid? entityId,
        Guid? agentTaskId)
        => new(
            project.Id,
            previousVersion,
            Version(project),
            tool,
            audit,
            entityId,
            agentTaskId);

    private static string ProjectName(ProjectRecord project)
        => project.NameRich?.Text ?? RichDocument.FromLegacy(project.Name ?? "").Text;

    private static string ProjectNotes(ProjectRecord project)
        => project.NotesRich?.Text ?? project.Notes ?? "";

    private static string RequiredText(string? value, int max, string label)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0 || value.Length > max || value.Contains('\0'))
            throw new ArgumentException(label + " is invalid.");
        return value;
    }

    private static string OptionalText(string? value, int max, string label)
    {
        value ??= "";
        if (value.Length > max || value.Contains('\0'))
            throw new ArgumentException(label + " is invalid.");
        return value;
    }

    private static string RequiredMatch(string? value)
    {
        value ??= "";
        if (value.Length == 0 || value.Length > 20_000 || value.Contains('\0'))
            throw new ArgumentException("Match text is invalid.");
        return value;
    }

    private static int UniqueIndex(string source, string match)
    {
        var first = source.IndexOf(match, StringComparison.Ordinal);
        if (first < 0
            || source.IndexOf(match, first + match.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException(
                "The note segment must exist exactly once in the current project.");
        return first;
    }

    private static string Bound(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string Audit(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : value[..180] + "…";
    }
}
