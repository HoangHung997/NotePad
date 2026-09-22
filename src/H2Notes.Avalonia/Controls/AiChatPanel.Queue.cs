using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private readonly ComboBox _busySendMode = new() { Name = "ChatBusySendMode", FontSize = 11,
        ItemsSource = new[] { "Bổ sung tác vụ này", "Xếp lượt tiếp theo" }, SelectedIndex = 0,
        IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private H2AgentTaskContext? _projectActiveContext;
    private bool _projectActiveReadOnly;
    private Guid? _projectQueueTail;
    private readonly Dictionary<Guid, (AiConversation Conversation, AiMessage Answer, AiChatScope Scope)> _queuedAgentTurns = [];
    private DispatcherTimer? _queuedAgentTimer;

    private async Task QueueProjectTurn(Guid active, string prompt)
    {
        if (_projectActiveContext is not { } context || _scope?.Project is not { } project || _conversation is null) return;
        if (_conversation.DraftAttachments.Count > 0)
        { _status.Text = "Lượt xếp hàng hiện nhận văn bản. Giữ tệp ở ô soạn và gửi khi tác vụ hiện tại kết thúc."; return; }
        var conversation = _conversation; var scope = _scope;
        try
        {
            var task = await _app.AgentAdapter.StartTaskAsync(project.Id, prompt,
                context with { TurnId = Guid.NewGuid(), AfterTaskId = _projectQueueTail ?? active }, _projectActiveReadOnly);
            _projectQueueTail = task;
            var user = new AiMessage { Role = "user", Content = prompt, AiRunId = task, CreatedAt = DateTime.UtcNow, DeviceId = _app.DeviceId };
            var answer = new AiMessage { Role = "assistant", ParentId = user.Id, AiRunId = task, Status = "streaming",
                CreatedAt = DateTime.UtcNow, DeviceId = _app.DeviceId, Model = (_profiles.SelectedItem as AiProfile)?.Model ?? "" };
            conversation.Messages.Add(user); conversation.Messages.Add(answer);
            _queuedAgentTurns.Add(task, (conversation, answer, scope)); ClearDraft(conversation); Touch(scope); Render();
            _status.Text = "Đã xếp lượt tiếp theo · giữ nguyên phạm vi và quyền của tác vụ.";
            _queuedAgentTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
                (_, _) => RefreshQueuedAgentTurns());
            _queuedAgentTimer.Start();
        }
        catch (Exception ex) { _status.Text = "Chưa xếp lượt: " + ex.Message; }
    }

    private void RefreshQueuedAgentTurns()
    {
        foreach (var (id, entry) in _queuedAgentTurns.ToArray())
        {
            H2AgentTaskObservation observation;
            try { observation = _app.AgentAdapter.ObserveTask(id); }
            catch (Exception ex) when (ex is KeyNotFoundException or IOException)
            {
                entry.Answer.Status = "error"; entry.Answer.ErrorText = "Không đọc được lượt đã xếp hàng: " + ex.Message;
                _queuedAgentTurns.Remove(id); Touch(entry.Scope); continue;
            }
            if (_conversation == entry.Conversation && _bubbles.TryGetValue(entry.Answer.Id, out var bubble))
                bubble.PresentAgent(_app.AgentAdapter, observation);
            var summary = observation.Summary;
            if (!IsTerminal(summary.Status)) continue;
            entry.Answer.Content = summary.FinalText ?? "";
            entry.Answer.ErrorText = summary.Error ?? (summary.Status == H2AgentTaskStatus.Cancelled ? "Đã dừng lượt này." : "");
            entry.Answer.Status = summary.Status == H2AgentTaskStatus.Completed ? "complete" : summary.Status == H2AgentTaskStatus.Cancelled ? "interrupted" : "error";
            if (_conversation == entry.Conversation && _bubbles.TryGetValue(entry.Answer.Id, out var completedBubble)) completedBubble.Refresh();
            _queuedAgentTurns.Remove(id); Touch(entry.Scope);
            if (_activeAgentTaskId == id) _activeAgentTaskId = _queuedAgentTurns.Keys.Cast<Guid?>().FirstOrDefault();
        }
        if (_queuedAgentTurns.Count == 0)
        {
            _queuedAgentTimer?.Stop(); _projectQueueTail = null;
            _send.IsVisible = _request is null; _stop.IsVisible = _request is not null;
            RefreshComposerOptions();
        }
    }
}
