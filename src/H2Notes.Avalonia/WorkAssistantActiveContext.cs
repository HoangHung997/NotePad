using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public interface IWorkAssistantActiveContextCapture
{
    H2ActiveWorkContext? Capture();
    bool Revalidate(H2ActiveWorkContext context);
}

internal sealed record WorkAssistantWindowSnapshot(
    long NativeWindowHandle,
    int ProcessId,
    long ProcessStartUtcTicks,
    string ProcessName,
    string WindowTitle);

internal interface IWorkAssistantWindowContextBackend
{
    WorkAssistantWindowSnapshot? CaptureForeground();
    WorkAssistantWindowSnapshot? InspectWindow(long nativeWindowHandle);
}

public sealed class WorkAssistantActiveContextCapture : IWorkAssistantActiveContextCapture
{
    private readonly IWorkAssistantWindowContextBackend _backend;
    private readonly Func<IH2ActiveWorkContextProvider?> _provider;

    internal WorkAssistantActiveContextCapture(
        IWorkAssistantWindowContextBackend backend,
        Func<IH2ActiveWorkContextProvider?> provider)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public WorkAssistantActiveContextCapture(
        Func<IH2ActiveWorkContextProvider?> provider)
        : this(new Win32WorkAssistantWindowContextBackend(), provider)
    {
    }

    public H2ActiveWorkContext? Capture()
    {
        var snapshot = _backend.CaptureForeground();
        if (snapshot is null || snapshot.ProcessId <= 0 || snapshot.NativeWindowHandle == 0)
            return null;

        var context = new H2ActiveWorkContext(
            snapshot.ProcessId,
            snapshot.ProcessStartUtcTicks,
            H2ActiveWorkContext.Bound(snapshot.ProcessName, 120),
            Classify(snapshot.ProcessName),
            snapshot.NativeWindowHandle,
            WindowIdentity(snapshot),
            H2ActiveWorkContext.Bound(snapshot.WindowTitle, 500),
            DocumentSessionId: null,
            DocumentPath: null,
            Selection: null,
            Provider: null,
            CapturedUtc: DateTime.UtcNow);

        var provider = _provider();
        if (provider is null)
            return context;

        try
        {
            var enrichment = provider.CaptureActiveWorkContext(context);
            if (enrichment is null)
                return context;

            return context with
            {
                ApplicationKind = enrichment.ApplicationKind ?? context.ApplicationKind,
                DocumentSessionId = H2ActiveWorkContext.BoundOrNull(enrichment.DocumentSessionId, 240),
                DocumentPath = H2ActiveWorkContext.BoundOrNull(enrichment.DocumentPath, 1_024),
                Selection = H2ActiveWorkContext.BoundOrNull(enrichment.Selection, 1_200),
                Provider = H2ActiveWorkContext.BoundOrNull(enrichment.Provider, 120)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception)
        {
            // Foreground process/window context is still useful even when an optional
            // structured provider cannot currently identify a document/session.
            return context;
        }
    }

    public bool Revalidate(H2ActiveWorkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = _backend.InspectWindow(context.NativeWindowHandle);
        if (snapshot is null
            || snapshot.ProcessId != context.ProcessId
            || snapshot.NativeWindowHandle != context.NativeWindowHandle)
            return false;

        if (context.ProcessStartUtcTicks != 0
            && snapshot.ProcessStartUtcTicks != 0
            && snapshot.ProcessStartUtcTicks != context.ProcessStartUtcTicks)
            return false;

        if (!string.Equals(
                WindowIdentity(snapshot),
                context.WindowIdentity,
                StringComparison.Ordinal))
            return false;

        var provider = _provider();
        if (provider is null)
            return true;

        try
        {
            return provider.RevalidateActiveWorkContext(context);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string WindowIdentity(WorkAssistantWindowSnapshot snapshot)
        => $"win32:{snapshot.NativeWindowHandle:x}:{snapshot.ProcessId}:{snapshot.ProcessStartUtcTicks}";

    private static H2ApplicationKind Classify(string processName)
        => (processName ?? "").Trim().ToLowerInvariant() switch
        {
            "h2notes.avalonia" or "h2notes" => H2ApplicationKind.H2Notes,
            "excel" => H2ApplicationKind.Excel,
            "winword" => H2ApplicationKind.Word,
            "powerpnt" => H2ApplicationKind.PowerPoint,
            "acad" or "acadlt" => H2ApplicationKind.AutoCAD,
            "chrome" or "msedge" or "firefox" or "brave" or "opera" => H2ApplicationKind.Browser,
            "explorer" => H2ApplicationKind.FileExplorer,
            "" => H2ApplicationKind.Unknown,
            _ => H2ApplicationKind.Other
        };
}

internal sealed class Win32WorkAssistantWindowContextBackend : IWorkAssistantWindowContextBackend
{
    public WorkAssistantWindowSnapshot? CaptureForeground()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var handle = GetForegroundWindow();
        return handle == IntPtr.Zero ? null : Inspect(handle);
    }

    public WorkAssistantWindowSnapshot? InspectWindow(long nativeWindowHandle)
    {
        if (!OperatingSystem.IsWindows() || nativeWindowHandle == 0)
            return null;

        var handle = new IntPtr(nativeWindowHandle);
        return !IsWindow(handle) ? null : Inspect(handle);
    }

    private static WorkAssistantWindowSnapshot? Inspect(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var pidRaw);
        if (pidRaw == 0 || pidRaw > int.MaxValue)
            return null;

        var pid = (int)pidRaw;
        string processName;
        long started;
        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            try { started = process.StartTime.ToUniversalTime().Ticks; }
            catch { started = 0; }
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            return null;
        }

        return new(
            handle.ToInt64(),
            pid,
            started,
            processName,
            WindowTitle(handle));
    }

    private static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0)
            return "";

        var builder = new StringBuilder(Math.Min(length + 1, 2_048));
        _ = GetWindowTextW(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);
}
