using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace H2Notes.Avalonia.Controls;

/// <summary>
/// Standards-based Markdown renderer for AI output. Markdig owns parsing/escaping rules;
/// this control only maps the parsed AST to safe Avalonia controls. HTML is never executed.
/// Tables are responsive and reflow with the chat bubble instead of keeping a fixed pixel width.
/// </summary>
public sealed class MarkdownMessageView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private string _source = "";
    private readonly List<(string Signature, Control[] Controls)> _blocks = [];

    public string Markdown { get; private set; } = "";
    public Action<string>? OpenResource { get; set; }

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
        _source = SeparateParagraphTables(markdown);
        var document = Markdig.Markdown.Parse(_source, Pipeline);
        var signatures = document.Select(b => b.GetType().Name + ":" + SourceText(b)).ToArray();
        var unchanged = 0;
        while (unchanged < _blocks.Count && unchanged < signatures.Length && _blocks[unchanged].Signature == signatures[unchanged]) unchanged++;
        for (var i = _blocks.Count - 1; i >= unchanged; i--)
        { foreach (var control in _blocks[i].Controls) Children.Remove(control); _blocks.RemoveAt(i); }
        for (var i = unchanged; i < document.Count; i++)
        {
            var before = Children.Count; RenderBlock(document[i], Children, 0);
            _blocks.Add((signatures[i], Children.Skip(before).ToArray()));
        }
    }

    private static string SeparateParagraphTables(string markdown)
    {
        // Providers often omit the blank line before a pipe table. Only repair parsed prose;
        // fenced/indented code and raw HTML are left byte-for-byte unchanged.
        var original = Markdig.Markdown.Parse(markdown, Pipeline);
        var positions = new List<int>();
        foreach (var paragraph in original.OfType<ParagraphBlock>())
        {
            var source = markdown.Substring(paragraph.Span.Start, paragraph.Span.Length);
            var lines = source.Split('\n'); var offset = paragraph.Span.Start;
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                if (i > 0 && lines[i].TrimStart().StartsWith('|')
                    && System.Text.RegularExpressions.Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$"))
                    positions.Add(offset);
                offset += lines[i].Length + 1;
            }
        }
        foreach (var position in positions.OrderDescending()) markdown = markdown.Insert(position, "\n");
        return markdown;
    }

    private void RenderBlock(Block block, global::Avalonia.Controls.Controls controls, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var level = Math.Clamp(heading.Level, 1, 6);
                var size = Math.Max(14, 18 - (level - 1) * .8);
                controls.Add(InlineBlock(heading.Inline, size, level <= 2 ? FontWeight.Bold : FontWeight.SemiBold,
                    "MarkdownHeading", new Thickness(0, level == 1 ? 6 : 3, 0, 1)));
                break;
            }
            case ParagraphBlock paragraph:
                controls.Add(InlineBlock(paragraph.Inline, 14, FontWeight.Normal, "MarkdownParagraph"));
                break;
            case ThematicBreakBlock:
                controls.Add(new Border { Name = "MarkdownRule", Height = 1, Background = RichEditor.Brush("#DDD4CB"), Margin = new Thickness(0, 5) });
                break;
            case QuoteBlock quote:
            {
                var panel = new StackPanel { Spacing = 4 };
                foreach (var child in quote) RenderBlock(child, panel.Children, depth + 1);
                controls.Add(new Border
                {
                    Name = "MarkdownQuote", BorderBrush = RichEditor.Brush("#B76A4B"), BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = RichEditor.Brush("#FBF6F1"), Padding = new Thickness(10, 7), Margin = new Thickness(0, 3), Child = panel
                });
                break;
            }
            case ListBlock list:
                RenderList(list, controls, depth);
                break;
            case Table table:
                controls.Add(RenderTable(table));
                break;
            case FencedCodeBlock fenced:
            {
                var code = fenced.Lines.ToString() ?? "";
                controls.Add(CodeBlock(code, fenced.Info?.ToString() ?? ""));
                break;
            }
            case CodeBlock code:
                controls.Add(CodeBlock(code.Lines.ToString() ?? "", ""));
                break;
            case HtmlBlock html:
            {
                var text = SourceText(html);
                if (IsOnlyBreakHtml(text)) controls.Add(new Border { Height = 4 });
                else controls.Add(PlainBlock(text, 12.5, FontWeight.Normal, "MarkdownHtmlLiteral"));
                break;
            }
            case ContainerBlock container:
                foreach (var child in container) RenderBlock(child, controls, depth + 1);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                controls.Add(InlineBlock(leaf.Inline, 14, FontWeight.Normal, "MarkdownParagraph"));
                break;
            default:
            {
                var raw = SourceText(block);
                if (!string.IsNullOrWhiteSpace(raw)) controls.Add(PlainBlock(raw, 13, FontWeight.Normal, "MarkdownFallback"));
                break;
            }
        }
    }

    private void RenderList(ListBlock list, global::Avalonia.Controls.Controls controls, int depth)
    {
        var ordinal = 0;
        _ = int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderedStart);
        if (orderedStart <= 0) orderedStart = 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            ordinal++;
            var row = new Grid { Name = "MarkdownListItem", ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(Math.Min(24, depth * 10), 0, 0, 1) };
            var prefix = list.IsOrdered ? (orderedStart + ordinal - 1).ToString(CultureInfo.InvariantCulture) + "." : "•";
            var body = new StackPanel { Spacing = 3 };
            var firstParagraph = item.OfType<ParagraphBlock>().FirstOrDefault();
            if (firstParagraph?.Inline?.FirstChild is TaskList task) prefix = task.Checked ? "☑" : "☐";
            row.Children.Add(new TextBlock { Text = prefix, FontSize = 12, Margin = new Thickness(0, 1, 7, 0), Foreground = RichEditor.Brush("#796C62") });
            foreach (var child in item)
            {
                if (child is ParagraphBlock paragraph && paragraph.Inline is not null)
                    body.Children.Add(InlineBlock(paragraph.Inline, 14, FontWeight.Normal, "MarkdownListText"));
                else RenderBlock(child, body.Children, depth + 1);
            }
            Grid.SetColumn(body, 1); row.Children.Add(body); controls.Add(row);
        }
    }

    private Control RenderTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToArray();
        var columns = rows.Select(r => r.OfType<TableCell>().Count()).DefaultIfEmpty(0).Max();
        if (columns == 0) return PlainBlock(SourceText(table), 12.5, FontWeight.Normal, "MarkdownFallback");
        var values = rows.Select(r => r.OfType<TableCell>().Select(CellPlainText).ToArray()).ToArray();
        string RowText(int r) => string.Join("\t", values[r]);
        var root = new StackPanel { Spacing = 5 };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var copy = CopyButton("Sao chép bảng", () => string.Join("\n", Enumerable.Range(0, rows.Length).Select(RowText)));
        var previous = new Button { Content = "‹", FontSize = 11 };
        var next = new Button { Content = "›", FontSize = 11 };
        var count = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(copy); toolbar.Children.Add(previous); toolbar.Children.Add(count); toolbar.Children.Add(next);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Children.Add(toolbar); root.Children.Add(scroll);
        var page = 0; const int pageSize = 30;
        void ShowPage()
        {
            var indices = new[] { 0 }.Concat(Enumerable.Range(1, Math.Max(0, rows.Length - 1)).Skip(page * pageSize).Take(pageSize)).ToArray();
            var grid = new Grid { Name = "MarkdownTable", HorizontalAlignment = HorizontalAlignment.Stretch };
            var weights = EstimateColumnWeights(rows.Take(32).ToArray(), columns);
            for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(weights[c], GridUnitType.Star), MinWidth = Math.Clamp(weights[c] * 30, 90, 220) });
            foreach (var _ in indices) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var r = 0; r < indices.Length; r++)
            {
                var rowIndex = indices[r];
                for (var c = 0; c < columns; c++)
                {
                    var columnIndex = c;
                    var text = c < values[rowIndex].Length ? values[rowIndex][c] : "";
                    var cellText = PlainBlock(text, 12, rowIndex == 0 ? FontWeight.SemiBold : FontWeight.Normal, "MarkdownTableCell");
                    if (c < table.ColumnDefinitions.Count)
                        cellText.TextAlignment = table.ColumnDefinitions[c].Alignment switch {
                            TableColumnAlign.Right => TextAlignment.Right, TableColumnAlign.Center => TextAlignment.Center, _ => TextAlignment.Left };
                    var copyCell = new MenuItem { Header = "Sao chép ô" };
                    copyCell.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
                    var copyRow = new MenuItem { Header = "Sao chép hàng" };
                    copyRow.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(RowText(rowIndex)); };
                    cellText.ContextMenu = new ContextMenu { ItemsSource = new[] { copyCell, copyRow } };
                    var cell = new Border { BorderBrush = RichEditor.Brush("#DED6CE"), BorderThickness = new Thickness(0, 0, 0, 1),
                        Background = rowIndex == 0 ? RichEditor.Brush("#F6F0EA") : Brushes.Transparent, Padding = new Thickness(9, 7), Child = cellText };
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c); grid.Children.Add(cell);
                }
            }
            scroll.Content = grid;
            previous.IsVisible = next.IsVisible = rows.Length > pageSize + 1;
            previous.IsEnabled = page > 0; next.IsEnabled = (page + 1) * pageSize < rows.Length - 1;
            count.Text = rows.Length <= pageSize + 1 ? $"{Math.Max(0, rows.Length - 1)} hàng" : $"{page * pageSize + 1}–{Math.Min((page + 1) * pageSize, rows.Length - 1)} / {rows.Length - 1} hàng";
        }
        previous.Click += (_, _) => { page--; ShowPage(); }; next.Click += (_, _) => { page++; ShowPage(); };
        ShowPage(); return root;
    }

    private static Button CopyButton(string label, Func<string> text)
    {
        var button = new Button { Content = label, FontSize = 11, Padding = new Thickness(6, 3) };
        button.Click += async (_, _) => { if (TopLevel.GetTopLevel(button)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text()); };
        return button;
    }

    private static double[] EstimateColumnWeights(IReadOnlyList<TableRow> rows, int columns)
    {
        var lengths = Enumerable.Repeat(6d, columns).ToArray();
        foreach (var row in rows)
        {
            var cells = row.OfType<TableCell>().ToArray();
            for (var c = 0; c < Math.Min(columns, cells.Length); c++)
            {
                var text = CellPlainText(cells[c]);
                lengths[c] = Math.Max(lengths[c], Math.Min(80, text.Length));
            }
        }
        return lengths.Select(value => Math.Clamp(Math.Sqrt(value), 1.0, 4.5)).ToArray();
    }

    private static string CellPlainText(TableCell cell)
    {
        var text = new StringBuilder();
        foreach (var block in cell)
        {
            if (block is LeafBlock leaf && leaf.Inline is not null) AppendPlain(leaf.Inline, text);
            if (text.Length > 0) text.Append(' ');
        }
        return text.ToString().Trim();
    }

    private static void AppendPlain(ContainerInline? container, StringBuilder output)
    {
        if (container is null) return;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: output.Append(literal.Content.ToString()); break;
                case CodeInline code: output.Append(code.Content); break;
                case LineBreakInline: output.Append(' '); break;
                case HtmlInline html when IsBreakHtml(html.Tag): output.Append(' '); break;
                case HtmlEntityInline entity: output.Append(entity.Transcoded.ToString()); break;
                case ContainerInline nested: AppendPlain(nested, output); break;
            }
        }
    }

    private SelectableTextBlock InlineBlock(ContainerInline? inline, double fontSize, FontWeight weight, string name, Thickness? margin = null)
    {
        var block = new SelectableTextBlock
        {
            Name = name, FontSize = fontSize, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = margin ?? new Thickness(0)
        };
        AddInlines(block.Inlines!, inline, new InlineStyle());
        return block;
    }

    private static SelectableTextBlock PlainBlock(string text, double fontSize, FontWeight weight, string name)
        => new() { Name = name, Text = text, FontSize = fontSize, FontWeight = weight, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };

    private void AddInlines(InlineCollection output, ContainerInline? container, InlineStyle style)
    {
        if (container is null) return;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case TaskList:
                    break;
                case LiteralInline literal:
                    AddRun(output, literal.Content.ToString(), style);
                    break;
                case CodeInline code:
                    AddRun(output, code.Content, style with { Code = true });
                    break;
                case LineBreakInline:
                    output.Add(new LineBreak());
                    break;
                case HtmlInline html:
                    if (IsBreakHtml(html.Tag)) output.Add(new LineBreak());
                    else AddRun(output, html.Tag ?? "", style);
                    break;
                case HtmlEntityInline entity:
                    AddRun(output, entity.Transcoded.ToString(), style);
                    break;
                case LinkInline link:
                    if (link.IsImage)
                    {
                        var alt = new StringBuilder(); AppendPlain(link, alt);
                        AddRun(output, alt.Length == 0 ? "[Ảnh]" : "[Ảnh: " + alt + "]", style with { Italic = true });
                    }
                    else if (OpenResource is not null && !string.IsNullOrWhiteSpace(link.Url))
                    {
                        var label = new StringBuilder(); AppendPlain(link, label);
                        var button = new Button { Content = label.ToString(), FontSize = 13, Padding = new Thickness(1, 0),
                            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = RichEditor.Brush("#A4573D") };
                        ToolTip.SetTip(button, link.Url);
                        button.Click += (_, _) => OpenResource?.Invoke(link.Url!);
                        output.Add(new InlineUIContainer(button));
                    }
                    else AddInlines(output, link, style with { Link = true });
                    break;
                case EmphasisInline emphasis:
                {
                    var next = style;
                    if (emphasis.DelimiterChar is '*' or '_') next = emphasis.DelimiterCount >= 2 ? next with { Bold = true } : next with { Italic = true };
                    else if (emphasis.DelimiterChar == '~') next = next with { Strike = true };
                    AddInlines(output, emphasis, next);
                    break;
                }
                case ContainerInline nested:
                    AddInlines(output, nested, style);
                    break;
                default:
                    AddRun(output, inline.ToString() ?? "", style);
                    break;
            }
        }
    }

    private static void AddRun(InlineCollection output, string text, InlineStyle style)
    {
        if (text.Length == 0) return;
        var run = new Run
        {
            Text = text,
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal
        };
        if (style.Code)
        {
            run.FontFamily = new FontFamily("Consolas");
            run.Foreground = RichEditor.Brush("#7B3E2B");
        }
        else if (style.Link)
        {
            run.Foreground = RichEditor.Brush("#A4573D");
            run.TextDecorations = TextDecorations.Underline;
        }
        if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
        output.Add(run);
    }

    private static Control CodeBlock(string code, string language)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(CopyButton("Sao chép mã", () => code));
        if (!string.IsNullOrWhiteSpace(language)) panel.Children.Add(new TextBlock { Text = language, FontSize = 10, Foreground = RichEditor.Brush("#796C62") });
        panel.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock { Name = "MarkdownCodeText", Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap } });
        return new Border
        {
            Name = "MarkdownCode", Background = RichEditor.Brush("#F3F0EC"), BorderBrush = RichEditor.Brush("#DED6CE"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 7), Child = panel
        };
    }

    private string SourceText(Block block)
    {
        if (block.Span.Start < 0 || block.Span.End < block.Span.Start || block.Span.End >= _source.Length) return block.ToString() ?? "";
        return _source.Substring(block.Span.Start, block.Span.End - block.Span.Start + 1);
    }

    private static bool IsBreakHtml(string? text)
    {
        var value = text?.Trim() ?? "";
        return value.Equals("<br>", StringComparison.OrdinalIgnoreCase)
            || value.Equals("<br/>", StringComparison.OrdinalIgnoreCase)
            || value.Equals("<br />", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnlyBreakHtml(string text)
    {
        var value = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();
        return IsBreakHtml(value);
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
                    result.Append(char.ConvertFromUtf32(char.ConvertToUtf32(high, (char)second))); i += 12; continue;
                }
                if (!char.IsSurrogate(high)) { result.Append(high); i += 6; continue; }
            }
            result.Append(text[i++]);
        }
        return result.ToString();
    }

    private sealed record InlineStyle(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, bool Link = false);
}
