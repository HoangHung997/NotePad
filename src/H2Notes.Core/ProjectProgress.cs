namespace H2Notes.Core;

public readonly record struct ProjectTaskProgress(int Completed, int Total)
{
    public string Label => $"{Completed}/{Total}";
    public double Percent => Total == 0 ? 0d : 100d * Completed / Total;
    public bool IsComplete => Total > 0 && Completed == Total;
}

/// <summary>
/// The canonical H2 project progress formula. Project progress is derived only from durable
/// human/project TaskRecord completion. Agent execution progress is a separate projection.
/// </summary>
public static class ProjectProgressCalculator
{
    public static ProjectTaskProgress Calculate(ProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var total = project.ChecklistItems.Count;
        var completed = project.ChecklistItems.Count(task => task.IsCompleted);
        return new(completed, total);
    }

    public static TaskRecord? NextTask(ProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.ChecklistItems.FirstOrDefault(task =>
            !task.IsCompleted && !string.IsNullOrWhiteSpace(TaskText(task)));
    }

    public static string TaskText(TaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.TextRich?.Text ?? RichDocument.FromLegacy(task.Text ?? "").Text;
    }
}
