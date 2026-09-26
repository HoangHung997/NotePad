using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using H2AgentLab.OfficeProtocol;
using Microsoft.CSharp.RuntimeBinder;

namespace H2AgentLab.OfficeHost;

/// <summary>Attaches through OBJID_NATIVEOM to EXCEL7/_WwG panes on the current desktop.
/// Does not use GetActiveObject, create an Office app, activate a window or read document content.
/// COM can block despite WM_NULL; the owning OfficeHostClient hard deadline isolates that call.</summary>
public sealed class OfficeNativeWindowProbe : IOfficeWindowProbe
{
    private readonly Func<Func<nint, bool>, bool> _enumerateRoots;
    private readonly OfficeRotWindowFallback _rotFallback;

    public OfficeNativeWindowProbe() : this(EnumerateRootWindows, new OfficeRotWindowFallback()) { }

    /// <summary>Injects only top-level enumeration for deterministic boundary tests.
    /// Production still uses the same Win32/COM probe; this creates no Office application.</summary>
    public OfficeNativeWindowProbe(Func<Func<nint, bool>, bool> enumerateRoots)
        : this(enumerateRoots, new OfficeRotWindowFallback()) { }

    public OfficeNativeWindowProbe(
        Func<Func<nint, bool>, bool> enumerateRoots,
        OfficeRotWindowFallback rotFallback)
    {
        _enumerateRoots = enumerateRoots ?? throw new ArgumentNullException(nameof(enumerateRoots));
        _rotFallback = rotFallback ?? throw new ArgumentNullException(nameof(rotFallback));
    }

    private static bool EnumerateRootWindows(Func<nint, bool> visit)
        => EnumWindows((hwnd, _) => visit(hwnd), 0);

    public long ForegroundRoot => GetAncestor(GetForegroundWindow(),2).ToInt64();
    public OfficeWindowScan Enumerate(string application,long? rootHandle=null)
    {
        if(!OperatingSystem.IsWindows()) return new([], [new("unsupported_operation")],false,0);
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
            throw new OfficeHostFaultException("invalid_apartment","Office discovery requires the helper STA.",true);
        if(application is not ("excel" or "word"))
            throw new OfficeHostFaultException("invalid_arguments","Unsupported Office application.",true);
        var roots=new List<nint>();var issues=new List<OfficeDiscoveryIssue>();var truncated=false;
        try
        {
            var enumerationSucceeded=_enumerateRoots(h=>{
                if(rootHandle.HasValue && h.ToInt64()!=rootHandle.Value)return true;
                if(ClassName(h)!=(application=="excel"?"XLMAIN":"OpusApp"))return true;
                if(roots.Count==OfficeDiscoveryLimits.MaxWindows){truncated=true;return false;}
                roots.Add(h);return true;
            });
            // EnumWindows FALSE means either our deliberate limit stop or an API failure.
            // A failed enumeration is NOT proof that there are no Office windows. Do not
            // attach to a partially enumerated set and accidentally report a unique target.
            // EnumChildWindows has a different contract: its BOOL return is unused.
            if(!enumerationSucceeded && !truncated)
                return new([], [new("native_object_unavailable",rootHandle)],false,roots.Count);
        }
        catch(Exception ex) when(IsProbeFailure(ex))
        {
            // Do not include raw native/provider exception messages in discovery metadata.
            return new([], [new(FaultCode(ex),rootHandle)],false,roots.Count);
        }
        var result=new List<OfficeWindowCandidate>();var clock=Stopwatch.StartNew();
        foreach(var root in roots)
        {
            if(clock.ElapsedMilliseconds>OfficeDiscoveryLimits.EnumerationBudgetMilliseconds){truncated=true;break;}
            try
            {
                var (pid,start,session)=ProcessIdentity(root,application);
                RequireResponsive(root);
                var countBefore=result.Count;
                EnumChildWindows(root,(h,_)=>{
                    if(ClassName(h)!=(application=="excel"?"EXCEL7":"_WwG"))return true;
                    if(result.Count==OfficeDiscoveryLimits.MaxViews){truncated=true;return false;}
                    GetWindowThreadProcessId(h,out var owner);
                    if(owner!=pid || GetAncestor(h,2)!=root){issues.Add(new("stale_resource",root.ToInt64(),pid));return true;}
                    result.Add(new(application,pid,start,session,root.ToInt64(),h.ToInt64()));return true;
                },0);
                if(result.Count==countBefore)
                {
                    var fallback = _rotFallback.TryCreateCandidate(
                        application,
                        root.ToInt64(),
                        pid,
                        start,
                        session);
                    if(fallback is not null) result.Add(fallback);
                    else issues.Add(new("native_object_unavailable",root.ToInt64(),pid));
                }
            }
            catch(Exception ex) when(IsProbeFailure(ex)) { issues.Add(new(FaultCode(ex),root.ToInt64())); }
        }
        if(truncated)issues.Add(new("discovery_limit"));
        return new(result,issues,!truncated,roots.Count);
    }
    public OfficeViewLease Open(OfficeWindowCandidate candidate)
    {
        if(candidate.PaneHandle<=0)
            return _rotFallback.OpenExact(candidate);
        try
        {
            return OpenNative(candidate);
        }
        catch(Exception ex) when(IsProbeFailure(ex)
            && FaultCode(ex) is "provider_busy" or "native_object_unavailable")
        {
            // AccessibleObjectFromWindow can be unavailable on otherwise valid Office builds/views.
            // The fallback still binds the exact previously observed top-level HWND/PID/start identity;
            // it never chooses ActiveDocument by name/order.
            return _rotFallback.OpenExact(candidate);
        }
    }

