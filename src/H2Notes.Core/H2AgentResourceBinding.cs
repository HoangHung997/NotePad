using System.Security.Cryptography;
using System.Text;

namespace H2Notes.Core;

public enum H2AgentResourceKind { DiskFile, LiveDocument, Window }
public enum H2AgentTargetIntent { OpenDocument, CapturedActive, CapturedSelection }

/// <summary>Host/provider observation, never a permission grant or a model-owned state store.
/// Missing native identity fields stay null; the current Office catalog does not prove PID/view identity.</summary>
public sealed record H2AgentResourceBinding(
    string ResourceId,
    H2AgentResourceKind Kind,
    H2ApplicationKind ApplicationKind,
    string ProviderId,
    string? ProviderInstanceId,
    string? ProviderVersion,
    string? DocumentSessionId,
    string? CanonicalPath,
    int? ProcessId,
    long? ProcessStartUtcTicks,
    string? WindowIdentity,
    string? ViewIdentity,
    string? ContentVersion,
    string? UiStateToken,
    bool? Dirty,
    DateTime ObservedUtc)
{
    public string Provenance => Kind switch {
        H2AgentResourceKind.DiskFile => "DiskSnapshot",
        H2AgentResourceKind.LiveDocument => "LiveDocument",
        _ => "WindowCapture" };

    public static H2AgentResourceBinding FromLiveObservation(H2ApplicationKind applicationKind,
        string providerId, string sessionId, string? path, DateTime observedUtc,
        string? providerInstanceId = null, string? providerVersion = null,
        int? processId = null, long? processStartUtcTicks = null,
        string? windowIdentity = null, string? viewIdentity = null,
        string? contentVersion = null, string? uiStateToken = null, bool? dirty = null)
    {
        if (!Enum.IsDefined(applicationKind) || applicationKind == H2ApplicationKind.Unknown
            || observedUtc.Kind != DateTimeKind.Utc || !Token(providerId) || !Token(sessionId)
            || !Optional(providerInstanceId) || !Optional(providerVersion)
            || !Optional(windowIdentity) || !Optional(viewIdentity)
            || !Optional(contentVersion) || !Optional(uiStateToken)
            || processId is <= 0 || processStartUtcTicks is <= 0)
            throw new ArgumentException("Live identity must contain exact bounded host/provider fields and UTC time.");
        // Office exposes an unsaved document name in FullName on some builds. It is NOT a disk path.
        var canonical = H2AgentTargetScope.TryNormalize(path, out var normalized) ? normalized : null;
        return new(Id("live", applicationKind.ToString(), providerId, providerInstanceId, providerVersion,
                sessionId, processId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                processStartUtcTicks?.ToString(System.Globalization.CultureInfo.InvariantCulture), windowIdentity, viewIdentity),
            H2AgentResourceKind.LiveDocument, applicationKind, providerId, providerInstanceId, providerVersion,
            sessionId, canonical, processId, processStartUtcTicks, windowIdentity, viewIdentity,
            contentVersion, uiStateToken, dirty, observedUtc);
    }

    public static H2AgentResourceBinding FromCaptured(H2ActiveWorkContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProcessId <= 0 || context.ProcessStartUtcTicks <= 0 || context.NativeWindowHandle == 0
            || !Token(context.WindowIdentity) || context.CapturedUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Captured window identity is incomplete.");
        var provider = context.Provider ?? "window-capture";
        if (!string.IsNullOrWhiteSpace(context.DocumentSessionId))
            return FromLiveObservation(context.ApplicationKind, provider, context.DocumentSessionId,
                context.DocumentPath, context.CapturedUtc, processId: context.ProcessId,
                processStartUtcTicks: context.ProcessStartUtcTicks, windowIdentity: context.WindowIdentity,
                viewIdentity: context.WindowIdentity, uiStateToken: context.Selection);
        return new(Id("window", context.WindowIdentity), H2AgentResourceKind.Window,
            context.ApplicationKind, provider, null, null, null, null, context.ProcessId,
            context.ProcessStartUtcTicks, context.WindowIdentity, context.WindowIdentity,
            null, context.Selection, null, context.CapturedUtc);
    }

    public static H2AgentResourceBinding FromDisk(string path, string contentVersion, DateTime observedUtc)
    {
        if (observedUtc.Kind != DateTimeKind.Utc || !Token(contentVersion)
            || !H2AgentTargetScope.TryNormalize(path, out var canonical)
            || !H2AgentTargetScope.HasNoReparsePoints(canonical))
            throw new ArgumentException("Disk binding needs a validated path and an observed content version.");
        var identity = OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
        return new(Id("disk-path", identity), H2AgentResourceKind.DiskFile, H2ApplicationKind.Unknown,
            "filesystem", null, null, null, canonical, null, null, null, null,
            contentVersion, null, false, observedUtc);
    }

    /// <summary>No implicit Save As/provider migration. UI movement alone does not change content.
    /// Callers using a live selection must separately require the bound UI-state precondition.</summary>
    public bool MatchesObservation(H2AgentResourceBinding current, bool requireContentVersion = true,
        bool requireUiState = false)
        => ResourceId == current.ResourceId && Kind == current.Kind
           && ApplicationKind == current.ApplicationKind && ProviderId == current.ProviderId
           && ProviderInstanceId == current.ProviderInstanceId && ProviderVersion == current.ProviderVersion
           && DocumentSessionId == current.DocumentSessionId && ProcessId == current.ProcessId
           && ProcessStartUtcTicks == current.ProcessStartUtcTicks && WindowIdentity == current.WindowIdentity
           && ViewIdentity == current.ViewIdentity
           && H2AgentTargetScope.PathComparer.Equals(CanonicalPath, current.CanonicalPath)
           && (!requireContentVersion || ContentVersion is not null && ContentVersion == current.ContentVersion)
           && (!requireUiState || UiStateToken is not null && UiStateToken == current.UiStateToken);

    private static bool Token(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);
    private static bool Optional(string? value) => value is null || Token(value);
    private static string Id(params string?[] values)
        => "resource-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(values)))).ToLowerInvariant();
}

