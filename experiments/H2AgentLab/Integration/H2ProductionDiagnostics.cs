using System.Text.Json;
using H2AgentLab.Desktop;
using H2AgentLab.Office;

namespace H2AgentLab.Integration;

public static class H2ProductionDiagnostics
{
    /// <summary>Starts packaged helpers and checks IPC, without observing or changing user documents.</summary>
    public static async Task<int> VerifyHelpersAsync(string outputPath)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var office = new OfficeHostClient(H2HelperLocator.Resolve("H2AgentLab.OfficeHost"));
            using var desktop = new DesktopHostClient(H2HelperLocator.Resolve("H2AgentLab.DesktopHost"));
            var officePing = await office.PingAsync(timeout.Token).ConfigureAwait(false);
            var desktopPing = await desktop.PingAsync(timeout.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new { ok = true, office = officePing, desktop = desktopPing }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            return 1;
        }
    }
}
