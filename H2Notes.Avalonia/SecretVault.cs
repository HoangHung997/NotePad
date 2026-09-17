using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace H2Notes.Avalonia;

internal static class SecretVault
{
    private static string KeyPath(Guid id) => Path.Combine(LocalConfiguration.SettingsDirectory, "credentials", id.ToString("N") + ".bin");
    public static string Read(Guid id)
    {
        if (!File.Exists(KeyPath(id))) return "";
        var plain = Transform(File.ReadAllBytes(KeyPath(id)), false);
        try { return Encoding.UTF8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Save(Guid id, string key)
    {
        if (key.Length == 0) { if (File.Exists(KeyPath(id))) File.Delete(KeyPath(id)); return; }
        var bytes = Encoding.UTF8.GetBytes(key);
        try { H2Notes.Core.ProjectWorkspaceStore.AtomicWrite(KeyPath(id), Transform(bytes, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Kho khóa bảo vệ hiện hỗ trợ Windows; không lưu khóa dạng văn bản.");
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không mở được kho khóa của tài khoản Windows này.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
