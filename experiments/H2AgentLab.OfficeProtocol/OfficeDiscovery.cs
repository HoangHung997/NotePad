namespace H2AgentLab.OfficeProtocol;

/// <summary>Observed identity, not a permission grant. DocumentId is helper-lifetime local;
/// SessionId changes on Save As/close/reopen and callers must rebind, never guess continuity.</summary>
public sealed record OfficeNativeIdentity(int ProcessId, long ProcessStartUtcTicks, int DesktopSessionId,
    long RootWindowHandle, long ViewWindowHandle, long NativePaneHandle,
    string DocumentId, string ProviderVersion)
{
    public string WindowIdentity => $"win32:{RootWindowHandle:x}:{ProcessId}:{ProcessStartUtcTicks}";
    public string ViewIdentity => $"{WindowIdentity}:{ViewWindowHandle}";
}
public sealed record OfficeDiscoveryIssue(string Code, long? WindowHandle = null, int? ProcessId = null);
public sealed record OfficeDiscoveryReport(bool Complete, string Coverage, long ElapsedMilliseconds,
    int WindowsVisited, int ViewsProbed, IReadOnlyList<OfficeDiscoveryIssue> Issues);
public sealed record OfficeCaptureRequest(string Application, long WindowHandle, int ProcessId, long ProcessStartUtcTicks);
public sealed record OfficeCaptureResult(string Status, string? Code, string? SessionId, string? FullName,
    string? Selection, OfficeNativeIdentity? Identity, long ElapsedMilliseconds);

public static class OfficeDiscoveryLimits
{
    public const int MaxWindows = 128;
    public const int MaxViews = 256;
    // Shared across Excel/Word and targeted captures for one helper lifetime.
    public const int MaxRetainedViews = 2 * MaxViews;
    public const int MaxRetiredSessions = 2048;
    // Soft enumeration budget, not an interrupt guarantee for COM. Client/helper isolation is
    // the hard deadline boundary. Latencies are measured; no native timing SLA is claimed.
    public const int EnumerationBudgetMilliseconds = 2000;
    public const int CaptureDeadlineMilliseconds = 3000;
    public const int DiscoveryDeadlineMilliseconds = 10000;
    public const string Coverage = "current-desktop-native-document-windows";
}
