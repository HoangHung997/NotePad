using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace H2AgentLab.Computer;

/// <summary>
/// Shared single-command capture for the existing process service and FullAccess command tool.
/// This is NOT a job registry, sandbox, durable worker or a proof of descendant termination.
/// Output limits constrain retained characters, never the amount drained from a pipe.
/// </summary>
internal static class BoundedProcessExecution
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    internal sealed record Result(int ProcessId, DateTime? StartedUtc, int? ExitCode,
        string Stdout, string Stderr, bool StdoutTruncated, bool StderrTruncated,
        bool TimedOut, bool RootProcessExited, bool StreamsDrained,
        string? CleanupIssue, TimeSpan Duration)
    {
        public bool OutputComplete => StreamsDrained && !StdoutTruncated && !StderrTruncated;
    }

    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout,
        int maxOutputCharacters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputCharacters is < 1_000 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
        if (start.UseShellExecute || !start.RedirectStandardOutput || !start.RedirectStandardError
            || !start.RedirectStandardInput)
            throw new ArgumentException("Captured execution requires redirected pipes and no shell execution.", nameof(start));

        // Validate before dispatch. A rejected timeout/argument or an already cancelled request
        // must not create a process. Cancellation racing after Start is handled by owned cleanup.
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start the requested process.");
        var timer = Stopwatch.StartNew();
        var processId = process.Id;
        DateTime? startedUtc = null;
        try { startedUtc = process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        // The drain token is deliberately NOT the caller/request deadline. Continue draining
        // while stopping a cancelled process; otherwise a chatty child can block on a full pipe.
        using var drainStop = new CancellationTokenSource();
        var stdout = new Capture(maxOutputCharacters);
        var stderr = new Capture(maxOutputCharacters);
        var drains = Task.WhenAll(
            stdout.DrainAsync(process.StandardOutput, drainStop.Token),
            stderr.DrainAsync(process.StandardError, drainStop.Token));
        var timedOut = false;
        Exception? primary = null;
        string? cleanupIssue = null;
        try
        {
            // Interactive stdin remains opt-in future job work; short commands keep EOF.
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            // A child may inherit a pipe after its parent exits. The SAME request deadline
            // bounds this phase too; waiting for EOF without it would hang indefinitely.
            await drains.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
                primary = new OperationCanceledException("Process request cancelled; effects require separate verification.", ex, cancellationToken);
            else timedOut = true;
        }
        catch (Exception ex)
        {
            primary = ex;
        }
        finally
        {
            if (primary is not null || timedOut)
            {
                if (!HasExited(process))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException
                        or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
                    { cleanupIssue = "process_stop_unconfirmed"; }
                    using var stopDeadline = new CancellationTokenSource(StopGrace);
                    try { await process.WaitForExitAsync(stopDeadline.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                    { cleanupIssue = "process_stop_unconfirmed"; }
                }

                // Give successful termination a chance to deliver buffered output and EOF.
                try { await drains.WaitAsync(DrainGrace).ConfigureAwait(false); }
                catch (TimeoutException) { cleanupIssue ??= "output_drain_incomplete"; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException
                    or InvalidOperationException or OperationCanceledException)
                { cleanupIssue ??= "output_read_failed"; }

                if (!drains.IsCompleted)
                {
                    // Record abandonment BEFORE closing/cancelling. Some pipe implementations
                    // may complete a pending read with zero on close; that is not natural EOF.
                    stdout.AbandonPendingRead();
                    stderr.AbandonPendingRead();
                    try { drainStop.Cancel(); }
                    catch (AggregateException) { cleanupIssue ??= "output_cancel_failed"; }
                    // Closing only our read ends also releases an inherited pipe. It does NOT
                    // establish that descendants exited, and no PID discovered by name is killed.
                    CloseReadEnd(process.StandardOutput);
                    CloseReadEnd(process.StandardError);
                    try { await drains.WaitAsync(DrainGrace).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is TimeoutException or IOException
                        or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    { cleanupIssue ??= "output_drain_incomplete"; }
                }
            }
            // Observe any late reader fault without waiting without a bound. No success claim
            // depends on a continuation: StreamsDrained requires both readers to reach EOF.
            _ = drains.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var rootExited = HasExited(process);
        int? exitCode = null;
        if (rootExited)
        {
            try { exitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
        }
        var output = stdout.Snapshot();
        var error = stderr.Snapshot();
        var drained = output.Eof && error.Eof;
        if (!rootExited) cleanupIssue ??= "process_stop_unconfirmed";
        if (!drained) cleanupIssue ??= "output_drain_incomplete";
        timer.Stop();

        if (primary is not null)
        {
            // Preserve caller cancellation / the original failure, not an incidental cleanup
            // exception. These are diagnostics only, not evidence of no side effects.
            primary.Data["H2.RootProcessExited"] = rootExited;
            primary.Data["H2.StreamsDrained"] = drained;
            primary.Data["H2.CleanupIssue"] = cleanupIssue ?? "none";
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(processId, startedUtc, exitCode, output.Text, error.Text,
            output.Truncated, error.Truncated, timedOut, rootExited, drained, cleanupIssue, timer.Elapsed);
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static void CloseReadEnd(StreamReader reader)
    {
        try { reader.BaseStream.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private sealed class Capture(int limit)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new(Math.Min(limit, 8_192));
        private bool _truncated;
        private bool _eof;
        private bool _abandoned;

        public void AbandonPendingRead()
        {
            lock (_gate)
            {
                if (_eof) return;
                _abandoned = true;
                _truncated = true;
            }
        }

        public async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4_096];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (read == 0)
                        {
                            if (!_abandoned) _eof = true;
                            return;
                        }
                        var keep = Math.Min(read, limit - _text.Length);
                        if (keep > 0) _text.Append(buffer, 0, keep);
                        if (keep < read) _truncated = true;
                    }
                    // Even after the retention cap, consume subsequent bytes through EOF.
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested
                && ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                lock (_gate) _truncated = true;
            }
        }

        public (string Text, bool Truncated, bool Eof) Snapshot()
        {
            lock (_gate) return (_text.ToString(), _truncated || !_eof || _abandoned,
                _eof && !_abandoned);
        }
    }
}
