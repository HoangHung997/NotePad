using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace H2Notes.Core;

public sealed record TextStyle(string Font = "Segoe UI", double Size = 16, bool Bold = false,
    bool Italic = false, bool Underline = false, bool Strike = false,
    string Color = "#302E2B", string? Highlight = null);

public sealed record StyledRun(string Text, TextStyle Style);

// Null means the selection contains multiple values, except Highlight where null also means no fill.
public sealed record SelectionStyle(string? Font, double? Size, bool? Bold, bool? Italic,
    bool? Underline, bool? Strike, string? Color, string? Highlight, bool MixedHighlight)
{
    public static SelectionStyle From(TextStyle style) => new(style.Font, style.Size, style.Bold,
        style.Italic, style.Underline, style.Strike, style.Color, style.Highlight, false);
}

public sealed class RichDocument
{
    public List<StyledRun> Runs { get; set; } = [];
    public string Text => string.Concat(Runs.Select(r => r.Text));
    public RichDocument Clone() => new() { Runs = [.. Runs] };
    public static RichDocument Plain(string? text) => new() { Runs = [new(text ?? "", new())] };

    public TextStyle StyleAt(int offset)
    {
        var position = 0;
        foreach (var run in Runs)
        {
            position += run.Text.Length;
            if (offset < position) return run.Style;
        }
        return Runs.LastOrDefault()?.Style ?? new();
    }

    public SelectionStyle SelectionStyleAt(int offset, int length)
    {
        ValidateRange(offset, length);
        if (length == 0) return SelectionStyle.From(StyleAt(Math.Max(0, offset - 1)));
        List<TextStyle> styles = [];
        var position = 0;
        foreach (var run in Runs)
        {
            if (position < offset + length && position + run.Text.Length > offset) styles.Add(run.Style);
            position += run.Text.Length;
            if (position >= offset + length) break;
        }
        var first = styles[0];
        return new(
            styles.All(s => s.Font == first.Font) ? first.Font : null,
            styles.All(s => s.Size == first.Size) ? first.Size : null,
            styles.All(s => s.Bold == first.Bold) ? first.Bold : null,
            styles.All(s => s.Italic == first.Italic) ? first.Italic : null,
            styles.All(s => s.Underline == first.Underline) ? first.Underline : null,
            styles.All(s => s.Strike == first.Strike) ? first.Strike : null,
            styles.All(s => s.Color == first.Color) ? first.Color : null,
            styles.All(s => s.Highlight == first.Highlight) ? first.Highlight : null,
            styles.Any(s => s.Highlight != first.Highlight));
    }

    public void Replace(int offset, int length, string inserted, TextStyle? typingStyle = null)
    {
        ValidateRange(offset, length);
        var result = Slice(0, offset);
        if (inserted.Length > 0) result.Add(new(inserted, typingStyle ?? StyleAt(Math.Max(0, offset - 1))));
        result.AddRange(Slice(offset + length, Text.Length - offset - length));
        Runs = Merge(result);
    }

    public void Format(int offset, int length, Func<TextStyle, TextStyle> change)
    {
        ValidateRange(offset, length);
        var result = Slice(0, offset);
        result.AddRange(Slice(offset, length).Select(r => r with { Style = change(r.Style) }));
        result.AddRange(Slice(offset + length, Text.Length - offset - length));
        Runs = Merge(result);
    }

    private void ValidateRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Runs.Sum(r => r.Text.Length) - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private List<StyledRun> Slice(int offset, int length)
    {
        List<StyledRun> result = [];
        var position = 0;
        foreach (var run in Runs)
        {
            var start = Math.Max(offset, position);
            var end = Math.Min(offset + length, position + run.Text.Length);
            if (end > start) result.Add(new(run.Text.Substring(start - position, end - start), run.Style));
            position += run.Text.Length;
        }
        return result;
    }

    private static List<StyledRun> Merge(IEnumerable<StyledRun> runs)
    {
        List<StyledRun> result = [];
        foreach (var run in runs.Where(r => r.Text.Length > 0))
        {
            if (result.Count > 0 && result[^1].Style == run.Style)
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            else result.Add(run);
        }
        return result;
    }

    public static RichDocument FromLegacy(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.TrimStart().StartsWith('<')) return Plain(stored);
        try
        {
            using var reader = XmlReader.Create(new StringReader(stored), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000
            });
            var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            if (root.Name.LocalName is not ("Section" or "FlowDocument" or "Paragraph" or "Span" or "Run"))
                return Plain(stored);
            var doc = new RichDocument();
            Walk(root, new TextStyle(), doc.Runs);
            // WPF TextRange adds a final paragraph terminator, not an extra user line.
            if (doc.Runs.Count > 0 && doc.Runs[^1].Text.EndsWith('\n'))
                doc.Runs[^1] = doc.Runs[^1] with { Text = doc.Runs[^1].Text[..^1] };
            doc.Runs = Merge(doc.Runs);
            return doc;
        }
        catch (XmlException) { return Plain(stored); }
    }

    private static void Walk(XElement element, TextStyle inherited, List<StyledRun> result)
    {
        string? Attr(string name) => element.Attribute(name)?.Value;
        var size = double.TryParse(Attr("FontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? Math.Clamp(s, 6, 120) : inherited.Size;
        var decorations = Attr("TextDecorations");
        var style = inherited with
        {
            Font = Attr("FontFamily") ?? inherited.Font, Size = size,
            Bold = Attr("FontWeight") is { } weight ? weight is "Bold" or "SemiBold" or "700" : inherited.Bold,
            Italic = Attr("FontStyle") is { } italic ? italic == "Italic" : inherited.Italic,
            Underline = decorations is not null ? decorations.Contains("Underline") : inherited.Underline,
            Strike = decorations is not null ? decorations.Contains("Strikethrough") : inherited.Strike,
            Color = Attr("Foreground") ?? inherited.Color, Highlight = Attr("Background") ?? inherited.Highlight
        };
        style = element.Name.LocalName switch
        {
            "Bold" => style with { Bold = true }, "Italic" => style with { Italic = true },
            "Underline" => style with { Underline = true }, _ => style
        };
        if (element.Name.LocalName == "LineBreak") { result.Add(new("\n", style)); return; }
        if (element.Name.LocalName.Contains('.')) return;
        foreach (var node in element.Nodes())
        {
            if (node is XText text) result.Add(new(text.Value.Replace("\r\n", "\n"), style));
            else if (node is XElement child) Walk(child, style, result);
        }
        if (element.Name.LocalName == "Paragraph") result.Add(new("\n", style));
    }
}
