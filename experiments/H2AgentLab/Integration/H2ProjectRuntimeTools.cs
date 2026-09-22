using System.Collections.Concurrent;
using System.Text.Json;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed class H2ProjectRuntimeTools(IH2ProjectToolHost host, Guid projectId, Guid taskId,
    Func<bool> authorized) : IAgentRuntimeDomainVerifier
{
    private readonly ConcurrentDictionary<string, AgentRuntimeDomainVerification> _verified = new();
    public string DomainId => "h2-project";

    public void Register(ToolRegistry registry)
    {
        foreach (var name in new[] { "read_project", "add_project_task", "append_project_note", "replace_project_note" })
        {
            var read = name == "read_project";
            var properties = new Dictionary<string, object> { ["project_id"] = new { type = "string", description = "Selected H2 project ID." } };
            var required = new List<string> { "project_id" };
            if (!read)
            {
                properties["expected_version"] = new { type = "string", description = "Exact Version token returned by read_project. Copy without rounding; re-read after each change." };
                properties["text"] = new { type = "string", description = "Requested plain text." };
                required.AddRange(["expected_version", "text"]);
            }
            if (name == "replace_project_note")
            { properties["match"] = new { type = "string", description = "Exact unique current note segment." }; required.Add("match"); }
            var description = name == "read_project" ? "Read the selected H2 project's current tasks, notes and version."
                : "Apply " + name + " to the selected H2 project using its latest observed version; host permission and verification are enforced.";
            registry.Register(new ToolDescriptor(name, new("h2", "Read and update the selected H2 project."), description,
                read ? AgentToolRisk.Low : AgentToolRisk.Medium, read ? AgentToolAccess.ReadOnly : AgentToolAccess.Mutating,
                read, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description,
                    parameters = new { type = "object", properties, required, additionalProperties = false } } }),
                new DelegatingToolExecutor("h2-project-tools", ExecuteAsync), resourceScope: new("h2-project:" + projectId.ToString("N"), "selected-project"),
                serializationKey: "h2-project", canProvideVerificationEvidence: true));
        }
    }

    private ValueTask<string> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(H2ProductionToolSession.Arg(call, "project_id"), out var requested) || requested != projectId)
            throw new InvalidOperationException("Tool is restricted to the selected H2 project.");
        if (call.Name == "read_project")
        {
            var snapshot = host.ReadProject(projectId);
            return ValueTask.FromResult(JsonSerializer.Serialize(new { snapshot.ProjectId,
                Version = snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                snapshot.Name, snapshot.Notes, snapshot.CompletedTasks, snapshot.TotalTasks,
                snapshot.NextTaskId, snapshot.NextTask, snapshot.Tasks }));
        }
        if (!authorized()) throw new UnauthorizedAccessException("Project mutation has no host authorization.");
        var version = long.Parse(H2ProductionToolSession.Arg(call, "expected_version") ?? "",
            System.Globalization.CultureInfo.InvariantCulture);
        var text = H2ProductionToolSession.Arg(call, "text") ?? "";
        var auth = new H2ProjectToolAuthorization(AiPermissionMode.ConfirmChanges, Approved: true, taskId);
        var before = Readback();
        var receipt = call.Name switch
        {
            "add_project_task" => host.AddTask(new(projectId, version, text, auth)),
            "append_project_note" => host.AppendNote(new(projectId, version, text, auth)),
            "replace_project_note" => host.ReplaceNote(new(projectId, version, H2ProductionToolSession.Arg(call, "match") ?? "", text, auth)),
            _ => throw new InvalidOperationException("Unknown H2 mutation.")
        };
        var after = Readback();
        var expectedNotes = call.Name == "append_project_note"
            ? before.Notes + (before.Notes.Length == 0 || before.Notes.EndsWith('\n') ? "" : "\n") + text.Trim()
            : before.Notes.Replace(H2ProductionToolSession.Arg(call, "match") ?? "\0", text, StringComparison.Ordinal);
        var passed = after.Version == receipt.Version && (call.Name == "add_project_task"
            ? after.Tasks.Any(task => task.TaskId == receipt.EntityId && task.Text == text.Trim())
            : after.Notes == expectedNotes);
        var evidenceId = "h2-project:" + taskId.ToString("N") + ":" + call.Id;
        _verified[call.Id] = new(DomainId, passed, [evidenceId], passed ? null : "Project readback differs from the requested change.");
        return ValueTask.FromResult(JsonSerializer.Serialize(new { receipt, verificationPassed = passed, evidenceId }));
    }

    private H2ProjectSummary Readback() => host is IH2ProjectToolVerifier verifier
        ? verifier.ReadProjectForVerification(projectId) : host.ReadProject(projectId);

    public bool CanVerify(ToolCall call, string rawToolOutput)
        => call.Name is "add_project_task" or "append_project_note" or "replace_project_note";
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context, ToolCall call,
        string rawToolOutput, CancellationToken cancellationToken)
        => Task.FromResult(_verified.TryGetValue(call.Id, out var report) ? report
            : new AgentRuntimeDomainVerification(DomainId, false, [], "No project mutation/readback was completed."));
}
