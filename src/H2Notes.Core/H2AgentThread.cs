namespace H2Notes.Core;

/// <summary>Durable conversation identity. Task data stays in the Agent archive; no grants are persisted here.</summary>
public sealed record H2AgentThread(Guid ThreadId, Guid? ProjectId, string Title,
    DateTime CreatedUtc, DateTime UpdatedUtc, string Draft = "", IReadOnlyList<Guid>? TaskIds = null);

public static class H2AgentActivity
{
    public static bool IsTerminal(H2AgentTaskStatus status) => status is H2AgentTaskStatus.Completed
        or H2AgentTaskStatus.Cancelled or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed;

    public static string Label(H2AgentProgress item) => item.ToolOutcome is { } outcome
        ? OutcomeLabel(item.Message, outcome) : item.Code switch
    {
        "queued" => "Đã nhận yêu cầu", "started" => "Đang làm việc",
        "cancel-requested" => "Đang dừng…", "completed" => "Đã hoàn tất",
        "cancelled" => "Đã dừng", "failed" => "Tác vụ gặp lỗi", "blocked" => "Chưa thể hoàn tất",
        "approval-requested" => "Cần bạn xác nhận", "approval-granted" => "Đã được cho phép",
        "approval-denied" => "Bạn đã từ chối thay đổi", "supplement-received" => "Đã nhận bổ sung · " + item.Message,
        "supplement-applied" => "Đã đưa bổ sung vào lượt xử lý",
        "tool-start" => "Đang thực hiện · " + ToolLabel(item.Message),
        "tool-result" => "Kết quả · " + ToolLabel(item.Message.Split(':', 2)[0]),
        "script" => "Đoạn xử lý dữ liệu", "plan" => "Kế hoạch công việc",
        "tool-ok" => "✓ " + ToolLabel(item.Message), "tool-error" => "Chưa thành công · " + ToolLabel(item.Message),
        _ => item.Message
    };

    private static string OutcomeLabel(string tool, H2AgentToolOutcome outcome)
    {
        var label = ToolLabel(tool);
        return outcome.Status switch {
            H2ToolRunStatus.Running => "Đang chạy · " + label + " · Job " + outcome.JobId,
            H2ToolRunStatus.Rejected => "Chưa thực hiện · " + label + " · " + outcome.ErrorCode,
            H2ToolRunStatus.PartiallyApplied => "Đã thay đổi một phần · cần đối soát · " + label,
            H2ToolRunStatus.OutcomeUnknown => "Chưa rõ tác động · không tự ghi lại · " + label,
            H2ToolRunStatus.Cancelled => (outcome.Effect == H2ToolMutationEffect.Unknown
                ? "Đã hủy · cần kiểm tra thay đổi · " : "Đã hủy · ") + label,
            H2ToolRunStatus.Failed => "Thực thi gặp lỗi · " + label + " · " + outcome.ErrorCode,
            _ when outcome.Verification == H2ToolVerificationStatus.Passed => "Đã xác minh · " + label,
            _ when outcome.Verification == H2ToolVerificationStatus.Failed => "Xác minh chưa đạt · " + label,
            _ when outcome.Effect == H2ToolMutationEffect.Applied => "Đã áp dụng · chưa xác minh · " + label,
            _ when !outcome.Complete => "Kết quả giới hạn/chưa xác nhận đầy đủ · " + label,
            _ => "Đã thực thi · " + label
        };
    }

    private static string ToolLabel(string name) => name switch
    {
        "read_file" or "read_text" => "Đọc tệp", "write_file" or "write_text" => "Ghi tệp",
        "list_files" or "list_directory" => "Xem thư mục", "exec_command" => "Chạy lệnh",
        "run_python" => "Chạy xử lý dữ liệu", "inspect_run" => "Kiểm tra kết quả xử lý",
        "read_document" => "Đọc tài liệu", "publish_artifact" => "Lưu tệp kết quả",
        "tool_search" => "Tìm công cụ phù hợp", "update_plan" => "Cập nhật kế hoạch",
        "discover_excel" => "Tìm bảng tính đã chọn", "discover_word" => "Tìm tài liệu đã chọn",
        _ => name.Replace('_', ' ')
    };
}
