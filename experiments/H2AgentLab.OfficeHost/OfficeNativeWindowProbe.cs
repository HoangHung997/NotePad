using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using H2AgentLab.OfficeProtocol;
using Microsoft.CSharp.RuntimeBinder;

namespace H2AgentLab.OfficeHost;

/// <summary>Attaches through OBJID_NATIVEOM to EXCEL7/_WwG panes on the current desktop.
/// Does not use GetActiveObject, create an Office app, activate a window or read document content.
/// COM can block despite WM_NULL; the owning OfficeHostClient hard deadline isolates that call.</summary>
public sealed class OfficeNativeWindowProbe : IOfficeWindowProbe
{
    public long ForegroundRoot => GetAncestor(GetForegroundWindow(),2).ToInt64();
    public OfficeWindowScan Enumerate(string application,long? rootHandle=null)
    {
        if(!OperatingSystem.IsWindows()) return new([], [new("unsupported_operation")],false,0);
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)
            throw new OfficeHostFaultException("invalid_apartment","Office discovery requires the helper STA.",true);
        var roots=new List<nint>();var issues=new List<OfficeDiscoveryIssue>();var truncated=false;
        EnumWindows((h,_)=>{
            if(rootHandle.HasValue && h.ToInt64()!=rootHandle.Value)return true;
            if(ClassName(h)!=(application=="excel"?"XLMAIN":"OpusApp"))return true;
            if(roots.Count==OfficeDiscoveryLimits.MaxWindows){truncated=true;return false;}
            roots.Add(h);return true;
        },0);
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
                if(result.Count==countBefore)issues.Add(new("native_object_unavailable",root.ToInt64(),pid));
            }
            catch(Exception ex) when(IsProbeFailure(ex)) { issues.Add(new(FaultCode(ex),root.ToInt64())); }
        }
        if(truncated)issues.Add(new("discovery_limit"));
        return new(result,issues,!truncated,roots.Count);
    }
    public OfficeViewLease Open(OfficeWindowCandidate candidate)
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
