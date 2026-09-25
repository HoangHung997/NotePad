using System.Text.Json;

namespace H2AgentLab.DesktopProtocol;

public sealed record DesktopRpcRequest(string Id, string Method, JsonElement Parameters);
public sealed record DesktopRpcError(string Code, string Message);
public sealed record DesktopRpcResponse(string Id, bool Ok, JsonElement? Result, DesktopRpcError? Error);

public sealed record DesktopBounds(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y)
        => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

public sealed record DesktopWindowInfo(
    string SessionId,
    long Handle,
    int ProcessId,
    long ProcessStartedUtcTicks,
    string ProcessName,
    string Title,
    DesktopBounds Bounds,
    uint Dpi,
    bool Foreground);

public sealed record DesktopApplicationLaunchRequest(
    string Application,
    bool PermissionGranted,
    int WaitMilliseconds = 20000,
    bool RequireNewWindow = false);

public sealed record DesktopApplicationWaitRequest(
    string Application,
    int WaitMilliseconds = 10000);

public sealed record DesktopApplicationActivateRequest(
    string SessionId,
    bool PermissionGranted);

public sealed record DesktopApplicationLaunchResult(
    string RequestedApplication,
    string ApplicationId,
    string ProcessName,
    bool NewWindowObserved,
    bool ReusedExistingWindow,
    DesktopWindowInfo Window);

public sealed record DesktopElementInfo(
    string Token,
    string Name,
    string ControlType,
    string Value,
    DesktopBounds Bounds,
    bool Enabled,
    bool Offscreen,
    bool CanInvoke,
    bool CanSetValue,
    bool CanScroll,
    int Depth);

public sealed record DesktopObservation(
    DesktopWindowInfo Window,
    string StateId,
    byte[] ScreenshotPng,
    string ScreenshotSha256,
    IReadOnlyList<DesktopElementInfo> Elements,
    long ObservationSequence,
    string? ObservedMutationId);

public sealed record DesktopObserveRequest(string SessionId);

public static class DesktopActionKinds
{
    public const string Click = "click";
    public const string DoubleClick = "double_click";
    public const string Key = "key";
    public const string Type = "type";
    public const string Scroll = "scroll";
    public const string Drag = "drag";
    public const string Wait = "wait";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Click, DoubleClick, Key, Type, Scroll, Drag, Wait
    };

    public static bool IsMutating(string action)
        => action is Click or DoubleClick or Key or Type or Scroll or Drag;
}

public sealed record DesktopActionRequest(
    string SessionId,
    string StateId,
    bool PermissionGranted,
    string Action,
    string? ElementToken = null,
    string? Text = null,
    string? Key = null,
    int? X = null,
    int? Y = null,
    int? EndX = null,
    int? EndY = null,
    int? ScrollDelta = null,
    int? WaitMilliseconds = null);

public sealed record DesktopActionResult(
    string Action,
    string PriorStateId,
    long ObservationSequence,
    bool Mutated,
    bool RequiresObservation,
    string? MutationId,
    string Detail);

public sealed record DesktopPingResult(
    int ProcessId,
    string ProtocolVersion,
    bool FixtureMode,
    bool StaThread);

public static class DesktopProtocolConstants
{
    public const string Version = "1.1";
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    public const int MaxScreenshotBytes = 4 * 1024 * 1024;
    public const int MaxElements = 200;
    public const int ElementTokenLifetimeSeconds = 60;
    public const int MaxApplicationWaitMilliseconds = 30_000;
}
