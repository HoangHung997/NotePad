using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

// This preview is deliberately labelled approximate: Word's layout engine is authoritative.
public sealed class LayoutReviewWindow : Window
{
    private readonly List<Bitmap> _bitmaps = [];
    public LayoutReviewWindow(AiPageLayout layout)
    {
        Title = "Word giữ bố cục · Đối chiếu trước khi lưu";
        Width = 1080; Height = 760; MinWidth = 680; MinHeight = 500;
        ShowInTaskbar = CanMinimize = CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var pages = new ComboBox { Name = "LayoutPage", ItemsSource = Enumerable.Range(1, layout.Pages.Count).Select(p => "Trang " + p).ToArray(), SelectedIndex = 0, Width = 120 };
        var warning = new TextBlock { Text = string.Join("\n", layout.Warnings), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = RichEditor.Brush("#905037") };
        var canvas = new Canvas { Background = Brushes.White };
        var preview = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
        var lines = new StackPanel { Spacing = 12 };
        void ShowPage()
        {
            canvas.Children.Clear(); lines.Children.Clear();
            var page = layout.Pages[Math.Max(0, pages.SelectedIndex)];
            canvas.Width = page.Width; canvas.Height = page.Height;
            var bitmap = new Bitmap(new MemoryStream(page.BackgroundPng)); _bitmaps.Add(bitmap);
            canvas.Children.Add(new Image { Source = bitmap, Width = page.Width, Height = page.Height, Stretch = Stretch.Fill });
            foreach (var item in page.Items)
            {
                var label = new TextBlock { Inlines = new(), FontFamily = new FontFamily(item.Font), FontSize = item.FontSize };
                var fitted = new Viewbox { Child = label, Width = item.Width, Height = Math.Max(item.Height, item.FontSize * 1.2), Stretch = Stretch.Fill };
                Canvas.SetLeft(fitted, item.X); Canvas.SetTop(fitted, Math.Max(0, item.Y - item.FontSize * .2)); canvas.Children.Add(fitted);
                var text = new TextBox { Text = item.Text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, FontSize = 13 };
                var font = new ComboBox { ItemsSource = new[] { "Times New Roman", "Arial" }, SelectedItem = item.Font, Width = 160 };
                var size = new NumericUpDown { Minimum = 6, Maximum = 48, Increment = .5m, Value = (decimal)item.FontSize, Width = 130 };
                var bold = new CheckBox { Content = "Đậm", IsChecked = item.Bold };
                var italic = new CheckBox { Content = "Nghiêng", IsChecked = item.Italic };
                void Refresh()
                {
                    label.FontFamily = new FontFamily(item.Font); label.FontSize = item.FontSize;
                    label.FontWeight = item.Bold ? FontWeight.Bold : FontWeight.Normal;
                    label.FontStyle = item.Italic ? FontStyle.Italic : FontStyle.Normal;
                    var spans = item.Runs ?? [new AiLayoutRun { Text = item.Text, Bold = item.Bold, Italic = item.Italic }];
                    label.Inlines?.Clear();
                    var measured = 0d;
                    foreach (var span in spans)
                    {
                        var style = span.Italic ? FontStyle.Italic : FontStyle.Normal;
                        var weight = span.Bold ? FontWeight.Bold : FontWeight.Normal;
                        label.Inlines?.Add(new global::Avalonia.Controls.Documents.Run(span.Text) { FontWeight = weight, FontStyle = style });
                        measured += new FormattedText(span.Text, System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, new Typeface(item.Font, style, weight), item.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace;
                    }
                    item.HorizontalScale = Math.Clamp((int)Math.Floor(100 * item.Width / Math.Max(1, measured)), 10, 400);
                }
                text.TextChanged += (_, _) => { var value = (text.Text ?? "").ReplaceLineEndings(" "); if (value != item.Text) { item.Text = value; item.Runs = null; } Refresh(); };
                font.SelectionChanged += (_, _) => { item.Font = font.SelectedItem as string ?? "Times New Roman"; Refresh(); };
                size.ValueChanged += (_, _) => { item.FontSize = (double)(size.Value ?? 12); Refresh(); };
                bold.IsCheckedChanged += (_, _) => { item.Bold = bold.IsChecked == true; item.Runs = null; Refresh(); };
                italic.IsCheckedChanged += (_, _) => { item.Italic = italic.IsChecked == true; item.Runs = null; Refresh(); };
                var context = new ContextMenu();
                void Format(string name, bool? b, bool? i)
                {
                    var action = new MenuItem { Header = name };
                    action.Click += async (_, _) =>
                    {
                        try { item.FormatSelection(Math.Min(text.SelectionStart, text.SelectionEnd), Math.Abs(text.SelectionEnd - text.SelectionStart), b, i); Refresh(); }
                        catch (InvalidDataException ex) { await Dialogs.Message(this, "Định dạng dòng", ex.Message); }
                    };
                    context.Items.Add(action);
                }
                Format("Bôi đen: chữ đậm", true, null); Format("Bôi đen: chữ nghiêng", null, true); Format("Bôi đen: chữ thường", false, false);
                context.Items.Add(new Separator());
                var copy = new MenuItem { Header = "Sao chép" }; copy.Click += (_, _) => text.Copy(); context.Items.Add(copy);
                var paste = new MenuItem { Header = "Dán" }; paste.Click += (_, _) => text.Paste(); context.Items.Add(paste);
                text.ContextMenu = context;
                Refresh();
                lines.Children.Add(new StackPanel { Spacing = 4, Children = { text,
                    new WrapPanel { Children = { font, size, bold, italic } } } });
            }
        }
        pages.SelectionChanged += (_, _) => { foreach (var bitmap in _bitmaps) bitmap.Dispose(); _bitmaps.Clear(); ShowPage(); };
        var save = new Button { Name = "LayoutReviewSave", Content = "Chọn nơi lưu Word…", Classes = { "accent" } };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        save.Click += async (_, _) =>
        {
            try { layout.Validate(); Close(true); }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { await Dialogs.Message(this, "Kiểm tra lại chữ", ex.Message); }
        };
        var header = new StackPanel { Spacing = 8, Children = { warning, pages } };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 12) };
        var view = new ScrollViewer { Content = preview };
        var edits = new ScrollViewer { Content = lines, Name = "LayoutEditableLines" };
        columns.Children.Add(view); columns.Children.Add(edits); Grid.SetColumn(edits, 2);
        var footer = new StackPanel { Spacing = 6, Children = {
            new TextBlock { Text = "Trái: bố cục ước lượng, không phải bản render Word. Phải mở Word kiểm tra lại trước khi sử dụng. Dòng dài vẫn giữ vị trí gốc; thêm nhiều chữ có thể cần dàn trang lại.", TextWrapping = TextWrapping.Wrap, FontSize = 11 },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } } } };
        var root = new Grid { Margin = new Thickness(18), RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { header, columns, footer } };
        Grid.SetRow(columns, 1); Grid.SetRow(footer, 2); Content = root; ShowPage();
        Closed += (_, _) => { foreach (var bitmap in _bitmaps) bitmap.Dispose(); _bitmaps.Clear(); };
    }
}
