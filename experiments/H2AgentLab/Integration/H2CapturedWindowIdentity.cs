using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal static class H2CapturedWindowIdentity
{
    // Best-effort preflight only. HWNDs can be recycled between observations; the selected
    // provider/session and state preconditions remain required at native execution/readback.
    public static bool IsCurrent(H2ActiveWorkContext context)
    {
        if (!OperatingSystem.IsWindows() || context.ProcessId <= 0 || context.ProcessStartUtcTicks <= 0
            || context.NativeWindowHandle == 0
            || context.WindowIdentity != $"win32:{context.NativeWindowHandle:x}:{context.ProcessId}:{context.ProcessStartUtcTicks}") return false;
        try
        {
            using var process = Process.GetProcessById(context.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != context.ProcessStartUtcTicks) return false;
            return GetWindowThreadProcessId(new IntPtr(context.NativeWindowHandle), out var pid) != 0
                && pid == context.ProcessId;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        { return false; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
