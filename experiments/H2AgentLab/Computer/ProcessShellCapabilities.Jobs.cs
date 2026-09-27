using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2AgentLab.Computer;

public sealed record ProcessJobSnapshot(string JobId, Guid OwnerTaskId, string GoalRevisionId,
    string RequestId, string RequestHash, int ProcessId, DateTime ProcessStartedUtc,
    DateTime CreatedUtc, DateTime DeadlineUtc, DateTime? LastOutputUtc, string Status,
    int? ExitCode, bool RootExited, bool AllJobProcessesExited, bool StreamsDrained,
    bool OutputComplete, long StdoutObservedCharacters, long StderrObservedCharacters,
    int StdoutRetainedCharacters, int StderrRetainedCharacters, string? Reason,
    string HostExitPolicy = "CancelOnHostExit")
{
    public bool Terminal => Status != "Running";
}
public sealed record ProcessJobOutput(string JobId, string Stream, string Text, string? NextCursor,
    int Offset, int RetainedCharacters, long ObservedCharacters, bool Truncated, bool StreamEof,
    bool JobTerminal, string Encoding = "UTF-8 decoded text; cursor offsets are UTF-16 characters");
public sealed record ProcessJobInputReceipt(string InputId, string ContentHash, string Status, bool Closed);

/// <summary>Job methods extend the existing process service. State is bounded owner-local working
/// state, not durable truth. The Agent's existing journal records receipts. No restart adoption,
/// PID lookup, silent replay or new daemon. Caller poll deadlines do not cancel the owned job.</summary>
public sealed partial class ProcessShellCapabilities
{
    private readonly object _jobsGate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Owner, string Request), string> _jobRequests = new();
    private const int MaxJobs = 32, MaxActiveJobs = 4;

    public ProcessJobSnapshot StartJob(Guid ownerTaskId, string goalRevisionId, string requestId,
        string executable, IReadOnlyList<string> arguments, string workingDirectory = ".",
        TimeSpan? lifetime = null, TimeSpan? idleTimeout = null, bool allowStdin = false,
        CancellationToken ownerCancellation = default, Action<ProcessJobSnapshot>? beforeResume = null)
    {
        if (ownerTaskId == Guid.Empty || string.IsNullOrWhiteSpace(goalRevisionId) || goalRevisionId.Length > 160
            || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 || requestId.Any(char.IsControl))
            throw new ArgumentException("A bounded task/revision/request identity is required.");
        if (!_policy.AllowStart || !_policy.AllowTerminate) throw new UnauthorizedAccessException("Owned jobs require start and lifetime-cleanup permission.");
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > 128 || arguments.Any(a => a is null)) throw new ArgumentException("Invalid arguments.");
        var duration = ValidateTimeout(lifetime); var idle = idleTimeout ?? duration;
        if (idle <= TimeSpan.Zero || idle > duration) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        var start = BuildStart(ValidateExecutable(executable), arguments.ToArray(), _workspace.Resolve(workingDirectory, true));
        if (!Path.IsPathFullyQualified(start.FileName)) throw new ArgumentException("Long jobs require an explicit executable path, not PATH lookup.");
        var fingerprint = Hash(JsonSerializer.Serialize(new { start.FileName, arguments, start.WorkingDirectory,
            lifetimeTicks = duration.Ticks, idleTicks = idle.Ticks, allowStdin, goalRevisionId }));
        lock (_jobsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ownerCancellation.ThrowIfCancellationRequested();
            if (_jobRequests.TryGetValue((ownerTaskId, requestId), out var id))
            {
                var prior = _jobs[id];
                if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("Job request identity conflicts with its original specification.");
                return prior.Snapshot(); // Reconnect/retry returns the same instance, including terminal/unknown state.
            }
            if (_jobs.Count >= MaxJobs || _jobs.Values.Count(j => !j.Snapshot().Terminal) >= MaxActiveJobs)
                throw new InvalidOperationException("Job capacity reached; existing jobs and receipts were not evicted.");
            var job = new Job(ownerTaskId, goalRevisionId, requestId, fingerprint,
                WindowsOwnedProcess.CreateSuspended(start), duration, idle, allowStdin, _policy.MaxOutputCharacters);
            _jobs.Add(job.Id, job); _jobRequests.Add((ownerTaskId, requestId), job.Id);
            try
            {
                beforeResume?.Invoke(job.Snapshot()); // Host records the real identity before any application instruction.
                ownerCancellation.ThrowIfCancellationRequested();
                job.Begin(ownerCancellation);
                return job.Snapshot();
            }
            catch
            {
                job.FailBeforeResume(); // Do not permit another process under the same idempotency key.
                throw;
            }
        }
    }
    private Job OwnedJob(Guid task, string id)
    {
        lock (_jobsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (task == Guid.Empty || id is null || !_jobs.TryGetValue(id, out var job) || job.Owner != task)
                throw new KeyNotFoundException("Unknown job for this task.");
            return job;
        }
    }
    public async Task<ProcessJobSnapshot> PollJobAsync(Guid ownerTaskId, string jobId, TimeSpan wait,
        CancellationToken requestCancellation = default)
    {
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(wait));
        requestCancellation.ThrowIfCancellationRequested();
        var job = OwnedJob(ownerTaskId, jobId);
        if (wait > TimeSpan.Zero && !job.Completion.IsCompleted)
        {
            try { await job.Completion.WaitAsync(wait, requestCancellation).ConfigureAwait(false); }
            catch (TimeoutException) { } // A request deadline is not the job's execution deadline.
        }
        requestCancellation.ThrowIfCancellationRequested();
        return job.Snapshot();
    }
    public ProcessJobSnapshot JobResult(Guid ownerTaskId, string jobId)
    {
        var value = OwnedJob(ownerTaskId, jobId).Snapshot();
        if (!value.Terminal) throw new InvalidOperationException("Job is still running; poll it instead of inventing a result.");
        return value;
    }
    public ProcessJobOutput ReadJobOutput(Guid ownerTaskId, string jobId, string stream,
        string? cursor = null, int maxCharacters = 4096) => OwnedJob(ownerTaskId, jobId).Read(stream, cursor, maxCharacters);
    public Task<ProcessJobInputReceipt> WriteJobInputAsync(Guid ownerTaskId, string jobId, string inputId,
        string text, bool close = false, CancellationToken requestCancellation = default)
        => OwnedJob(ownerTaskId, jobId).WriteInput(inputId, text, close, requestCancellation);
    public async Task<ProcessJobSnapshot> CancelJobAsync(Guid ownerTaskId, string jobId,
        CancellationToken requestCancellation = default)
    {
        requestCancellation.ThrowIfCancellationRequested(); var job = OwnedJob(ownerTaskId, jobId);
        job.Cancel("cancel_requested");
        try { await job.Completion.WaitAsync(TimeSpan.FromSeconds(8), requestCancellation).ConfigureAwait(false); }
        catch (TimeoutException) { } // Snapshot keeps Running/unknown until stop/EOF have actually been observed.
        return job.Snapshot();
    }
    public IReadOnlyList<ProcessJobSnapshot> ObserveJobs(Guid ownerTaskId)
    {
        lock (_jobsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _jobs.Values.Where(j => j.Owner == ownerTaskId).Select(j => j.Snapshot()).ToArray();
        }
    }
    private void DisposeJobs()
    {
        Job[] jobs;
        lock (_jobsGate) { jobs = _jobs.Values.ToArray(); foreach (var job in jobs) job.Cancel("owner_disposed"); }
        // No UI-context continuation is required. All job awaits use ConfigureAwait(false).
        try { Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { }
        finally { foreach (var job in jobs) job.Dispose(); }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Job : IDisposable
    {
        private readonly object _gate = new();
        private readonly WindowsOwnedProcess _process;
        private readonly TimeSpan _lifetime, _idle;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DateTime _created = DateTime.UtcNow;
        private readonly bool _allowStdin;
        private readonly Capture _stdout, _stderr;
        private readonly CancellationTokenSource _stop = new();
        private CancellationTokenRegistration _ownerRegistration;
        private readonly SemaphoreSlim _inputGate = new(1, 1);
        private readonly Dictionary<string, ProcessJobInputReceipt> _inputs = new(StringComparer.Ordinal);
        private bool _inputClosed, _rootExited, _allExited, _drained, _resumed;
        private string _status = "Running";
        private string? _reason, _requestedStop;
        private int? _exitCode;
        private DateTime? _lastOutput;
        private long _lastOutputTicks;
        public string Id { get; } = "job-" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; }
        public string Revision { get; }
        public string Request { get; }
        public string Fingerprint { get; }
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _completion.Task;
        public Job(Guid owner, string revision, string request, string fingerprint, WindowsOwnedProcess process,
            TimeSpan lifetime, TimeSpan idle, bool stdin, int cap)
        {
            Owner = owner; Revision = revision; Request = request; Fingerprint = fingerprint; _process = process;
            _lifetime = lifetime; _idle = idle; _allowStdin = stdin; _stdout = new(cap); _stderr = new(cap);
        }
        public void Begin(CancellationToken owner)
        {
            _ownerRegistration = owner.Register(() => Cancel("owner_cancelled"));
            _stop.Token.ThrowIfCancellationRequested();
            _resumed = true; // Conservative: a lost ResumeThread result may already have run user code.
            _process.Resume();
            if (!_allowStdin) { _process.CloseInput(); _inputClosed = true; }
            _ = WatchAsync(); // Owned by Completion and service disposal, not unobserved background work.
        }
        public void FailBeforeResume()
        {
            try
            {
                _process.Stop();
                var wait = Stopwatch.StartNew();
                // Job accounting may reach zero before the root handle becomes signaled.
                while ((!_process.RootState().Exited || _process.ActiveProcesses() != 0)
                    && wait.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(25);
                var root = _process.RootState();
                lock (_gate) { _rootExited = root.Exited; _exitCode = root.ExitCode; _allExited = _process.ActiveProcesses() == 0; }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException) { }
            finally { _process.Dispose(); _ownerRegistration.Dispose(); }
            lock (_gate) { _status = !_resumed && _rootExited && _allExited ? "Rejected" : "NeedsReconciliation"; _reason = "start_acknowledgement_incomplete"; }
            _completion.TrySetResult();
        }
        public void Cancel(string reason)
        {
            lock (_gate) { if (_status != "Running") return; _requestedStop ??= reason; }
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        private async Task Drain(StreamReader reader, Capture capture)
        {
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (count == 0)
                        {
                            if (capture.Text.Length > 0 && char.IsHighSurrogate(capture.Text[^1])) capture.Text.Length--;
                            capture.Eof = !capture.Abandoned; return;
                        }
                        capture.Append(buffer, count); _lastOutput = DateTime.UtcNow; _lastOutputTicks = _clock.Elapsed.Ticks;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or DecoderFallbackException)
            { lock (_gate) capture.Error = true; }
        }
        private async Task WatchAsync()
        {
            var drains = Task.WhenAll(Drain(_process.Output, _stdout), Drain(_process.Error, _stderr));
            string? stopReason = null;
            try
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_stdout.Error || _stderr.Error) stopReason = "output_read_failed";
                        else if (_stop.IsCancellationRequested) stopReason = _requestedStop ?? "cancel_requested";
                        else if (_clock.Elapsed >= _lifetime) stopReason = "lifetime_exceeded";
                        else if (_clock.Elapsed.Ticks - _lastOutputTicks >= _idle.Ticks) stopReason = "no_output_deadline";
                    }
                    var root = _process.RootState(); var active = _process.ActiveProcesses();
                    lock (_gate) { _rootExited = root.Exited; _exitCode = root.ExitCode; _allExited = active == 0; }
                    if (stopReason is not null || (root.Exited && active == 0 && drains.IsCompleted)) break;
                    await Task.Delay(25).ConfigureAwait(false);
                }
                if (stopReason is not null)
                {
                    _process.Stop();
                    var grace = Stopwatch.StartNew();
                    // Observe both identities before finalizing cancellation/deadline; job
                    // accounting alone may lead the process termination signal.
                    while ((!_process.RootState().Exited || _process.ActiveProcesses() != 0)
                        && grace.Elapsed < TimeSpan.FromSeconds(5))
                        await Task.Delay(25).ConfigureAwait(false);
                }
                try { await drains.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    lock (_gate) { _stdout.Abandoned = !_stdout.Eof; _stderr.Abandoned = !_stderr.Eof; }
                    _process.CloseReaders();
                    stopReason ??= "output_drain_incomplete";
                }
                var finalRoot = _process.RootState(); var finalActive = _process.ActiveProcesses();
                lock (_gate)
                {
                    _rootExited = finalRoot.Exited; _exitCode = finalRoot.ExitCode; _allExited = finalActive == 0;
                    _drained = _stdout.Eof && _stderr.Eof && !_stdout.Error && !_stderr.Error
                        && !_stdout.Abandoned && !_stderr.Abandoned;
                    _reason = stopReason;
                    _status = !_rootExited || !_allExited || !_drained ? "NeedsReconciliation"
                        : stopReason is "lifetime_exceeded" or "no_output_deadline" ? "TimedOut"
                        : stopReason is not null ? "Cancelled" : _exitCode == 0 ? "Succeeded" : "Failed";
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or ObjectDisposedException)
            {
                lock (_gate) { _status = "NeedsReconciliation"; _reason = "process_observation_failed"; }
            }
            finally
            {
                _process.Dispose(); _ownerRegistration.Dispose();
                _ = drains.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _completion.TrySetResult();
            }
        }
        public ProcessJobSnapshot Snapshot()
        {
            lock (_gate) return new(Id, Owner, Revision, Request, Fingerprint, _process.ProcessId,
                _process.StartedUtc, _created, _created + _lifetime, _lastOutput, _status, _exitCode,
                _rootExited, _allExited, _drained, _drained && !_stdout.Truncated && !_stderr.Truncated,
                _stdout.Observed, _stderr.Observed, _stdout.Text.Length, _stderr.Text.Length, _reason);
        }
        public ProcessJobOutput Read(string stream, string? cursor, int maximum)
        {
            if (maximum is < 2 or > 8192 || stream is not ("stdout" or "stderr")) throw new ArgumentException("Invalid output page.");
            lock (_gate)
            {
                var capture = stream == "stdout" ? _stdout : _stderr;
                var offset = 0;
                if (cursor is not null)
                {
                    // The opaque job identity binds this offset to one stream in one owning service.
                    var prefix = Id + ":" + stream + ":";
                    if (!cursor.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(cursor[prefix.Length..],
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset)
                        || offset < 0 || offset > capture.Text.Length) throw new ArgumentException("Invalid or foreign output cursor.");
                }
                if (offset > 0 && offset < capture.Text.Length && char.IsLowSurrogate(capture.Text[offset])
                    && char.IsHighSurrogate(capture.Text[offset - 1])) throw new ArgumentException("Cursor splits a Unicode character.");
                var length = Math.Min(maximum, capture.Text.Length - offset);
                if (length > 0 && offset + length < capture.Text.Length && char.IsHighSurrogate(capture.Text[offset + length - 1])) length--;
                if (length > 0 && offset + length == capture.Text.Length && _status == "Running"
                    && char.IsHighSurrogate(capture.Text[offset + length - 1])) length--;
                var end = offset + length;
                var next = end < capture.Text.Length || _status == "Running" ? Id + ":" + stream + ":" + end : null;
                return new(Id, stream, capture.Text.ToString(offset, length), next, offset, capture.Text.Length,
                    capture.Observed, capture.Truncated, capture.Eof && !capture.Abandoned, _status != "Running");
            }
        }
        public async Task<ProcessJobInputReceipt> WriteInput(string id, string text, bool close, CancellationToken ct)
        {
            if (!_allowStdin) throw new UnauthorizedAccessException("Stdin was not enabled for this job.");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || text is null || text.Length > 4096 || text.Contains('\0'))
                throw new ArgumentException("Invalid input receipt or size.");
            var hash = Hash(text + (close ? "\0close" : "\0open"));
            await _inputGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (_inputs.TryGetValue(id, out var previous))
                    {
                        if (previous.ContentHash != hash) throw new InvalidOperationException("Input ID conflicts with previous content.");
                        return previous; // Unknown is not replayed after a lost write acknowledgement.
                    }
                    if (_status != "Running" || _inputClosed) throw new InvalidOperationException("Input is closed.");
                    if (_inputs.Count >= 64) throw new InvalidOperationException("Input receipt budget reached.");
                    ct.ThrowIfCancellationRequested();
                    _inputs.Add(id, new(id, hash, "Unknown", close));
                }
                try
                {
                    // Independent finite write deadline. Cancelling the poll/request must never
                    // cause an implicit repeat of a partially written stdin buffer.
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _process.Input.WriteAsync(text.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await _process.Input.FlushAsync(deadline.Token).ConfigureAwait(false);
                    if (close) _process.CloseInput();
                    lock (_gate) { _inputClosed = close; return _inputs[id] = new(id, hash, "Accepted", close); }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    lock (_gate) _inputClosed = true;
                    _process.CloseInput(); Cancel("stdin_write_unknown");
                    lock (_gate) return _inputs[id];
                }
            }
            finally { _inputGate.Release(); }
        }
        public void Dispose() { Cancel("owner_disposed"); _process.Dispose(); }
        private sealed class Capture(int capacity)
        {
            public StringBuilder Text { get; } = new();
            public long Observed; public bool Eof, Error, Abandoned;
            public bool Truncated => Observed > Text.Length;
            public void Append(char[] buffer, int count)
            {
                Observed = checked(Observed + count);
                var keep = Math.Min(count, capacity - Text.Length);
                if (keep > 0) Text.Append(buffer, 0, keep);
            }
        }
    }
}
