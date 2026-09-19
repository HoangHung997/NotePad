using System.Text.Json;
using H2Notes.Core;

internal static class H2AgentTaskCorrelationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Agent task correlation supports project and unscoped runs without ProjectRecord state", () =>
        {
            var index = new H2AgentTaskCorrelationIndex();
            var projectId = Guid.NewGuid();
            var projectTask = Guid.NewGuid();
            var quickTask = Guid.NewGuid();
            var now = DateTime.UtcNow;

            index.Register(projectTask, projectId, now);
            index.Register(quickTask, null, now.AddSeconds(1));

            if (index.QueryByProject(projectId).Single().TaskId != projectTask)
                throw new Exception("Project query lost task correlation.");
            if (index.QueryUnscoped().Single().TaskId != quickTask)
                throw new Exception("Unscoped Agent run is not queryable.");
            if (index.QueryAll().Count != 2)
                throw new Exception("All-task query lost correlations.");
        });

        test("Agent task correlation can attach quick work to a project without rewriting task state", () =>
        {
            var index = new H2AgentTaskCorrelationIndex();
            var taskId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            index.Register(taskId, null);

            if (!index.AttachProject(taskId, projectId))
                throw new Exception("AttachProject failed.");
            if (index.Find(taskId)?.ProjectId != projectId)
                throw new Exception("Attached ProjectId was not projected.");
            if (index.QueryUnscoped().Count != 0 || index.QueryByProject(projectId).Single().TaskId != taskId)
                throw new Exception("Correlation queries did not update after attachment.");
        });

        test("Agent task correlation does not mutate project JSON or embed Agent execution state", () =>
        {
            var project = new ProjectRecord { Name = "Correlation guard" };
            project.ChecklistItems.Add(new TaskRecord { Text = "Human project task" });
            var before = JsonSerializer.Serialize(project);

            var index = new H2AgentTaskCorrelationIndex();
            var taskId = Guid.NewGuid();
            index.Register(taskId, null);
            index.AttachProject(taskId, project.Id);
            var after = JsonSerializer.Serialize(project);

            if (before != after) throw new Exception("Correlation index mutated ProjectRecord.");

            var projectProperties = typeof(ProjectRecord).GetProperties().Select(p => p.PropertyType.FullName ?? "").ToArray();
            foreach (var marker in new[] { "H2Agent", "AgentIntegration", "VerificationReport", "ToolRun" })
                if (projectProperties.Any(type => type.Contains(marker, StringComparison.Ordinal)))
                    throw new Exception("ProjectRecord embeds Agent state: " + marker);
        });

        test("Agent correlation index fails closed on conflicting registration", () =>
        {
            var index = new H2AgentTaskCorrelationIndex();
            var taskId = Guid.NewGuid();
            index.Register(taskId, Guid.NewGuid());
            try
            {
                index.Register(taskId, Guid.NewGuid());
                throw new Exception("Conflicting task correlation was silently replaced.");
            }
            catch (InvalidOperationException)
            {
            }
        });
    }
}
