using System.Diagnostics;

namespace H2AgentLab.Computer;

public sealed record ProcessShellPolicy(
    IReadOnlySet<string> AllowedExecutables,
    bool AllowStart,
    bool AllowTerminate,
    TimeSpan DefaultTimeout,
    int MaxOutputCharacters = 64_000);

public sealed record ProcessInfoSnapshot(
    int ProcessId,
    string ProcessName,
    bool HasExited,
    int? ExitCode,
    DateTime? StartedUtc,
    string Ownership);

public sealed record BoundedProcessResult(
    string ExecutionId,
    string Executable,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    TimeSpan Duration)
{
    // These describe retained output / the associated process only, not goal verification.
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
    public bool OutputComplete { get; init; }
    public bool StreamsDrained { get; init; }
    public bool RootProcessExited { get; init; }
    public string? CleanupIssue { get; init; }
}

public sealed class ProcessShellCapabilities : IDisposable
{
    private readonly global::H2AgentLab.SafeWorkspace _workspace;
    private readonly ProcessShellPolicy _policy;
    private readonly Dictionary<string, Process> _owned = new(StringComparer.Ordinal);
    private bool _disposed;

    public ProcessShellCapabilities(
        global::H2AgentLab.SafeWorkspace workspace,
        ProcessShellPolicy policy)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (_policy.DefaultTimeout <= TimeSpan.Zero
            || _policy.DefaultTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (_policy.MaxOutputCharacters is < 1_000 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    public IReadOnlyList<ProcessInfoSnapshot> List()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ownedPids = _owned.Values
            .Where(x => !SafeHasExited(x))
            .Select(x => x.Id)
            .ToHashSet();

        return Process.GetProcesses()
            .OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .Take(512)
            .Select(process =>
            {
                using (process)
                {
                    DateTime? started = null;
                    try { started = process.StartTime.ToUniversalTime(); } catch { }
                    return new ProcessInfoSnapshot(
                        process.Id,
                        process.ProcessName,
                        SafeHasExited(process),
                        SafeExitCode(process),
                        started,
                        ownedPids.Contains(process.Id) ? "host-started" : "external");
                }
            })
            .ToArray();
    }

    public ProcessInfoSnapshot Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory = ".")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_policy.AllowStart)
            throw new UnauthorizedAccessException("process.start is not allowed by current policy.");

        var normalized = ValidateExecutable(executable);
        var working = _workspace.Resolve(workingDirectory, directory: true);
        var start = BuildStart(normalized, arguments, working);
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;

        var process = Process.Start(start)
            ?? throw new IOException("Failed to start process.");
        var id = Guid.NewGuid().ToString("N");
        _owned[id] = process;

        DateTime? started = null;
        try { started = process.StartTime.ToUniversalTime(); } catch { }
        return new ProcessInfoSnapshot(
            process.Id,
            process.ProcessName,
            process.HasExited,
            process.HasExited ? process.ExitCode : null,
            started,
            "host-started:" + id);
    }

    public async Task<ProcessInfoSnapshot> WaitAsync(
        string executionId,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var process = Owned(executionId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ValidateTimeout(timeout));

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SnapshotOwned(executionId, process);
        }

        return SnapshotOwned(executionId, process);
    }

    public void Terminate(string executionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_policy.AllowTerminate)
            throw new UnauthorizedAccessException("process.terminate is not allowed by current policy.");

        var process = Owned(executionId);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
    }

    public async Task<BoundedProcessResult> RunBoundedAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory = ".",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_policy.AllowStart)
            throw new UnauthorizedAccessException("shell.run_bounded is not allowed by current policy.");

        ArgumentNullException.ThrowIfNull(arguments);
        var argumentSnapshot = arguments.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var duration = ValidateTimeout(timeout); // Reject invalid input before dispatch.
        var normalized = ValidateExecutable(executable);
        var working = _workspace.Resolve(workingDirectory, directory: true);
        var start = BuildStart(normalized, argumentSnapshot, working);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        var executionId = "proc-" + Guid.NewGuid().ToString("N");
        var observed = await BoundedProcessExecution.RunAsync(start, duration,
            _policy.MaxOutputCharacters, cancellationToken).ConfigureAwait(false);
        return new BoundedProcessResult(executionId, normalized, argumentSnapshot,
            observed.TimedOut ? -1 : observed.ExitCode ?? -1,
            observed.Stdout + (observed.StdoutTruncated ? "\n[truncated]" : ""),
            observed.Stderr + (observed.StderrTruncated ? "\n[truncated]" : ""),
            observed.TimedOut, observed.Duration)
        {
            StdoutTruncated = observed.StdoutTruncated,
            StderrTruncated = observed.StderrTruncated,
            OutputComplete = observed.OutputComplete,
            StreamsDrained = observed.StreamsDrained,
            RootProcessExited = observed.RootProcessExited,
            CleanupIssue = observed.CleanupIssue
        };
    }

    public Task<BoundedProcessResult> RunBuildAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var full = _workspace.Resolve(projectPath);
        var relative = Path.GetRelativePath(
            _workspace.Root,
            full);
        return RunBoundedAsync(
            "dotnet",
            ["build", relative, "-c", "Release", "--nologo"],
            ".",
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    public Task<BoundedProcessResult> RunTestAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var full = _workspace.Resolve(projectPath);
        var relative = Path.GetRelativePath(
            _workspace.Root,
            full);
        return RunBoundedAsync(
            "dotnet",
            ["test", relative, "-c", "Release", "--nologo"],
            ".",
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    public Task<BoundedProcessResult> RunScriptAsync(
        string executable,
        string scriptPath,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var full = _workspace.Resolve(scriptPath);
        var relative = Path.GetRelativePath(_workspace.Root, full);
        var args = new List<string> { relative };
        if (arguments is not null)
            args.AddRange(arguments);
        return RunBoundedAsync(
            executable,
            args,
            ".",
            _policy.DefaultTimeout,
            cancellationToken);
    }

    private ProcessInfoSnapshot SnapshotOwned(
        string executionId,
        Process process)
    {
        DateTime? started = null;
        try { started = process.StartTime.ToUniversalTime(); } catch { }
        return new ProcessInfoSnapshot(
            process.Id,
            process.ProcessName,
            process.HasExited,
            process.HasExited ? process.ExitCode : null,
            started,
            "host-started:" + executionId);
    }

    private Process Owned(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        return _owned.TryGetValue(executionId, out var process)
            ? process
            : throw new KeyNotFoundException("Unknown host-started process execution ID.");
    }

    private string ValidateExecutable(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var name = Path.GetFileNameWithoutExtension(executable.Trim());
        if (!_policy.AllowedExecutables.Contains(name)
            && !_policy.AllowedExecutables.Contains(executable.Trim()))
            throw new UnauthorizedAccessException(
                $"Executable '{name}' is not allowed by current process/shell policy.");
        return executable.Trim();
    }

    private static ProcessStartInfo BuildStart(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = false
        };
        foreach (var argument in arguments)
        {
            if (argument.IndexOf((char)0) >= 0 || argument.Length > 8_000)
                throw new ArgumentException("Process argument is invalid or too long.", nameof(arguments));
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("OPENAI_API_KEY");
        start.Environment.Remove("ANTHROPIC_API_KEY");
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("AZURE_OPENAI_API_KEY");
        return start;
    }

    private TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? _policy.DefaultTimeout;
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return value;
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; } catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var process in _owned.Values)
        {
            try
            {
                if (!process.HasExited && _policy.AllowTerminate)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.Dispose();
        }
        _owned.Clear();
    }
}
