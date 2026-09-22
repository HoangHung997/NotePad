using System.Text.Json;
using H2Notes.Core;
using H2AgentLab.Tasking;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter
{
    private readonly object _chatGate = new();
    private readonly Dictionary<Guid, H2AgentThread> _threads = [];

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
            // A persistence fault must not publish a successful terminal state or recurse
            // through the failing persistence path. The durable prefix remains authoritative.
            lock (live.Gate)
            {
                live.Status = _archive.Status.CanWrite ? H2AgentTaskStatus.Failed : H2AgentTaskStatus.Blocked;
                live.Error = _archive.Status.CanWrite ? ex.Message : "Agent archive cần phục hồi; kết quả chưa được xác nhận bền vững.";
                live.PendingApproval = null;
            }
            if (_archive.Status.CanWrite) _archive.Upsert(live.Snapshot());
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
        foreach (var item in _archive.Threads()) _threads[item.ThreadId] = item;
        foreach (var task in _archive.Recent(null, 500))
        {
            var id = task.ThreadId ?? task.TaskId;
            if (_threads.TryGetValue(id, out var existing) && existing.TaskIds?.Contains(task.TaskId) == true) continue;
            var thread = existing ?? new H2AgentThread(id, task.ProjectId, Bound(task.Goal, 120), task.CreatedUtc, task.UpdatedUtc);
            var rebuilt = thread with { TaskIds = (thread.TaskIds ?? []).Append(task.TaskId).Distinct().ToArray() };
            if (_archive.Status.CanWrite) SaveThread(rebuilt); else _threads[id] = rebuilt;
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
            _archive.SaveThread(next);
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

    private void PersistProgress(Guid taskId, H2AgentProgress progress) => _archive.AppendProgress(taskId, progress);
    private IReadOnlyList<H2AgentProgress> ReadProgress(Guid taskId, long afterSequence) => _archive.ReadProgress(taskId, afterSequence);
}
