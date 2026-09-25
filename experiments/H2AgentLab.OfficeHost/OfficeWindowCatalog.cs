using System.Diagnostics;
using System.Runtime.InteropServices;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed record OfficeWindowCandidate(string Application, int ProcessId, long ProcessStartUtcTicks,
    int DesktopSessionId, long RootHandle, long PaneHandle);
public sealed record OfficeWindowScan(IReadOnlyList<OfficeWindowCandidate> Windows,
    IReadOnlyList<OfficeDiscoveryIssue> Issues, bool Complete, int RootsVisited);

/// <summary>OS boundary only; the production implementation never creates Office or activates a view.</summary>
public interface IOfficeWindowProbe
{
    OfficeWindowScan Enumerate(string application, long? rootHandle = null);
    OfficeViewLease Open(OfficeWindowCandidate candidate);
    long ForegroundRoot { get; }
}

/// <summary>Exactly one owned reference from each native property access. Borrowed by the backend
/// while the catalog's STA operation is running; never FinalRelease shared RCWs.</summary>
public sealed class OfficeViewLease : IDisposable
{
    private readonly Action? _release;
    private bool _disposed;
    public OfficeViewLease(OfficeWindowCandidate candidate, long viewHandle, object app, object document,
        object view, string name, string fullName, bool saved, string version, Func<string> selection, Action? release = null)
    { Candidate=candidate; ViewHandle=viewHandle; App=app; Document=document; View=view;
      Name=name; FullName=fullName; Saved=saved; Version=version; ReadSelection=selection; _release=release; }
    public OfficeWindowCandidate Candidate { get; }
    public long ViewHandle { get; }
    public object App { get; }
    public object Document { get; }
    public object View { get; }
    public string Name { get; }
    public string FullName { get; }
    public bool Saved { get; }
    public string Version { get; }
    public Func<string> ReadSelection { get; }
    public string SessionId { get; internal set; } = "";
    public OfficeNativeIdentity Identity { get; internal set; } = null!;
    public void Dispose() { if (_disposed) return; _disposed=true; _release?.Invoke(); }
}

/// <summary>Bounded helper-local COM identity cache, not an Agent store. Every lookup re-probes
/// windows and document identity; names/order/active object are never fallback locators.</summary>
public sealed class OfficeWindowCatalog : IDisposable
{
    private static readonly int[] TransientOpenRetryDelaysMilliseconds = [0, 40, 100];

    private readonly IOfficeWindowProbe _probe;
    private readonly Dictionary<string, OfficeViewLease> _views = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private bool _disposed;
    public OfficeWindowCatalog(IOfficeWindowProbe probe) => _probe=probe ?? throw new ArgumentNullException(nameof(probe));

