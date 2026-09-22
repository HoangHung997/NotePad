using Avalonia.Controls;
using System.Text;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private Guid? _activeAgentTaskId;
    private long _activeAgentProgressSequence = -1;
    private readonly AgentApprovalPanel _agentApproval = new();

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

        if (_app.AgentAdapter is IH2AgentArchiveStatus archive && !archive.GetArchiveStatus().CanWrite)
        { _status.Text = "Agent: cần phục hồi nhật ký trước khi chạy; bản nháp được giữ nguyên."; return; }
        PrepareProjectContext?.Invoke();
        if (_profiles.SelectedItem is not AiProfile selectedProfile || string.IsNullOrWhiteSpace(selectedProfile.Model))
        { _status.Text = "Mở Thiết lập AI để chọn model và lưu kết nối trước."; return; }
        var profile = CreateRequestProfile(selectedProfile);

        var user = new AiMessage
        {
            Role = "user",
            Content = prompt,
            Provider = profile.Name,
            Model = profile.Model,
            CreatedAt = DateTime.UtcNow,
            DeviceId = _app.DeviceId,
            Attachments = draftAttachments
        };

        IReadOnlyList<AiTurn> preparedTurns = [new AiTurn("user", prompt,
            AiDocuments.NativeImages(draftAttachments), AiDocuments.NativeFiles(draftAttachments))];
        if (AiPdfProcessor.NeedsPreparation(preparedTurns, _app.LocalSettings.Ai.Pdf))
        {
            var prepared = await PreparePdfRequest(scope, conversation, user, profile, "");
            if (prepared is null) return;
            preparedTurns = prepared;
        }
        var context = BuildAgentTaskContext(project, user.Attachments) with
        {
            Images = preparedTurns[^1].Images,
            Files = preparedTurns[^1].Files
        };
        var readOnly = CurrentPermission == AiPermissionMode.ReadOnly;
        _projectActiveContext = context; _projectActiveReadOnly = readOnly; _projectQueueTail = null;
        var cts = new CancellationTokenSource();
        _request = cts;
        _activeAgentProgressSequence = -1;
        user.Context = context.Summary ?? "";

        var answer = new AiMessage
        {
            Role = "assistant",
            ParentId = user.Id,
            Provider = profile.Name,
            Model = profile.Model,
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
                    bubble.PresentAgent(_app.AgentAdapter, observation);
                    _chatSurface.NotifyActivity();
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
                    if (summary.Status == H2AgentTaskStatus.Cancelled && string.IsNullOrWhiteSpace(answer.ErrorText))
                        answer.ErrorText = "Đã dừng Agent theo yêu cầu. Các thay đổi đã thực hiện không tự hoàn tác.";

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
            _activeAgentTaskId = _queuedAgentTurns.Keys.Cast<Guid?>().FirstOrDefault();
            _agentApproval.Present(_app.AgentAdapter, taskId, null);
            _activeAgentProgressSequence = -1;
            _flushStreaming = null;
            _activeAnswer = null;
            _request = null;
            cts.Dispose();

            _send.IsVisible = !_activeAgentTaskId.HasValue;
            _stop.IsVisible = _activeAgentTaskId.HasValue;
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
        var includeProject = _includeProject.IsChecked == true;
        var selectedContext = includeProject ? ReadContext?.Invoke() ?? "" : "";
        var progress = ProjectProgressCalculator.Calculate(project);
        var next = ProjectProgressCalculator.NextTask(project);

        var summary = new StringBuilder();
        summary.AppendLine("H2 project context");
        summary.AppendLine("ProjectId: " + project.Id);
        var projectName = project.NameRich?.Text ?? RichDocument.FromLegacy(project.Name ?? "").Text;
        summary.AppendLine("Project: " + BoundAgentContext(projectName, 500));
        if (includeProject) summary.AppendLine($"Progress: {progress.Completed}/{progress.Total}");
        if (includeProject && next is not null)
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
        if (includeProject && openTasks.Length > 0)
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
                summary.Append("- attachmentId=").Append(item.Id).Append(" | ")
                    .Append(BoundAgentContext(item.Name, 180))
                    .Append(" | sha256=")
                    .Append(BoundAgentContext(item.Sha256, 128));
                if (!string.IsNullOrWhiteSpace(item.Text))
                    summary.Append(" | extractedText=")
                        .Append(BoundAgentContext(item.Text, 1_500));
                summary.AppendLine();
            }
        }

        var conversation = EnsureConversation();
        var history = new List<H2AgentChatTurn>();
        if (_includeHistory.IsChecked == true)
            foreach (var other in project.Conversations.Where(item => item.Id != conversation.Id).TakeLast(4))
                history.AddRange(ChatTurns(other).TakeLast(2));
        history.AddRange(ChatTurns(conversation).TakeLast(AiHistory.RecentMessageLimit));
        var now = DateTime.UtcNow;
        var permission = CurrentPermission;
        var permissionScope = new H2AgentPermissionScope(
            permission == AiPermissionMode.ReadOnly ? H2AgentPermissionMode.ObserveOnly
                : permission == AiPermissionMode.ProjectAccess ? H2AgentPermissionMode.AllowScopedChanges
                : H2AgentPermissionMode.AskBeforeChanges,
            H2AgentResourceScopeKind.Project, "h2-project:" + project.Id.ToString("N"),
            permission != AiPermissionMode.ReadOnly, permission != AiPermissionMode.ProjectAccess,
            now, now.AddMinutes(15));
        if (permission == AiPermissionMode.FullAccess)
            permissionScope = new(H2AgentPermissionMode.FullAccess, H2AgentResourceScopeKind.Machine,
                H2AgentPermissionScope.CurrentMachineResourceKey, true, false, now,
                _fullAccessExpiresUtc < now.AddHours(1) ? _fullAccessExpiresUtc : now.AddHours(1));
        var targets = new List<H2AgentTargetPath>();
        if (includeProject) foreach (var link in project.Links) H2AgentTargetScope.Add(targets, link.Target, "project-link");
        var projectWorkspace = Path.Combine(_app.AgentWorkspaceRoot, "projects", project.Id.ToString("N"));
        Directory.CreateDirectory(projectWorkspace);
        return new H2AgentTaskContext(
            projectWorkspace,
            BoundAgentContext(summary.ToString(), 15_000),
            project.UpdatedAtUtc?.Ticks ?? 0,
            permissionScope,
            (_profiles.SelectedItem as AiProfile)?.Id,
            SelectedReasoningEffort,
            history,
            AiDocuments.NativeImages(attachments), AiDocuments.NativeFiles(attachments),
            attachments.ToArray(), includeProject, conversation.Id, Guid.NewGuid(), TargetPaths: targets);
    }

    private static IEnumerable<H2AgentChatTurn> ChatTurns(AiConversation conversation)
        => conversation.Messages.Where(message => !message.IsTimelineMarker && message.Status == "complete"
            && message.Role is "user" or "assistant")
            .Select(message => new H2AgentChatTurn(message.Id.ToString("N"), message.Role,
                AiHistory.TimeMetadata(message) + message.Content + AiDocuments.Describe(message.Attachments)));

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
