using System.Text.Json;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

/// <summary>Task-local composition and authorization. Grants are checked against tool arguments,
/// never against a namespace-wide placeholder or text produced by the model.</summary>
internal sealed partial class H2ProductionToolSession : IAgentRuntimePermissionPolicy, IDisposable
{
    private readonly Guid? _projectId;
    private readonly bool _readOnly;
    private readonly H2AgentPermissionScope? _scope;
    private readonly Func<string, string, CancellationToken, Task<bool>> _approve;
    private readonly SemaphoreSlim _approvalGate = new(1, 1);
    private readonly AsyncLocal<bool> _executingAuthorizedCall = new();
    private readonly HashSet<string> _declined = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _owned = [];
    private readonly H2AgentTaskContext? _context;
    private readonly IH2ProjectToolHost? _projects;
    private readonly Guid _taskId;
    private readonly H2HistoryRuntimeTools? _history;
    private readonly Func<string>? _jobRevision;
    private readonly CancellationToken _ownerCancellation;
    private readonly Action<ToolCall, H2AgentProcessJobInfo>? _jobObserved;
    private H2LocalCommandTool? _commands;
    internal IReadOnlyList<AgentRuntimeJobObservation> ObserveJobResults() => _commands?.ObserveJobResults() ?? [];
    private SafeWorkspace? _fileTargets;
    private readonly H2AgentTargetBindingPolicy? _targetPolicy;
    private readonly Action<H2AgentTargetResolution>? _targetObserved;
    private readonly Func<Office.IOfficeSessionClient>? _officeClientFactory;
    private readonly Func<H2ActiveWorkContext, bool>? _captureValidator;

    public H2ProductionToolSession(Guid taskId, Guid? projectId, bool readOnly,
        H2AgentTaskContext? context, IH2ProjectToolHost? projects,
        Func<string, string, CancellationToken, Task<bool>> approve,
        H2AgentTargetBindingPolicy? targetPolicy = null,
        Action<H2AgentTargetResolution>? targetObserved = null,
        Func<Office.IOfficeSessionClient>? officeClientFactory = null,
        Func<H2ActiveWorkContext, bool>? captureValidator = null, H2HistoryRuntimeTools? history = null,
        Func<string>? jobRevision = null, CancellationToken ownerCancellation = default,
        Action<ToolCall, H2AgentProcessJobInfo>? jobObserved = null, string? userGoal = null,
        Func<string, string, CancellationToken, Task<SourceApprovalReply>>? approveSource = null,
        Func<string>? sourceAuthorityStamp = null, Action<H2AgentSourceDecision>? sourceDecisionObserved = null)
    {
        _liveRequirement = H2AgentLiveResourceRequirement.FromUserRequest(userGoal, context?.ActiveWorkContext, context?.TargetIntent);
        _approveSource = approveSource; _sourceAuthorityStamp = sourceAuthorityStamp; _sourceDecisionObserved = sourceDecisionObserved;
        _jobRevision = jobRevision; _ownerCancellation = ownerCancellation; _jobObserved = jobObserved;
        _history = history; _taskId = taskId; _projectId = projectId; _readOnly = readOnly;
        _context = context; _scope = context?.PermissionScope; _projects = projects; _approve = approve;
        _targetPolicy = targetPolicy; _targetObserved = targetObserved;
        _officeClientFactory = officeClientFactory; _captureValidator = captureValidator;
    }

    public bool IsExecutingAuthorizedCall => _executingAuthorizedCall.Value;

