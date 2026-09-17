using System.Text.Json;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class LocalConfiguration
{
    public string? DataFolder { get; set; }
    public AiConnectionSettings Ai { get; set; } = new();
    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes");
    public static string DefaultDataFolder => Path.Combine(SettingsDirectory, "workspace-v2");
    public static string ConfigPath => Path.Combine(SettingsDirectory, "local-config-v2.json");
    public static LocalConfiguration Read() => File.Exists(ConfigPath)
        ? JsonSerializer.Deserialize<LocalConfiguration>(File.ReadAllText(ConfigPath)) ?? throw new InvalidDataException("Cài đặt máy bị lỗi.") : new();
    public void Save() => ProjectWorkspaceStore.AtomicWrite(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}
