namespace H2Notes.Core;

public enum H2ApplicationKind
{
    Unknown = 0,
    H2Notes = 1,
    Excel = 2,
    Word = 3,
    PowerPoint = 4,
    AutoCAD = 5,
    Browser = 6,
    FileExplorer = 7,
    Other = 8
}

/// <summary>
/// Ephemeral machine/session context captured before Work Assistant takes foreground focus.
/// This record is grounding metadata only; it is not project truth or an execution permission.
/// </summary>
public sealed record H2ActiveWorkContext(
    int ProcessId,
    long ProcessStartUtcTicks,
    string ProcessName,
    H2ApplicationKind ApplicationKind,
    long NativeWindowHandle,
    string WindowIdentity,
    string WindowTitle,
    string? DocumentSessionId,
    string? DocumentPath,
    string? Selection,
    string? Provider,
    DateTime CapturedUtc)
{
    public string? NativeViewIdentity { get; init; }
    public string? EnrichmentStatus { get; init; }
    public string? EnrichmentErrorCode { get; init; }
    public long? EnrichmentElapsedMilliseconds { get; init; }

    public string ToBoundedSummary()
    {
        var parts = new List<string>
        {
            "App=" + ApplicationKind,
            "Process=" + ProcessName + "#" + ProcessId,
            "Window=" + WindowTitle
        };
        if (!string.IsNullOrWhiteSpace(DocumentSessionId))
            parts.Add("Session=" + DocumentSessionId);
        if (!string.IsNullOrWhiteSpace(DocumentPath))
            parts.Add("Document=" + DocumentPath);
        if (!string.IsNullOrWhiteSpace(Selection))
            parts.Add("Selection=" + Selection);
        if (EnrichmentStatus is not null) parts.Add("OfficeCapture=" + EnrichmentStatus + ":" + EnrichmentErrorCode);
        return Bound(string.Join("\n", parts), 4_000);
    }

    internal static string Bound(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    internal static string? BoundOrNull(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return null;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed record H2ActiveWorkContextEnrichment(
    H2ApplicationKind? ApplicationKind = null,
    string? DocumentSessionId = null,
    string? DocumentPath = null,
    string? Selection = null,
    string? Provider = null)
{
    public string? NativeViewIdentity { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
    public long? ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Optional H2-facing enrichment/revalidation capability.
/// Implementations may be backed by Office/MCP/native providers, but H2 only sees this bounded contract.
/// </summary>
public interface IH2ActiveWorkContextProvider
{
    H2ActiveWorkContextEnrichment? CaptureActiveWorkContext(
        H2ActiveWorkContext foregroundContext);

    bool RevalidateActiveWorkContext(
        H2ActiveWorkContext context);
}
