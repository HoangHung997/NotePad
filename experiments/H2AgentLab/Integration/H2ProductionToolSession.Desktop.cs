using H2AgentLab.Desktop;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    private string? _selectedWindowIdentity;
    private string? _desktopBindingFailure;

    public async Task PrepareDesktopAsync(AgentTools tools, CancellationToken ct)
    {
        // Window grounding is the captured host context, never the permission grant or current
        // foreground. A machine-wide FullAccess grant alone identifies no desktop target.
        var captured = _targetPolicy?.CapturedContext;
        if (captured is null) { _desktopBindingFailure = "needs_configuration"; return; }
        if (!H2CapturedWindowIdentity.IsCurrent(captured))
        { _desktopBindingFailure = "stale_resource"; return; }
        DesktopHostClient? client = null;
        try
        {
            client = DesktopHostLocator.CreateClient();
            var windows = await client.ListWindowsAsync(ct).ConfigureAwait(false);
            var candidates = windows.Where(w => Identity(w) == captured.WindowIdentity).ToArray();
            if (candidates.Length != 1) { _desktopBindingFailure = candidates.Length > 1 ? "ambiguous_target" : "stale_resource"; return; }
            var selected = candidates[0];
            var observed = new H2AgentResourceBinding("window:" + Identity(selected), H2AgentResourceKind.Window,
                captured.ApplicationKind, "desktop-host", null, null, null, null, selected.ProcessId,
                selected.ProcessStartedUtcTicks, Identity(selected), Identity(selected), null, null, null, DateTime.UtcNow);
            var decision = _targetPolicy!.ResolveCapturedWindow(observed, DateTime.UtcNow);
            if (!decision.Resolved) { _desktopBindingFailure = decision.Code; return; }
            tools.Desktop = new SelectedDesktopWindowController(client, selected, ownsClient: true);
            _selectedWindowIdentity = Identity(selected);
            _targetObserved?.Invoke(decision);
            client = null; // AgentTools owns the selected controller/client.
        }
        catch (Exception ex) when (ex is IOException or AgentFaultException)
        { _desktopBindingFailure = "provider_unavailable"; }
        finally { client?.Dispose(); }
    }

    private static string Identity(DesktopWindowInfo window)
        => $"win32:{window.Handle:x}:{window.ProcessId}:{window.ProcessStartedUtcTicks}";
}