    private OfficeViewLease OpenNative(OfficeWindowCandidate candidate)
    {
        var root=(nint)candidate.RootHandle;var pane=(nint)candidate.PaneHandle;
        Validate(candidate);RequireResponsive(root);
        object? view=null,app=null,document=null,sheet=null;
        try
        {
            var iid=new Guid("00020400-0000-0000-C000-000000000046"); // IID_IDispatch
            var hr=AccessibleObjectFromWindow(pane,0xFFFFFFF0,ref iid,out view);
            if(hr<0)Marshal.ThrowExceptionForHR(hr);
            if(view is null)throw new OfficeHostFaultException("native_object_unavailable","Native Office view is unavailable.",true);
            dynamic window=view;
            app=window.Application;
            long viewHandle=Handle((object)window.Hwnd);
            GetWindowThreadProcessId((nint)viewHandle,out var viewPid);
            if(viewPid!=candidate.ProcessId || GetAncestor((nint)viewHandle,2)!=root)
                throw new OfficeHostFaultException("stale_resource","Native Window does not belong to the observed process/view.",true);
            if(candidate.Application=="excel") { sheet=window.ActiveSheet;document=((dynamic)sheet).Parent; }
            else document=window.Document;
            dynamic doc=document!;
            string name=Convert.ToString((object)doc.Name,CultureInfo.InvariantCulture) ?? "";
            string path=Convert.ToString((object)doc.Path,CultureInfo.InvariantCulture) ?? "";
            string fullName=path.Length==0?name:Convert.ToString((object)doc.FullName,CultureInfo.InvariantCulture) ?? "";
            bool saved=Convert.ToBoolean((object)doc.Saved,CultureInfo.InvariantCulture);
            string version=Convert.ToString((object)((dynamic)app).Version,CultureInfo.InvariantCulture) ?? "unknown";
            Validate(candidate);
            var ownedView=view;var ownedApp=app;var ownedDocument=document!;
            var lease=new OfficeViewLease(candidate,viewHandle,ownedApp!,ownedDocument,ownedView!,name,fullName,saved,version,
                ()=>Selection(candidate.Application,ownedView!),()=>{Release(ownedDocument);Release(ownedApp);Release(ownedView);});
            view=null;app=null;document=null;return lease;
        }
        finally { Release(sheet);Release(document);Release(app);Release(view); }
    }
    private static string Selection(string kind,object view)
    {
        object? selected=null,sheet=null;
        try
        {
            selected=((dynamic)view).Selection;
            if(kind=="word")
            {
                var start=Convert.ToInt32(((dynamic)selected!).Start,CultureInfo.InvariantCulture);
                var end=Convert.ToInt32(((dynamic)selected!).End,CultureInfo.InvariantCulture);
                // Positions are sufficient to bind a selection; never copy Word plaintext into capture metadata.
                return $"word-range:{start}:{end}";
            }
            sheet=((dynamic)view).ActiveSheet;
            return Convert.ToString(((dynamic)sheet!).Name,CultureInfo.InvariantCulture)+"!"+
                Convert.ToString(((dynamic)selected!).Address[false,false],CultureInfo.InvariantCulture);
        }
        catch(RuntimeBinderException) { throw new OfficeHostFaultException("unsupported_selection","The selected object is not a supported text/cell range.",true); }
        finally { Release(sheet);Release(selected); }
    }
    private static void Validate(OfficeWindowCandidate c)
    {
        var (pid,start,session)=ProcessIdentity((nint)c.RootHandle,c.Application);
        GetWindowThreadProcessId((nint)c.PaneHandle,out var owner);
        if(pid!=c.ProcessId || start!=c.ProcessStartUtcTicks || session!=c.DesktopSessionId || owner!=pid
            || !IsWindow((nint)c.PaneHandle) || GetAncestor((nint)c.PaneHandle,2)!=(nint)c.RootHandle
            || ClassName((nint)c.PaneHandle)!=(c.Application=="excel"?"EXCEL7":"_WwG"))
            throw new OfficeHostFaultException("stale_resource","Process or view identity changed; rebind.",true);
    }
    private static (int Pid,long Start,int Session) ProcessIdentity(nint hwnd,string kind)
    {
        if(!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd,out var pid)==0)
            throw new OfficeHostFaultException("stale_resource","Office window no longer exists.",true);
        using var process=Process.GetProcessById((int)pid);
        using var self=Process.GetCurrentProcess();
        if(!string.Equals(process.ProcessName,kind=="excel"?"EXCEL":"WINWORD",StringComparison.OrdinalIgnoreCase)
            || process.SessionId!=self.SessionId)
            throw new OfficeHostFaultException("stale_resource","Window is not a supported Office process in this desktop session.",true);
        return ((int)pid,process.StartTime.ToUniversalTime().Ticks,process.SessionId);
    }
    private static void RequireResponsive(nint hwnd)
    {
        if(!IsWindowEnabled(hwnd))throw new OfficeHostFaultException("modal_blocked","Office window is disabled by a modal state.",true);
        if(SendMessageTimeout(hwnd,0,0,0,0x0002|0x0001,100,out _)==0)
            throw new OfficeHostFaultException("provider_busy","Office window did not answer a bounded readiness probe.",true);
    }
    private static long Handle(object value)
    { var handle=Convert.ToInt64(value,CultureInfo.InvariantCulture);return handle<0?unchecked((uint)handle):handle; }
    public static bool IsProbeFailure(Exception ex) => ex is OfficeHostFaultException or COMException or RuntimeBinderException
        or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException;
    public static string FaultCode(Exception ex) => ex switch {
        OfficeHostFaultException fault=>fault.Code,
        COMException com when com.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A)=>"provider_busy",
        COMException com when com.HResult is unchecked((int)0x80010108) or unchecked((int)0x800401FD)=>"stale_resource",
        UnauthorizedAccessException=>"permission_denied",_=>"native_object_unavailable" };
    private static void Release(object? value)
    {if(value is not null && Marshal.IsComObject(value)){try{Marshal.ReleaseComObject(value);}catch(InvalidComObjectException){}}}
    private static string ClassName(nint hwnd){var text=new StringBuilder(128);GetClassName(hwnd,text,text.Capacity);return text.ToString();}
    private delegate bool EnumProc(nint hwnd,nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent,EnumProc callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,StringBuilder name,int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SendMessageTimeout(nint hwnd,uint message,nuint wparam,nint lparam,uint flags,uint timeout,out nuint result);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint hwnd,uint objectId,ref Guid iid,[MarshalAs(UnmanagedType.Interface)] out object? result);
}

