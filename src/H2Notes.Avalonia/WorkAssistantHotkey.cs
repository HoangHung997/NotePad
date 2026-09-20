using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace H2Notes.Avalonia;

public sealed record WorkAssistantHotkeyChord(
    uint Modifiers,
    uint VirtualKey,
    string Normalized);

public static class WorkAssistantHotkeyParser
{
    public static bool TryParse(
        string? value,
        out WorkAssistantHotkeyChord chord,
        out string? error)
    {
        chord = new(0, 0, "");
        error = null;
        var tokens = (value ?? "")
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            error = "Hotkey cần ít nhất một phím bổ trợ và một phím chính.";
            return false;
        }

        uint modifiers = 0;
        uint? key = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedModifiers = new List<string>();

        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (!seen.Add(token))
            {
                error = "Hotkey có phím lặp: " + token;
                return false;
            }

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    if ((modifiers & 0x0002) != 0) return Duplicate("Ctrl", out chord, out error);
                    modifiers |= 0x0002; normalizedModifiers.Add("Ctrl"); continue;
                case "shift":
                    if ((modifiers & 0x0004) != 0) return Duplicate("Shift", out chord, out error);
                    modifiers |= 0x0004; normalizedModifiers.Add("Shift"); continue;
                case "alt":
                    if ((modifiers & 0x0001) != 0) return Duplicate("Alt", out chord, out error);
                    modifiers |= 0x0001; normalizedModifiers.Add("Alt"); continue;
                case "win":
                case "windows":
                case "meta":
                    if ((modifiers & 0x0008) != 0) return Duplicate("Win", out chord, out error);
                    modifiers |= 0x0008; normalizedModifiers.Add("Win"); continue;
            }

            if (key is not null)
            {
                error = "Hotkey chỉ được có một phím chính.";
                return false;
            }

            if (token.Equals("space", StringComparison.OrdinalIgnoreCase))
                key = 0x20;
            else if (token.Length == 1 && char.IsLetter(token[0]))
                key = (uint)char.ToUpperInvariant(token[0]);
            else if (token.Length == 1 && char.IsDigit(token[0]))
                key = token[0];
            else if (token.Length >= 2
                     && token[0] is 'F' or 'f'
                     && int.TryParse(token[1..], out var function)
                     && function is >= 1 and <= 24)
                key = (uint)(0x70 + function - 1);
            else
            {
                error = "Phím chính chưa được hỗ trợ: " + token;
                return false;
            }
        }

        if (modifiers == 0 || key is null)
        {
            error = "Hotkey cần ít nhất một phím bổ trợ và một phím chính.";
            return false;
        }

        // MOD_NOREPEAT prevents key-repeat from opening the compact assistant repeatedly.
        modifiers |= 0x4000;
        var keyName = KeyName(key.Value);
        chord = new(
            modifiers,
            key.Value,
            string.Join("+", normalizedModifiers.Append(keyName)));
        return true;
    }

    private static bool Duplicate(
        string name,
        out WorkAssistantHotkeyChord chord,
        out string? error)
    {
        chord = new(0, 0, "");
        error = "Hotkey có phím bổ trợ lặp: " + name;
        return false;
    }

    private static string KeyName(uint key)
    {
        if (key == 0x20) return "Space";
        if (key is >= 0x70 and <= 0x87) return "F" + (key - 0x70 + 1);
        return ((char)key).ToString();
    }
}

public interface IWorkAssistantHotkeyRegistration : IDisposable
{
    bool IsSupported { get; }

    bool TryRegister(
        WorkAssistantHotkeyChord chord,
        Action onPressed,
        out string? error);

    void Unregister();
}

public sealed class WorkAssistantHotkeyController : IDisposable
{
    private readonly IWorkAssistantHotkeyRegistration _registration;
    private readonly Action _onPressed;

    public WorkAssistantHotkeyController(
        IWorkAssistantHotkeyRegistration registration,
        Action onPressed)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _onPressed = onPressed ?? throw new ArgumentNullException(nameof(onPressed));
    }

    public bool IsRegistered { get; private set; }
    public string? Error { get; private set; }
    public string? Binding { get; private set; }

    public bool Apply(string? binding)
    {
        _registration.Unregister();
        IsRegistered = false;
        Error = null;
        Binding = null;

        if (!_registration.IsSupported)
        {
            Error = "Global hotkey chưa được hỗ trợ trên hệ điều hành này.";
            return false;
        }

        if (!WorkAssistantHotkeyParser.TryParse(binding, out var chord, out var parseError))
        {
            Error = parseError;
            return false;
        }

        if (!_registration.TryRegister(chord, _onPressed, out var registerError))
        {
            Error = string.IsNullOrWhiteSpace(registerError)
                ? "Hotkey đang được ứng dụng khác sử dụng."
                : registerError;
            return false;
        }

        IsRegistered = true;
        Binding = chord.Normalized;
        return true;
    }

    public void Disable()
    {
        _registration.Unregister();
        IsRegistered = false;
        Error = null;
        Binding = null;
    }

    public void Dispose()
    {
        Disable();
        _registration.Dispose();
    }
}

internal sealed class WindowsWorkAssistantHotkeyRegistration : IWorkAssistantHotkeyRegistration
{
    private const int HotkeyId = 0x4832;
    private const uint WmHotkey = 0x0312;

    private Window? _host;
    private IntPtr _handle;
    private bool _hooked;
    private bool _registered;
    private Action? _onPressed;

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool TryRegister(
        WorkAssistantHotkeyChord chord,
        Action onPressed,
        out string? error)
    {
        error = null;
        Unregister();

        if (!IsSupported)
        {
            error = "Global hotkey chỉ khả dụng trên Windows.";
            return false;
        }

        if (!EnsureHost(out error))
            return false;

        _onPressed = onPressed;
        if (!RegisterHotKey(_handle, HotkeyId, chord.Modifiers, chord.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            _onPressed = null;
            error = code == 1409
                ? "Hotkey đang được ứng dụng khác sử dụng."
                : "Không đăng ký được hotkey Windows (Win32 " + code + ").";
            return false;
        }

        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered && _handle != IntPtr.Zero)
            UnregisterHotKey(_handle, HotkeyId);
        _registered = false;
        _onPressed = null;
    }

    public void Dispose()
    {
        Unregister();
        if (_host is not null)
        {
            if (_hooked)
                Win32Properties.RemoveWndProcHookCallback(_host, WndProc);
            _hooked = false;
            _host.Close();
            _host = null;
            _handle = IntPtr.Zero;
        }
    }

    private bool EnsureHost(out string? error)
    {
        error = null;
        if (_host is not null && _handle != IntPtr.Zero)
            return true;

        _host = new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            CanMinimize = false,
            CanMaximize = false,
            WindowDecorations = WindowDecorations.None,
            Position = new PixelPoint(-32000, -32000)
        };

        _host.Show();
        _handle = _host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero)
        {
            _host.Close();
            _host = null;
            error = "Không lấy được Windows handle cho global hotkey.";
            return false;
        }

        Win32Properties.AddWndProcHookCallback(_host, WndProc);
        _hooked = true;
        _host.Hide();
        return true;
    }

    private IntPtr WndProc(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt32() != HotkeyId)
            return IntPtr.Zero;

        handled = true;
        var callback = _onPressed;
        if (callback is not null)
            Dispatcher.UIThread.Post(callback);
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
}
