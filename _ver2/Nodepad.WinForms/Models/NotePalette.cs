namespace Nodepad.WinForms.Models;

public sealed record NotePalette(
    string Key,
    string Label,
    string BackgroundHex,
    string SurfaceHex,
    string BorderHex,
    string AccentHex,
    string ForegroundHex);

