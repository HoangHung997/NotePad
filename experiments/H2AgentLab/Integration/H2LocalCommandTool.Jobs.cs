using System.Text;
using System.Text.Json;
using H2AgentLab.Computer;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed partial class H2LocalCommandTool
{
    private sealed record Registration(ToolCall Call, string Revision, string JobId)
    { public bool Delivered { get; set; } }
    private ProcessShellCapabilities? _processes;
    private ArtifactStore? _jobArtifacts;
    private Guid _owner;
    private Func<string>? _revision;
    private CancellationToken _ownerToken;
    private DateTime _grantExpiry;
    private Action<ToolCall, H2AgentProcessJobInfo>? _journalJob;
    private readonly Dictionary<Guid, Registration> _registrations = [];
    private readonly Dictionary<string, string[]> _storedOutput = new(StringComparer.Ordinal);
    private string _powershell = "";

    internal void RegisterJobs(ToolRegistry registry, SafeWorkspace workspace, string stateRoot,
        Guid owner, Func<string> revision, CancellationToken ownerToken, DateTime grantExpiry,
        Action<ToolCall, H2AgentProcessJobInfo> journal)
    {
        _owner = owner; _revision = revision; _ownerToken = ownerToken; _grantExpiry = grantExpiry; _journalJob = journal;
        _powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        _processes = new(workspace, new(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _powershell }, true, true,
            TimeSpan.FromSeconds(60), 1_000_000));
        _jobArtifacts = new(stateRoot);
        Add("start_command_job", "Start one owned PowerShell job with a stable JobId. Poll that ID, never start a replacement to wait. Full access only; no filesystem/network sandbox or elevation. Host exit cancels owned descendants, not external Office/services. Exit zero is process evidence only.", true,
            new Dictionary<string, object> { ["command"] = new { type = "string", maxLength = 2000 },
                ["working_directory"] = new { type = "string" }, ["lifetime_seconds"] = new { type = "integer", minimum = 1, maximum = 600 },
                ["idle_seconds"] = new { type = "integer", minimum = 1, maximum = 600 }, ["allow_stdin"] = new { type = "boolean", description = "Explicit opt-in for input to this job only; never send credentials or answer privilege prompts." } }, ["command"],
            (call, ct) => ValueTask.FromResult(Start(workspace, call, ct)));
        Add("poll_command_job", "Observe the same owned JobId. This request waits at most wait_seconds; request timeout does not restart or cancel the job. Continue polling or read output while Running.", false,
            new Dictionary<string, object> { ["job_id"] = new { type = "string" }, ["wait_seconds"] = new { type = "integer", minimum = 0, maximum = 5 } }, ["job_id"],
            async (call, ct) => ReadOnly(call, await _processes.PollJobAsync(_owner, OwnedId(call),
                TimeSpan.FromSeconds(Number(call, "wait_seconds", 1, 0, 5)), ct).ConfigureAwait(false)));
        Add("read_command_output", "Read a bounded page of stdout or stderr from this task's job. Preserve the returned cursor; truncated output is not complete. Terminal retained output is also in the existing ArtifactStore.", false,
            new Dictionary<string, object> { ["job_id"] = new { type = "string" }, ["stream"] = new { type = "string", @enum = new[] { "stdout", "stderr" } },
                ["cursor"] = new { type = "string" }, ["max_characters"] = new { type = "integer", minimum = 2, maximum = 8192 } }, ["job_id", "stream"],
            (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var page = _processes.ReadJobOutput(_owner, OwnedId(call), Required(call, "stream"),
                    H2ProductionToolSession.Arg(call, "cursor"), Number(call, "max_characters", 4096, 2, 8192));
                return ValueTask.FromResult(new ToolExecutionOutput(JsonSerializer.Serialize(page),
                    ToolOutcome.Success(call, ToolMutationEffect.None,
                        new(page.JobTerminal && page.NextCursor is null && page.StreamEof && !page.Truncated,
                            page.NextCursor, page.Truncated ? "retention_limit" : page.NextCursor is not null ? "paged" : null))));
            });
        Add("write_command_stdin", "Write bounded text to the opted-in owned job. Reuse input_id on a retry; changed content with the same ID is rejected. Unknown input is never replayed. This is not an interactive shell or permission escalation.", true,
            new Dictionary<string, object> { ["job_id"] = new { type = "string" }, ["input_id"] = new { type = "string", maxLength = 128 },
                ["text"] = new { type = "string", maxLength = 4096 }, ["close"] = new { type = "boolean" } }, ["job_id", "input_id", "text"],
            async (call, ct) =>
            {
                var receipt = await _processes.WriteJobInputAsync(_owner, OwnedId(call), Required(call, "input_id"),
                    Required(call, "text", allowEmpty: true), Flag(call, "close"), ct).ConfigureAwait(false);
                var raw = JsonSerializer.Serialize(receipt);
                var accepted = receipt.Status == "Accepted";
                _observed[call.Id] = (raw, accepted);
                return accepted ? new(raw, ToolOutcome.Success(call, ToolMutationEffect.Applied, new(true)))
                    : ToolOutcomeBridge.Failure(call, null, "outcome_unknown", ToolErrorPhase.Execution, ToolMutationEffect.Unknown, raw);
            });
        Add("cancel_command_job", "Request cancellation of only this task's owned process tree and observe termination. Already completed jobs retain their result. Cancellation never proves external side effects were undone.", true,
            new Dictionary<string, object> { ["job_id"] = new { type = "string" } }, ["job_id"],
            async (call, ct) =>
            {
                var snapshot = await _processes.CancelJobAsync(_owner, OwnedId(call), ct).ConfigureAwait(false);
                var raw = JsonSerializer.Serialize(snapshot);
                // Cancelling is a control effect, not evidence that the started script had no effects.
                return new(raw, ToolOutcome.Success(call, ToolMutationEffect.None, new(snapshot.Terminal)));
            });
        Add("get_command_result", "Get the observed terminal result for an owned job. Running is not a result. Artifact handles contain retained output, with explicit completeness; independently verify requested files/application state.", false,
            new Dictionary<string, object> { ["job_id"] = new { type = "string" } }, ["job_id"],
            (call, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReadOnly(call, _processes.JobResult(_owner, OwnedId(call)))); });
        return;

        void Add(string name, string description, bool mutating, Dictionary<string, object> properties,
            string[] required, Func<ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> execute)
        {
            var schema = JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description,
                parameters = new { type = "object", properties, required, additionalProperties = false } } });
            registry.Register(new ToolDescriptor(name, new("shell", "Owned Windows jobs with explicit full access."), description,
                mutating ? AgentToolRisk.High : AgentToolRisk.Low, mutating ? AgentToolAccess.Mutating : AgentToolAccess.ReadOnly,
                false, "v1", schema, new DelegatingOutcomeToolExecutor("h2-owned-command-job", execute),
                resourceScope: new("local-machine", "current-windows-account"), serializationKey: "local-command",
                canProvideVerificationEvidence: true,
                limits: new(16_384, SupportsPagination: name == "read_command_output"),
                preflight: call => ValidArguments(call.Arguments, schema.GetProperty("function").GetProperty("parameters"))
                    ? null : ToolOutcomeBridge.Failure(call, null, "invalid_arguments", ToolErrorPhase.Preflight, ToolMutationEffect.None)));
        }
    }

    private static bool ValidArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var properties = schema.GetProperty("properties");
        foreach (var property in arguments.EnumerateObject())
        {
            if (!names.Add(property.Name) || !properties.TryGetProperty(property.Name, out var spec)) return false;
            var value = property.Value;
            switch (spec.GetProperty("type").GetString())
            {
                case "string":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Contains('\0')
                        || text.Length > (spec.TryGetProperty("maxLength", out var length) ? length.GetInt32() : 2048)
                        || spec.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(x => x.GetString() == text)) return false;
                    break;
                case "integer":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)
                        || number < spec.GetProperty("minimum").GetInt32() || number > spec.GetProperty("maximum").GetInt32()) return false;
                    break;
                case "boolean": if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; break;
                default: return false;
            }
        }
        return schema.GetProperty("required").EnumerateArray().All(x => names.Contains(x.GetString()!));
    }

    private ToolExecutionOutput Start(SafeWorkspace workspace, ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); _ownerToken.ThrowIfCancellationRequested();
        var command = Required(call, "command");
        if (command.Length > 2000 || command.Contains('\0')) throw new ToolPreflightException("invalid_arguments");
        var working = workspace.Resolve(H2ProductionToolSession.Arg(call, "working_directory") ?? ".", true);
        if (!Directory.Exists(working)) throw new ToolPreflightException("resource_not_found");
        var seconds = Number(call, "lifetime_seconds", 60, 1, 600);
        var idle = Number(call, "idle_seconds", seconds, 1, seconds);
        var remaining = _grantExpiry - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new ToolPreflightException("permission_denied");
        var lifetime = TimeSpan.FromSeconds(seconds) < remaining ? TimeSpan.FromSeconds(seconds) : remaining;
        var idleTime = TimeSpan.FromSeconds(idle) < lifetime ? TimeSpan.FromSeconds(idle) : lifetime;
        var revision = _revision!();
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::InputEncoding=[Text.UTF8Encoding]::new($false); "
            + command + "\nif ($LASTEXITCODE) { exit $LASTEXITCODE }";
        var snapshot = _processes!.StartJob(_owner, revision, call.Invocation!.LogicalOperationId, _powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], working,
            lifetime, idleTime, Flag(call, "allow_stdin"), _ownerToken,
            native => _journalJob!(call, Receipt(native, [])));
        var registration = new Registration(call, revision, snapshot.JobId);
        _registrations.Add(call.Invocation.InvocationId, registration);
        if (snapshot.Terminal) _journalJob!(call, Receipt(snapshot, StoreOutput(snapshot)));
        return ExecutionOutput(registration, snapshot);
    }

    internal IReadOnlyList<AgentRuntimeJobObservation> ObserveJobResults()
    {
        if (_processes is null) return [];
        var snapshots = _processes.ObserveJobs(_owner).ToDictionary(x => x.JobId, StringComparer.Ordinal);
        var values = new List<AgentRuntimeJobObservation>();
        foreach (var entry in _registrations.Values.Where(x => !x.Delivered))
        {
            var current = snapshots[entry.JobId];
            if (!current.Terminal) continue;
            var output = ExecutionOutput(entry, current);
            _journalJob!(entry.Call, Receipt(current, StoreOutput(current)));
            entry.Delivered = true;
            values.Add(new(entry.Call.Invocation!.InvocationId, entry.JobId, entry.Revision, output));
        }
        return values;
    }

    private ToolExecutionOutput ExecutionOutput(Registration entry, ProcessJobSnapshot snapshot)
    {
        var artifacts = snapshot.Terminal ? StoreOutput(snapshot) : [];
        var raw = JsonSerializer.Serialize(new { job = snapshot, output_artifacts = artifacts,
            verification_scope = "owned process exit only; not document content, layout, or external broker effects",
            next = snapshot.Terminal ? "Read retained output; verify requested postconditions." : "Poll this JobId or read its output; do not start another process to wait." });
        var passed = snapshot.Status == "Succeeded" && snapshot.RootExited && snapshot.AllJobProcessesExited && snapshot.StreamsDrained && snapshot.ExitCode == 0;
        if (snapshot.Terminal) _observed[entry.Call.Id] = (raw, passed);
        var outcome = snapshot.Status == "Running"
            ? ToolOutcome.Success(entry.Call, ToolMutationEffect.Applied, new(false, Reason: "awaiting_job")) with { Status = ToolOutcomeStatus.Running }
            : passed ? ToolOutcome.Success(entry.Call, ToolMutationEffect.Applied, new(snapshot.OutputComplete, Reason: snapshot.OutputComplete ? null : "retention_limit"))
            : ToolOutcomeBridge.Failure(entry.Call, null, "outcome_unknown", ToolErrorPhase.Execution, ToolMutationEffect.Unknown, raw).Outcome;
        if (!passed && snapshot.Terminal)
        {
            var code = snapshot.Status == "Cancelled" ? "cancelled" : snapshot.Status == "TimedOut" ? "deadline_exceeded" : "outcome_unknown";
            outcome = ToolOutcomeBridge.Failure(entry.Call, null, code, ToolErrorPhase.Execution, ToolMutationEffect.Unknown, raw).Outcome
                with { Status = snapshot.Status == "Cancelled" ? ToolOutcomeStatus.Cancelled
                    : snapshot.Status is "TimedOut" or "Failed" ? ToolOutcomeStatus.Failed : ToolOutcomeStatus.OutcomeUnknown };
        }
        return new(raw, outcome with { Job = new(snapshot.JobId), ArtifactRefs = artifacts });
    }
    private ToolExecutionOutput ReadOnly(ToolCall call, ProcessJobSnapshot snapshot)
    {
        var artifacts = snapshot.Terminal ? StoreOutput(snapshot) : [];
        return new(JsonSerializer.Serialize(new { job = snapshot, output_artifacts = artifacts }),
            ToolOutcome.Success(call, ToolMutationEffect.None, new(true)) with { ArtifactRefs = artifacts });
    }
    private string[] StoreOutput(ProcessJobSnapshot snapshot)
    {
        if (_storedOutput.TryGetValue(snapshot.JobId, out var existing)) return existing;
        var handles = new List<string>();
        foreach (var stream in new[] { "stdout", "stderr" })
        {
            var text = new StringBuilder(); string? cursor = null;
            do
            {
                var page = _processes!.ReadJobOutput(_owner, snapshot.JobId, stream, cursor, 8192);
                text.Append(page.Text); cursor = page.NextCursor;
                if (text.Length > 1_000_000) throw new IOException("Job output retention budget exceeded.");
            } while (cursor is not null);
            var stored = _jobArtifacts!.StoreText(stream == "stdout" ? AgentArtifactKind.Stdout : AgentArtifactKind.Stderr,
                "job:" + snapshot.JobId + ":" + stream, "start_command_job", text.ToString(),
                snapshot.OutputComplete ? "Retained terminal process output." : "Retained excerpt; complete process output is unavailable.", 0);
            handles.Add(stored.Handle.Id);
        }
        return _storedOutput[snapshot.JobId] = handles.ToArray();
    }
    private static H2AgentProcessJobInfo Receipt(ProcessJobSnapshot s, IReadOnlyList<string> artifacts)
        => new(s.JobId, s.OwnerTaskId, s.GoalRevisionId, s.ProcessId, s.ProcessStartedUtc, s.DeadlineUtc,
            s.Status, s.RootExited, s.AllJobProcessesExited, s.StreamsDrained, s.OutputComplete, s.ExitCode, s.HostExitPolicy, artifacts);
    private string OwnedId(ToolCall call)
    {
        var id = Required(call, "job_id");
        if (id.Length > 128 || id.Any(char.IsControl) || !_processes!.ObserveJobs(_owner).Any(j => j.JobId == id))
            throw new ToolPreflightException("resource_not_found");
        return id;
    }
    private static string Required(ToolCall call, string name, bool allowEmpty = false)
    {
        var text = H2ProductionToolSession.Arg(call, name);
        if (text is null || !allowEmpty && string.IsNullOrWhiteSpace(text)) throw new ToolPreflightException("invalid_arguments");
        return text;
    }
    private static int Number(ToolCall call, string name, int fallback, int minimum, int maximum)
    {
        if (!call.Arguments.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum) throw new ToolPreflightException("invalid_arguments");
        return number;
    }
    private static bool Flag(ToolCall call, string name)
    {
        if (!call.Arguments.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ToolPreflightException("invalid_arguments");
        return value.GetBoolean();
    }
    public void Dispose()
    {
        if (_processes is null) return;
        try
        {
            foreach (var job in _processes.ObserveJobs(_owner).Where(x => !x.Terminal))
                _processes.CancelJobAsync(_owner, job.JobId).GetAwaiter().GetResult();
            // Best-effort terminal ownership receipt on an already-failing/cancelled run. A
            // storage failure must not prevent OS cleanup or replace the original run error.
            try { _ = ObserveJobResults(); } catch (IOException) { } catch (InvalidOperationException) { }
        }
        finally { _processes.Dispose(); _processes = null; }
    }
}
