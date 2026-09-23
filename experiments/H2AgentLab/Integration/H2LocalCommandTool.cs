using System.Diagnostics;
using System.Text;
using System.Text.Json;
using H2AgentLab.Tools;
using H2AgentLab.Runtime;
using H2AgentLab.Computer;

namespace H2AgentLab.Integration;

/// <summary>Only registered for an explicit task-local full-access grant. Never offered in
/// scoped/read-only modes: a working directory alone is not a process sandbox.</summary>
internal sealed class H2LocalCommandTool : IAgentRuntimeDomainVerifier
{
    private readonly Dictionary<string, (string Output, bool Passed)> _observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failedDirectories = new(StringComparer.Ordinal);
    public string DomainId => "local-command-exit";
    public bool CanVerify(ToolCall call, string output) => call.Name == "exec_command";
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context, ToolCall call, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var passed = _observed.TryGetValue(call.Id, out var result) && result.Output == output && result.Passed;
        return Task.FromResult(new AgentRuntimeDomainVerification(DomainId, passed,
            passed ? ["command-exit-zero:" + call.Id] : [], passed ? null : "Local command failed or timed out."));
    }

    internal void Register(ToolRegistry registry, SafeWorkspace workspace)
    {
        const string description = "Run a PowerShell command on this Windows machine with the user's full-access permission. "
            + "Use for local files, scripts, builds and installed applications. Network and filesystem are not sandboxed. "
            + "No administrator elevation. Inspect stdout, stderr and exit_code; exit 0 alone does not prove the user's goal. "
            + "Use literal paths; do not mix shells for file operations. Long commands are cancelled at timeout.";
        registry.Register(new ToolDescriptor("exec_command", new("shell", "Local Windows commands with explicit full access."), description,
            AgentToolRisk.High, AgentToolAccess.Mutating, false, "v1",
            JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "exec_command", description,
                parameters = new { type = "object", properties = new {
                    command = new { type = "string", description = "PowerShell script to execute." },
                    working_directory = new { type = "string", description = "Existing absolute directory or relative to the selected workspace; defaults to ." },
                    retry_of = new { type = "string", description = "failureId from a failed command being repaired. Use the same working directory and verify the intended result after correcting the command." },
                    timeout_seconds = new { type = "integer", minimum = 1, maximum = 300 } },
                    required = new[] { "command" }, additionalProperties = false } } }),
            new DelegatingToolExecutor("h2-local-command", (call, ct) => RunAsync(workspace, call, ct)),
            resourceScope: new("local-machine", "current-windows-account"), serializationKey: "local-command", canProvideVerificationEvidence: true));
    }

    private async ValueTask<string> RunAsync(SafeWorkspace workspace, ToolCall call, CancellationToken ct)
    {
        var command = H2ProductionToolSession.Arg(call, "command") ?? "";
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32000 || command.Contains('\0'))
            throw new ArgumentException("Command is empty or too long.");
        var directory = workspace.Resolve(H2ProductionToolSession.Arg(call, "working_directory") ?? ".", true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var seconds = call.Arguments.TryGetProperty("timeout_seconds", out var value) ? value.GetInt32() : 60;
        if (seconds is < 1 or > 300) throw new ArgumentOutOfRangeException("timeout_seconds");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // No profile, interactive prompt, elevation or shell-string interpolation at the host boundary.
        var script = "$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
            + command + "\nif ($LASTEXITCODE) { exit $LASTEXITCODE }";
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        foreach (var secret in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GITHUB_TOKEN", "AZURE_OPENAI_API_KEY" })
            start.Environment.Remove(secret);
        var observed = await BoundedProcessExecution.RunAsync(start,
            TimeSpan.FromSeconds(seconds), 32_000, ct).ConfigureAwait(false);
        var timedOut = observed.TimedOut;
        var passed = !timedOut && observed.RootProcessExited && observed.StreamsDrained
            && observed.ExitCode == 0;
        var retry = H2ProductionToolSession.Arg(call, "retry_of");
        var recovered = passed && retry is not null && _failedDirectories.TryGetValue(retry, out var failedDirectory)
            && string.Equals(directory, failedDirectory, StringComparison.OrdinalIgnoreCase);
        if (!passed) _failedDirectories[call.Id] = directory;
        var result = JsonSerializer.Serialize(new { ok = passed,
            failureId = passed ? null : call.Id, resolvedFailureIds = recovered ? new[] { retry } : Array.Empty<string>(),
            exit_code = observed.ExitCode, timed_out = timedOut, stdout = observed.Stdout, stderr = observed.Stderr,
            truncated = observed.StdoutTruncated || observed.StderrTruncated,
            output_complete = observed.OutputComplete, streams_drained = observed.StreamsDrained,
            root_process_exited = observed.RootProcessExited, cleanup_issue = observed.CleanupIssue,
            verification_scope = "process exit only; inspect files/application state to verify the requested outcome",
            next = passed ? "Verify the requested output and preservation."
                : timedOut ? "Read actual effects before any further write. The deadline does not prove no changes occurred; do not repeat the command."
                : "Correct the command and retry in the same working directory with retry_of='" + call.Id + "'. Do not repeat unchanged or claim completion." });
        _observed[call.Id] = (result, passed);
        return result;
    }

}