    private OfficeViewLease OpenWithTransientRetry(OfficeWindowCandidate candidate, Stopwatch? discoveryClock = null)
    {
        Exception? last = null;
        foreach (var delay in TransientOpenRetryDelaysMilliseconds)
        {
            if (delay > 0)
            {
                if (discoveryClock is not null
                    && discoveryClock.ElapsedMilliseconds + delay >= OfficeDiscoveryLimits.EnumerationBudgetMilliseconds)
                    break;
                Thread.Sleep(delay);
            }

            try { return _probe.Open(candidate); }
            catch (Exception ex) when (OfficeNativeWindowProbe.IsProbeFailure(ex))
            {
                last = ex;
                var code = OfficeNativeWindowProbe.FaultCode(ex);
                if (code is not ("provider_busy" or "native_object_unavailable"))
                    throw;
            }
        }

        throw last ?? new OfficeHostFaultException(
            "native_object_unavailable",
            "Native Office object remained unavailable after bounded no-effect probing.",
            true);
    }
    public OfficeDiscoveryReport LastReport { get; private set; } = new(false, OfficeDiscoveryLimits.Coverage,0,0,0,[]);
    public IReadOnlyList<OfficeViewLease> Refresh(string application, long? rootHandle = null)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if (application is not ("excel" or "word")) throw new OfficeHostFaultException("invalid_arguments","Unsupported Office application.",true);
        var clock=Stopwatch.StartNew(); var scan=_probe.Enumerate(application,rootHandle);
        var issues=scan.Issues.Take(OfficeDiscoveryLimits.MaxWindows).ToList();
        var next=new Dictionary<string,OfficeViewLease>(StringComparer.Ordinal);
        var ambiguous=new HashSet<string>(StringComparer.Ordinal);
        var probed=0;
        try
        {
            foreach(var candidate in scan.Windows)
            {
                if (probed>=OfficeDiscoveryLimits.MaxViews || clock.ElapsedMilliseconds>OfficeDiscoveryLimits.EnumerationBudgetMilliseconds)
                { issues.Add(new("discovery_limit")); break; }
                OfficeViewLease? view=null;
                try
                {
                    probed++;
                    view=OpenWithTransientRetry(candidate, clock);
                    if (view.Candidate != candidate || view.ViewHandle<=0 || candidate.ProcessId<=0 || candidate.ProcessStartUtcTicks<=0
                        || string.IsNullOrWhiteSpace(view.Name) || view.Name.Length>512 || view.FullName.Length>4096 || view.Version.Length>128)
                        throw new OfficeHostFaultException("stale_resource","Native view metadata did not match its window.",true);
                    var key=$"{application}:{candidate.ProcessId}:{candidate.ProcessStartUtcTicks}:{candidate.RootHandle}:{view.ViewHandle}";
                    if (ambiguous.Contains(key)) { view.Dispose(); continue; }
                    if (next.TryGetValue(key,out var duplicate))
                    {
                        if (!SameObject(duplicate.Document,view.Document) || duplicate.FullName!=view.FullName)
                        { next.Remove(key); duplicate.Dispose(); ambiguous.Add(key); issues.Add(new("ambiguous_target",candidate.RootHandle,candidate.ProcessId)); }
                        view.Dispose(); continue;
                    }
                    _views.TryGetValue(key,out var previous);
                    var same=previous is not null && SameObject(previous.Document,view.Document) && previous.FullName==view.FullName && previous.Name==view.Name;
                    var baseId=OfficeHostSafety.StableSessionId("native-v2",key,view.Name,view.FullName);
                    // Close/reopen or temporarily lost views cannot resurrect a retired handle.
                    var session=same ? previous!.SessionId : _retired.Contains(baseId) || previous is not null
                        ? baseId+"-"+Guid.NewGuid().ToString("N")[..12] : baseId;
                    var sameDocument=next.Values.Concat(_views.Values).FirstOrDefault(v=>v.Candidate.ProcessId==candidate.ProcessId
                        && v.Candidate.ProcessStartUtcTicks==candidate.ProcessStartUtcTicks && SameObject(v.Document,view.Document));
                    view.SessionId=session;
                    view.Identity=new(candidate.ProcessId,candidate.ProcessStartUtcTicks,candidate.DesktopSessionId,
                        candidate.RootHandle,view.ViewHandle,candidate.PaneHandle,sameDocument?.Identity.DocumentId ?? "doc-"+Guid.NewGuid().ToString("N"),view.Version);
                    next.Add(key,view); view=null;
                }
                catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
                { view?.Dispose(); issues.Add(new(OfficeNativeWindowProbe.FaultCode(ex),candidate.RootHandle,candidate.ProcessId)); }
            }
            // A transient probe failure is not evidence that a previously observed document
            // closed. Keep its bounded owned reference ONLY for future identity comparison;
            // never return it as a current observation or dispatch through it without re-probing.
            // A successful absence, ambiguity or stale identity still retires the old binding.
            bool RetainUnobserved(KeyValuePair<string, OfficeViewLease> pair)
            {
                if (next.ContainsKey(pair.Key) || ambiguous.Contains(pair.Key)) return false;
                var old = pair.Value.Candidate;
                var relevant = issues.Where(i => i.WindowHandle is null || i.WindowHandle == old.RootHandle).ToArray();
                if (relevant.Any(i => i.Code is "stale_resource" or "ambiguous_target" or "permission_denied")) return false;
                return !scan.Complete || relevant.Any(i => i.Code is "native_object_unavailable"
                    or "provider_busy" or "modal_blocked" or "discovery_limit");
            }
            var replaced = _views.Where(p=>p.Value.Candidate.Application==application
                && (rootHandle is null || p.Value.Candidate.RootHandle==rootHandle)
                && !RetainUnobserved(p)).ToArray();
            // A targeted refresh replaces only one root. Without a lifetime-wide bound,
            // closed roots from successive captures would retain COM references forever.
            // Reject BEFORE mutating the accepted cache; the finally block releases the
            // new probe leases. Full discovery can reclaim closed roots, or the capture
            // owner can retire just its helper. Never evict identity/tombstone history.
            if (_views.Count - replaced.Length + next.Count > OfficeDiscoveryLimits.MaxRetainedViews)
            {
                LastReport=new(false,OfficeDiscoveryLimits.Coverage,clock.ElapsedMilliseconds,
                    scan.RootsVisited,probed,[new("session_capacity",rootHandle)]);
                throw new OfficeHostFaultException("session_capacity",
                    "The native view cache reached its bound. Rediscover or rebind through a new owned helper.",true);
            }
            foreach(var pair in replaced)
            {
                if (!next.TryGetValue(pair.Key,out var replacement) || replacement.SessionId!=pair.Value.SessionId)
                {
                    // A bounded tombstone table fails closed instead of evicting safety history.
                    _retired.Add(OfficeHostSafety.StableSessionId("native-v2",pair.Key,pair.Value.Name,pair.Value.FullName));
                }
                _views.Remove(pair.Key); pair.Value.Dispose();
            }
            if (_retired.Count>OfficeDiscoveryLimits.MaxRetiredSessions)
                throw new OfficeHostFaultException("session_capacity","Rebind with a new helper after excessive native session churn.",true);
            foreach(var pair in next) _views.Add(pair.Key,pair.Value);
            var result=next.Values.ToArray(); next.Clear();
            LastReport=new(scan.Complete && issues.Count==0,OfficeDiscoveryLimits.Coverage,clock.ElapsedMilliseconds,
                scan.RootsVisited,probed,issues.Take(OfficeDiscoveryLimits.MaxViews).ToArray());
            return result;
        }
        finally { foreach(var pending in next.Values) pending.Dispose(); }
    }
    /// <summary>Re-probe borrowed identities without replacing catalog references mid-operation.
    /// After an effect, a stale view must never be described as a no-effect preflight rejection.</summary>
    public void ValidateCurrent(OfficeViewLease selected, bool noEffect)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        try
        {
            if (!_views.Values.Any(v=>ReferenceEquals(v,selected)))
                throw new OfficeHostFaultException("stale_resource","The native binding is no longer owned by this helper.",noEffect);
            using var observed=OpenWithTransientRetry(selected.Candidate);
            if (observed.Candidate!=selected.Candidate || observed.ViewHandle!=selected.ViewHandle
                || observed.FullName!=selected.FullName || observed.Name!=selected.Name
                || !SameObject(selected.Document,observed.Document))
                throw new OfficeHostFaultException("stale_resource","Native view/document identity changed during the operation.",noEffect);
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
        { throw new OfficeHostFaultException(OfficeNativeWindowProbe.FaultCode(ex),
            "Native identity revalidation failed; inspect the effect separately before retrying.",noEffect); }
    }
    public OfficeViewLease Require(string application,string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new OfficeHostFaultException("invalid_arguments","An exact observed session is required.",true);
        var candidates=Refresh(application).Where(v=>v.SessionId==sessionId).ToArray();
        if(candidates.Length==1) return candidates[0];
        throw new OfficeHostFaultException(LastReport.Issues.FirstOrDefault()?.Code ?? "stale_resource",
            "The observed Office session is no longer available. Discover and bind again; no operation was dispatched.",true);
    }
    public OfficeCaptureResult Capture(OfficeCaptureRequest request)
    {
        var clock=Stopwatch.StartNew();
        if(request.Application is not ("excel" or "word") || request.WindowHandle<=0 || request.ProcessId<=0 || request.ProcessStartUtcTicks<=0)
            return new("Rejected","invalid_arguments",null,null,null,null,clock.ElapsedMilliseconds);
        try
        {
            var matches=Refresh(request.Application,request.WindowHandle).Where(v=>v.Candidate.ProcessId==request.ProcessId
                && v.Candidate.ProcessStartUtcTicks==request.ProcessStartUtcTicks && v.Candidate.RootHandle==request.WindowHandle).ToArray();
            if(matches.Length!=1 || !LastReport.Complete)
                return new("Unavailable",LastReport.Issues.FirstOrDefault()?.Code ?? (matches.Length>1?"ambiguous_target":"stale_resource"),null,null,null,null,clock.ElapsedMilliseconds);
            var selected=matches[0]; var selection=selected.ReadSelection();
            ValidateCurrent(selected,true);
            if(selection.Length>1024) return new("Degraded","selection_too_large",selected.SessionId,selected.FullName,null,selected.Identity,clock.ElapsedMilliseconds);
            return new("Ready",null,selected.SessionId,selected.FullName,selection,selected.Identity,clock.ElapsedMilliseconds);
        }
        catch(Exception ex) when(OfficeNativeWindowProbe.IsProbeFailure(ex))
        { return new("Unavailable",OfficeNativeWindowProbe.FaultCode(ex),null,null,null,null,clock.ElapsedMilliseconds); }
    }
    public string? ActiveSession(IReadOnlyList<OfficeViewLease> views)
    { var root=_probe.ForegroundRoot;var matches=views.Where(v=>v.Candidate.RootHandle==root).ToArray(); return matches.Length==1?matches[0].SessionId:null; }
    private static bool SameObject(object a,object b)
    {
        if(ReferenceEquals(a,b))return true;
        if(!Marshal.IsComObject(a)||!Marshal.IsComObject(b))return false;
        nint x=0,y=0;
        try { x=Marshal.GetIUnknownForObject(a);y=Marshal.GetIUnknownForObject(b);return x==y; }
        finally { if(y!=0)Marshal.Release(y);if(x!=0)Marshal.Release(x); }
    }
    public void Dispose()
    { if(_disposed)return;_disposed=true;foreach(var view in _views.Values)view.Dispose();_views.Clear();_retired.Clear(); }
}
