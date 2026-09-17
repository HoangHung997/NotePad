using System.Globalization;

namespace Nodepad.WinForms.Services;

public static class ColorHelper
{
    public static Color FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Color.White;
        }

        var value = hex.Trim().TrimStart('#');
        return value.Length switch
        {
            6 => Color.FromArgb(
                255,
                ParseByte(value[..2]),
                ParseByte(value.Substring(2, 2)),
                ParseByte(value.Substring(4, 2))),
            8 => Color.FromArgb(
                ParseByte(value[..2]),
                ParseByte(value.Substring(2, 2)),
                ParseByte(value.Substring(4, 2)),
                ParseByte(value.Substring(6, 2))),
            _ => Color.White
        };
    }

    private static byte ParseByte(string value)
    {
        return byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