public sealed record H2AgentTargetResolution(H2AgentResourceBinding? Binding, string Code,
    bool IsExternal, string Source)
{
    public bool Resolved => Binding is not null && Code == "resolved";
    public string ScopeLabel => Binding is null ? Code
        : (IsExternal ? "External: " : "Target: ") + (Binding.CanonicalPath ?? Binding.DocumentSessionId ?? Binding.ResourceId);
}

/// <summary>One task's immutable grounding policy. No model content or grant is accepted here.
/// Live resources must come from provider observations. A disk file never substitutes for an open document.</summary>
public sealed class H2AgentTargetBindingPolicy
{
    private readonly H2AgentTargetPath[] _targets;
    private readonly H2AgentTargetPath[] _workspace;
    private readonly H2ActiveWorkContext? _captured;
    public Guid? ExecutionProjectId { get; }
    public static readonly TimeSpan MaxCaptureAge = TimeSpan.FromMinutes(10);

    public H2AgentTargetBindingPolicy(Guid? projectId, string workspaceRoot,
        IReadOnlyList<H2AgentTargetPath>? targets = null, H2ActiveWorkContext? captured = null)
    {
        if (projectId == Guid.Empty || !H2AgentTargetScope.TryNormalize(workspaceRoot, out var root)
            || root == Path.GetPathRoot(root) || !Directory.Exists(root)
            || !H2AgentTargetScope.HasNoReparsePoints(root))
            throw new ArgumentException("Choose an existing ordinary workspace folder.");
        ExecutionProjectId = projectId;
        _workspace = [new(root, true, projectId.HasValue ? "project-workspace" : "selected-workspace")];
        _targets = (targets ?? []).ToArray();
        _captured = captured; // immutable record captured by the host, not a later foreground query
    }

    public bool AllowsPath(string? path)
        => H2AgentTargetScope.Contains(_workspace, path) || H2AgentTargetScope.Contains(_targets, path);

    public bool IsExternal(string? path) => !H2AgentTargetScope.Contains(_workspace, path);

