using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public enum AgentUiState
{
    Queued,
    Running,
    WaitingForApproval,
    Compacting,
    WaitingForJob,
    Reconnecting,
    ReconcileRequired,
    Interrupted,
    AppliedUnverified,
    Verified,
    CancelRequested,
    Completed,
    Blocked,
    Cancelled,
    Failed
}

public sealed record AgentUiProjection(
    AgentUiState State,
    string Label,
    string Detail,
    bool NeedsAttention,
    bool IsTerminal,
    bool IsVerified);

/// <summary>
/// Pure UI projection over authoritative Agent facts. It never mutates task state, executes tools,
/// infers hidden reasoning, or treats a model message as verification evidence.
/// </summary>
public static class AgentUiProjector
{
    public static AgentUiProjection Project(
        H2AgentTaskSummary task,
        IEnumerable<H2AgentProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var events = (progress ?? Array.Empty<H2AgentProgress>()).OrderBy(x => x.Sequence).ToArray();
        var last = events.LastOrDefault();
        var terminal = H2AgentActivity.IsTerminal(task.Status);

        if (task.PendingApproval is not null || task.Status == H2AgentTaskStatus.WaitingForApproval)
            return P(AgentUiState.WaitingForApproval, "Cần bạn xác nhận",
                task.PendingApproval?.Title ?? "Đang chờ quyết định của bạn.", true, terminal);

        if (!terminal && last?.Code == "cancel-requested")
            return P(AgentUiState.CancelRequested, "Đang dừng…",
                "Đã gửi yêu cầu hủy; trạng thái tác động vẫn phải được xác nhận.", true, false);

        if (task.Recovery is { ReconcileRequired: true })
            return P(AgentUiState.ReconcileRequired, "Cần đối soát sau gián đoạn",
                "Có tác động chưa xác định; H2 sẽ không tự lặp thao tác ghi.", true, terminal);

        if (task.Recovery is { Interrupted: true })
            return P(AgentUiState.Interrupted, "Tác vụ bị gián đoạn",
                "Quyền cũ không được tự khôi phục; tiếp tục cần bind/quyền hợp lệ.", true, terminal);

        if (!terminal && IsCompacting(last))
            return P(AgentUiState.Compacting, "Đang thu gọn ngữ cảnh",
                "Giữ yêu cầu hiện hành, công việc còn dở và bằng chứng đã có.", false, false);

        if (!terminal && IsReconnecting(last))
            return P(AgentUiState.Reconnecting, "Đang kết nối lại",
                "Đang khôi phục kết nối; không tự phát lại thao tác ghi.", false, false);

        if (!terminal && IsWaitingForJob(task, last))
            return P(AgentUiState.WaitingForJob, "Đang chờ công việc nền",
                JobDetail(task, last), false, false);

        if (task.Status == H2AgentTaskStatus.Completed && H2AgentVerification.IsVerified(task))
            return P(AgentUiState.Verified, "Đã xác minh",
                "Kết quả có bằng chứng xác minh của host.", false, true, true);

        if (task.Status == H2AgentTaskStatus.Completed && HasUnverifiedMutation(task, events))
            return P(AgentUiState.AppliedUnverified, "Đã áp dụng · chưa xác minh",
                "Có thay đổi đã thực hiện nhưng chưa có bằng chứng xác minh đầy đủ.", true, true);

        return task.Status switch
        {
            H2AgentTaskStatus.Queued => P(AgentUiState.Queued, "Đã xếp lượt tiếp theo", "Chưa bắt đầu thực thi.", false, false),
            H2AgentTaskStatus.Running => P(AgentUiState.Running, "Đang làm việc", LastDetail(last), false, false),
            H2AgentTaskStatus.Completed => P(AgentUiState.Completed, "Đã hoàn tất", "Không có tác động chưa xác minh được chiếu vào UI.", false, true),
            H2AgentTaskStatus.Blocked => P(AgentUiState.Blocked, "Chưa thể hoàn tất", Safe(task.Error, "Cần xử lý blocker trước khi tiếp tục."), true, true),
            H2AgentTaskStatus.Cancelled => P(AgentUiState.Cancelled, "Đã dừng", Safe(task.Error, "Tác vụ đã được hủy."), false, true),
            H2AgentTaskStatus.Failed => P(AgentUiState.Failed, "Tác vụ gặp lỗi", Safe(task.Error, "Xem hoạt động để biết lỗi gần nhất."), true, true),
            _ => P(AgentUiState.Running, "Đang làm việc", LastDetail(last), false, terminal)
        };
    }

