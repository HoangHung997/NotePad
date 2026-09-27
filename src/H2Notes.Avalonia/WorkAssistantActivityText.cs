using H2Notes.Core;

namespace H2Notes.Avalonia;

/// <summary>Public activity only; never displays tool arguments, document bodies or hidden reasoning.</summary>
public static class WorkAssistantActivityText
{
    public static string? FromProgress(H2AgentProgress progress) => progress.ToolOutcome is not null || progress.TargetBinding is not null
        ? H2AgentActivity.Label(progress) : progress.Code switch
    {
        "queued" => "Đang chuẩn bị yêu cầu…",
        "started" => "Đang suy nghĩ…",
        "commentary" => progress.Message,
        "tool-start" => ToolAction(progress.Message),
        "tool-ok" => "Đã xong bước vừa rồi · đang xử lý tiếp…",
        "tool-error" => "Đang kiểm tra và xử lý lỗi…",
        "verification-pass" => "Đã kiểm tra kết quả · đang hoàn tất…",
        "verification-fail" => "Đang kiểm tra lại kết quả…",
        "approval-requested" => "Cần bạn xác nhận · " + progress.Message,
        "approval-granted" => "Đã được cho phép · đang tiếp tục…",
        "cancel-requested" => "Đang dừng…",
        "plan" => "Đang lên kế hoạch công việc…",
        "supplement-received" or "supplement-applied" => "Đang xử lý yêu cầu bổ sung…",
        // Tool output and script bodies belong in the chat evidence, not a desktop ticker.
        _ => null
    };

    private static string ToolAction(string name) => name.Trim() switch
    {
        "word.replace_range" or "word.insert_text" => "Đang sửa nội dung Word…",
        "word.apply_format" => "Đang định dạng tài liệu Word…",
        "word.save_copy" => "Đang lưu tài liệu Word…",
        "excel.write_range" or "excel.set_formula" => "Đang cập nhật bảng tính…",
        "excel.apply_format" => "Đang định dạng bảng tính…",
        "excel.save_copy" => "Đang lưu bảng tính…",
        "excel.recalculate" => "Đang tính lại bảng tính…",
        "web.read_feed" => "Đang đọc và lọc tin tức…",
        "web.fetch" or "web.fetch_url" => "Đang đọc trang web…",
        "save_news_digest" => "Đang lưu bản tin đã chọn…",
        "read_attachment" => "Đang đọc tài liệu đính kèm…",
        "read_tool_output" => "Đang xem kết quả thao tác…",
        var tool when tool.StartsWith("word.", StringComparison.Ordinal) => "Đang đọc và kiểm tra tài liệu Word…",
        var tool when tool.StartsWith("excel.", StringComparison.Ordinal) => "Đang đọc và kiểm tra bảng tính…",
        var tool when tool.StartsWith("autocad.", StringComparison.Ordinal) => "Đang xử lý bản vẽ AutoCAD…",
        var tool when tool.StartsWith("web.", StringComparison.Ordinal) => "Đang tra cứu thông tin trên mạng…",
        var tool => H2AgentActivity.Label(new(0, DateTime.UtcNow, "tool", "tool-start", tool))
    };
}
