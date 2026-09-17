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
            var preview = new Button
            {
                Content = message.ProjectActionsApplied ? "Đã áp dụng · Xem thay đổi" : $"Xem {actions.Count} thay đổi dự án",
                FontSize = 11, Name = "ChatProjectActions"
            };
            preview.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner || scope != _scope || conversation != _conversation) return;
                var description = scope.Title + "\n\n" + AiProjectActions.Preview(scope.Project, actions);
                if (message.ProjectActionsApplied || CurrentPermission == AiPermissionMode.ReadOnly)
                {
                    await TextPreview(owner, message.ProjectActionsApplied ? "Các thay đổi đã áp dụng" : "Chỉ đọc · chưa sửa dự án", description, false);
                    return;
                }
                var approved = await Dialogs.Confirm(owner, "Áp dụng thay đổi vào dự án?", description, "Đồng ý và áp dụng");
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
            var oldTaskIds = project.ChecklistItems.Select(t => t.Id).ToHashSet();
            ProjectActionsRequested.Invoke(project, actions);
            var now = DateTime.UtcNow;
            var local = now.ToLocalTime();
            project.UpdatedAtUtc = now;
            foreach (var task in project.ChecklistItems.Where(t => !oldTaskIds.Contains(t.Id)))
            {
                task.CreatedAtUtc ??= now;
                task.UpdatedAtUtc ??= now;
            }
            message.ProjectActionsApplied = true;
            var items = string.Join(" | ", actions.Select((a, i) => $"{i + 1}:{a.Kind}:{AuditSubject(a)}"));
            message.ProjectActionsAudit = $"appliedUtc={now:O}; appliedLocal={local:yyyy-MM-ddTHH:mm:sszzz}; count={actions.Count}; items={items}";
            Touch(scope);
        }
        catch (Exception ex) when (IsProjectActionException(ex)) { _status.Text = "Chưa áp dụng: " + ex.Message; }
    }

    private async Task ApplyAutomaticProjectActions(AiChatScope scope, AiConversation conversation, AiMessage answer, AiPermissionMode sentPermission)
    {
        if (scope != _scope || conversation != _conversation || answer.Status != "complete" || sentPermission != CurrentPermission) return;
        try
        {
            var actions = AiProjectActions.Parse(answer.Content);
            if (actions.Count == 0 || scope.Project is null || sentPermission == AiPermissionMode.ReadOnly) return;

            if (sentPermission == AiPermissionMode.ProjectAccess)
            {
                // Full project access means exactly that: no second confirmation for an allowlisted,
                // validated project mutation requested by the newest user turn.
                ApplyProjectActions(scope, answer, actions, false);
                return;
            }

            if (sentPermission == AiPermissionMode.ConfirmChanges && TopLevel.GetTopLevel(this) is Window owner)
            {
                PrepareProjectContext?.Invoke();
                // Validate before showing the dialog so the user never confirms an action that can
                // no longer target the current project state.
                AiProjectActions.Validate(scope.Project, actions, AiPermissionMode.ProjectAccess, false);
                var description = scope.Title + "\n\n" + AiProjectActions.Preview(scope.Project, actions);
                var approved = await Dialogs.Confirm(owner, "AI muốn thay đổi dự án", description, "Đồng ý và áp dụng");
                if (approved && scope == _scope && conversation == _conversation && CurrentPermission == AiPermissionMode.ConfirmChanges
                    && _request is null && !_preparing)
                    ApplyProjectActions(scope, answer, actions, true);
            }
        }
        catch (Exception ex) when (IsProjectActionException(ex)) { _status.Text = "AI đã trả lời nhưng thay đổi chưa hợp lệ: " + ex.Message; }
    }

    private static string AuditSubject(AiProjectAction action)
    {
        var value = action.Text ?? action.Match ?? action.TaskId?.ToString() ?? "";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length > 160 ? value[..160] + "…" : value;
    }
}
