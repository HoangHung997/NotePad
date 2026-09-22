using Avalonia;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class App
{
    private CancellationTokenSource? _workAssistantPreparation;
    private Guid? _workAssistantThreadId;
    private H2AgentTaskContext? _workAssistantLastContext;
    private bool _workAssistantLastReadOnly;
    private Guid? _workAssistantQueueTail;

    private async Task QueueWorkAssistantTurn(string prompt, H2AgentTaskSummary running)
    {
        if (_workAssistantLastContext is not { } context) return;
        try
        {
            _workAssistantQueueTail = await _agentAdapter.StartTaskAsync(null, prompt,
                context with { TurnId = Guid.NewGuid(), AfterTaskId = _workAssistantQueueTail ?? running.TaskId }, _workAssistantLastReadOnly);
            _workAssistantCompact?.ClearPrompt();
            _workAssistantCompact?.SetStatus("Đã xếp lượt tiếp theo · giữ nguyên phạm vi, model và thời hạn quyền.");
            RefreshWorkAssistantHistory();
        }
        catch (Exception ex) { _workAssistantCompact?.SetStatus("Chưa xếp lượt: " + BoundUiError(ex.Message), true); }
    }
    private bool _loadingWorkAssistantThread;
    private readonly global::Avalonia.Threading.DispatcherTimer _foregroundMemoryTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly global::Avalonia.Threading.DispatcherTimer _workAssistantDraftTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _positioningWorkAssistant;

    private void RefreshWorkAssistantHistory()
    {
        if (_workAssistantCompact is null) return;
        try
        {
            EnsureWorkAssistantThread();
            var thread = _agentAdapter.GetThread(_workAssistantThreadId!.Value);
            var tasks = (thread?.TaskIds ?? []).Select(id => _agentAdapter.GetTaskSummary(id)).ToList();
            if (_workAssistantTaskSnapshot is { } current && tasks.All(t => t.TaskId != current.TaskId)) tasks.Add(current);
            _workAssistantCompact.Threads = _agentAdapter.GetThreads();
            _workAssistantCompact.ConversationId = _workAssistantThreadId.Value;
            _workAssistantCompact.ShowHistory(tasks, _workAssistantQuickTaskId);
        }
        catch (Exception ex) { _workAssistantCompact.SetStatus("Không tải được lịch sử: " + BoundUiError(ex.Message), true); }
    }

    private void StartNewWorkAssistantConversation()
    {
        if (_workAssistantPreparation is not null) { _workAssistantCompact?.SetStatus("Dừng đọc tài liệu trước khi đổi hội thoại."); return; }
        if (_workAssistantTaskSnapshot is { } task && !IsTerminal(task.Status))
        { _workAssistantCompact?.SetStatus("Đợi tác vụ hiện tại kết thúc hoặc bấm Hủy."); return; }
        SaveWorkAssistantDraft();
        _workAssistantThreadId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _agentAdapter.SaveThread(new(_workAssistantThreadId.Value, null, "Cuộc trò chuyện mới", now, now));
        _workAssistantQuickTaskId = null; _workAssistantTaskSnapshot = null; _workAssistantLastGoal = null;
        var compact = EnsureWorkAssistantCompact();
        compact.PrepareRetry(""); compact.SetStatus("Cuộc trò chuyện mới");
        RefreshWorkAssistantHistory();
    }

    private void EnsureWorkAssistantThread()
    {
        if (_workAssistantThreadId.HasValue) return;
        var thread = _agentAdapter.GetThreads().FirstOrDefault();
        if (thread is null)
        {
            var now = DateTime.UtcNow;
            thread = new(Guid.NewGuid(), null, "Cuộc trò chuyện mới", now, now);
            _agentAdapter.SaveThread(thread);
        }
        _workAssistantThreadId = thread.ThreadId;
        _loadingWorkAssistantThread = true;
        if (_workAssistantCompact is { } compact) compact.PromptText = thread.Draft;
        _loadingWorkAssistantThread = false;
    }

    private void SaveWorkAssistantDraft()
    {
        _workAssistantDraftTimer.Stop();
        if (_loadingWorkAssistantThread || _workAssistantCompact is null || !_workAssistantThreadId.HasValue) return;
        if (_agentAdapter.GetThread(_workAssistantThreadId.Value) is { } thread)
            _agentAdapter.SaveThread(thread with { Draft = _workAssistantCompact.PromptText, UpdatedUtc = DateTime.UtcNow });
    }

    private void SelectWorkAssistantThread(Guid id)
    {
        if (_workAssistantPreparation is not null) { _workAssistantCompact?.SetStatus("Dừng đọc tài liệu trước khi đổi hội thoại."); return; }
        if (_workAssistantTaskSnapshot is { } current && !IsTerminal(current.Status))
        { _workAssistantCompact?.SetStatus("Tác vụ đang chạy · dừng hoặc chờ hoàn tất trước khi đổi hội thoại."); return; }
        if (_agentAdapter.GetThread(id) is not { } thread) return;
        SaveWorkAssistantDraft(); _workAssistantThreadId = id;
        _workAssistantQuickTaskId = null; _workAssistantTaskSnapshot = null;
        _loadingWorkAssistantThread = true;
        _workAssistantCompact?.PrepareRetry(thread.Draft);
        _loadingWorkAssistantThread = false;
        RefreshWorkAssistantHistory();
    }

    private void PositionWorkAssistantCompact()
    {
        if (_positioningWorkAssistant || _workAssistantCompact is not { } panel || _workAssistantBubble is not { } bubble) return;
        var screen = bubble.Screens.ScreenFromPoint(bubble.Position) ?? bubble.Screens.Primary;
        if (screen is null) return;
        _positioningWorkAssistant = true;
        try
        {
            var scale = screen.Scaling;
            var area = screen.WorkingArea;
            var maxHeight = Math.Max(panel.MinHeight, (area.Height - 12) / scale);
            var maxWidth = Math.Max(panel.MinWidth, (area.Width - 12) / scale);
            if (panel.Height > maxHeight) panel.Height = maxHeight;
            if (panel.Width > maxWidth) panel.Width = maxWidth;
            panel.Position = WorkAssistantPlacement.AttachPanel(bubble.Position,
                new PixelSize((int)Math.Ceiling(bubble.Width * scale), (int)Math.Ceiling(bubble.Height * scale)),
                new PixelSize((int)Math.Ceiling(panel.Width * scale), (int)Math.Ceiling(panel.Height * scale)), area,
                (int)Math.Ceiling(10 * scale));
        }
        finally { _positioningWorkAssistant = false; }
    }
}
