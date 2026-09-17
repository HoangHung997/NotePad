using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace Nodepad.Desktop.Services;

public static class RichTextDocumentSerializer
{
    public static FlowDocument CreateDocument(string? storedValue)
    {
        var document = CreateEmptyDocument();
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return document;
        }

        if (LooksLikeXaml(storedValue))
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(storedValue));
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                range.Load(stream, System.Windows.DataFormats.Xaml);
                NormalizeDocument(document);
                return document;
            }
            catch
            {
            }
        }

        document.Blocks.Clear();
        var lines = storedValue.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0)
        {
            document.Blocks.Add(new Paragraph());
        }
        else
        {
            foreach (var line in lines)
            {
                document.Blocks.Add(new Paragraph(new Run(line)));
            }
        }

        NormalizeDocument(document);
        return document;
    }

    public static string Serialize(FlowDocument? document)
    {
        var value = document ?? CreateEmptyDocument();
        NormalizeDocument(value);

        using var stream = new MemoryStream();
        var range = new TextRange(value.ContentStart, value.ContentEnd);
        range.Save(stream, System.Windows.DataFormats.Xaml);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

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

        try
        {
            var document = CreateDocument(storedValue);
            var range = new TextRange(document.ContentStart, document.ContentEnd);
            return NormalizePlainText(range.Text);
        }
        catch
        {
            return NormalizePlainText(storedValue);
        }
    }

    public static bool HasPlainText(string? storedValue)
    {
        return !string.IsNullOrWhiteSpace(ExtractPlainText(storedValue));
    }

    public static void NormalizeDocument(FlowDocument document)
    {
        document.PagePadding = new Thickness(0);
        document.ColumnWidth = double.PositiveInfinity;

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph());
        }

        NormalizeBlocks(document.Blocks);
    }

    private static void NormalizeBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    paragraph.Margin = new Thickness(0);
                    break;
                case List list:
                    list.Margin = new Thickness(0);
                    NormalizeListItems(list.ListItems);
                    break;
                case Section section:
                    section.Margin = new Thickness(0);
                    NormalizeBlocks(section.Blocks);
                    break;
            }
        }
    }

    private static void NormalizeListItems(ListItemCollection items)
    {
        foreach (var item in items)
        {
            item.Margin = new Thickness(0);
            NormalizeBlocks(item.Blocks);
        }
    }

    private static FlowDocument CreateEmptyDocument()
    {
        var document = new FlowDocument();
        NormalizeDocument(document);
        return document;
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
            .Replace("\r\n", Environment.NewLine)
            .TrimEnd('\r', '\n');
    }
}
