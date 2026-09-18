using H2Notes.Core;
using H2AgentLab.Metrics;
using H2AgentLab.Verification;

namespace H2AgentLab.Tasking;

public enum AgentTraceEventKind
{
    Phase = 0,
    Tool = 1,
    Verification = 2,
    Evidence = 3,
    Warning = 4,
    Final = 5
}

public sealed record AgentTraceEvent(
    long Sequence,
    DateTime AtUtc,
    AgentTraceEventKind Kind,
    string Code,
    string Message);

public sealed class AgentTraceEventStream
{
    private readonly List<AgentTraceEvent> _events = [];
    public IReadOnlyList<AgentTraceEvent> Events => _events.ToArray();

    public AgentTraceEvent Add(
        AgentTraceEventKind kind,
        string code,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        message ??= "";
        var bounded = message.Length <= 2_000 ? message : message[..2_000];
        var item = new AgentTraceEvent(
            _events.Count,
            DateTime.UtcNow,
            kind,
            code.Trim(),
            bounded);
        _events.Add(item);
        return item;
    }
}

public sealed record AgentInspectionSnapshot(
    Guid TaskId,
    string Goal,
    AgentTaskState State,
    AgentTaskRouteClass Route,
    IReadOnlyList<AgentAcceptanceCriterion> Criteria,
    IReadOnlyList<AgentTraceEvent> TraceEvents,
    int ActiveContextCharacters,
    bool ContextUnderPressure,
    AgentDiagnosticsSnapshot Diagnostics);

/// <summary>
/// UI-facing v2 run facade. AgentOrchestrator owns contract/route/state. The preserved v1 runner is
/// only the temporary compatibility executor until the end-to-end transport loop is fully migrated.
/// </summary>
public sealed class AgentOrchestratedRun
{
    private readonly AgentOrchestrator _orchestrator;

    public AgentOrchestratedRun(AgentOrchestrator? orchestrator = null)
    {
        _orchestrator = orchestrator ?? new AgentOrchestrator();
    }

    public async Task<AgentInspectionSnapshot> RunAsync(
        AiProfile profile,
        string key,
        LabSession labSession,
        AgentTools tools,
        string prompt,
        Action<string, string> output,
        Action save,
        AgentRunTelemetry telemetry,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            prompt.Trim(),
            "workspace:" + labSession.Workspace,
            null,
            readOnly ? null : ["perform requested approved changes"],
            ["preserve unrelated user state"],
            ["concise final answer"],
            [new AgentAcceptanceCriterion("final-response", "Task reaches a verified or explicitly non-mutating final result.")],
            readOnly ? AgentTaskRiskClass.ReadOnly : AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: !readOnly));

        var routing = new AgentTaskRoutingSignals(
            NeedsExternalRetrieval: false,
            NeedsAction: !readOnly,
            NeedsComplexPlanning: true);
        var session = _orchestrator.Receive(contract, routing);
        var trace = new AgentTraceEventStream();

        Emit(trace, output, AgentTraceEventKind.Phase, "received", "Đã tiếp nhận tác vụ.");
        _orchestrator.Ground(session, "UI request and workspace are grounded.");
        Emit(trace, output, AgentTraceEventKind.Phase, "grounded", "Đã xác định phạm vi và trạng thái hiện tại.");
        _orchestrator.Plan(session, "Compatibility executor will operate under v2 task boundaries.");
        Emit(trace, output, AgentTraceEventKind.Phase, "planned", "Đã lập kế hoạch thực thi.");
        _orchestrator.Execute(session, "Compatibility execution started.");
        Emit(trace, output, AgentTraceEventKind.Phase, "executing", "Đang thực thi qua AgentOrchestrator.");

        try
        {
            using var runner = _orchestrator.CreateCompatibilityRunner();
            await runner.Run(
                profile,
                key,
                labSession,
                tools,
                prompt,
                (kind, text) =>
                {
                    if (kind == "tool")
                        Emit(trace, output, AgentTraceEventKind.Tool, "tool", Bound(text));
                    else if (kind == "recovery")
                        Emit(trace, output, AgentTraceEventKind.Warning, "recovery", Bound(text));
                    else if (kind == "final")
                        Emit(trace, output, AgentTraceEventKind.Final, "model-final", Bound(text));
                    output(kind, text);
                },
                save,
                telemetry,
                cancellationToken).ConfigureAwait(false);

            _orchestrator.Verify(session, "Compatibility execution ended; completion gate evaluated.");
            Emit(trace, output, AgentTraceEventKind.Verification, "verifying", "Đang kiểm tra điều kiện hoàn tất.");

            if (readOnly)
            {
                _orchestrator.Complete(
                    session,
                    new AgentVerificationOutcome(passed: false),
                    "Read-only task has no mutation requiring mechanical verification.");
            }
            else
            {
                // Until semantic domain verifiers are wired into the UI loop, mutating compatibility
                // runs must not be promoted to Completed merely because the legacy runner returned.
                _orchestrator.Block(session, "Mutating compatibility run requires v2 domain verification before completion.");
                Emit(trace, output, AgentTraceEventKind.Warning, "verification-required",
                    "Tác vụ có thay đổi chưa được đánh dấu hoàn tất nếu chưa có verifier v2.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!session.StateMachine.IsTerminal)
                _orchestrator.Cancel(session, "User cancelled the orchestrated run.");
            Emit(trace, output, AgentTraceEventKind.Warning, "cancelled", "Tác vụ đã bị hủy.");
            throw;
        }
        catch
        {
            if (!session.StateMachine.IsTerminal)
                _orchestrator.Fail(session, "Unhandled executor failure.");
            throw;
        }

        var context = new Context.LabSessionContextAdapter(_orchestrator.ContextManager)
            .Build(labSession, taskContract: prompt, currentState: session.StateMachine.State.ToString());

        var diagnostics = AgentDiagnostics.FromContext(context);
        return new AgentInspectionSnapshot(
            contract.TaskId,
            contract.UserGoal,
            session.StateMachine.State,
            session.Route.RouteClass,
            contract.AcceptanceCriteria,
            trace.Events,
            context.Usage.TotalCharacters,
            context.Pressure.RequiresCompaction,
            diagnostics);
    }

    private static void Emit(
        AgentTraceEventStream trace,
        Action<string, string> output,
        AgentTraceEventKind kind,
        string code,
        string message)
    {
        var item = trace.Add(kind, code, message);
        output("trace", $"{item.Kind}: {item.Message}");
    }

    private static string Bound(string? value)
    {
        value ??= "";
        return value.Length <= 2_000 ? value : value[..2_000];
    }
}
