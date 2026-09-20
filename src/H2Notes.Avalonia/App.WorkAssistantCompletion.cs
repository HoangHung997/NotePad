using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class App
{
    private readonly DispatcherTimer _workAssistantTaskTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(750)
    };
    private bool _workAssistantTaskMonitorInitialized;
    private H2AgentTaskSummary? _workAssistantTaskSnapshot;
    private string? _workAssistantLastGoal;

    private void EnsureWorkAssistantTaskMonitor()
    {
        if (_workAssistantTaskMonitorInitialized)
            return;

        _workAssistantTaskMonitorInitialized = true;
        _workAssistantTaskTimer.Tick += (_, _) => RefreshWorkAssistantTaskState();
    }

    private void BeginWorkAssistantTaskMonitor(
        Guid taskId,
        string goal)
    {
        _workAssistantQuickTaskId = taskId;
        _workAssistantLastGoal = BoundWorkAssistantText(goal, 8_000);
        _workAssistantTaskSnapshot = null;
        EnsureWorkAssistantTaskMonitor();
        _workAssistantTaskTimer.Start();
        RefreshWorkAssistantTaskState();
    }

    private void RefreshWorkAssistantTaskState()
    {
        if (_workAssistantQuickTaskId is not { } taskId)
        {
            _workAssistantTaskTimer.Stop();
            return;
        }

        H2AgentTaskSummary summary;
        try
        {
            summary = _agentAdapter.GetTaskSummary(taskId);
        }
        catch (Exception ex)
        {
            SetWorkAssistantBubbleState(
                WorkAssistantBubbleState.Attention,
                "Không đọc được trạng thái");
            _workAssistantCompact?.SetStatus(
                "Không đọc được trạng thái Agent: " + BoundUiError(ex.Message),
                isError: true);
            return;
        }

        _workAssistantTaskSnapshot = summary;
        var presentation = BuildWorkAssistantPresentation(summary);
        UpdateWorkAssistantBubble(presentation);

        if (_workAssistantCompact?.IsTaskResultVisible == true)
            _workAssistantCompact.ShowTaskPresentation(
                presentation,
                WorkAssistantProjectChoices());

        if (IsTerminal(summary.Status))
            _workAssistantTaskTimer.Stop();
    }

    private WorkAssistantTaskPresentation BuildWorkAssistantPresentation(
        H2AgentTaskSummary summary)
    {
        var evidence = summary.Evidence
            .Select(reference =>
            {
                try { return _agentAdapter.GetEvidence(reference.EvidenceId) ?? reference; }
                catch { return reference; }
            })
            .ToArray();

        var verified = evidence.Any(IsVerifiedEvidence);
        var attention = summary.Status is
            H2AgentTaskStatus.WaitingForApproval
            or H2AgentTaskStatus.Blocked
            or H2AgentTaskStatus.Failed;

        var stateText = summary.Status switch
        {
            H2AgentTaskStatus.Queued => "Đang xếp hàng",
            H2AgentTaskStatus.Running => "Agent đang làm",
            H2AgentTaskStatus.WaitingForApproval => "Cần bạn xác nhận",
            H2AgentTaskStatus.Completed when verified => "Hoàn thành · đã xác minh",
            H2AgentTaskStatus.Completed => "Hoàn thành",
            H2AgentTaskStatus.Blocked => "Bị chặn · cần bạn xem",
            H2AgentTaskStatus.Cancelled => "Đã hủy",
            H2AgentTaskStatus.Failed => "Thất bại · cần bạn xem",
            _ => summary.Status.ToString()
        };

        var resultText = summary.Status switch
        {
            H2AgentTaskStatus.WaitingForApproval when summary.PendingApproval is { } approval =>
                approval.Title + (string.IsNullOrWhiteSpace(approval.Details)
                    ? ""
                    : " · " + approval.Details),
            H2AgentTaskStatus.Completed =>
                string.IsNullOrWhiteSpace(summary.FinalText)
                    ? "Tác vụ đã hoàn thành."
                    : summary.FinalText!,
            H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed =>
                string.IsNullOrWhiteSpace(summary.Error)
                    ? "Agent chưa thể hoàn thành tác vụ."
                    : summary.Error!,
            H2AgentTaskStatus.Cancelled => "Tác vụ đã được hủy.",
            H2AgentTaskStatus.Queued => "Agent đang chuẩn bị tác vụ.",
            _ => "Agent đang xử lý: " + summary.Goal
        };

        var evidenceLines = evidence
            .Take(20)
            .Select(item =>
            {
                var parts = new List<string>
                {
                    item.Kind,
                    "ID " + item.EvidenceId
                };
                if (!string.IsNullOrWhiteSpace(item.Summary))
                    parts.Add(BoundWorkAssistantText(item.Summary!, 260));
                if (!string.IsNullOrWhiteSpace(item.Sha256))
                    parts.Add("SHA256 " + BoundWorkAssistantText(item.Sha256!, 128));
                if (!string.IsNullOrWhiteSpace(item.Provenance))
                    parts.Add(BoundWorkAssistantText(item.Provenance!, 180));
                return string.Join(" · ", parts);
            })
            .ToArray();

        return new(
            summary.TaskId,
            summary.Status,
            stateText,
            BoundWorkAssistantText(resultText, 1_200),
            verified,
            attention,
            evidenceLines,
            summary.ProjectId,
            CanCancel: summary.Status is
                H2AgentTaskStatus.Queued
                or H2AgentTaskStatus.Running
                or H2AgentTaskStatus.WaitingForApproval,
            CanRetry: summary.Status is
                H2AgentTaskStatus.Blocked
                or H2AgentTaskStatus.Cancelled
                or H2AgentTaskStatus.Failed);
    }

    private void UpdateWorkAssistantBubble(
        WorkAssistantTaskPresentation presentation)
    {
        var (state, detail) = presentation.Status switch
        {
            H2AgentTaskStatus.Queued or H2AgentTaskStatus.Running =>
                (WorkAssistantBubbleState.Working, "Đang làm"),
            H2AgentTaskStatus.WaitingForApproval =>
                (WorkAssistantBubbleState.Attention, "Cần xác nhận"),
            H2AgentTaskStatus.Blocked =>
                (WorkAssistantBubbleState.Attention, "Bị chặn"),
            H2AgentTaskStatus.Failed =>
                (WorkAssistantBubbleState.Attention, "Cần xem"),
            H2AgentTaskStatus.Cancelled =>
                (WorkAssistantBubbleState.Attention, "Đã hủy"),
            H2AgentTaskStatus.Completed when presentation.Verified =>
                (WorkAssistantBubbleState.Completed, "Đã xác minh"),
            H2AgentTaskStatus.Completed =>
                (WorkAssistantBubbleState.Completed, "Đã xong"),
            _ => (WorkAssistantBubbleState.Idle, "Sẵn sàng")
        };
        SetWorkAssistantBubbleState(state, detail);
    }

    private void ShowCurrentWorkAssistantTaskDetails()
    {
        if (_workAssistantQuickTaskId is null)
        {
            ShowWorkAssistantCompact();
            return;
        }

        RefreshWorkAssistantTaskState();
        if (_workAssistantTaskSnapshot is null)
            return;

        var compact = EnsureWorkAssistantCompact();
        compact.ShowTaskPresentation(
            BuildWorkAssistantPresentation(_workAssistantTaskSnapshot),
            WorkAssistantProjectChoices(),
            activate: true);
    }

    private void CancelCurrentWorkAssistantTask()
    {
        if (_workAssistantQuickTaskId is not { } taskId)
            return;

        try
        {
            _agentAdapter.CancelTask(taskId);
            RefreshWorkAssistantTaskState();
        }
        catch (Exception ex)
        {
            EnsureWorkAssistantCompact().SetStatus(
                "Không hủy được tác vụ: " + BoundUiError(ex.Message),
                isError: true);
        }
    }

    private void PrepareRetryCurrentWorkAssistantTask()
    {
        var goal = _workAssistantLastGoal
            ?? _workAssistantTaskSnapshot?.Goal
            ?? "";

        _workAssistantTaskTimer.Stop();
        _workAssistantQuickTaskId = null;
        _workAssistantTaskSnapshot = null;
        CurrentWorkAssistantContext = null;

        var compact = EnsureWorkAssistantCompact();
        compact.PrepareRetry(goal);
        SetWorkAssistantBubbleState(
            WorkAssistantBubbleState.Idle,
            "Sẵn sàng");
    }

    private void LinkCurrentWorkAssistantTaskFromUi(Guid projectId)
    {
        if (!LinkCurrentWorkAssistantTaskToProject(projectId))
        {
            EnsureWorkAssistantCompact().SetStatus(
                "Không gắn được tác vụ vào dự án.",
                isError: true);
            return;
        }

        RefreshWorkAssistantTaskState();
        ShowCurrentWorkAssistantTaskDetails();
    }

    private void OpenCurrentWorkAssistantWorkspace()
    {
        ShowMain();

        if (_workAssistantTaskSnapshot?.ProjectId is { } projectId)
            _main?.OpenProjectWorkspace(projectId);
    }

    private IReadOnlyList<WorkAssistantProjectChoice> WorkAssistantProjectChoices()
        => State.Notes
            .Where(note => note.IsBoard && !note.IsArchived)
            .SelectMany(note => note.Projects)
            .GroupBy(project => project.Id)
            .Select(group => group.First())
            .OrderBy(project => ProjectDisplayName(project), StringComparer.OrdinalIgnoreCase)
            .Select(project => new WorkAssistantProjectChoice(
                project.Id,
                ProjectDisplayName(project)))
            .ToArray();

    private static string ProjectDisplayName(ProjectRecord project)
        => project.NameRich?.Text
           ?? RichDocument.FromLegacy(project.Name ?? "").Text;

    private static bool IsVerifiedEvidence(H2AgentEvidence evidence)
    {
        var kind = (evidence.Kind ?? "").ToLowerInvariant();
        return kind.Contains("verified", StringComparison.Ordinal)
               || kind.Contains("verification", StringComparison.Ordinal)
               || kind.Contains("mutation", StringComparison.Ordinal);
    }

    private static bool IsTerminal(H2AgentTaskStatus status)
        => status is H2AgentTaskStatus.Completed
            or H2AgentTaskStatus.Blocked
            or H2AgentTaskStatus.Cancelled
            or H2AgentTaskStatus.Failed;

    private static string BoundWorkAssistantText(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }
}