    public H2AgentTargetResolution ResolveOpen(H2ApplicationKind application,
        IReadOnlyList<H2AgentResourceBinding> observations, H2AgentTargetIntent intent,
        DateTime utcNow, string? requestedSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (utcNow.Kind != DateTimeKind.Utc || !Enum.IsDefined(intent))
            throw new ArgumentException("Resolve requires a UTC clock and a known target intent.");
        var live = observations.Where(r => r.Kind == H2AgentResourceKind.LiveDocument
            && r.ApplicationKind == application && r.ObservedUtc.Kind == DateTimeKind.Utc
            && r.ObservedUtc <= utcNow && utcNow - r.ObservedUtc <= MaxCaptureAge).ToArray();
        if (live.GroupBy(r => r.ResourceId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            return Missing("ambiguous_target"); // Duplicate/contradictory provider identity is not first-match selection.
        var allowed = live.Where(r => AllowsPath(r.CanonicalPath) || CapturedMatch(r, utcNow)
            && !ExecutionProjectId.HasValue).ToArray();

        if (intent is H2AgentTargetIntent.CapturedActive or H2AgentTargetIntent.CapturedSelection)
        {
            if (_captured is null || _captured.ApplicationKind != application || !CaptureFresh(utcNow))
                return Missing("stale_resource");
            var matching = live.Where(r => CapturedMatch(r, utcNow)).ToArray();
            if (matching.Length != 1) return Missing(matching.Length == 0 ? "stale_resource" : "ambiguous_target");
            if (ExecutionProjectId.HasValue && !AllowsPath(matching[0].CanonicalPath)) return Missing("outside_resource_scope");
            if (requestedSessionId is not null && requestedSessionId != matching[0].DocumentSessionId)
                return Missing("outside_resource_scope");
            if (intent == H2AgentTargetIntent.CapturedSelection
                && (string.IsNullOrWhiteSpace(_captured.Selection) || matching[0].UiStateToken != _captured.Selection))
                return Missing("stale_resource");
            return Selected(matching[0], "captured-active");
        }

        // A session supplied by the model is a selection among allowed observations, never a scope grant.
        if (requestedSessionId is not null)
        {
            var exact = allowed.Where(r => r.DocumentSessionId == requestedSessionId).ToArray();
            return exact.Length == 1 ? Selected(exact[0], "allowed-session")
                : Missing(exact.Length > 1 ? "ambiguous_target" : "outside_resource_scope");
        }
        var explicitlyNamed = allowed.Where(r => H2AgentTargetScope.Contains(
            _targets.Where(t => t.Source == "user-path").ToArray(), r.CanonicalPath)).ToArray();
        if (explicitlyNamed.Length > 0) return Unique(explicitlyNamed, "user-path");
        var active = allowed.Where(r => CapturedMatch(r, utcNow)).ToArray();
        if (active.Length > 0) return Unique(active, "captured-active");
        if (!ExecutionProjectId.HasValue && _captured is not null && _captured.ApplicationKind == application)
            return Missing("stale_resource"); // Never retarget a stale captured document to a new foreground one.
        return Unique(allowed, ExecutionProjectId.HasValue ? "project-open" : "selected-open");
    }

    private bool CaptureFresh(DateTime now) => _captured is not null && _captured.CapturedUtc.Kind == DateTimeKind.Utc
        && _captured.CapturedUtc <= now && now - _captured.CapturedUtc <= MaxCaptureAge;
    private bool CapturedMatch(H2AgentResourceBinding r, DateTime now)
        => CaptureFresh(now) && !string.IsNullOrWhiteSpace(_captured!.DocumentSessionId)
           && r.DocumentSessionId == _captured.DocumentSessionId && r.ApplicationKind == _captured.ApplicationKind
           && r.ProviderId == _captured.Provider
           && H2AgentTargetScope.PathComparer.Equals(r.CanonicalPath,
               H2AgentTargetScope.TryNormalize(_captured.DocumentPath, out var path) ? path : null)
           && (r.ProcessId is null || r.ProcessId == _captured.ProcessId)
           && (r.ProcessStartUtcTicks is null || r.ProcessStartUtcTicks == _captured.ProcessStartUtcTicks)
           && (r.WindowIdentity is null || r.WindowIdentity == _captured.WindowIdentity);
    private H2AgentTargetResolution Unique(H2AgentResourceBinding[] candidates, string source)
        => candidates.Length == 1 ? Selected(candidates[0], source)
            : Missing(candidates.Length == 0 ? "resource_not_found" : "ambiguous_target");
    private H2AgentTargetResolution Selected(H2AgentResourceBinding binding, string source)
        => new(binding, "resolved", IsExternal(binding.CanonicalPath), source);
    private static H2AgentTargetResolution Missing(string code) => new(null, code, false, "host-grounding");
}
