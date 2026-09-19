namespace H2Notes.Core;

/// <summary>
/// Bounded, machine-readable state for one shared-workspace synchronization/recovery event.
/// It intentionally contains hashes/relative paths only; no project/note contents are copied here.
/// </summary>
public sealed record WorkspaceSyncDiagnostic
{
    public DateTime ObservedUtc { get; init; } = DateTime.UtcNow;
    public string Code { get; init; } = "";
    public string? FailureCode { get; init; }
    public string ExceptionType { get; init; } = "";
    public string Message { get; init; } = "";
    public string WriterId { get; init; } = "";
    public string? RelativeFile { get; init; }
    public string? ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
    public string? IndexHash { get; init; }
    public int Attempt { get; init; }
    public bool IsPersistent { get; init; }
    public bool RecoveryAvailable { get; init; }
    public bool Recovered { get; init; }
    public string? RecoverySnapshotId { get; init; }
    public string? QuarantinePath { get; init; }
}

/// <summary>
/// A validated workspace index and one of its referenced files disagree. This is distinct
/// from an ordinary transient I/O failure so the host can retry, diagnose and recover safely.
/// </summary>
public sealed class WorkspaceConsistencyException : IOException
{
    public string Code { get; }
    public string? RelativeFile { get; }
    public string? ExpectedHash { get; }
    public string? ActualHash { get; }
    public string? IndexHash { get; }

    public WorkspaceConsistencyException(
        string code,
        string message,
        string? relativeFile = null,
        string? expectedHash = null,
        string? actualHash = null,
        string? indexHash = null,
        Exception? inner = null) : base(message, inner)
    {
        Code = code;
        RelativeFile = relativeFile;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
        IndexHash = indexHash;
    }
}
