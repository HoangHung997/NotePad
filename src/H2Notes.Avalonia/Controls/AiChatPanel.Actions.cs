using Avalonia.Controls;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    /// <summary>
    /// Legacy h2-actions blocks are historical user data only. New project mutations flow through
    /// IH2AgentAdapter + typed IH2ProjectToolHost. This renderer may preview old proposals/audits,
    /// but it must never execute them.
    /// </summary>
    private void AddProjectActions(ChatMessageView bubble, AiMessage message)
    {
        if (_scope?.Project is null
            || message.Role != "assistant"
            || message.Status != "complete"
            || message.AiRunId.HasValue
            || string.Equals(message.Provider, "H2 Agent", StringComparison.Ordinal))
            return;

        IReadOnlyList<AiProjectAction> actions;
        try
        {
            actions = AiProjectActions.Parse(message.Content);
        }
        catch (Exception ex) when (
            ex is System.Text.Json.JsonException
            or InvalidDataException
            or InvalidOperationException
            or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            bubble.Actions.Children.Add(new TextBlock
            {
                Text = "Legacy h2-actions không còn được thực thi. Nội dung lịch sử được giữ nguyên.",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (actions.Count == 0)
        {
            if (message.ProjectActionsApplied && !string.IsNullOrWhiteSpace(message.ProjectActionsAudit))
            {
                bubble.Actions.Children.Add(new TextBlock
                {
                    Text = message.ProjectActionsAudit,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            return;
        }

        bubble.Body.Text = AiProjectActions.WithoutBlocks(
            bubble.Body.Text ?? message.Content);

        var scope = _scope;
        var conversation = _conversation;
        var preview = new Button
        {
            Content = message.ProjectActionsApplied
                ? "Lịch sử đã áp dụng · Xem"
                : $"Legacy đề xuất · Xem {actions.Count} thay đổi (chỉ đọc)",
            FontSize = 11,
            Name = "ChatProjectActions"
        };

        preview.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner
                || scope != _scope
                || conversation != _conversation)
                return;

            var description =
                "Đây là đề xuất h2-actions từ runtime cũ. H2 hiện không cho phép áp dụng lại "
                + "pseudo-action từ nội dung model; thay đổi mới phải đi qua Agent + typed project tools."
                + Environment.NewLine
                + Environment.NewLine
                + scope.Title
                + Environment.NewLine
                + Environment.NewLine
                + AiProjectActions.Preview(scope.Project, actions);

            await TextPreview(
                owner,
                message.ProjectActionsApplied
                    ? "Lịch sử thay đổi dự án"
                    : "Legacy thay đổi dự án · Chỉ đọc",
                description,
                false);
        };

        bubble.Actions.Children.Add(preview);

        if (message.ProjectActionsApplied && !string.IsNullOrWhiteSpace(message.ProjectActionsAudit))
        {
            bubble.Actions.Children.Add(new TextBlock
            {
                Text = message.ProjectActionsAudit,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }
}
