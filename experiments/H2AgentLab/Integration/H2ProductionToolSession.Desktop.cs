using H2AgentLab.Desktop;
using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    private string? _selectedWindowIdentity;

    public async Task PrepareDesktopAsync(AgentTools tools, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_scope?.WindowIdentity)) return;
        DesktopHostClient? client = null;
        try
        {
            client = DesktopHostLocator.CreateClient();
            var windows = await client.ListWindowsAsync(ct).ConfigureAwait(false);
            var selected = windows.SingleOrDefault(w => Identity(w) == _scope.WindowIdentity);
            if (selected is null) return;
            tools.Desktop = new SelectedDesktopWindowController(client, selected, ownsClient: true);
            _selectedWindowIdentity = Identity(selected);
            client = null; // AgentTools owns the selected controller/client.
        }
        catch (Exception ex) when (ex is IOException or AgentFaultException)
        {
            // Structured Office tools remain usable; desktop tools are omitted when unavailable.
        }
        finally { client?.Dispose(); }
    }

    private static string Identity(DesktopWindowInfo window)
        => $"win32:{window.Handle:x}:{window.ProcessId}:{window.ProcessStartedUtcTicks}";
}
