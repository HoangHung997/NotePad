using Avalonia.Controls;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    public event Action<ProjectRecord, IReadOnlyList<AiProjectAction>>? ProjectActionsRequested;

    private void AddProjectActions(ChatMessageView bubble, AiMessage message)
    {
        if (_scope?.Project is null || message.Role != "assistant" || message.Status != "complete") return;
        try
        {
            var actions = AiProjectActions.Parse(message.Content);
            if (actions.Count == 0) return;
            bubble.Body.Text = AiProjectActions.WithoutBlocks(bubble.Body.Text ?? message.Content);
            var scope = _scope; var conversation = _conversation;
            var preview = new Button { Content = message.ProjectActionsApplied ? "Đã áp dụng · Xem thay đổi" : $"Xem {actions.Count} thay đổi dự án", FontSize = 11, Name = "ChatProjectActions" };
            preview.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner || scope != _scope || conversation != _conversation) return;
                var description = scope.Title + "\n\n" + AiProjectActions.Preview(actions);
                if (message.ProjectActionsApplied || CurrentPermission == AiPermissionMode.ReadOnly)
                {
                    await TextPreview(owner, message.ProjectActionsApplied ? "Các thay đổi đã áp dụng" : "Chỉ đọc · chưa sửa dự án", description, false);
                    return;
                }
                var approved = await Dialogs.Confirm(owner, "Áp dụng thay đổi vào dự án?", description, "Áp dụng");
                if (approved && scope == _scope && conversation == _conversation && _request is null && !_preparing)
                { ApplyProjectActions(scope, message, actions, true); Render(); }
            };
            bubble.Actions.Children.Add(preview);
            if (message.ProjectActionsApplied)
                bubble.Actions.Children.Add(new TextBlock { Text = message.ProjectActionsAudit, FontSize = 10, TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception ex) when (IsProjectActionException(ex))
        { bubble.Actions.Children.Add(new TextBlock { Text = "Chưa áp dụng thay đổi: " + ex.Message, FontSize = 11, TextWrapping = TextWrapping.Wrap }); }
    }

    private static bool IsProjectActionException(Exception ex) => ex is System.Text.Json.JsonException or InvalidDataException
        or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException;

    private void ApplyProjectActions(AiChatScope scope, AiMessage message, IReadOnlyList<AiProjectAction> actions, bool approved)
    {
        if (scope != _scope || scope.Project is not { } project || message.ProjectActionsApplied || ProjectActionsRequested is null) return;
        try
        {
            PrepareProjectContext?.Invoke();
            AiProjectActions.Validate(project, actions, CurrentPermission, approved);
            // UI callback is synchronous, targets this exact project and preserves editor undo.
            ProjectActionsRequested.Invoke(project, actions);
            message.ProjectActionsApplied = true;
            message.ProjectActionsAudit = $"H2 Notes đã áp dụng {actions.Count} thay đổi · {DateTime.Now:HH:mm dd/MM/yyyy}";
            Touch(scope);
        }
        catch (Exception ex) when (IsProjectActionException(ex)) { _status.Text = "Chưa áp dụng: " + ex.Message; }
    }

    private void ApplyAutomaticProjectActions(AiChatScope scope, AiConversation conversation, AiMessage answer, AiPermissionMode sentPermission)
    {
        if (sentPermission != AiPermissionMode.ProjectAccess || CurrentPermission != AiPermissionMode.ProjectAccess
            || scope != _scope || conversation != _conversation || answer.Status != "complete") return;
        try
        {
            var actions = AiProjectActions.Parse(answer.Content);
            if (actions.Count > 0) ApplyProjectActions(scope, answer, actions, false);
        }
        catch (Exception ex) when (IsProjectActionException(ex)) { _status.Text = "AI đã trả lời nhưng thay đổi chưa hợp lệ: " + ex.Message; }
    }
}
