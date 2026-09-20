using System.Text.Json;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed record WorkAssistantBubblePosition(
    double XDip,
    double YDip);

public sealed class WorkAssistantSettings
{
    public bool Enabled { get; set; }
    public bool StartWithH2 { get; set; } = true;
    public bool StartCollapsed { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";
    public WorkAssistantBubblePosition? BubblePosition { get; set; }
    public string? PreferredMonitor { get; set; }
    public string NotificationPreference { get; set; } = "attention-and-completed";

    internal void Normalize()
    {
        Hotkey = BoundOrDefault(Hotkey, 80, "Ctrl+Shift+Space");
        PreferredMonitor = BoundOrNull(PreferredMonitor, 240);
        NotificationPreference = NotificationPreference switch
        {
            "none" or "attention-only" or "completed-only" or "attention-and-completed"
                => NotificationPreference,
            _ => "attention-and-completed"
        };

        if (BubblePosition is { } position
            && (!double.IsFinite(position.XDip) || !double.IsFinite(position.YDip)))
            BubblePosition = null;
    }

    private static string BoundOrDefault(string? value, int max, string fallback)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return fallback;
        return value.Length <= max ? value : value[..max];
    }

    private static string? BoundOrNull(string? value, int max)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return null;
        return value.Length <= max ? value : value[..max];
    }
}

public sealed class LocalConfiguration
{
    public string? DataFolder { get; set; }
    public WorkspaceLocationProfile? WorkspaceLocation { get; set; }
    public string DeviceId { get; set; } = "";
    public DesktopSessionState? DesktopSession { get; set; }
    public AiConnectionSettings Ai { get; set; } = new();
    public WorkAssistantSettings WorkAssistant { get; set; } = new();

    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes");
    public static string DefaultDataFolder => Path.Combine(SettingsDirectory, "workspace-v2");
    public static string ConfigPath => Path.Combine(SettingsDirectory, "local-config-v2.json");

    public static LocalConfiguration Read()
    {
        var config = File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<LocalConfiguration>(File.ReadAllText(ConfigPath)) ?? throw new InvalidDataException("Cài đặt máy bị lỗi.")
            : new LocalConfiguration();
        config.WorkAssistant ??= new WorkAssistantSettings();
        config.WorkAssistant.Normalize();

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
