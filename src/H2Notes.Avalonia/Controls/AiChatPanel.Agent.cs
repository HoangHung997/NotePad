using Avalonia.Controls;
using System.Text;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private Guid? _activeAgentTaskId;
    private long _activeAgentProgressSequence = -1;

    private void CancelActiveAgentTask()
    {
        if (_activeAgentTaskId is not { } taskId)
            return;

        try { _app.AgentAdapter.CancelTask(taskId); }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }
    }

    private async Task SendProjectAgent()
    {
        if (_scope?.Project is not { } project)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
            return;

        var scope = _scope;
        var prompt = _composer.Text?.Trim() ?? "";
        var conversation = EnsureConversation();
        var draftAttachments = conversation.DraftAttachments.ToList();
        if (prompt.Length == 0)
            prompt = "Phân tích các tệp đính kèm và tóm tắt nội dung chính.";

        PrepareProjectContext?.Invoke();

        var context = BuildAgentTaskContext(project, draftAttachments);
        var readOnly = CurrentPermission == AiPermissionMode.ReadOnly;
        var cts = new CancellationTokenSource();
        _request = cts;
        _activeAgentProgressSequence = -1;

        var user = new AiMessage
        {
            Role = "user",
            Content = prompt,
            Provider = "H2 Agent",
            Model = "Agent",
            CreatedAt = DateTime.UtcNow,
            DeviceId = _app.DeviceId,
            Attachments = draftAttachments
        };

        var answer = new AiMessage
        {
            Role = "assistant",
            ParentId = user.Id,
            Provider = "H2 Agent",
            Model = "Agent",
            Status = "streaming",
            CreatedAt = DateTime.UtcNow,
            DeviceId = _app.DeviceId
        };

        SetTitle(conversation, prompt);
        conversation.Messages.Add(user);
        conversation.Messages.Add(answer);
        ClearDraft(conversation);
        RefreshHistory();
        Render();
        ScrollToLatest();

        _activeAnswer = answer;
        _streamFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.IsVisible = false;
        _stop.IsVisible = true;
        RefreshComposerOptions();
        Touch(scope);

        var progressText = new StringBuilder();
        _streamingReasoning = "";
        _flushStreaming = () =>
        {
            if (scope.Project is { } p)
                _app.MarkProjectDirty(p.Id);
        };

        string? error = null;
        Guid taskId = Guid.Empty;

        try
        {
            taskId = await _app.AgentAdapter.StartTaskAsync(
                project.Id,
                prompt,
                context,
                readOnly,
                cts.Token);

            if (taskId == Guid.Empty)
                throw new InvalidOperationException("Agent adapter returned an empty task ID.");

            _activeAgentTaskId = taskId;
            user.AiRunId = taskId;
            answer.AiRunId = taskId;
            _status.Text = "Agent đang làm việc…";

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                var observation = _app.AgentAdapter.ObserveTask(
                    taskId,
                    _activeAgentProgressSequence);

                foreach (var progress in observation.Progress
                             .OrderBy(item => item.Sequence))
                {
                    if (progress.Sequence <= _activeAgentProgressSequence)
                        continue;
                    _activeAgentProgressSequence = progress.Sequence;
                    AppendBoundedProgress(progressText, progress.Message);
                }

                _streamingReasoning = progressText.ToString();
                if (_conversation == conversation
                    && _bubbles.TryGetValue(answer.Id, out var bubble))
                {
                    bubble.SetThinking(_streamingReasoning);
                }

                var summary = observation.Summary;
                if (summary.PendingApproval is { } approval)
                {
                    _status.Text = "Agent cần phê duyệt: " + approval.Title;
                }
                else
                {
                    _status.Text = AgentStatusText(summary.Status);
                }

                if (IsTerminal(summary.Status))
                {
                    answer.Content = summary.FinalText ?? "";
                    answer.ErrorText = summary.Error ?? "";
                    answer.Status = summary.Status switch
                    {
                        H2AgentTaskStatus.Completed => "complete",
                        H2AgentTaskStatus.Cancelled => "interrupted",
                        _ => "error"
                    };

                    if (summary.Status == H2AgentTaskStatus.Blocked
                        && string.IsNullOrWhiteSpace(answer.ErrorText))
                        answer.ErrorText = "Agent bị chặn trước khi hoàn tất.";
                    if (summary.Status == H2AgentTaskStatus.Failed
                        && string.IsNullOrWhiteSpace(answer.ErrorText))
                        answer.ErrorText = "Agent không hoàn tất tác vụ.";

                    break;
                }

                await Task.Delay(120, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (taskId != Guid.Empty)
            {
                try { _app.AgentAdapter.CancelTask(taskId); } catch { }
            }

            answer.Status = "interrupted";
            answer.ErrorText = "Đã dừng Agent theo yêu cầu.";
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or IOException
            or KeyNotFoundException
            or ArgumentException)
        {
            answer.Status = "error";
            error = ex.Message;
            answer.ErrorText = error;
        }
        finally
        {
            _streamingReasoning = "";
            _activeAgentTaskId = null;
            _activeAgentProgressSequence = -1;
            _flushStreaming = null;
            _activeAnswer = null;
            _request = null;
            cts.Dispose();

            _send.IsVisible = true;
            _stop.IsVisible = false;
            RefreshComposerOptions();

            if (_conversation == conversation)
            {
                Render();
                ScrollToLatest();
                if (answer.Status == "complete")
                    _status.Text = "Agent đã hoàn tất.";
                else if (!string.IsNullOrWhiteSpace(answer.ErrorText))
                    _status.Text = "Agent chưa hoàn tất · xem chi tiết trong tin nhắn.";
            }

            Touch(scope);
            _streamFinished?.TrySetResult();
            _streamFinished = null;
        }
    }

    private H2AgentTaskContext BuildAgentTaskContext(
        ProjectRecord project,
        IReadOnlyList<AiAttachment> attachments)
    {
        var selectedContext = ReadContext?.Invoke() ?? "";
        var progress = ProjectProgressCalculator.Calculate(project);
        var next = ProjectProgressCalculator.NextTask(project);

        var summary = new StringBuilder();
        summary.AppendLine("H2 project context");
        summary.AppendLine("ProjectId: " + project.Id);
        var projectName = project.NameRich?.Text ?? RichDocument.FromLegacy(project.Name ?? "").Text;
        summary.AppendLine("Project: " + BoundAgentContext(projectName, 500));
        summary.AppendLine($"Progress: {progress.Completed}/{progress.Total}");
        if (next is not null)
            summary.AppendLine("Next task: " + BoundAgentContext(
                ProjectProgressCalculator.TaskText(next),
                1_000));

        if (!string.IsNullOrWhiteSpace(selectedContext))
        {
            summary.AppendLine();
            summary.AppendLine("Current user-visible context:");
            summary.AppendLine(BoundAgentContext(selectedContext, 6_000));
        }

        var openTasks = project.ChecklistItems
            .Where(task => !task.IsCompleted)
            .Take(20)
            .Select(task => "- " + BoundAgentContext(
                ProjectProgressCalculator.TaskText(task),
                300))
            .ToArray();
        if (openTasks.Length > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Open project tasks:");
            foreach (var task in openTasks)
                summary.AppendLine(task);
        }

        if (attachments.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("Composer attachments (presentation metadata; Agent tools remain authoritative for file access):");
            foreach (var item in attachments.Take(12))
            {
                summary.Append("- ")
                    .Append(BoundAgentContext(item.Name, 180))
                    .Append(" | sha256=")
                    .Append(BoundAgentContext(item.Sha256, 128));
                if (!string.IsNullOrWhiteSpace(item.Text))
                    summary.Append(" | extractedText=")
                        .Append(BoundAgentContext(item.Text, 1_500));
                summary.AppendLine();
            }
        }

        return new H2AgentTaskContext(
            _app.DataFolder,
            BoundAgentContext(summary.ToString(), 15_000),
            project.UpdatedAtUtc?.Ticks ?? 0);
    }

    private static void AppendBoundedProgress(StringBuilder builder, string? text)
    {
        text = (text ?? "").Replace('\r', ' ').Trim();
        if (text.Length == 0) return;

        if (builder.Length > 0)
            builder.AppendLine();
        builder.Append(BoundAgentContext(text, 2_000));

        const int max = 24_000;
        if (builder.Length > max)
            builder.Remove(0, builder.Length - max);
    }

    private static string BoundAgentContext(string? value, int max)
    {
        value ??= "";
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static bool IsTerminal(H2AgentTaskStatus status)
        => status is H2AgentTaskStatus.Completed
            or H2AgentTaskStatus.Blocked
            or H2AgentTaskStatus.Cancelled
            or H2AgentTaskStatus.Failed;

    private static string AgentStatusText(H2AgentTaskStatus status)
        => status switch
        {
            H2AgentTaskStatus.Queued => "Agent đang xếp hàng…",
            H2AgentTaskStatus.Running => "Agent đang làm việc…",
            H2AgentTaskStatus.WaitingForApproval => "Agent đang chờ phê duyệt…",
            H2AgentTaskStatus.Completed => "Agent đã hoàn tất.",
            H2AgentTaskStatus.Blocked => "Agent bị chặn.",
            H2AgentTaskStatus.Cancelled => "Agent đã dừng.",
            H2AgentTaskStatus.Failed => "Agent thất bại.",
            _ => "Agent đang làm việc…"
        };
}
