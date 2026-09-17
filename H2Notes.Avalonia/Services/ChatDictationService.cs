using System.Runtime.InteropServices;

namespace H2Notes.Avalonia.Services;

public sealed record ChatDictationResult(bool ShortcutSent, string Message);

public interface IChatDictationService
{
    bool IsSupported { get; }
    ChatDictationResult TryOpen(nint windowHandle);
}

// Only launches the OS voice-typing UI. No audio capture, uploads, recognizers,
// privacy-setting changes, transcript callbacks or automatic sending live here.
public sealed class ChatDictationService : IChatDictationService
{
    public const string AvailabilityHint = "Micro và ngôn ngữ nhập do Windows quản lý; cần hệ thống hỗ trợ. Có thể dùng Internet/nhận dạng giọng nói trực tuyến của Microsoft theo cài đặt Windows, không theo nhà cung cấp AI đang chọn. "
        + "H2 Notes không tự ghi âm. Văn bản nằm trong bản nháp; bạn xem rồi bấm Gửi. Nếu không mở, bấm vào ô soạn và nhấn Win+H.";
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);

    public ChatDictationResult TryOpen(nint windowHandle)
    {
        if (!IsSupported) return new(false, "Windows trên máy chưa hỗ trợ phím tắt nhập giọng nói này. " + AvailabilityHint);
        try
        {
            if (windowHandle == 0 || GetForegroundWindow() != windowHandle)
                return new(false, "Cửa sổ soạn không ở phía trước; chưa mở nhập giọng nói. Bấm lại trong H2 Notes.");
            GetWindowThreadProcessId(windowHandle, out var process);
            if (process != Environment.ProcessId)
                return new(false, "Chưa mở nhập giọng nói: ô nhập không thuộc H2 Notes.");
            // Do not release/restore keys held by the user or turn Ctrl+click into another shortcut.
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x48 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
                return new(false, "Thả các phím Ctrl/Alt/Shift/Windows/H rồi bấm lại nút micro.");
            if (GetForegroundWindow() != windowHandle)
                return new(false, "Tiêu điểm đã đổi; chưa gửi Win+H. Bấm lại trong ô soạn.");
            var input = BuildShortcut();
            var sent = SendInput((uint)input.Length, input, Marshal.SizeOf<Input>());
            if (sent != input.Length)
            {
                // Release only keys inserted by our partial batch, never arbitrary user modifiers.
                var cleanup = BuildCleanup(sent);
                if (cleanup.Length > 0) SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf<Input>());
                return new(false, "Windows chưa nhận đủ phím tắt. Thả phím Windows/H rồi nhấn Win+H trong ô soạn; H2 Notes không thay đổi quyền hệ thống.");
            }
            // SendInput success does not prove speech is available or listening.
            return new(true, "Đã gửi Win+H để mở nhập giọng nói Windows. " + AvailabilityHint);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(false, "Không gọi được phím tắt Windows. " + AvailabilityHint);
        }
    }

    private static Input[] BuildShortcut() => [Key(0x5B), Key(0x48), Key(0x48, true), Key(0x5B, true)];
    private static Input[] BuildCleanup(uint sent) => sent switch
    {
        1 or 3 => [Key(0x5B, true)],
        2 => [Key(0x48, true), Key(0x5B, true)],
        _ => []
    };
    private static Input Key(ushort key, bool up = false) => new()
    { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    // Include MOUSEINPUT so INPUT has the native size/alignment (40 bytes on x64, 28 on x86).
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
