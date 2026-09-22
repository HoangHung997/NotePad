namespace H2Notes.Core;

/// <summary>
/// H2-facing task lifecycle state. This is a product projection, not Agent runtime state.
/// </summary>
public enum H2AgentTaskStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    Completed = 3,
    Blocked = 4,
    Cancelled = 5,
    Failed = 6
}

public enum H2AgentPermissionMode
{
    ObserveOnly = 0,
    AskBeforeChanges = 1,
    AllowScopedChanges = 2,
    UseProjectPolicy = 3,
    FullAccess = 4
}

public enum H2AgentResourceScopeKind
{
    None = 0,
    Window = 1,
    Document = 2,
    Session = 3,
    Project = 4,
    Workspace = 5,
    Machine = 6
}

/// <summary>
/// Host-issued, task-local permission scope. This is an input to the Agent task, not a second
/// permission engine or durable grant store. Agent/provider permission enforcement remains authoritative.
/// </summary>
public sealed record H2AgentPermissionScope
{
    public static string CurrentMachineResourceKey => "machine:" + Environment.MachineName + ":" + Environment.UserName;
    public H2AgentPermissionScope(
        H2AgentPermissionMode mode,
        H2AgentResourceScopeKind scopeKind,
        string? resourceKey,
        bool mutationAllowed,
        bool approvalRequired,
        DateTime issuedUtc,
        DateTime expiresUtc,
        H2ApplicationKind applicationKind = H2ApplicationKind.Unknown,
        string? windowIdentity = null,
        string? documentSessionId = null,
        string? documentPath = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Enum.IsDefined(scopeKind)) throw new ArgumentOutOfRangeException(nameof(scopeKind));
        if (issuedUtc.Kind != DateTimeKind.Utc || expiresUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Permission timestamps must be UTC.");
        if (expiresUtc <= issuedUtc)
            throw new ArgumentException("Permission scope must expire after it is issued.", nameof(expiresUtc));
        if (expiresUtc - issuedUtc > TimeSpan.FromHours(1))
            throw new ArgumentException("Work Assistant permission scope lifetime is bounded to one hour.", nameof(expiresUtc));

        if (mode == H2AgentPermissionMode.ObserveOnly && mutationAllowed)
            throw new ArgumentException("Observe-only scope cannot allow mutation.", nameof(mutationAllowed));
        if (mode == H2AgentPermissionMode.AskBeforeChanges && (!mutationAllowed || !approvalRequired))
            throw new ArgumentException("Ask-before-changes requires mutation capability plus approval.", nameof(approvalRequired));
        if (mode == H2AgentPermissionMode.AllowScopedChanges && (!mutationAllowed || approvalRequired))
            throw new ArgumentException("Allow-scoped-changes must allow mutation without per-change approval.", nameof(approvalRequired));
        if (mode == H2AgentPermissionMode.FullAccess && (!mutationAllowed || approvalRequired
            || scopeKind != H2AgentResourceScopeKind.Machine || resourceKey != CurrentMachineResourceKey))
            throw new ArgumentException("Full access requires an explicit grant for the current Windows account and machine.");
        if (scopeKind == H2AgentResourceScopeKind.Machine && mode != H2AgentPermissionMode.FullAccess)
            throw new ArgumentException("Only full access can grant machine scope.");
        if (mutationAllowed && scopeKind == H2AgentResourceScopeKind.None)
            throw new ArgumentException("Mutating scope requires a concrete host-owned resource identity.", nameof(scopeKind));

        Mode = mode;
        ScopeKind = scopeKind;
        ResourceKey = BoundOrNull(resourceKey, 1_024);
        MutationAllowed = mutationAllowed;
        ApprovalRequired = approvalRequired;
        IssuedUtc = issuedUtc;
        ExpiresUtc = expiresUtc;
        ApplicationKind = applicationKind;
        WindowIdentity = BoundOrNull(windowIdentity, 400);
        DocumentSessionId = BoundOrNull(documentSessionId, 400);
        DocumentPath = BoundOrNull(documentPath, 2_048);

        if (mutationAllowed && string.IsNullOrWhiteSpace(ResourceKey))
            throw new ArgumentException("Mutating scope requires ResourceKey.", nameof(resourceKey));
    }

