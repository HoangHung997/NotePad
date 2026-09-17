using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace H2AgentLab;

// OS-enforced AppContainer, no network capabilities, no host handles, one process.
// This is not a claim of isolation against kernel vulnerabilities or disk exhaustion.
public static class WindowsPythonSandbox
{
    public static string RuntimeRoot => ResolveRuntimeRoot(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    internal static string ResolveRuntimeRoot(string appDirectory, string localData)
    {
        var bundled = Path.Combine(appDirectory, "python");
        // A broken portable bundle must be diagnosed, not masked by this PC's runtime.
        return Directory.Exists(bundled) ? bundled : Path.Combine(localData, "H2AgentLab", "runtime", "python");
    }
    public static string? RuntimeProblem(string? root = null)
    {
        root ??= RuntimeRoot;
        var required = new[] { ".ready", "python.exe", "python312.dll", "Lib/encodings/__init__.py",
            "Lib/site-packages/docx/__init__.py", "Lib/site-packages/openpyxl/__init__.py",
            "Lib/site-packages/pypdf/__init__.py", "Lib/site-packages/pypdfium2/__init__.py",
            "Lib/site-packages/PIL/__init__.py", "Lib/site-packages/reportlab/__init__.py", "Lib/site-packages/lxml/__init__.py" };
        var missing = required.Where(p => !File.Exists(Path.Combine(root, p))).ToArray();
        return missing.Length == 0 ? null : "Thiếu bộ Python/thư viện tại " + root + ": " + string.Join(", ", missing)
            + ". Giải nén toàn bộ gói Portable, gồm thư mục python cạnh H2AgentLab.exe; không chỉ chép tệp exe.";
    }
    public static bool IsReady => RuntimeProblem() is null;
    private static readonly SemaphoreSlim Gate = new(1);
    public static async Task<int> Run(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !IsReady) throw new IOException((RuntimeProblem() ?? "Cần Windows.") + " Không chạy mã ngoài sandbox để thay thế.");
        directory = Path.GetFullPath(directory);
        _ = new SafeWorkspace(directory); _ = new SafeWorkspace(RuntimeRoot);
        await Gate.WaitAsync(cancellationToken);
        var moniker = "H2AgentLab." + Guid.NewGuid().ToString("N");
        IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, caps = IntPtr.Zero, env = IntPtr.Zero, job = IntPtr.Zero;
        ProcessInfo process = default; SecurityIdentifier? identity = null;
        FileStream? runtimeLease = null;
        var created = false;
        try
        {
            // ACL updates are read-modify-write. Serialize across Lab processes,
            // not only threads; a crashed host automatically releases this lease.
            runtimeLease = await AcquireRuntimeLease(cancellationToken);
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(moniker, moniker, "Temporary isolated H2 Agent Lab Python task", IntPtr.Zero, 0, out sid));
            created = true; identity = new SecurityIdentifier(sid);
            Grant(RuntimeRoot, identity, FileSystemRights.ReadAndExecute, false);
            Grant(directory, identity, FileSystemRights.Modify, false);
            nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Win(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            caps = Marshal.AllocHGlobal(Marshal.SizeOf<Capabilities>());
            Marshal.StructureToPtr(new Capabilities { AppContainerSid = sid }, caps, false);
            Win(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, caps, (nuint)Marshal.SizeOf<Capabilities>(), IntPtr.Zero, IntPtr.Zero));
            job = CreateJobObject(IntPtr.Zero, null); Win(job != IntPtr.Zero);
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 | 0x100 | 0x8, ActiveProcessLimit = 1 }, ProcessMemoryLimit = (nuint)(768 * 1024 * 1024) };
            Win(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["TEMP"] = Path.Combine(directory, "tmp"), ["TMP"] = Path.Combine(directory, "tmp"),
                ["USERPROFILE"] = directory, ["LOCALAPPDATA"] = directory, ["APPDATA"] = directory,
                ["SystemDrive"] = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\'),
                ["PATH"] = RuntimeRoot
            };
            env = Marshal.StringToHGlobalUni(string.Join('\0', vars.Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var start = new StartupInfoEx { Startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var exe = Path.Combine(RuntimeRoot, "python.exe");
            var command = new StringBuilder($"\"{exe}\" -I -B \"{Path.Combine(directory, "worker.py")}\"");
            Win(CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000 | 0x400 | 0x4 | 0x08000000, env, directory, ref start, out process));
            Win(AssignProcessToJobObject(job, process.Process));
            Win(ResumeThread(process.Thread) != uint.MaxValue);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                while (WaitForSingleObject(process.Process, 0) == 0x102)
                {
                    await Task.Delay(80, timeout.Token);
                    // Limits script output and ordinary disk growth, not a disk quota.
                    if (SafeSize(directory) > 128L * 1024 * 1024) throw new IOException("Tác vụ vượt giới hạn đầu ra 128 MB.");
                }
                Win(GetExitCodeProcess(process.Process, out var exit)); return unchecked((int)exit);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("Đoạn mã vượt 120 giây; đã dừng riêng tiến trình thực thi. Model có thể chia nhỏ công việc."); }
        }
        finally
        {
            if (process.Process != IntPtr.Zero) { TerminateProcess(process.Process, 1223); WaitForSingleObject(process.Process, 5000); CloseHandle(process.Process); }
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (job != IntPtr.Zero) CloseHandle(job);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (caps != IntPtr.Zero) Marshal.FreeHGlobal(caps);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
            if (identity is not null)
            {
                try { Grant(RuntimeRoot, identity, FileSystemRights.ReadAndExecute, true); } catch (Exception) { }
                try { Grant(directory, identity, FileSystemRights.Modify, true); } catch (Exception) { }
            }
            if (sid != IntPtr.Zero) FreeSid(sid);
            if (created) DeleteAppContainerProfile(moniker);
            runtimeLease?.Dispose();
            Gate.Release();
        }
    }
    private static async Task<FileStream> AcquireRuntimeLease(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(RuntimeRoot, ".execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { await Task.Delay(100, ct); }
        }
    }
    private static long SafeSize(string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        long size = 0; var count = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", options))
        { size += file.Length; if (++count > 3000 || size > 128L * 1024 * 1024) return long.MaxValue; }
        return size;
    }
    private static void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, bool remove)
    {
        var info = new DirectoryInfo(path); var acl = info.GetAccessControl();
        if (remove) acl.PurgeAccessRules(sid);
        else acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    private static void Win(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Capabilities { public IntPtr AppContainerSid, CapabilitiesPointer; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx start, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JobLimits value, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
