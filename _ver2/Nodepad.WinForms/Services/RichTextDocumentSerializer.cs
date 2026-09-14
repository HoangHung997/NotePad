using System.Net;
using System.Text.RegularExpressions;

namespace Nodepad.WinForms.Services;

public static class RichTextDocumentSerializer
{
    public static string ExtractPlainText(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return string.Empty;
        }

        if (!LooksLikeXaml(storedValue))
        {
            return NormalizePlainText(storedValue);
        }

        var text = storedValue
            .Replace("</Paragraph>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("</Section>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("<LineBreak />", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
            .Replace("<LineBreak/>", Environment.NewLine, StringComparison.OrdinalIgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return NormalizePlainText(text);
    }

    public static bool HasPlainText(string? storedValue)
    {
        return !string.IsNullOrWhiteSpace(ExtractPlainText(storedValue));
    }

    private static bool LooksLikeXaml(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("<Section", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<FlowDocument", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePlainText(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal)
            .TrimEnd('\r', '\n');
    }
}