    public H2AgentPermissionMode Mode { get; }
    public H2AgentResourceScopeKind ScopeKind { get; }
    public string? ResourceKey { get; }
    public bool MutationAllowed { get; }
    public bool ApprovalRequired { get; }
    public DateTime IssuedUtc { get; }
    public DateTime ExpiresUtc { get; }
    public H2ApplicationKind ApplicationKind { get; }
    public string? WindowIdentity { get; }
    public string? DocumentSessionId { get; }
    public string? DocumentPath { get; }

    public bool IsActiveAt(DateTime utcNow)
        => utcNow.Kind == DateTimeKind.Utc
           && utcNow >= IssuedUtc
           && utcNow < ExpiresUtc;

    public bool HasFullAccessAt(DateTime utcNow) => Mode == H2AgentPermissionMode.FullAccess
        && ScopeKind == H2AgentResourceScopeKind.Machine && ResourceKey == CurrentMachineResourceKey
        && MutationAllowed && !ApprovalRequired && IsActiveAt(utcNow);

    public bool MatchesActiveContext(H2ActiveWorkContext context, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsActiveAt(utcNow)) return false;

        return ScopeKind switch
        {
            H2AgentResourceScopeKind.None => !MutationAllowed,
            H2AgentResourceScopeKind.Window =>
                string.Equals(WindowIdentity, context.WindowIdentity, StringComparison.Ordinal),
            H2AgentResourceScopeKind.Session =>
                !string.IsNullOrWhiteSpace(DocumentSessionId)
                && string.Equals(DocumentSessionId, context.DocumentSessionId, StringComparison.Ordinal)
                && ProviderCompatible(context),
            H2AgentResourceScopeKind.Document =>
                !string.IsNullOrWhiteSpace(DocumentPath)
                && string.Equals(
                    NormalizePath(DocumentPath),
                    NormalizePath(context.DocumentPath),
                    StringComparison.OrdinalIgnoreCase),
            H2AgentResourceScopeKind.Project => false,
            _ => false
        };
    }

    private bool ProviderCompatible(H2ActiveWorkContext context)
        => ApplicationKind == H2ApplicationKind.Unknown
           || context.ApplicationKind == ApplicationKind;

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Trim()); }
        catch { return value.Trim(); }
    }

    private static string? BoundOrNull(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return null;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed record H2AgentTaskContext(
    string? WorkspaceRoot,
    string? Summary,
    long Version = 0,
    H2AgentPermissionScope? PermissionScope = null,
    Guid? ModelProfileId = null,
    string? ReasoningEffort = null,
    IReadOnlyList<H2AgentChatTurn>? RecentTurns = null,
    IReadOnlyList<AiImage>? Images = null,
    IReadOnlyList<AiFile>? Files = null,
    IReadOnlyList<AiAttachment>? Attachments = null,
    bool IncludeProjectContent = true,
    Guid? ThreadId = null,
    Guid? TurnId = null,
    Guid? AfterTaskId = null,
    IReadOnlyList<H2AgentTargetPath>? TargetPaths = null,
    H2ActiveWorkContext? ActiveWorkContext = null,
    H2AgentTargetIntent? TargetIntent = null);

public sealed record H2AgentChatTurn(string SourceId, string Role, string Content);

public sealed record H2AgentProgress(
    long Sequence,
    DateTime AtUtc,
    string Kind,
    string Code,
    string Message)
{
    public H2AgentToolOutcome? ToolOutcome { get; init; }
    public H2AgentTargetResolution? TargetBinding { get; init; }
}

public sealed record H2AgentApproval(
    Guid ApprovalId,
    string Title,
    string Details,
    DateTime CreatedUtc);

public sealed record H2AgentEvidence(
    string EvidenceId,
    string Kind,
    string? Sha256,
    string? Summary,
    string? SourceUri = null,
    string? LocalPath = null,
    string? Provenance = null,
    bool? VerificationPassed = null);

public sealed record H2AgentTaskSummary(
    Guid TaskId,
    Guid? ProjectId,
    string Goal,
    H2AgentTaskStatus Status,
    H2AgentApproval? PendingApproval,
    IReadOnlyList<H2AgentEvidence> Evidence,
    string? FinalText,
    string? Error,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? ThreadId = null,
    Guid? TurnId = null)
{
    // Read-only Agent archive projection; never added to ProjectRecord or H2 TaskRecord.
    public H2AgentGoalSnapshot? GoalState { get; init; }
}

public sealed record H2AgentOutcomeSnapshot(string Id, string Requirement, string SourceId,
    string RevisionId, string TargetScope, string Status, string? ReplacedBy, IReadOnlyList<string> EvidenceIds);
public sealed record H2AgentGoalRevisionSnapshot(string Id, string? ParentId, int Sequence,
    string SourceId, string SourceText, IReadOnlyList<string> Added, IReadOnlyList<string> Retired);
public sealed record H2AgentGoalSnapshot(string RevisionId, IReadOnlyList<H2AgentGoalRevisionSnapshot> Revisions,
    IReadOnlyList<H2AgentOutcomeSnapshot> Outcomes, IReadOnlyList<string> MutationRevisions);

public sealed record H2AgentTaskObservation(
    H2AgentTaskSummary Summary,
    IReadOnlyList<H2AgentProgress> Progress,
    string? StreamingText = null);

public sealed record H2AgentPagePreview(byte[] Png, int PageCount, string Text, string SourceSha256);

public static class H2AgentVerification
{
    public static bool IsVerified(H2AgentTaskSummary task)
        => task.Status == H2AgentTaskStatus.Completed
            && task.Evidence.LastOrDefault(item => item.VerificationPassed.HasValue)?.VerificationPassed == true;
}

/// <summary>
/// The only Agent-shaped service surface H2 product code should consume.
/// A concrete bridge may wrap the accepted Agent public integration boundary,
/// but H2 product code must not depend on model/provider/runtime/tool internals.
/// </summary>
public interface IH2AgentAdapter
{
    IReadOnlyList<H2AgentThread> GetThreads(Guid? projectId = null) => [];
    H2AgentThread? GetThread(Guid threadId) => null;
    void SaveThread(H2AgentThread thread) { }
    bool SupplementTask(Guid taskId, Guid inputId, string text) => false;
    Task<H2AgentPagePreview> PreviewPdfAsync(string evidenceId, int page, CancellationToken cancellationToken = default)
        => Task.FromException<H2AgentPagePreview>(new NotSupportedException("Bộ xem PDF chưa khả dụng."));

    Task<H2AgentPagePreview> PreviewDocumentPdfAsync(H2AgentEvidence evidence, int page, CancellationToken cancellationToken = default)
        => PreviewPdfAsync(evidence.EvidenceId, page, cancellationToken);

    Task<Guid> StartTaskAsync(
        Guid? projectId,
        string goal,
        H2AgentTaskContext? context = null,
        bool readOnly = true,
        CancellationToken cancellationToken = default);

    H2AgentTaskObservation ObserveTask(
        Guid taskId,
        long afterSequence = -1);

    void CancelTask(Guid taskId);

    bool RespondToApproval(
        Guid taskId,
        Guid approvalId,
        bool approved);

    H2AgentTaskSummary GetTaskSummary(Guid taskId);

    IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(
        Guid? projectId = null,
        int limit = 50);

    H2AgentEvidence? GetEvidence(string evidenceId);

    bool AttachProject(
        Guid taskId,
        Guid projectId);
}
