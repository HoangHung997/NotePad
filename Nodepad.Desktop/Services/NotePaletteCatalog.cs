using Nodepad.Desktop.Models;

namespace Nodepad.Desktop.Services;

public static class NotePaletteCatalog
{
    public static IReadOnlyList<NotePalette> All { get; } =
    [
        new("honey", "Honey", "#FFF4D38A", "#FFFFF2D1", "#FFD8B05C", "#FF9A5A14", "#FF33240E"),
        new("mint", "Mint", "#FFDCEFD8", "#FFF2FBF0", "#FF9EC39A", "#FF3D7A4C", "#FF17311F"),
        new("sky", "Sky", "#FFD8EAFE", "#FFF0F7FF", "#FF93BEE6", "#FF2B6FAA", "#FF11263A"),
        new("rose", "Rose", "#FFF8D9E3", "#FFFFF1F5", "#FFD9A3B8", "#FFAA466C", "#FF351824"),
        new("lilac", "Lilac", "#FFE5DDF8", "#FFF7F2FF", "#FFB9A9E6", "#FF6650A4", "#FF241C39"),
        new("stone", "Stone", "#FFE6E0D8", "#FFF7F4F0", "#FFC2B8AB", "#FF76685A", "#FF2C241D")
    ];

    public static NotePalette Get(string? key)
    {
        return All.FirstOrDefault(palette => string.Equals(palette.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? All[0];
    }

    public static string GetLabel(string? key)
    {
        return Get(key).Label;
    }
}
