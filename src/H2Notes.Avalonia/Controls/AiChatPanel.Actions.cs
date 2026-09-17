using Avalonia.Controls;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    // The project has already been mutated when this event fires. The event exists so the host
    // can flush/reload its editor and refresh task rows without duplicating mutation semantics.
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
            // First flush a human edit that may still be in the rich editor. Then validate the whole
            // action batch before mutating anything, and finally apply it atomically to the model.
            PrepareProjectContext?.Invoke();
            AiProjectActions.Validate(project, actions, CurrentPermission, approved);
            var now = DateTime.UtcNow;
            AiProjectActions.Apply(project, actions, now);

            // Existing host callback is used as a refresh signal. Passing an empty action set avoids
            // replaying legacy add/append logic now that Core owns every edit/delete/rich-format operation.
            ProjectActionsRequested.Invoke(project, []);

            var local = now.ToLocalTime();
            message.ProjectActionsApplied = true;
            var items = string.Join(" | ", actions.Select((a, i) => $"{i + 1}:{a.Kind}:{AuditSubject(a)}"));
            message.ProjectActionsAudit = $"appliedUtc={now:O}; appliedLocal={local:yyyy-MM-ddTHH:mm:sszzz}; count={actions.Count}; items={items}";
            Touch(scope);
        }
        catch (Exception ex) when (IsProjectActionException(ex)) { _status.Text = "Chưa áp dụng: " + ex.Message; }
    }

    private void ApplyAutomaticProjectActions(AiChatScope scope, AiConversation conversation, AiMessage answer, AiPermissionMode sentPermission)
        => _ = ApplyAutomaticProjectActionsAsync(scope, conversation, answer, sentPermission);

    private async Task ApplyAutomaticProjectActionsAsync(AiChatScope scope, AiConversation conversation, AiMessage answer, AiPermissionMode sentPermission)
    {
        if (scope != _scope || conversation != _conversation || answer.Status != "complete" || sentPermission != CurrentPermission) return;
        try
        {
            var actions = AiProjectActions.Parse(answer.Content);
            if (actions.Count == 0 || scope.Project is null || sentPermission == AiPermissionMode.ReadOnly) return;

            if (sentPermission == AiPermissionMode.ProjectAccess)
            {
                // Full project access is intentionally one-step for allowlisted project actions.
                ApplyProjectActions(scope, answer, actions, false);
                if (_conversation == conversation) Render();
                return;
            }

            if (sentPermission == AiPermissionMode.ConfirmChanges && TopLevel.GetTopLevel(this) is Window owner)
            {
                PrepareProjectContext?.Invoke();
                // Validate against the latest project snapshot before asking. The dialog therefore
                // shows a concrete, currently-applicable before/after change rather than a vague proposal.
                AiProjectActions.Validate(scope.Project, actions, AiPermissionMode.ProjectAccess, false);
                var description = scope.Title + "\n\n" + AiProjectActions.Preview(scope.Project, actions);
                var approved = await Dialogs.Confirm(owner, "AI muốn thay đổi dự án", description, "Đồng ý và áp dụng");
                if (approved && scope == _scope && conversation == _conversation && CurrentPermission == AiPermissionMode.ConfirmChanges
                    && _request is null && !_preparing)
                {
                    ApplyProjectActions(scope, answer, actions, true);
                    Render();
                }
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
