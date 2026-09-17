using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed class RichEditor : UserControl
{
    public TextEditor Editor { get; } = new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = 16, WordWrap = true,
        ShowLineNumbers = false, Background = Brushes.Transparent, Padding = new Thickness(10, 7)
    };
    private RichDocument _document = RichDocument.Plain("");
    private bool _loading;
    private TextStyle? _typingStyle;
    private readonly List<RichDocument> _undo = [];
    private readonly List<RichDocument> _redo = [];
    private DateTime _lastEdit;
    private bool _syncingFormat;
    private SelectionStyle? _lastSyncedStyle;
    private readonly List<Action<SelectionStyle>> _formatBindings = [];
    private static readonly string[] FontChoices = ["Segoe UI", "Calibri", "Cambria", "Georgia", "Times New Roman", "Consolas"];
    private static readonly double[] SizeChoices = [12, 14, 16, 18, 20, 24, 28, 32];
    public SelectionStyle SelectedStyle => Editor.SelectionLength == 0 && _typingStyle is { } style
        ? SelectionStyle.From(style) : _document.SelectionStyleAt(Editor.SelectionStart, Editor.SelectionLength);
    public event Action? Changed;
    public bool HasChanges { get; private set; }
    public RichDocument Snapshot() => _document.Clone();

    public RichEditor()
    {
        Content = Editor;
        Editor.TextArea.TextView.LineTransformers.Add(new RunColorizer(() => _document));
        Editor.Document.Changing += (_, e) =>
        {
            if (_loading) return;
            if ((DateTime.UtcNow - _lastEdit).TotalMilliseconds > 700) PushUndo();
            _lastEdit = DateTime.UtcNow;
            _document.Replace(e.Offset, e.RemovalLength, e.InsertedText.Text, _typingStyle);
        };
        Editor.TextChanged += (_, _) => { if (!_loading) NotifyChanged(); };
        Editor.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.ContextMenu = CreateMenu();
        Editor.TextArea.SelectionChanged += (_, _) => SelectionMoved();
        Editor.TextArea.Caret.PositionChanged += (_, _) => SelectionMoved();
    }

    public void Load(RichDocument document)
    {
        _loading = true;
        _document = document.Clone();
        Editor.Text = _document.Text;
        Editor.Document.UndoStack.ClearAll();
        _typingStyle = null; _undo.Clear(); _redo.Clear(); _lastEdit = default;
        HasChanges = false; _loading = false;
        Editor.TextArea.TextView.Redraw();
        SyncFormatting(true);
    }

    public void MarkSaved() => HasChanges = false;
    public void AppendText(string text)
    {
        var suffix = (Editor.Text.Length == 0 ? "" : "\n") + text;
        Editor.Document.Insert(Editor.Text.Length, suffix);
        FocusEditor();
    }
    public void FocusEditor(bool selectAll = false)
    {
        Editor.TextArea.Focus();
        if (selectAll) Editor.SelectAll();
    }

    public void Apply(Func<TextStyle, TextStyle> change)
    {
        if (Editor.SelectionLength == 0)
            _typingStyle = change(_typingStyle ?? _document.StyleAt(Math.Max(0, Editor.CaretOffset - 1)));
        else
        {
            PushUndo(); _lastEdit = default;
            _document.Format(Editor.SelectionStart, Editor.SelectionLength, change);
            Editor.TextArea.TextView.Redraw(); NotifyChanged();
        }
        FocusEditor();
        SyncFormatting();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_document.Clone());
        Restore(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
    }
    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_document.Clone());
        Restore(_redo[^1]); _redo.RemoveAt(_redo.Count - 1);
    }
    private void Restore(RichDocument document)
    {
        var caret = Editor.CaretOffset;
        _loading = true; _document = document.Clone(); Editor.Text = _document.Text;
        Editor.CaretOffset = Math.Min(caret, Editor.Text.Length);
        _loading = false; _lastEdit = default; _typingStyle = null;
        Editor.TextArea.TextView.Redraw(); NotifyChanged();
    }
    private void PushUndo()
    {
        _undo.Add(_document.Clone());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }
    private void NotifyChanged() { HasChanges = true; SyncFormatting(); Changed?.Invoke(); }
    private void SelectionMoved()
    {
        if (_loading) return;
        _typingStyle = null;
        SyncFormatting();
    }
    private void SyncFormatting(bool force = false)
    {
        if (_loading || _syncingFormat) return;
        _syncingFormat = true;
        try
        {
            var state = SelectedStyle;
            if (!force && state == _lastSyncedStyle) return;
            foreach (var update in _formatBindings) update(state);
            _lastSyncedStyle = state;
        }
        finally { _syncingFormat = false; }
    }
    private void ToggleBold() { var value = SelectedStyle.Bold != true; Apply(s => s with { Bold = value }); }
    private void ToggleItalic() { var value = SelectedStyle.Italic != true; Apply(s => s with { Italic = value }); }
    private void ToggleUnderline() { var value = SelectedStyle.Underline != true; Apply(s => s with { Underline = value }); }
    private void ToggleStrike() { var value = SelectedStyle.Strike != true; Apply(s => s with { Strike = value }); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        switch (e.Key)
        {
            case Key.Z: if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo(); else Undo(); break;
            case Key.Y: Redo(); break;
            case Key.B: ToggleBold(); break;
            case Key.I: ToggleItalic(); break;
            case Key.U: ToggleUnderline(); break;
            default: return;
        }
        e.Handled = true;
    }

    public Control CreateToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(8, 3) };
        Button Add(IconKind icon, Action action, string tip)
        {
            var button = AppIcon.Button(icon, tip); button.Focusable = false;
            button.Classes.Add("quiet"); ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action(); bar.Children.Add(button);
            return button;
        }
        void Toggle(IconKind icon, string name, Action action, Func<SelectionStyle, bool?> read, string tip)
        {
            var button = new ToggleButton { Name = name, Content = new AppIcon(icon, 18), Focusable = false, IsThreeState = true };
            global::Avalonia.Automation.AutomationProperties.SetName(button, tip);
            button.Classes.Add("format");
            ToolTip.SetTip(button, tip); button.Click += (_, _) => action(); bar.Children.Add(button);
            _formatBindings.Add(s => { button.IsChecked = read(s); ToolTip.SetTip(button, tip + (read(s) is null ? " · Nhiều định dạng" : "")); });
        }
        Toggle(IconKind.Bold, "FormatBold", ToggleBold, s => s.Bold, "Đậm · Ctrl+B");
        Toggle(IconKind.Italic, "FormatItalic", ToggleItalic, s => s.Italic, "Nghiêng · Ctrl+I");
        Toggle(IconKind.Underline, "FormatUnderline", ToggleUnderline, s => s.Underline, "Gạch chân · Ctrl+U");
        var fonts = new ComboBox { Name = "FormatFont", ItemsSource = FontChoices, PlaceholderText = "Nhiều phông", Width = 125, FontSize = 13 };
        fonts.SelectionChanged += (_, _) => { if (!_syncingFormat && fonts.SelectedItem is string font) Apply(s => s with { Font = font }); };
        _formatBindings.Add(s =>
        {
            if (s.Font is not null && !fonts.Items.Contains(s.Font)) fonts.ItemsSource = FontChoices.Append(s.Font).Distinct().ToArray();
            fonts.SelectedItem = s.Font;
        });
        bar.Children.Insert(0, fonts);
        var sizes = new ComboBox { Name = "FormatSize", ItemsSource = SizeChoices, PlaceholderText = "—", Width = 68, FontSize = 13 };
        sizes.SelectionChanged += (_, _) => { if (!_syncingFormat && sizes.SelectedItem is double size) Apply(s => s with { Size = size }); };
        _formatBindings.Add(s =>
        {
            if (s.Size is { } size && !sizes.Items.Contains(size)) sizes.ItemsSource = SizeChoices.Append(size).Distinct().Order().ToArray();
            sizes.SelectedItem = s.Size;
        });
        bar.Children.Insert(1, sizes);
        var color = Add(IconKind.TextColor, () => ColorMenu(false).Open(bar), "Màu chữ / RGB");
        Add(IconKind.Bullets, () => ToggleList(false), "Danh sách dấu chấm · bật / tắt cho dòng được chọn").Name = "FormatBullets";
        Add(IconKind.NumberedList, () => ToggleList(true), "Danh sách đánh số · bật / tắt cho dòng được chọn").Name = "FormatNumbering";
        Add(IconKind.Link, async () => await InsertLink(), "Chèn địa chỉ liên kết").Name = "FormatLink";
        var fill = Add(IconKind.Highlight, () => ColorMenu(true).Open(bar), "Tô nền chữ / RGB");
        Toggle(IconKind.Strike, "FormatStrike", ToggleStrike, s => s.Strike, "Gạch ngang");
        Add(IconKind.Undo, Undo, "Hoàn tác · Ctrl+Z");
        Add(IconKind.Redo, Redo, "Làm lại · Ctrl+Y");
        global::Avalonia.Automation.AutomationProperties.SetName(color, "Màu chữ / RGB");
        global::Avalonia.Automation.AutomationProperties.SetName(fill, "Tô nền chữ / RGB");
        _formatBindings.Add(s =>
        {
            color.Content = ColorLabel(IconKind.TextColor, s.Color, s.Color is null);
            fill.Content = ColorLabel(IconKind.Highlight, s.Highlight, s.MixedHighlight);
        });
        SyncFormatting(true);
        return new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string label, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = label, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action(); menu.Items.Add(item);
            return item;
        }
        void Toggle(string label, Action action, Func<SelectionStyle, bool?> read, string? gesture = null)
        {
            var item = Add(label, action, gesture); item.ToggleType = MenuItemToggleType.CheckBox;
            _formatBindings.Add(s => { item.IsChecked = read(s) == true; item.Header = label + (read(s) is null ? " · Nhiều định dạng" : ""); });
        }
        Toggle("Đậm", ToggleBold, s => s.Bold, "Ctrl+B");
        Toggle("Nghiêng", ToggleItalic, s => s.Italic, "Ctrl+I");
        Toggle("Gạch chân", ToggleUnderline, s => s.Underline, "Ctrl+U");
        Toggle("Gạch ngang", ToggleStrike, s => s.Strike);
        var fonts = new MenuItem { Header = "Phông chữ" };
        foreach (var font in FontChoices)
        {
            var item = new MenuItem { Header = font, ToggleType = MenuItemToggleType.CheckBox }; item.Click += (_, _) => Apply(s => s with { Font = font }); fonts.Items.Add(item);
            _formatBindings.Add(s => item.IsChecked = s.Font == font);
        }
        _formatBindings.Add(s => fonts.Header = "Phông chữ: " + (s.Font ?? "Nhiều phông"));
        menu.Items.Add(fonts);
        var sizes = new MenuItem { Header = "Cỡ chữ" };
        foreach (var size in SizeChoices)
        {
            var item = new MenuItem { Header = size.ToString(), ToggleType = MenuItemToggleType.CheckBox }; item.Click += (_, _) => Apply(s => s with { Size = size }); sizes.Items.Add(item);
            _formatBindings.Add(s => item.IsChecked = s.Size == size);
        }
        _formatBindings.Add(s => sizes.Header = "Cỡ chữ: " + (s.Size?.ToString("0.##") ?? "Nhiều cỡ"));
        menu.Items.Add(sizes);
        var color = Add("Màu chữ…", () => ColorMenu(false).Open(Editor));
        var fill = Add("Màu nền chữ…", () => ColorMenu(true).Open(Editor));
        _formatBindings.Add(s =>
        {
            color.Header = "Màu chữ: " + (s.Color ?? "Nhiều màu"); color.Icon = Swatch(s.Color);
            fill.Header = "Màu nền: " + (s.MixedHighlight ? "Nhiều màu" : s.Highlight ?? "Không tô nền"); fill.Icon = Swatch(s.Highlight);
        });
        menu.Items.Add(new Separator());
        Add("Hoàn tác", Undo, "Ctrl+Z"); Add("Làm lại", Redo, "Ctrl+Y");
        menu.Items.Add(new Separator());
        Add("Cắt", Editor.Cut, "Ctrl+X"); Add("Sao chép", Editor.Copy, "Ctrl+C"); Add("Dán văn bản", Editor.Paste, "Ctrl+V");
        Add("Chọn tất cả", Editor.SelectAll, "Ctrl+A");
        Add("Xóa định dạng", () => Apply(_ => new TextStyle()));
        menu.Items.Add(new Separator());
        Add("Danh sách dấu chấm", () => ToggleList(false)).Icon = new AppIcon(IconKind.Bullets);
        Add("Danh sách đánh số", () => ToggleList(true)).Icon = new AppIcon(IconKind.NumberedList);
        Add("Chèn địa chỉ liên kết…", async () => await InsertLink()).Icon = new AppIcon(IconKind.Link);
        menu.Opened += (_, _) => SyncFormatting(true);
        return menu;
    }

    private ContextMenu ColorMenu(bool highlight)
    {
        var menu = new ContextMenu();
        var selected = SelectedStyle;
        foreach (var hex in new[] { "#163D35", "#000000", "#FFFFFF", "#C62828", "#E17D10", "#F9D65C", "#268455", "#008F99", "#2167AD", "#8A4380" })
        {
            var item = new MenuItem { Header = hex, Icon = new Border { Width = 20, Height = 20, Background = Brush(hex), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) } };
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = string.Equals(highlight ? selected.Highlight : selected.Color, hex, StringComparison.OrdinalIgnoreCase);
            item.Click += (_, _) => Apply(s => highlight ? s with { Highlight = hex } : s with { Color = hex });
            menu.Items.Add(item);
        }
        if (highlight)
        {
            var clear = new MenuItem { Header = "Không tô nền", ToggleType = MenuItemToggleType.CheckBox, IsChecked = selected.Highlight is null && !selected.MixedHighlight }; clear.Click += (_, _) => Apply(s => s with { Highlight = null }); menu.Items.Add(clear);
        }
        var rgb = new MenuItem { Header = "Màu tùy chỉnh RGB…" };
        rgb.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var hex = await Dialogs.ChooseColor(owner);
            if (hex is not null) Apply(s => highlight ? s with { Highlight = hex } : s with { Color = hex });
        };
        menu.Items.Add(rgb); return menu;
    }

    internal static IBrush Brush(string? color)
    {
        try { return new SolidColorBrush(Color.Parse(color ?? "#163D35")); }
        catch (FormatException) { return Brushes.DarkSlateGray; }
    }

    private static Border Swatch(string? color) => new() { Width = 16, Height = 5, Background = color is null ? Brushes.Transparent : Brush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.5) };
    private static Control ColorLabel(IconKind icon, string? color, bool mixed) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 4,
        Children = { new StackPanel { Spacing = 1, Children = { new AppIcon(icon, 18),
            new Border { Width = 17, Height = 3, Background = color is null ? Brushes.Transparent : Brush(color), BorderBrush = mixed ? Brushes.Gray : null, BorderThickness = new Thickness(mixed ? 1 : 0) } } },
            new AppIcon(IconKind.ChevronDown, 11) }
    };

    public void ToggleList(bool numbered)
    {
        var start = Editor.SelectionStart;
        var end = start + Editor.SelectionLength;
        var first = Editor.Document.GetLineByOffset(start);
        var last = Editor.Document.GetLineByOffset(end > start && end > 0 && Editor.Text[end - 1] == '\n' ? end - 1 : end);
        var lines = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1)
            .Select(Editor.Document.GetLineByNumber).ToArray();
        var prefixes = lines.Select(line => System.Text.RegularExpressions.Regex.Match(Editor.Document.GetText(line), @"^(?:\u2022 |\d+\. )").Value).ToArray();
        var selectionStart = first.Offset; var selectionEnd = last.EndOffset;
        var remove = prefixes.All(p => numbered ? p.Length > 0 && char.IsDigit(p[0]) : p == "\u2022 ");
        PushUndo(); _loading = true;
        try
        {
            // Edit backwards so existing styled runs retain their offsets and formatting.
            for (var i = lines.Length - 1; i >= 0; i--)
                _document.Replace(lines[i].Offset, prefixes[i].Length, remove ? "" : numbered ? $"{i + 1}. " : "\u2022 ");
            var delta = _document.Text.Length - Editor.Text.Length;
            Editor.Text = _document.Text;
            Editor.Select(selectionStart, Math.Max(0, selectionEnd - selectionStart + delta));
        }
        finally { _loading = false; }
        _lastEdit = default; _typingStyle = null; Editor.TextArea.TextView.Redraw(); NotifyChanged(); FocusEditor();
    }

    private async Task InsertLink()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var offset = Editor.SelectionStart; var length = Editor.SelectionLength;
        var link = await Dialogs.Prompt(owner, "Chèn liên kết", "Địa chỉ liên kết (chèn dưới dạng văn bản)", "https://");
        if (string.IsNullOrWhiteSpace(link)) return;
        PushUndo(); _loading = true;
        try
        {
            _document.Replace(offset, length, link.Trim(), new TextStyle { Underline = true, Color = "#A4573D" });
            Editor.Text = _document.Text; Editor.Select(offset, link.Trim().Length);
        }
        finally { _loading = false; }
        _lastEdit = default; Editor.TextArea.TextView.Redraw(); NotifyChanged(); FocusEditor();
    }

    private sealed class RunColorizer(Func<RichDocument> document) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var position = 0;
            foreach (var run in document().Runs)
            {
                var start = Math.Max(position, line.Offset);
                var end = Math.Min(position + run.Text.Length, line.EndOffset);
                if (end > start)
                {
                    var style = run.Style;
                    ChangeLinePart(start, end, element =>
                    {
                        var properties = element.TextRunProperties;
                        properties.SetTypeface(new Typeface(style.Font, style.Italic ? FontStyle.Italic : FontStyle.Normal,
                            style.Bold ? FontWeight.Bold : FontWeight.Normal));
                        properties.SetFontRenderingEmSize(style.Size);
                        properties.SetForegroundBrush(Brush(style.Color));
                        if (style.Highlight is not null) properties.SetBackgroundBrush(Brush(style.Highlight));
                        var decorations = new TextDecorationCollection();
                        if (style.Underline) decorations.Add(TextDecorations.Underline[0]);
                        if (style.Strike) decorations.Add(TextDecorations.Strikethrough[0]);
                        properties.SetTextDecorations(decorations);
                    });
                }
                position += run.Text.Length;
            }
        }
    }
}