    /// <summary>Coalesces UI-only noisy progress. Durable progress remains untouched in the adapter/archive.</summary>
    public static IReadOnlyList<(H2AgentProgress Event, int Count)> CollapseForDisplay(
        IEnumerable<H2AgentProgress> source,
        int maxRows)
    {
        if (maxRows < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));
        var items = source.OrderBy(x => x.Sequence).ToArray();
        if (items.Length == 0) return [];
        var groups = new List<(H2AgentProgress Event, int Count)>();
        foreach (var item in items)
        {
            var noisy = IsNoisy(item);
            if (noisy && groups.Count > 0)
            {
                var prior = groups[^1];
                if (prior.Event.Kind == item.Kind && prior.Event.Code == item.Code
                    && prior.Event.TargetBinding is null && item.TargetBinding is null)
                {
                    groups[^1] = (item, prior.Count + 1);
                    continue;
                }
            }
            groups.Add((item, 1));
        }
        return groups.TakeLast(maxRows).ToArray();
    }

    private static bool IsCompacting(H2AgentProgress? item)
        => item is not null && (item.Code.Contains("compact", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Contains("compact", StringComparison.OrdinalIgnoreCase));

    private static bool IsReconnecting(H2AgentProgress? item)
        => item is not null && (item.Code.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Contains("reconnect", StringComparison.OrdinalIgnoreCase));

    private static bool IsWaitingForJob(H2AgentTaskSummary task, H2AgentProgress? item)
        => item?.ToolOutcome?.Status == H2ToolRunStatus.Running
            || task.Recovery?.Operations.Any(x => x.Job?.Status == "Running") == true
            || item?.Code is "job-running" or "job-poll" or "waiting-for-job";

    private static string JobDetail(H2AgentTaskSummary task, H2AgentProgress? item)
    {
        var id = item?.ToolOutcome?.JobId
            ?? task.Recovery?.Operations.Select(x => x.Job?.JobId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return string.IsNullOrWhiteSpace(id) ? "Công việc nền vẫn đang chạy." : "Job " + id + " vẫn đang chạy.";
    }

    private static bool HasUnverifiedMutation(H2AgentTaskSummary task, IReadOnlyList<H2AgentProgress> events)
        => task.GoalState?.Outcomes.Any(x => x.Status is "AppliedUnverified" or "Failed") == true
            || task.GoalState?.MutationRevisions.Count > 0
            || events.Any(x => x.ToolOutcome is { Effect: H2ToolMutationEffect.Applied, Verification: not H2ToolVerificationStatus.Passed });

    private static bool IsNoisy(H2AgentProgress item)
        => item.Code.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)
            || item.Code is "job-poll" or "job-running" or "progress"
            || item.Kind.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);

    private static string LastDetail(H2AgentProgress? item)
        => item is null ? "Đang chuẩn bị." : H2AgentActivity.Label(item);

    private static string Safe(string? value, string fallback)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return fallback;
        return value.Length <= 500 ? value : value[..500] + "…";
    }

    private static AgentUiProjection P(
        AgentUiState state,
        string label,
        string detail,
        bool attention,
        bool terminal,
        bool verified = false)
        => new(state, label, detail, attention, terminal, verified);
}