    public ToolRegistry Configure(AgentTools tools, ToolRegistry registry,
        List<IAgentRuntimeDomainVerifier> verifiers)
    {
        _fileTargets = tools.Workspace;
        if (_projects is not null && _projectId is { } projectId && _context?.IncludeProjectContent != false)
        {
            var projectTools = new H2ProjectRuntimeTools(_projects, projectId, _taskId,
                () => IsExecutingAuthorizedCall);
            projectTools.Register(registry);
            verifiers.Add(projectTools);
        }
        H2AttachmentRuntimeTools.Register(registry, _context);
        _history?.Register(registry);
        ConfigureDomains(tools, registry, verifiers);
        RegisterSourceSelection(registry);
        if (tools.Desktop is null && _desktopBindingFailure is not null)
            registry.RegisterCapabilityNotice(new("desktop.bound_window", "Desktop actions require a current host-captured target; Full Access does not choose one.",
                new(ToolReadinessState.Unavailable, _desktopBindingFailure)));
        if (!_readOnly && _scope?.Mode == H2AgentPermissionMode.FullAccess)
        {
            var commands = new H2LocalCommandTool();
            commands.Register(registry, tools.Workspace); verifiers.Add(commands);
            _commands = commands; _owned.Add(commands);
            if (_jobRevision is not null && _jobObserved is not null)
                commands.RegisterJobs(registry, tools.Workspace, tools.StateRoot, _taskId,
                    _jobRevision, _ownerCancellation, _scope.ExpiresUtc, _jobObserved);
        }
        var wrapped = new ToolRegistry();
        foreach (var notice in registry.CapabilityNotices) wrapped.RegisterCapabilityNotice(notice);
        foreach (var descriptor in registry.Tools)
        {
            if (descriptor.Namespace.Name == "desktop" && tools.Desktop is null) continue;
            if (descriptor.Name == "open_file" && _scope?.ScopeKind == H2AgentResourceScopeKind.Workspace) continue;
            var executor = new DelegatingOutcomeToolExecutor("h2-authorized-tool", async (call, ct) =>
            {
                var permission = Check(descriptor, call);
                if (!permission.Allowed) return Rejected(call, descriptor, permission);
                var key = permission.ResourceKey ?? descriptor.Name;
                if (descriptor.IsMutating)
                {
                    await _approvalGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_declined.Contains(key)) return Rejected(call, descriptor, permission with { Allowed = false, Code = "denied", Message = "This resource was declined for this task." });
                        if (!MayAutoApprove(descriptor, call)
                            && !await _approve("Cho phép " + descriptor.Name + "?",
                                "Phạm vi: " + key + "\nThay đổi đề xuất:\n" + call.Arguments.GetRawText(), ct).ConfigureAwait(false))
                        {
                            _declined.Add(key);
                            return Rejected(call, descriptor, permission with { Allowed = false, Code = "denied", Message = "Người dùng đã từ chối thay đổi." });
                        }
                        permission = Check(descriptor, call);
                        if (!permission.Allowed) return Rejected(call, descriptor, permission);
                    }
                    finally { _approvalGate.Release(); }
                }
                ct.ThrowIfCancellationRequested();
                if (_targetPolicy is not null && _fileTargets is not null && descriptor.Namespace.Name is "files" or "autocad")
                {
                    var path = Arg(call, "path") ?? Arg(call, "destination");
                    if (path is not null && H2AgentTargetScope.TryNormalize(path, out var canonical, _fileTargets.Root))
                    {
                        // No content hash was read here; do not label this path binding ContentVerified.
                        var id = "path-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical))).ToLowerInvariant();
                        _targetObserved?.Invoke(new(new(id, H2AgentResourceKind.DiskFile, H2ApplicationKind.Unknown,
                            "filesystem", null, null, null, canonical, null, null, null, null, null, null, null, DateTime.UtcNow),
                            "resolved", _targetPolicy.IsExternal(canonical), "host-file-target"));
                    }
                }
                _executingAuthorizedCall.Value = true;
                try
                {
                    // Common metadata stays out-of-band; the domain verifier sees exact old bytes.
                    // Typed executors preserve their effect/job/completeness across this wrapper.
                    var output = descriptor.Executor is IAgentToolOutcomeExecutor typed
                        ? await typed.ExecuteOutcomeAsync(call, ct).ConfigureAwait(false)
                        : ToolOutcomeBridge.FromLegacy(call, descriptor,
                            await descriptor.Executor.ExecuteAsync(call, ct).ConfigureAwait(false));
                    output = ToolOutcomeBridge.Validate(output, call, descriptor);
                    ObserveSourceResult(descriptor, call, output);
                    return output;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or FormatException
                    or ArgumentException or InvalidOperationException or TimeoutException or KeyNotFoundException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    if (ex is H2AgentLab.Office.OfficeHostClientException { NoEffect: true } rejected)
                        return ToolOutcomeBridge.Failure(call, descriptor, rejected.Code, ToolErrorPhase.Preflight, ToolMutationEffect.None);
                    var failure = ToolOutcomeBridge.FromException(call, descriptor, ex,
                        ex is H2AgentLab.Office.OfficeHostClientException office ? office.Code : null);
                    ObserveSourceResult(descriptor, call, failure);
                    return failure;
                }
                finally { _executingAuthorizedCall.Value = false; }
            });
            wrapped.Register(new ToolDescriptor(descriptor.Name, descriptor.Namespace, descriptor.Description,
                descriptor.Risk, descriptor.Access, descriptor.SupportsParallel, descriptor.SchemaVersion,
                descriptor.CallableSchema, executor, descriptor.Provenance, descriptor.ResourceScope,
                descriptor.SerializationKey, descriptor.CanProvideVerificationEvidence, descriptor.Preference,
                descriptor.Readiness, descriptor.Limits, descriptor.Dependencies, descriptor.SupportedOperations, descriptor.ResultFormat,
                descriptor.Preflight, descriptor.ReadinessSnapshot));
        }
        return wrapped;
    }

    public ValueTask<AgentRuntimePermissionDecision> AuthorizeAsync(AgentRuntimePermissionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Check(request.Descriptor, request.Call));
    }

    public void ObserveResult(AgentRuntimePermissionRequest request, string output) { }

    private AgentRuntimePermissionDecision Check(ToolDescriptor descriptor, ToolCall call)
    {
        var key = ResourceKey(descriptor, call);
        if (_scope?.Mode == H2AgentPermissionMode.FullAccess && !_scope.HasFullAccessAt(DateTime.UtcNow))
            return AgentRuntimePermissionDecision.Deny("expired_permission", "Quyền toàn máy đã hết hạn; chọn lại quyền và gửi yêu cầu mới.", key);
        var semantics = CheckResourceSemantics(descriptor, call, key);
        if (semantics is not null) return semantics;
        var grounding = CheckFileGrounding(descriptor, call, key);
        if (grounding is not null) return grounding;
        if (!descriptor.IsMutating) return AgentRuntimePermissionDecision.Allow(key);
        if (_readOnly) return AgentRuntimePermissionDecision.Deny("permission_required", "Chế độ Chỉ đọc không cho phép thay đổi.", key);
        if (_scope is null) return AgentRuntimePermissionDecision.Allow(key); // exact call still needs approval
        if (!_scope.IsActiveAt(DateTime.UtcNow) || !_scope.MutationAllowed)
            return AgentRuntimePermissionDecision.Deny("expired_permission", "Quyền thay đổi đã hết hạn; gửi lại sau khi chọn phạm vi.", key);
        if (!MatchesScope(descriptor, call))
            return AgentRuntimePermissionDecision.Deny("outside_resource_scope", "Thao tác không thuộc tài liệu hoặc dự án được cấp quyền.", key);
        return AgentRuntimePermissionDecision.Allow(key);
    }


    // Only ordinary filesystem arguments are resolved here. Artifact-relative paths and source
    // code are not file grants; their existing domain executors keep their own validation.
    private AgentRuntimePermissionDecision? CheckFileGrounding(ToolDescriptor descriptor, ToolCall call, string key)
    {
        if (_fileTargets is null) return null;
        var targets = new List<(string Path, bool Directory)>();
        if (descriptor.Namespace.Name is "files" or "autocad")
        {
            if (Arg(call, "path") is { } path) targets.Add((path, call.Name is "list_files" or "find_files"));
            if (Arg(call, "destination") is { } destination) targets.Add((destination, false));
        }
        else if (call.Name == "publish_artifact" && Arg(call, "destination") is { } publishDestination)
            targets.Add((publishDestination, false));
        else if (call.Name == "run_python" && Arg(call, "inputs") is { } inputs)
            targets.AddRange(inputs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => (path.Trim(), false)));
        try
        {
            foreach (var target in targets) _ = _fileTargets.Resolve(target.Path, target.Directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return AgentRuntimePermissionDecision.Deny("target_not_grounded",
                "Chỉ chọn workspace hoặc đúng tệp được chỉ định. Quyền Full Access không tự chọn đích ngoài phạm vi.", key);
        }
    }

    private bool MatchesScope(ToolDescriptor descriptor, ToolCall call)
    {
        if (_scope is null) return true;
        if (_scope.HasFullAccessAt(DateTime.UtcNow)) return true;
        if (descriptor.Namespace.Name is "files" or "python" && H2AgentTargetScope.Contains(_context?.TargetPaths,
            Arg(call, "path") ?? Arg(call, "destination") ?? Arg(call, "target"))) return true;
        if (_scope.ScopeKind == H2AgentResourceScopeKind.Workspace)
        {
            // This grant selects a filesystem root, never an Office session or desktop window.
            // The existing SafeWorkspace executor performs relative-path, link and hash checks.
            if (descriptor.Namespace.Name is not ("files" or "python") || _context?.WorkspaceRoot is not { } root) return false;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(_scope.ResourceKey, "workspace:" + root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_scope.DocumentPath, root, StringComparison.OrdinalIgnoreCase);
        }
        if (descriptor.Namespace.Name == "app")
            return call.Name switch
            {
                "app.launch" => !string.IsNullOrWhiteSpace(Arg(call, "application")),
                "app.activate" => !string.IsNullOrWhiteSpace(Arg(call, "session_id")),
                _ => true
            };
        if (_scope.ScopeKind == H2AgentResourceScopeKind.Project)
            return _projectId is { } project
                && (descriptor.Namespace.Name == "h2" && Guid.TryParse(Arg(call, "project_id"), out var requested) && requested == project
                    || descriptor.Namespace.Name is "files" or "python" or "excel" or "word")
                && (_scope.ResourceKey == "h2-project:" + project.ToString("N")
                    || _scope.ResourceKey == "project:" + project.ToString("N"));
        if (_scope.ScopeKind is H2AgentResourceScopeKind.Session or H2AgentResourceScopeKind.Document)
        {
            var requested = Arg(call, "session_id") ?? Arg(call, "document_session_id");
            if (descriptor.Namespace.Name is "excel" or "word" or "autocad")
                return !string.IsNullOrWhiteSpace(_scope.DocumentSessionId)
                    && string.Equals(requested, _scope.DocumentSessionId, StringComparison.Ordinal)
                    && (_scope.ApplicationKind == H2ApplicationKind.Unknown
                        || string.Equals(descriptor.Namespace.Name, _scope.ApplicationKind.ToString(), StringComparison.OrdinalIgnoreCase));
            // A document/session grant never authorizes a generic filesystem or Python mutation.
            return false;
        }
        if (_scope.ScopeKind == H2AgentResourceScopeKind.Window)
            return descriptor.Namespace.Name == "desktop" && _selectedWindowIdentity == _scope.WindowIdentity
                && _selectedWindowIdentity is not null;
        return false;
    }

    private bool MayAutoApprove(ToolDescriptor descriptor, ToolCall call)
        => descriptor.Namespace.Name != "app"
            && _scope is { ApprovalRequired: false } && MatchesScope(descriptor, call)
            && !(_scope.Mode != H2AgentPermissionMode.FullAccess && _context?.TargetPaths?.Any(t => t.Source == "user-path"
                && H2AgentTargetScope.Contains([t], Arg(call, "path") ?? Arg(call, "destination") ?? Arg(call, "target"))) == true)
            && (_scope.HasFullAccessAt(DateTime.UtcNow) || call.Name != "replace_project_note");

    private string ResourceKey(ToolDescriptor descriptor, ToolCall call)
    {
        if (call.Name is "write_command_stdin" or "cancel_command_job")
            return call.Name + ":" + _taskId.ToString("N") + ":" + (Arg(call, "job_id") ?? "unbound");
        if (descriptor.Namespace.Name == "h2") return "h2-project:" + Arg(call, "project_id");
        if (descriptor.Namespace.Name == "desktop") return _selectedWindowIdentity ?? "unbound-window";
        if (descriptor.Namespace.Name == "app")
            return call.Name == "app.activate"
                ? "app-window:" + (Arg(call, "session_id") ?? "unbound")
                : "app:" + (Arg(call, "application") ?? "inventory").Trim().ToLowerInvariant();
        if ((Arg(call, "session_id") ?? Arg(call, "document_session_id")) is { } session)
            return descriptor.Namespace.Name + ":session:" + session;
        var path = Arg(call, "destination") ?? Arg(call, "path");
        if (path is not null && _fileTargets is not null && H2AgentTargetScope.TryNormalize(path, out var canonical, _fileTargets.Root))
            return "file:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical))).ToLowerInvariant();
        return descriptor.ResourceScope?.ScopeId ?? descriptor.Name;
    }

    internal static string? Arg(ToolCall call, string name)
        => call.Arguments.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    private static ToolExecutionOutput Rejected(ToolCall call, ToolDescriptor descriptor, AgentRuntimePermissionDecision decision)
        => ToolOutcomeBridge.Failure(call, descriptor, decision.Code, ToolErrorPhase.Preflight,
            ToolMutationEffect.None, Denied(decision));
    private static string Denied(AgentRuntimePermissionDecision decision)
        => JsonSerializer.Serialize(new { ok = false, error = decision.Code, message = decision.Message, scope = decision.ResourceKey });

    private partial void ConfigureDomains(AgentTools tools, ToolRegistry registry, List<IAgentRuntimeDomainVerifier> verifiers);
    public void Dispose()
    {
        foreach (var item in _owned) item.Dispose();
        _approvalGate.Dispose();
    }
}
