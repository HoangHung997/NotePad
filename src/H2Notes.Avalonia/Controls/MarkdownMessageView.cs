using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

/// <summary>
/// Lightweight, local Markdown renderer for AI chat output. The model remains the author:
/// this control preserves the order and structure it emitted instead of forcing a template.
/// Links are rendered as styled text only; the renderer never opens URLs or executes content.
/// </summary>
public sealed class MarkdownMessageView : StackPanel
{
    private static readonly Regex Heading = new("^(#{1,6})\\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new("^\\s*(?<mark>[-+*]|\\d+\\.)\\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex TableDivider = new("^:?-{3,}:?$", RegexOptions.Compiled);
    private static readonly Regex Rule = new("^\\s*((-{3,})|(\\*\\s*){3,}|(_\\s*){3,})\\s*$", RegexOptions.Compiled);

    public string Markdown { get; private set; } = "";

    public MarkdownMessageView()
    {
        Spacing = 7;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public void SetMarkdown(string? markdown)
    {
        markdown = NormalizeDisplayText(markdown ?? "");
        if (Markdown == markdown) return;
        Markdown = markdown;
        Children.Clear();
        if (markdown.Length == 0) return;

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var fence = lines[i].TrimStart();
                var language = fence.Length > 3 ? fence[3..].Trim() : "";
                i++;
                var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i++]);
                }
                if (i < lines.Length) i++;
                var codeText = code.ToString();
                // Vision models sometimes correctly transcribe a table but wrap the Markdown in
                // a text/code fence. Preserve the table semantics instead of showing pipe text.
                if (TryFencedTable(codeText, out var rows)) Children.Add(Table(rows));
                else Children.Add(CodeBlock(codeText, language));
                continue;
            }

            var heading = Heading.Match(lines[i]);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var size = Math.Max(14, 18 - (level - 1) * .8);
                Children.Add(InlineBlock(heading.Groups[2].Value.Trim(), size,
                    level <= 2 ? FontWeight.Bold : FontWeight.SemiBold, "MarkdownHeading", new Thickness(0, level == 1 ? 6 : 3, 0, 1)));
                i++;
                continue;
            }

            if (Rule.IsMatch(lines[i]))
            {
                Children.Add(new Border { Name = "MarkdownRule", Height = 1, Background = RichEditor.Brush("#DDD4CB"), Margin = new Thickness(0, 5) });
                i++;
                continue;
            }

            if (LooksLikeTable(lines, i))
            {
                var rows = new List<string[]> { TableCells(lines[i]) };
                i += 2; // header + alignment divider
                while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
                    rows.Add(TableCells(lines[i++]));
                Children.Add(Table(rows));
                continue;
            }

            if (lines[i].TrimStart().StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var text = lines[i].TrimStart()[1..].TrimStart();
                    if (quote.Length > 0) quote.Append('\n');
                    quote.Append(text); i++;
                }
                Children.Add(Quote(quote.ToString()));
                continue;
            }

            var list = ListItem.Match(lines[i]);
            if (list.Success)
            {
                while (i < lines.Length && (list = ListItem.Match(lines[i])).Success)
                {
                    Children.Add(ListRow(list.Groups["mark"].Value, list.Groups["text"].Value));
                    i++;
                }
                continue;
            }

            var paragraph = new StringBuilder(lines[i++].Trim());
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines, i))
            {
                paragraph.Append(' ').Append(lines[i++].Trim());
            }
            Children.Add(InlineBlock(paragraph.ToString(), 13, FontWeight.Normal, "MarkdownParagraph"));
        }
    }

    private static bool StartsBlock(string[] lines, int i)
    {
        if (i >= lines.Length) return false;
        var line = lines[i];
        return Heading.IsMatch(line) || Rule.IsMatch(line) || ListItem.IsMatch(line)
            || line.TrimStart().StartsWith('>') || line.TrimStart().StartsWith("```", StringComparison.Ordinal)
            || LooksLikeTable(lines, i);
    }

    private static bool LooksLikeTable(string[] lines, int i)
    {
        if (i + 1 >= lines.Length || !lines[i].Contains('|') || !lines[i + 1].Contains('|')) return false;
        var divider = TableCells(lines[i + 1]);
        return divider.Length > 0 && divider.All(c => TableDivider.IsMatch(c.Trim()));
    }

    private static bool TryFencedTable(string text, out IReadOnlyList<string[]> rows)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length < 2 || !LooksLikeTable(lines, 0)) { rows = []; return false; }
        var parsed = new List<string[]> { TableCells(lines[0]) };
        for (var i = 2; i < lines.Length; i++)
        {
            if (!lines[i].Contains('|')) { rows = []; return false; }
            parsed.Add(TableCells(lines[i]));
        }
        rows = parsed;
        return true;
    }

    private static string[] TableCells(string line)
    {
        line = line.Trim();
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|')) line = line[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var ch in line)
        {
            if (escaped) { current.Append(ch); escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString().Trim());
        return cells.ToArray();
    }

    private static Control Table(IReadOnlyList<string[]> rows)
    {
        var columns = rows.Max(r => r.Length);
        var grid = new Grid { Name = "MarkdownTable", Margin = new Thickness(0, 3) };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var content = InlineBlock(text, 12, r == 0 ? FontWeight.SemiBold : FontWeight.Normal, "MarkdownTableCell");
                content.MinWidth = 60;
                var cell = new Border
                {
                    BorderBrush = RichEditor.Brush("#DED6CE"), BorderThickness = new Thickness(1),
                    Background = r == 0 ? RichEditor.Brush("#F6F0EA") : Brushes.Transparent,
                    Padding = new Thickness(8, 6), Child = content
                };
                Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static Control ListRow(string mark, string text)
    {
        var check = Regex.Match(text, "^\\[(?<state>[ xX])\\]\\s*(?<body>.*)$");
        var prefix = mark.EndsWith('.') ? mark : "•";
        if (check.Success)
        {
            prefix = check.Groups["state"].Value == " " ? "☐" : "☑";
            text = check.Groups["body"].Value;
        }
        var grid = new Grid { Name = "MarkdownListItem", ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 0) };
        grid.Children.Add(new TextBlock { Text = prefix, FontSize = 12, Margin = new Thickness(0, 1, 7, 0), Foreground = RichEditor.Brush("#796C62") });
        var body = InlineBlock(text, 13, FontWeight.Normal, "MarkdownListText"); Grid.SetColumn(body, 1); grid.Children.Add(body);
        return grid;
    }

    private static Control Quote(string text) => new Border
    {
        Name = "MarkdownQuote", BorderBrush = RichEditor.Brush("#B76A4B"), BorderThickness = new Thickness(3, 0, 0, 0),
        Background = RichEditor.Brush("#FBF6F1"), Padding = new Thickness(10, 7), Margin = new Thickness(0, 3),
        Child = InlineBlock(text, 12.5, FontWeight.Normal, "MarkdownQuoteText")
    };

    private static Control CodeBlock(string code, string language)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (language.Length > 0) panel.Children.Add(new TextBlock { Text = language, FontSize = 10, Foreground = RichEditor.Brush("#796C62") });
        panel.Children.Add(new SelectableTextBlock
        {
            Name = "MarkdownCodeText", Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            TextWrapping = TextWrapping.WrapWithOverflow
        });
        return new Border
        {
            Name = "MarkdownCode", Background = RichEditor.Brush("#F3F0EC"), BorderBrush = RichEditor.Brush("#DED6CE"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 7), Child = panel
        };
    }

    private static SelectableTextBlock InlineBlock(string markdown, double fontSize, FontWeight weight, string name, Thickness? margin = null)
    {
        var block = new SelectableTextBlock
        {
            Name = name, FontSize = fontSize, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0)
        };
        AddInlines(block, markdown);
        return block;
    }

    private static void AddInlines(SelectableTextBlock block, string text)
    {
        var inlines = block.Inlines!;
        var i = 0;
        while (i < text.Length)
        {
            if (TryDelimited(text, ref i, "**", "**", out var bold))
            {
                inlines.Add(new Run { Text = bold, FontWeight = FontWeight.Bold });
                continue;
            }
            if (TryDelimited(text, ref i, "__", "__", out var boldUnderscore))
            {
                inlines.Add(new Run { Text = boldUnderscore, FontWeight = FontWeight.Bold });
                continue;
            }
            if (TryDelimited(text, ref i, "`", "`", out var code))
            {
                inlines.Add(new Run { Text = code, FontFamily = new FontFamily("Consolas"), Foreground = RichEditor.Brush("#7B3E2B") });
                continue;
            }
            if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length)
            {
                var marker = text[i].ToString();
                if (TryDelimited(text, ref i, marker, marker, out var italic))
                {
                    inlines.Add(new Run { Text = italic, FontStyle = FontStyle.Italic });
                    continue;
                }
            }
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        inlines.Add(new Run { Text = text[(i + 1)..close], Foreground = RichEditor.Brush("#A4573D") });
                        i = end + 1; continue;
                    }
                }
            }
            var next = NextInlineMarker(text, i + 1);
            var endPlain = next < 0 ? text.Length : next;
            inlines.Add(new Run { Text = text[i..endPlain].Replace("\\*", "*", StringComparison.Ordinal).Replace("\\_", "_", StringComparison.Ordinal) });
            i = endPlain;
        }
    }

    private static string NormalizeDisplayText(string text)
    {
        if (!text.Contains("\\u", StringComparison.Ordinal)) return text;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (i + 6 <= text.Length && text[i] == '\\' && text[i + 1] == 'u'
                && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first))
            {
                var high = (char)first;
                if (char.IsHighSurrogate(high) && i + 12 <= text.Length && text[i + 6] == '\\' && text[i + 7] == 'u'
                    && ushort.TryParse(text.AsSpan(i + 8, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var second)
                    && char.IsLowSurrogate((char)second))
                {
                    result.Append(char.ConvertFromUtf32(char.ConvertToUtf32(high, (char)second)));
                    i += 12;
                    continue;
                }
                if (!char.IsSurrogate(high))
                {
                    result.Append(high);
                    i += 6;
                    continue;
                }
            }
            result.Append(text[i++]);
        }
        return result.ToString();
    }

    private static int NextInlineMarker(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
            if (text[i] is '*' or '_' or '`' or '[') return i;
        return -1;
    }

    private static bool TryDelimited(string text, ref int index, string open, string close, out string value)
    {
        value = "";
        if (!text.AsSpan(index).StartsWith(open, StringComparison.Ordinal)) return false;
        var start = index + open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        if (end < start) return false;
        value = text[start..end]; index = end + close.Length; return true;
    }
}
