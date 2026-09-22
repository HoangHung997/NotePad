using System.Text.Json;
using H2Notes.Core;
using H2AgentLab.Tasking;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter
{
    private readonly object _chatGate = new();
    private readonly Dictionary<Guid, H2AgentThread> _threads = [];
    private string ChatRoot => Path.Combine(_stateRoot, "integration", "threads");

    private async Task ExecuteWhenReadyAsync(LiveTask live)
    {
        try
        {
            if (live.RequestContext?.AfterTaskId is { } id && TryLive(id, out var previous))
            {
                await previous.Finished.Task.WaitAsync(live.Cancellation.Token).ConfigureAwait(false);
                var summary = previous.Snapshot();
                if (!string.IsNullOrWhiteSpace(summary.FinalText))
                    live.RequestContext = live.RequestContext with { RecentTurns = (live.RequestContext.RecentTurns ?? [])
                        .Concat(new[] { new H2AgentChatTurn(id + ":user", "user", summary.Goal),
                            new H2AgentChatTurn(id + ":assistant", "assistant", summary.FinalText) }).TakeLast(32).ToArray() };
                if (live.PermissionScope is { } scope && !scope.IsActiveAt(DateTime.UtcNow))
                    throw new InvalidOperationException("Quyền của lượt xếp hàng đã hết hạn. Chọn lại quyền và gửi tiếp; chưa thực hiện thay đổi.");
            }
            live.Cancellation.Token.ThrowIfCancellationRequested();
            await ExecuteAsync(live).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { SetStatus(live, H2AgentTaskStatus.Cancelled, "cancelled", "Đã hủy lượt xếp hàng."); }
        catch (Exception ex)
        {
            lock (live.Gate) live.Error = ex.Message;
            SetStatus(live, H2AgentTaskStatus.Failed, "failed", ex.Message);
        }
        finally { live.Finished.TrySetResult(); }
    }

    public bool SupplementTask(Guid taskId, Guid inputId, string text)
    {
        if (inputId == Guid.Empty || !TryLive(taskId, out var live)) return false;
        text = BoundRequired(text, nameof(text), 8_000, true);
        lock (live.Gate)
        {
            if (live.SupplementalIds.TryGetValue(inputId, out var previous)) return previous == text;
            if (!live.AcceptingInput || IsTerminal(live.Status) || live.Cancellation.IsCancellationRequested
                || live.SupplementalIds.Count >= 24) return false;
            live.SupplementalIds.Add(inputId, text); live.SupplementalInput.Enqueue(new(inputId, text));
            AddProgressLocked(live, "user", "supplement-received", text);
            return true;
        }
    }

    private IReadOnlyList<AgentGoalInput> TakeSupplementalInput(LiveTask live, bool closing)
    {
        lock (live.Gate)
        {
            var inputs = live.SupplementalInput.ToArray(); live.SupplementalInput.Clear();
            if (closing && inputs.Length == 0) live.AcceptingInput = false;
            if (inputs.Length > 0) AddProgressLocked(live, "lifecycle", "supplement-applied", $"{inputs.Length} lời nhắn bổ sung");
            return inputs;
        }
    }

    private void InitializeChatArchive()
    {
        Directory.CreateDirectory(ChatRoot);
        foreach (var path in Directory.EnumerateFiles(ChatRoot, "*.json"))
        {
            // A broken thread is preserved for recovery; never reinterpret it as another conversation.
            try
            {
                var item = JsonSerializer.Deserialize<H2AgentThread>(File.ReadAllText(path));
                if (item is { ThreadId: var id } && id != Guid.Empty) _threads[id] = item;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        foreach (var task in _archive.Recent(null, 500))
        {
            var id = task.ThreadId ?? task.TaskId;
            if (_threads.TryGetValue(id, out var existing) && existing.TaskIds?.Contains(task.TaskId) == true) continue;
            var thread = existing ?? new H2AgentThread(id, task.ProjectId, Bound(task.Goal, 120), task.CreatedUtc, task.UpdatedUtc);
            SaveThread(thread with { TaskIds = (thread.TaskIds ?? []).Append(task.TaskId).Distinct().ToArray() });
        }
    }

    public IReadOnlyList<H2AgentThread> GetThreads(Guid? projectId = null)
    {
        lock (_chatGate) return _threads.Values.Where(t => t.ProjectId == projectId)
            .OrderByDescending(t => t.UpdatedUtc).Select(CloneThread).ToArray();
    }

    public H2AgentThread? GetThread(Guid threadId)
    {
        lock (_chatGate) return _threads.TryGetValue(threadId, out var thread) ? CloneThread(thread) : null;
    }

    public void SaveThread(H2AgentThread thread)
    {
        if (thread.ThreadId == Guid.Empty) throw new ArgumentException("ThreadId cannot be empty.");
        lock (_chatGate)
        {
            // Draft saves must not drop tasks that the runtime registered concurrently.
            var ids = (_threads.GetValueOrDefault(thread.ThreadId)?.TaskIds ?? []).Concat(thread.TaskIds ?? []).Distinct().ToArray();
            var next = thread with { TaskIds = ids, Title = Bound(thread.Title, 120) };
            ProjectWorkspaceStore.AtomicWrite(Path.Combine(ChatRoot, thread.ThreadId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(next));
            _threads[thread.ThreadId] = next;
        }
    }

    private static H2AgentThread CloneThread(H2AgentThread thread) => thread with { TaskIds = thread.TaskIds?.ToArray() };

    private void RegisterChatTask(LiveTask task)
    {
        var id = task.RequestContext?.ThreadId ?? task.TaskId;
        var thread = GetThread(id) ?? new H2AgentThread(id, task.ProjectId, Bound(task.Goal, 120), task.CreatedUtc, task.UpdatedUtc);
        SaveThread(thread with { Title = thread.TaskIds?.Count > 0 ? thread.Title : Bound(task.Goal, 120),
            Draft = "", UpdatedUtc = task.UpdatedUtc, TaskIds = (thread.TaskIds ?? []).Append(task.TaskId).Distinct().ToArray() });
    }

    private string ProgressPath(Guid taskId) => Path.Combine(_stateRoot, "integration", "task-records", taskId.ToString("N") + ".jsonl");

    private void PersistProgress(Guid taskId, H2AgentProgress progress)
    {
        var path = ProgressPath(taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(progress) + "\n");
    }

    private IReadOnlyList<H2AgentProgress> ReadProgress(Guid taskId, long afterSequence)
    {
        var path = ProgressPath(taskId);
        if (!File.Exists(path)) return [];
        var events = new SortedDictionary<long, H2AgentProgress>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                var item = JsonSerializer.Deserialize<H2AgentProgress>(line);
                if (item is not null && item.Sequence > afterSequence) events[item.Sequence] = item;
            }
            catch (JsonException) { /* A crash may leave one partial append. Preserve all complete events. */ }
        }
        return events.Values.ToArray();
    }
}

internal sealed partial class AgentIntegrationTaskArchive
{
    private string TaskPath(Guid id) => Path.Combine(Path.GetDirectoryName(_path)!, "task-records", id.ToString("N") + ".json");

    private void SaveDurableTask(H2AgentTaskSummary summary)
    {
        var path = TaskPath(summary.TaskId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProjectWorkspaceStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(summary));
    }

    private H2AgentTaskSummary? LoadDurableTask(Guid id)
    {
        var path = TaskPath(id);
        if (!File.Exists(path)) return null;
        var task = JsonSerializer.Deserialize<H2AgentTaskSummary>(File.ReadAllText(path));
        return task is null || IsTerminal(task.Status) ? task : task with {
            Status = H2AgentTaskStatus.Failed, PendingApproval = null,
            Error = "Tác vụ bị gián đoạn khi ứng dụng đóng. Quyền cũ không được khôi phục; gửi tiếp để tiếp tục." };
    }

    private H2AgentEvidence? FindDurableEvidence(string evidenceId)
    {
        var root = Path.GetDirectoryName(TaskPath(Guid.Empty))!;
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var task = JsonSerializer.Deserialize<H2AgentTaskSummary>(File.ReadAllText(path));
                if (task?.Evidence.FirstOrDefault(e => e.EvidenceId == evidenceId) is { } evidence) return evidence;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return null;
    }
}
