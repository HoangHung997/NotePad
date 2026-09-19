using System.Text.Json;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class LocalConfiguration
{
    public string? DataFolder { get; set; }
    public WorkspaceLocationProfile? WorkspaceLocation { get; set; }
    public string DeviceId { get; set; } = "";
    public DesktopSessionState? DesktopSession { get; set; }
    public AiConnectionSettings Ai { get; set; } = new();

    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes");
    public static string DefaultDataFolder => Path.Combine(SettingsDirectory, "workspace-v2");
    public static string ConfigPath => Path.Combine(SettingsDirectory, "local-config-v2.json");

    public static LocalConfiguration Read()
    {
        var config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<LocalConfiguration>(File.ReadAllText(ConfigPath)) ?? throw new InvalidDataException("Cài đặt máy bị lỗi.")
            : new LocalConfiguration();
        if (string.IsNullOrWhiteSpace(config.DeviceId))
        {
            config.DeviceId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..12];
            config.Save();
        }
        return config;
    }

    public void Save() => ProjectWorkspaceStore.AtomicWrite(ConfigPath,
        JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}
