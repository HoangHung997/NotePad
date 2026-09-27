using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Computer;

/// <summary>Owned disposable process tests only. No Office, model, network or user data.
/// Probe dispatch must stay before Avalonia initialization in Program.cs.</summary>
internal static class H2AgentProcessDrainTests
{
    public static void Run(Action<string, Action> test)
    {
        void Case(string name, Func<Task> body) => test("AR-040 drain " + name,
            () => Task.Run(body).GetAwaiter().GetResult());

        Case("stdout and stderr drain beyond retention caps without blocking child", async () =>
        {
            using var fixture = new Fixture();
            using var service = fixture.Service(1_024);
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("flood"),
                timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Check(!result.TimedOut && result.ExitCode == 0, "Chatty child failed or hit its deadline.");
            Check(File.Exists(fixture.File("finished-flood")), "Child did not reach its post-output marker.");
            Check(result.StdoutTruncated && result.StderrTruncated, "Retention loss was not labelled.");
            Check(result.StreamsDrained && result.RootProcessExited && !result.OutputComplete,
                "Pipe EOF, root exit and full retained output were conflated.");
            Check(result.Stdout.Length <= 1_024 + "\n[truncated]".Length
                && result.Stderr.Length <= 1_024 + "\n[truncated]".Length, "Output exceeded retention bound.");
            fixture.RecordResult("flood", result);
        });
        Case("ordinary output stays complete and exit status is exact", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("fast"))
                .ConfigureAwait(false);
            Check(result.ExitCode == 0 && !result.TimedOut && result.OutputComplete
                && result.StreamsDrained && result.RootProcessExited, "Successful capture lost its facts.");
            Check(result.Stdout == "ready" && result.Stderr.Length == 0, "Output text changed.");
            fixture.RecordResult("ordinary", result);
        });
        Case("nonzero exit is preserved rather than rewritten as success", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("exit-error"))
                .ConfigureAwait(false);
            Check(result.ExitCode == 17 && !result.TimedOut && result.Stderr == "expected-error",
                "Child exit/error was not preserved.");
            fixture.RecordResult("nonzero", result);
        });
        Case("pre-cancelled request creates no child or fixture marker", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => service.RunBoundedAsync(fixture.Executable, fixture.Arguments("touch"),
                cancellationToken: cancelled.Token), cancelled.Token).ConfigureAwait(false);
            Check(!Directory.EnumerateFiles(fixture.Root, "*.identity.json").Any()
                && !File.Exists(fixture.File("dispatched")), "Cancelled request started a process.");
            fixture.Record("pre-cancel", new { dispatched = false });
        });
        Case("invalid timeout is rejected before child dispatch", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            try
            {
                await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("touch"),
                    timeout: TimeSpan.Zero).ConfigureAwait(false);
                throw new InvalidOperationException("Invalid timeout was accepted.");
            }
            catch (ArgumentOutOfRangeException) { }
            Check(!Directory.EnumerateFiles(fixture.Root, "*.identity.json").Any()
                && !File.Exists(fixture.File("dispatched")), "Rejected timeout dispatched a child.");
            fixture.Record("preflight-timeout", new { dispatched = false });
        });
        Case("caller cancellation stops the actual child and preserves its token", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            using var cancel = new CancellationTokenSource();
            var running = service.RunBoundedAsync(fixture.Executable, fixture.Arguments("quiet"),
                timeout: TimeSpan.FromSeconds(20), cancellationToken: cancel.Token);
            await fixture.WaitFor("started-quiet").ConfigureAwait(false);
            cancel.Cancel();
            var error = await Cancelled(() => running, cancel.Token).ConfigureAwait(false);
            Check(error.Data["H2.RootProcessExited"] is true, "Cancellation did not confirm root exit.");
            Check(error.Data["H2.StreamsDrained"] is true, "Cancelled child's owned pipes did not reach EOF.");
            fixture.Record("cancel", new { rootExited = error.Data["H2.RootProcessExited"], drained = error.Data["H2.StreamsDrained"] });
            await fixture.AssertRecordedProcessesExited().ConfigureAwait(false);
        });
        Case("timeout is distinct from caller cancellation and never returns exit zero", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("quiet"),
                timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Check(File.Exists(fixture.File("started-quiet")), "Timeout probe never reached its readiness marker.");
            Check(result.TimedOut && result.ExitCode == -1 && result.RootProcessExited,
                "Deadline was reported as successful execution or unobserved root exit.");
            fixture.RecordResult("timeout", result);
            await fixture.AssertRecordedProcessesExited().ConfigureAwait(false);
        });
        Case("cancelling one owner cannot terminate a different task's process", async () =>
        {
            using var fixture = new Fixture(); using var sentinel = new Fixture();
            using var service = fixture.Service();
            using var outside = sentinel.StartDirect("quiet");
            await sentinel.WaitFor("started-quiet").ConfigureAwait(false);
            using var cancel = new CancellationTokenSource();
            var running = service.RunBoundedAsync(fixture.Executable, fixture.Arguments("quiet"),
                cancellationToken: cancel.Token);
            await fixture.WaitFor("started-quiet").ConfigureAwait(false); cancel.Cancel();
            await Cancelled(() => running, cancel.Token).ConfigureAwait(false);
            Check(!outside.HasExited, "Another owner's process was terminated.");
            fixture.Record("other-owner", new { otherProcessId = outside.Id, otherStillRunning = !outside.HasExited });
            await fixture.AssertRecordedProcessesExited().ConfigureAwait(false);
        });
        Case("owned process-tree cancellation is checked against each observed child identity", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            using var cancel = new CancellationTokenSource();
            var running = service.RunBoundedAsync(fixture.Executable, fixture.Arguments("tree"),
                cancellationToken: cancel.Token);
            await fixture.WaitFor("started-tree").ConfigureAwait(false); cancel.Cancel();
            await Cancelled(() => running, cancel.Token).ConfigureAwait(false);
            Check(Directory.EnumerateFiles(fixture.Root, "*.identity.json").Count() == 2,
                "Tree fixture did not observe both process identities.");
            fixture.Record("owned-tree", new { cancellationObserved = true });
            await fixture.AssertRecordedProcessesExited().ConfigureAwait(false);
        });
        Case("inherited pipe after root exit is bounded and not claimed as complete output", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var elapsed = Stopwatch.StartNew();
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("orphan-pipe"),
                timeout: TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(25), "Inherited-pipe cleanup exceeded its fixture bound.");
            Check(File.Exists(fixture.File("parent-exiting")), "Parent fixture did not exit normally.");
            Check(result.TimedOut && result.RootProcessExited && !result.OutputComplete && !result.StreamsDrained,
                "An open inherited pipe was reported as complete output.");
            Check(result.CleanupIssue == "output_drain_incomplete", "Incomplete pipe has no diagnostic.");
            fixture.RecordResult("inherited-pipe", result);
            // The helper deliberately does not claim it can kill a child after its parent exits.
            // The fixture owns the child's exact PID/start identity and cleans it independently.
        });
        Case("noninteractive captured command receives stdin EOF", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service();
            var result = await service.RunBoundedAsync(fixture.Executable, fixture.Arguments("stdin-eof"),
                timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Check(!result.TimedOut && result.ExitCode == 0 && result.Stdout == "EOF",
                "A short command waited indefinitely for inherited input.");
            fixture.RecordResult("stdin-eof", result);
        });
        Case("cancellation after large output is still cancellation not successful truncation", async () =>
        {
            using var fixture = new Fixture(); using var service = fixture.Service(1_024);
            using var cancel = new CancellationTokenSource();
            var running = service.RunBoundedAsync(fixture.Executable, fixture.Arguments("flood-quiet"),
                cancellationToken: cancel.Token);
            await fixture.WaitFor("finished-flood").ConfigureAwait(false); cancel.Cancel();
            fixture.Record("cancel-after-output-cap", new { retentionCap = 1024 });
            await Cancelled(() => running, cancel.Token).ConfigureAwait(false);
            await fixture.AssertRecordedProcessesExited().ConfigureAwait(false);
        });
    }

    public static int Probe(string mode, string root, string nonce)
    {
        string[] modes = ["fast", "quiet", "flood", "flood-quiet", "exit-error", "touch", "tree", "orphan-pipe", "stdin-eof"];
        if (!modes.Contains(mode, StringComparer.Ordinal) || !Guid.TryParseExact(nonce, "N", out _)) return 64;
        root = Path.GetFullPath(root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || Path.GetFileName(root) != "H2-AR040-process-" + nonce
            || File.ReadAllText(Path.Combine(root, ".fixture-owner")) != nonce) return 65;
        Console.OutputEncoding = new UTF8Encoding(false);
        using var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(root, self.Id + ".identity.json"), JsonSerializer.Serialize(
            new Identity(self.Id, self.StartTime.ToUniversalTime().Ticks)));
        File.WriteAllText(Path.Combine(root, "started-" + mode), "started");
        switch (mode)
        {
            case "fast": Console.Write("ready"); return 0;
            case "touch": File.WriteAllText(Path.Combine(root, "dispatched"), "effect"); return 0;
            case "exit-error": Console.Error.Write("expected-error"); return 17;
            case "stdin-eof": Console.Write(Console.ReadLine() is null ? "EOF" : "unexpected-input"); return 0;
            case "flood":
            case "flood-quiet":
                var block = new string('x', 4_096);
                for (var i = 0; i < 256; i++) { Console.Out.Write(block); Console.Error.Write(block); }
                Console.Out.Flush(); Console.Error.Flush();
                File.WriteAllText(Path.Combine(root, "finished-flood"), "both-streams-finished");
                if (mode == "flood-quiet") Thread.Sleep(30_000);
                return 0;
            case "tree":
            case "orphan-pipe":
                var start = MakeStart("quiet", root, nonce);
                using (var child = Process.Start(start) ?? throw new IOException("Cannot create fixture child."))
                {
                    if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "started-quiet")), 10_000)) return 66;
                    if (mode == "tree")
                    {
                        File.WriteAllText(Path.Combine(root, "tree-ready"), "ready");
                        Thread.Sleep(30_000);
                    }
                    else File.WriteAllText(Path.Combine(root, "parent-exiting"), "root-only");
                }
                return 0;
            default: Thread.Sleep(30_000); return 0;
        }
    }

    private static ProcessStartInfo MakeStart(string mode, string root, string nonce)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test host path.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in MakeArguments(mode, root, nonce)) start.ArgumentList.Add(argument);
        return start;
    }
    private static string[] MakeArguments(string mode, string root, string nonce)
    {
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(["--ar040-process-probe", mode, root, nonce]);
        return arguments.ToArray();
    }
    private static async Task<OperationCanceledException> Cancelled(Func<Task<BoundedProcessResult>> run, CancellationToken token)
    {
        try { await run().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        { Check(ex.CancellationToken == token, "Caller cancellation token changed."); return ex; }
        throw new InvalidOperationException("Expected caller cancellation, not a returned result.");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed record Identity(int ProcessId, long StartedUtcTicks);
    private sealed class Fixture : IDisposable
    {
        public string Nonce { get; } = Guid.NewGuid().ToString("N");
        public string Root { get; }
        public string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("No test host.");
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "H2-AR040-process-" + Nonce);
            Directory.CreateDirectory(Root); System.IO.File.WriteAllText(File(".fixture-owner"), Nonce);
        }
        public string File(string name) => Path.Combine(Root, name);
        public string[] Arguments(string mode) => MakeArguments(mode, Root, Nonce);
        public ProcessShellCapabilities Service(int cap = 4_096) => new(new SafeWorkspace(Root),
            new(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Executable }, true, true,
                TimeSpan.FromSeconds(20), cap));
        public Process StartDirect(string mode) => Process.Start(MakeStart(mode, Root, Nonce))
            ?? throw new IOException("Cannot create sentinel fixture.");
        public async Task WaitFor(string marker)
        {
            var timer = Stopwatch.StartNew();
            while (!System.IO.File.Exists(File(marker)))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Fixture startup marker missing: " + marker);
                await Task.Delay(25).ConfigureAwait(false);
            }
            if (marker == "started-tree") await WaitFor("tree-ready").ConfigureAwait(false);
        }
        public async Task AssertRecordedProcessesExited()
        {
            var timer = Stopwatch.StartNew();
            while (ReadIdentities().Any(IsSameLiveProcess))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("An owned fixture process remains alive.");
                await Task.Delay(25).ConfigureAwait(false);
            }
        }
        private Identity[] ReadIdentities() => Directory.EnumerateFiles(Root, "*.identity.json")
            .Select(path => JsonSerializer.Deserialize<Identity>(System.IO.File.ReadAllText(path))
                ?? throw new IOException("Invalid fixture identity.")).ToArray();
        private static bool IsSameLiveProcess(Identity identity)
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public void RecordResult(string name, BoundedProcessResult result) => Record(name, new
        {
            result.ExecutionId, result.ExitCode, result.TimedOut, result.RootProcessExited,
            result.StreamsDrained, result.OutputComplete, result.StdoutTruncated, result.StderrTruncated,
            result.CleanupIssue, stdoutCharacters = result.Stdout.Length, stderrCharacters = result.Stderr.Length
        });
        public void Record(string name, object result)
        {
            var output = Environment.GetEnvironmentVariable("H2_AR040_EVIDENCE_DIR");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            System.IO.File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(new
            {
                Case = name, ObservedUtc = DateTime.UtcNow, FixtureOwner = Nonce,
                Processes = ReadIdentities(), Observation = result,
                Boundary = "Owned disposable subprocess fixture; not native Office/model or long-job acceptance."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        public void Dispose()
        {
            // Test-only cleanup: never select processes by name or accept an unverified PID.
            foreach (var identity in ReadIdentities())
            {
                try
                {
                    using var process = Process.GetProcessById(identity.ProcessId);
                    if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks)
                    { process.Kill(entireProcessTree: true); process.WaitForExit(5_000); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