/// <summary>
/// Exact-window fallback for Office builds where OBJID_NATIVEOM is unavailable. It enumerates the
/// Windows Running Object Table, but accepts a document only when a COM Window.Hwnd matches the
/// already-observed top-level HWND and its PID/start/session identity also matches. It never selects
/// a document by filename, ROT order, ActiveDocument, or fuzzy title.
/// </summary>
public sealed record OfficeRotWindowIdentity(
    int ProcessId,
    long ProcessStartUtcTicks,
    int DesktopSessionId,
    long RootWindowHandle);

public interface IOfficeRunningObjectSource
{
    IReadOnlyList<object> Snapshot();
}

public sealed class OfficeRotWindowFallback
{
    private readonly IOfficeRunningObjectSource _source;
    private readonly Func<string,long,OfficeRotWindowIdentity> _identityResolver;

    public OfficeRotWindowFallback()
        : this(new WindowsOfficeRunningObjectSource(), ResolveIdentity) { }

    public OfficeRotWindowFallback(
        IOfficeRunningObjectSource source,
        Func<string,long,OfficeRotWindowIdentity> identityResolver)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
    }

    public OfficeWindowCandidate? TryCreateCandidate(
        string application,
        long rootHandle,
        int processId,
        long processStartUtcTicks,
        int desktopSessionId)
    {
        using var match = ResolveExact(
            application,
            rootHandle,
            processId,
            processStartUtcTicks,
            desktopSessionId);
        return match is null
            ? null
            : new OfficeWindowCandidate(
                application,
                processId,
                processStartUtcTicks,
                desktopSessionId,
                rootHandle,
                0);
    }

    public OfficeViewLease OpenExact(OfficeWindowCandidate candidate)
    {
        if(candidate.Application is not ("excel" or "word")
            || candidate.RootHandle<=0
            || candidate.ProcessId<=0
            || candidate.ProcessStartUtcTicks<=0)
            throw new OfficeHostFaultException(
                "invalid_arguments",
                "ROT Office binding requires an exact observed root/PID/start identity.",
                true);

        var match = ResolveExact(
            candidate.Application,
            candidate.RootHandle,
            candidate.ProcessId,
            candidate.ProcessStartUtcTicks,
            candidate.DesktopSessionId)
            ?? throw new OfficeHostFaultException(
                "native_object_unavailable",
                "The exact observed Office window is not available through the Running Object Table.",
                true);
        return match.Detach(candidate);
    }

    private RotResolvedWindow? ResolveExact(
        string application,
        long rootHandle,
        int processId,
        long processStartUtcTicks,
        int desktopSessionId)
    {
        if(application is not ("excel" or "word"))
            throw new OfficeHostFaultException("invalid_arguments","Unsupported Office application.",true);

        IReadOnlyList<object> running;
        try
        {
            running = _source.Snapshot();
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
        {
            throw new OfficeHostFaultException(
                OfficeNativeWindowProbe.FaultCode(ex),
                "Running Object Table discovery failed.",
                true);
        }

        RotResolvedWindow? selected = null;
        var sawRoot = false;
        var staleRoot = false;
        try
        {
            foreach(var sourceObject in running)
            {
                object? app=null,windows=null,window=null,document=null,sheet=null;
                try
                {
                    app = TryGetApplication(sourceObject, application);
                    if(app is null) continue;
                    windows = ((dynamic)app).Windows;
                    var count = Math.Min(
                        Convert.ToInt32(((dynamic)windows).Count, CultureInfo.InvariantCulture),
                        OfficeDiscoveryLimits.MaxViews);
                    for(var index=1;index<=count;index++)
                    {
                        window = ((dynamic)windows).Item(index);
                        var viewHandle = Handle((object)((dynamic)window).Hwnd);
                        if(viewHandle!=rootHandle)
                        {
                            Release(window);window=null;
                            continue;
                        }

                        sawRoot=true;
                        OfficeRotWindowIdentity identity;
                        try
                        {
                            identity = _identityResolver(application, viewHandle);
                        }
                        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
                        {
                            staleRoot=true;
                            Release(window);window=null;
                            continue;
                        }

                        if(identity.RootWindowHandle!=rootHandle
                            || identity.ProcessId!=processId
                            || identity.ProcessStartUtcTicks!=processStartUtcTicks
                            || identity.DesktopSessionId!=desktopSessionId)
                        {
                            staleRoot=true;
                            Release(window);window=null;
                            continue;
                        }

                        if(application=="excel")
                        {
                            sheet=((dynamic)window).ActiveSheet;
                            document=((dynamic)sheet).Parent;
                        }
                        else
                        {
                            document=((dynamic)window).Document;
                        }

                        dynamic doc=document!;
                        var name=Convert.ToString((object)doc.Name,CultureInfo.InvariantCulture) ?? "";
                        var path=Convert.ToString((object)doc.Path,CultureInfo.InvariantCulture) ?? "";
                        var fullName=path.Length==0
                            ? name
                            : Convert.ToString((object)doc.FullName,CultureInfo.InvariantCulture) ?? "";
                        var saved=Convert.ToBoolean((object)doc.Saved,CultureInfo.InvariantCulture);
                        var version=Convert.ToString((object)((dynamic)app).Version,CultureInfo.InvariantCulture) ?? "unknown";

                        selected = new RotResolvedWindow(
                            sourceObject,
                            app,
                            window,
                            document,
                            viewHandle,
                            name,
                            fullName,
                            saved,
                            version,
                            application);
                        app=null;window=null;document=null;
                        return selected;
                    }
                }
                catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
                {
                    // A single unrelated/stale ROT object must not poison an exact-root search.
                }
                finally
                {
                    Release(sheet);
                    Release(document);
                    Release(window);
                    Release(windows);
                    if(app is not null && !ReferenceEquals(app,sourceObject)) Release(app);
                }
            }
        }
        finally
        {
            foreach(var item in running)
                if(selected is null || !ReferenceEquals(item,selected.SourceObject))
                    Release(item);
        }

        if(sawRoot && staleRoot)
            throw new OfficeHostFaultException(
                "stale_resource",
                "The ROT window matched the HWND but not the observed process/start identity.",
                true);
        return null;
    }

    private static object? TryGetApplication(object candidate,string application)
    {
        try
        {
            dynamic direct=candidate;
            var name=Convert.ToString((object)direct.Name,CultureInfo.InvariantCulture) ?? "";
            if(IsApplicationName(name,application)) return candidate;
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex)) { }

        try
        {
            object app=((dynamic)candidate).Application;
            dynamic typed=app;
            var name=Convert.ToString((object)typed.Name,CultureInfo.InvariantCulture) ?? "";
            if(IsApplicationName(name,application)) return app;
            Release(app);
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex)) { }
        return null;
    }

    private static bool IsApplicationName(string name,string application)
        => application=="word"
            ? name.Contains("Word",StringComparison.OrdinalIgnoreCase)
            : name.Contains("Excel",StringComparison.OrdinalIgnoreCase);

    private static string Selection(string application,object view)
    {
        object? selected=null,sheet=null;
        try
        {
            selected=((dynamic)view).Selection;
            if(application=="word")
            {
                var start=Convert.ToInt32(((dynamic)selected!).Start,CultureInfo.InvariantCulture);
                var end=Convert.ToInt32(((dynamic)selected!).End,CultureInfo.InvariantCulture);
                return $"word-range:{start}:{end}";
            }
            sheet=((dynamic)view).ActiveSheet;
            return Convert.ToString((object)((dynamic)sheet!).Name,CultureInfo.InvariantCulture)+"!"+
                Convert.ToString((object)((dynamic)selected!).Address[false,false],CultureInfo.InvariantCulture);
        }
        catch(RuntimeBinderException)
        {
            throw new OfficeHostFaultException(
                "unsupported_selection",
                "The selected object is not a supported text/cell range.",
                true);
        }
        finally
        {
            Release(sheet);
            Release(selected);
        }
    }

    private static OfficeRotWindowIdentity ResolveIdentity(string application,long hwnd)
    {
        if(!OperatingSystem.IsWindows())
            throw new OfficeHostFaultException("unsupported_operation","Office ROT discovery requires Windows.",true);
        var handle=(nint)hwnd;
        if(!IsWindow(handle) || GetWindowThreadProcessId(handle,out var pid)==0)
            throw new OfficeHostFaultException("stale_resource","Office window no longer exists.",true);
        var root=GetAncestor(handle,2);
        using var process=Process.GetProcessById((int)pid);
        using var self=Process.GetCurrentProcess();
        if(!string.Equals(process.ProcessName,application=="excel"?"EXCEL":"WINWORD",StringComparison.OrdinalIgnoreCase)
            || process.SessionId!=self.SessionId)
            throw new OfficeHostFaultException("stale_resource","ROT window is not a supported Office process in this desktop session.",true);
        return new((int)pid,process.StartTime.ToUniversalTime().Ticks,process.SessionId,root.ToInt64());
    }

    private static long Handle(object value)
    {
        var handle=Convert.ToInt64(value,CultureInfo.InvariantCulture);
        return handle<0?unchecked((uint)handle):handle;
    }

    private static void Release(object? value)
    {
        if(value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch(InvalidComObjectException) { }
        }
    }

    private static void ReleaseDistinct(params object?[] values)
    {
        var seen=new List<object>();
        foreach(var value in values)
        {
            if(value is null || seen.Any(item=>ReferenceEquals(item,value))) continue;
            seen.Add(value);
            Release(value);
        }
    }

    private sealed class RotResolvedWindow : IDisposable
    {
        private bool _detached;
        public RotResolvedWindow(object sourceObject,object app,object window,object document,long viewHandle,
            string name,string fullName,bool saved,string version,string application)
        {
            SourceObject=sourceObject;App=app;Window=window;Document=document;ViewHandle=viewHandle;
            Name=name;FullName=fullName;Saved=saved;Version=version;Application=application;
        }
        public object SourceObject { get; }
        public object App { get; }
        public object Window { get; }
        public object Document { get; }
        public long ViewHandle { get; }
        public string Name { get; }
        public string FullName { get; }
        public bool Saved { get; }
        public string Version { get; }
        public string Application { get; }

        public OfficeViewLease Detach(OfficeWindowCandidate candidate)
        {
            _detached=true;
            return new OfficeViewLease(
                candidate,
                ViewHandle,
                App,
                Document,
                Window,
                Name,
                FullName,
                Saved,
                Version,
                ()=>Selection(Application,Window),
                ()=>ReleaseDistinct(SourceObject,Document,App,Window));
        }

        public void Dispose()
        {
            if(!_detached) ReleaseDistinct(SourceObject,Document,App,Window);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}

internal sealed class WindowsOfficeRunningObjectSource : IOfficeRunningObjectSource
{
    private const int Success=0;

    public IReadOnlyList<object> Snapshot()
    {
        if(!OperatingSystem.IsWindows()) return [];
        var result=new List<object>();
        ComTypes.IRunningObjectTable? table=null;
        ComTypes.IEnumMoniker? enumerator=null;
        try
        {
            if(GetRunningObjectTable(0,out table)!=Success || table is null) return result;
            table.EnumRunning(out enumerator);
            var monikers=new ComTypes.IMoniker[1];
            while(result.Count<OfficeDiscoveryLimits.MaxRetainedViews
                && enumerator.Next(1,monikers,IntPtr.Zero)==Success)
            {
                object? candidate=null;
                try
                {
                    table.GetObject(monikers[0],out candidate);
                    if(candidate is not null)
                    {
                        result.Add(candidate);
                        candidate=null;
                    }
                }
                finally
                {
                    Release(candidate);
                    Release(monikers[0]);
                    monikers[0]=null!;
                }
            }
            return result;
        }
        catch
        {
            foreach(var item in result) Release(item);
            throw;
        }
        finally
        {
            Release(enumerator);
            Release(table);
        }
    }

    private static void Release(object? value)
    {
        if(value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch(InvalidComObjectException) { }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(
        int reserved,
        [MarshalAs(UnmanagedType.Interface)] out ComTypes.IRunningObjectTable? runningObjectTable);
}

