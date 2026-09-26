using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace H2AgentLab.Computer;

/// <summary>Native lifetime boundary of a job in the existing process service. The process is
/// assigned at creation, before its first instruction. No name/PID-based kill or breakaway.
/// This contains ordinary CreateProcess descendants, not work delegated to an external broker,
/// an existing Office process, service or remote machine. It is not a permissions sandbox.</summary>
internal sealed class WindowsOwnedProcess : IDisposable
{
    private readonly KernelHandle _job;
    private readonly KernelHandle _process;
    private readonly KernelHandle _thread;
    private readonly NativePipe _input, _output, _error;
    private int _disposed;
    public int ProcessId { get; }
    public DateTime StartedUtc { get; }
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }

    private WindowsOwnedProcess(KernelHandle job, ProcessInformation pi,
        NativePipe input, NativePipe output, NativePipe error, DateTime startedUtc)
    {
        _job = job; _process = new(pi.Process); _thread = new(pi.Thread);
        _input = input; _output = output; _error = error; ProcessId = checked((int)pi.ProcessId);
        StartedUtc = startedUtc;
        Output = new StreamReader(output.Server, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        Error = new StreamReader(error.Server, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        Input = new StreamWriter(input.Server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = false };
    }

    public static WindowsOwnedProcess CreateSuspended(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Contained jobs require Windows 10 or later.");
        if (start.UseShellExecute || !Path.IsPathFullyQualified(start.FileName)
            || start.ArgumentList.Any(a => a.Contains('\0')) || start.Arguments.Length != 0)
            throw new ArgumentException("Job creation requires an absolute executable and explicit argument list.");
        var command = Quote(start.FileName) + " " + string.Join(" ", start.ArgumentList.Select(Quote));
        if (command.Length > 30000) throw new ArgumentOutOfRangeException(nameof(start), "Command line limit exceeded.");
        var environment = string.Join('\0', start.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Where(p => p.Value is not null).Select(p => p.Key + "=" + p.Value)) + "\0\0";
        if (environment.Length > 65536 || start.Environment.Any(p => p.Key.Contains('\0') || p.Value?.Contains('\0') == true))
            throw new ArgumentException("Invalid bounded job environment.");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw NativeError("job_create"); }
        NativePipe? input = null, output = null, error = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero, env = IntPtr.Zero;
        ProcessInformation pi = default;
        var initialized = false;
        try
        {
            var limit = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>())) throw NativeError("job_limit");
            input = new(PipeDirection.Out);
            output = new(PipeDirection.In);
            error = new(PipeDirection.In);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0 || size > 65536) throw NativeError("job_attributes_size");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw NativeError("job_attributes");
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.Client.SafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, output.Client.SafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, error.Client.SafePipeHandle.DangerousGetHandle());
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            // HANDLE_LIST and JOB_LIST: only these three inherited pipe handles; atomic job assignment.
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)
                || !UpdateProcThreadAttribute(attributes, 0, (nuint)0x2000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw NativeError("job_attributes_update");
            var startup = new StartupInfoEx { Attributes = attributes, Startup = new StartupInfo {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = input.Client.SafePipeHandle.DangerousGetHandle(),
                Output = output.Client.SafePipeHandle.DangerousGetHandle(),
                Error = error.Client.SafePipeHandle.DangerousGetHandle() } };
            env = Marshal.StringToHGlobalUni(environment);
            if (!CreateProcessW(start.FileName, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                    0x08000000 | 0x00080000 | 0x00000400 | 0x00000004, env, start.WorkingDirectory, ref startup, out pi))
                throw NativeError("job_process_create");
            if (!GetProcessTimes(pi.Process, out var created, out _, out _, out _)) throw NativeError("process_identity");
            input.Client.Dispose(); output.Client.Dispose(); error.Client.Dispose();
            var owned = new WindowsOwnedProcess(job, pi, input, output, error, DateTime.FromFileTimeUtc(created));
            pi = default; // Handle ownership has moved, including the still-suspended primary thread.
            return owned;
        }
        catch
        {
            // The job was assigned atomically even if the caller crashes before returning here.
            if (pi.Process != IntPtr.Zero) { TerminateJobObject(job, 1); CloseHandle(pi.Process); CloseHandle(pi.Thread); }
            job.Dispose(); input?.Dispose(); output?.Dispose(); error?.Dispose(); throw;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (ResumeThread(_thread) == uint.MaxValue) throw NativeError("job_resume");
        _thread.Dispose();
    }
    public uint ActiveProcesses()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!QueryInformationJobObject(_job, 1, out BasicAccounting value, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
            throw NativeError("job_accounting");
        return value.ActiveProcesses;
    }
    public (bool Exited, int? ExitCode) RootState()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var wait = WaitForSingleObject(_process, 0);
        if (wait == 0x102) return (false, null);
        if (wait != 0 || !GetExitCodeProcess(_process, out var code)) throw NativeError("job_process_state");
        return (true, unchecked((int)code));
    }
    public void Stop()
    {
        if (_disposed != 0) return;
        if (!TerminateJobObject(_job, 1)) throw NativeError("job_stop");
    }
    public void CloseInput() => _input.Dispose();
    public void CloseReaders() { _output.Dispose(); _error.Dispose(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job.Dispose(); // Closing the non-inherited owner handle terminates every remaining job member.
        _thread.Dispose(); _process.Dispose(); _input.Dispose(); _output.Dispose(); _error.Dispose();
        // Stream wrappers have no separate native handle. Never synchronously flush stdin here.
    }
    private static Exception NativeError(string operation) => new Win32Exception(Marshal.GetLastWin32Error(), operation);
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    // The parent's ends are OVERLAPPED so a blocked stdin write can be cancelled without
    // abandoning a thread in synchronous WriteFile. Child ends use ordinary blocking handles.
    private sealed class NativePipe : IDisposable
    {
        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }
        public NativePipe(PipeDirection parentDirection)
        {
            var name = "H2-owned-process-" + Guid.NewGuid().ToString("N");
            Server = new(name, parentDirection, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            Client = new(".", name, parentDirection == PipeDirection.In ? PipeDirection.Out : PipeDirection.In,
                PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.Inheritable);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var connected = Server.WaitForConnectionAsync(deadline.Token);
                Client.Connect(3000); connected.GetAwaiter().GetResult();
            }
            catch { Client.Dispose(); Server.Dispose(); throw; }
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        public KernelHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorking, MaxWorking; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    { public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int kind, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int kind, out BasicAccounting info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(KernelHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
