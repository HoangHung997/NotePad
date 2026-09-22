using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed class AgentArtifactView : TabControl
{
    protected override Type StyleKeyOverride => typeof(TabControl);
    public AgentArtifactView(H2AgentEvidence evidence, IH2AgentAdapter? adapter = null)
    {
        Styles.Add(new global::Avalonia.Styling.Style(x=>x.OfType<TabItem>()) { Setters={
            new global::Avalonia.Styling.Setter(FontSizeProperty,13d),
            new global::Avalonia.Styling.Setter(PaddingProperty,new Thickness(9,10)) } });
        var preview = new StackPanel { Spacing = 12 };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = evidence.Summary };
        preview.Children.Add(status);
        var metadata = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"Mã: {evidence.EvidenceId}\nLoại: {evidence.Kind}\nNguồn: {evidence.Provenance}\nTệp: {evidence.LocalPath}\nURI: {evidence.SourceUri}\nSHA-256: {evidence.Sha256}" };
        var path = evidence.LocalPath;
        if (path is not null && Path.IsPathFullyQualified(path) && File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var allowed = AiDocuments.Extensions.Contains(ext) || ext is ".log" or ".diff";
            var open = new Button { Content = ext switch { ".xlsx"=>"Mở bằng Excel", ".docx"=>"Mở bằng Word", _=>"Mở tệp gốc" }, IsEnabled = allowed, FontSize = 12 };
            open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } };
            preview.Children.Add(open);
            try
            {
                if (ext is ".xlsx" or ".csv") preview.Children.Add(Spreadsheet(path));
                else if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
                {
                    Bitmap? bitmap = null;
                    var picture = new Image { Stretch = Stretch.Uniform, MaxHeight = 600 };
                    picture.AttachedToVisualTree += (_, _) => { bitmap ??= new Bitmap(path); picture.Source = bitmap; };
                    picture.DetachedFromVisualTree += (_, _) => { picture.Source = null; bitmap?.Dispose(); bitmap = null; };
                    preview.Children.Add(picture);
                }
                else if (ext == ".docx")
                {
                    preview.Children.Add(new TextBlock { Text = "Bản xem nội dung và định dạng · mở Word để xem phân trang đầy đủ.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
                    preview.Children.Add(Word(path));
                }
                else if (ext is ".md" or ".txt" or ".json" or ".log" or ".diff")
                {
                    if (new FileInfo(path).Length <= 1_000_000)
                    {
                        var text = File.ReadAllText(path);
                        if (ext == ".md") { var md = new MarkdownMessageView(); md.SetMarkdown(text); preview.Children.Add(md); }
                        else preview.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") });
                    }
                    else status.Text = "Tệp lớn · mở bằng ứng dụng trên máy để xem đầy đủ.";
                }
                else if (ext == ".pdf" && adapter is not null) preview.Children.Add(Pdf(evidence, adapter));
                else status.Text = "Loại tệp này hiện chỉ có thông tin nguồn; không tự thực thi nội dung.";
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
            { status.Text = "Không đọc được bản xem trước: " + ex.Message; }
        }
        else if (path is not null) status.Text = "Tệp không còn ở vị trí đã lưu. Thông tin nguồn được giữ lại bên dưới.";
        ItemsSource = new[] {
            new TabItem { Header = "Xem trước", Content = new ScrollViewer { Content = preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } },
            new TabItem { Header = "Thay đổi", Content = new ScrollViewer { Content = new SelectableTextBlock {
                Text=path is not null && Path.GetExtension(path).Equals(".diff",StringComparison.OrdinalIgnoreCase) && File.Exists(path) && new FileInfo(path).Length<=1_000_000
                    ? File.ReadAllText(path) : "Chưa có bản so sánh trước/sau được ghi nhận cho tệp này.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8) } } },
            new TabItem { Header = "Bằng chứng", Content = new ScrollViewer { Content = new StackPanel { Spacing=12,Children={
                new TextBlock { Text=evidence.VerificationPassed switch { true=>"✓ Xác minh đạt",false=>"! Xác minh chưa đạt",_=>"Chỉ xem trước · chưa xác minh thay đổi" },TextWrapping=TextWrapping.Wrap },
                new SelectableTextBlock { Text=evidence.Summary,TextWrapping=TextWrapping.Wrap },
                new TextBlock { Text="Nguồn: "+(evidence.Provenance??"Tệp được chọn"),TextWrapping=TextWrapping.Wrap },
                new Expander { Header="Thông tin kỹ thuật",Content=metadata } } } } } };
    }

    private static Control Word(string path)
    {
        var root=new StackPanel { Spacing=8 };
        var zoom=new Slider { Minimum=.5,Maximum=2,Value=1,Width=120 };
        var zoomText=new TextBlock { Text="Vừa khung",FontSize=12,VerticalAlignment=VerticalAlignment.Center };
        var page=new Viewbox { Child=WordDocumentPreview.Read(path),Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left };
        void Fit()
        {
            page.Width=Math.Max(240,root.Bounds.Width-8)*zoom.Value;
            zoomText.Text=zoom.Value==1 ? "Vừa khung" : Math.Round(zoom.Value*100)+"% khung";
        }
        zoom.ValueChanged+=(_,_)=>Fit();root.SizeChanged+=(_,_)=>Fit();
        var reset=new Button { Content="Vừa khung",FontSize=12 };reset.Click+=(_,_)=>zoom.Value=1;
        root.Children.Add(new StackPanel { Orientation=Orientation.Horizontal,Spacing=8,Children={zoomText,zoom,reset} });
        root.Children.Add(new ScrollViewer { Content=page,MaxHeight=700,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto });
        return root;
    }

    private static Control Pdf(H2AgentEvidence evidence, IH2AgentAdapter adapter)
    {
        var root = new StackPanel { Spacing = 8 };
        var previous = new Button { Content = "‹" }; var next = new Button { Content = "›" };
        var pageLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var zoom = new Slider { Minimum = .5, Maximum = 2, Value = 1, Width = 100 };
        var picture = new Image { Stretch = Stretch.Uniform, Width = 600 };
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        CancellationTokenSource? cts = null; var page = 0; var pages = 1; Bitmap? bitmap = null;
        async Task Load()
        {
            var current = cts;
            if (current is null || current.IsCancellationRequested) return;
            previous.IsEnabled = next.IsEnabled = false; pageLabel.Text = "Đang mở trang…";
            try
            {
                var preview = await adapter.PreviewDocumentPdfAsync(evidence, page, current.Token);
                if (current.IsCancellationRequested || !ReferenceEquals(current, cts)) return;
                using var input = new MemoryStream(preview.Png); var nextBitmap = new Bitmap(input);
                picture.Source = nextBitmap; bitmap?.Dispose(); bitmap = nextBitmap;
                text.Text = preview.Text.Length == 0 ? "Trang ảnh · không tự chạy OCR." : preview.Text;
                pages = preview.PageCount; pageLabel.Text = $"Trang {page + 1}/{pages}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (ReferenceEquals(current, cts)) pageLabel.Text = ex.Message; }
            finally { if (ReferenceEquals(current, cts)) { previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < pages; } }
        }
        previous.Click += async (_, _) => { page--; await Load(); }; next.Click += async (_, _) => { page++; await Load(); };
        void FitPage()=>picture.Width=Math.Max(200,root.Bounds.Width-8)*zoom.Value;
        zoom.ValueChanged += (_, _) => FitPage();root.SizeChanged+=(_,_)=>FitPage();
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { previous, pageLabel, next, zoom } });
        root.Children.Add(new ScrollViewer { Content = picture, MaxHeight = 700, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(new Expander { Header = "Văn bản trong trang", Content = text });
        root.AttachedToVisualTree += async (_, _) => { cts = new CancellationTokenSource(); await Load(); };
        root.DetachedFromVisualTree += (_, _) => { cts?.Cancel(); cts = null; picture.Source = null; bitmap?.Dispose(); bitmap = null; };
        return root;
    }

    private static Control Spreadsheet(string path)
    {
        var root = new StackPanel { Spacing = 6 };
        var formulas = new CheckBox { Name="SpreadsheetFormulaMode", Content="Hiện công thức", FontSize=12 };
        var sheets = new ComboBox { ItemsSource = AgentSpreadsheetPreview.SheetNames(path), SelectedIndex = 0 };
        var selected = new SelectableTextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = "Chọn ô để xem địa chỉ, giá trị lưu và công thức." };
        var previous = new Button { Content = "‹" }; var next = new Button { Content = "›" };
        var copy = new Button { Content = "Sao chép trang", FontSize = 11 };
        var pageText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        var body = new ContentControl(); var page = 0; string copyText = "";
        void Render()
        {
            var data = AgentSpreadsheetPreview.Read(path, sheets.SelectedItem as string ?? "", page);
            var columns = data.Rows.SelectMany(r => r.Cells).Select(c => new string(c.Address.TakeWhile(char.IsLetter).ToArray()))
                .Distinct().OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToArray();
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(40)));
            var cellWidth=Math.Max(55,((body.Bounds.Width>0 ? body.Bounds.Width : 320)-40)/Math.Max(1,columns.Length));
            foreach (var _ in columns) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cellWidth)));
            for (var r = 0; r <= data.Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Cell(Control control, int row, int col) { Grid.SetRow(control, row); Grid.SetColumn(control, col); grid.Children.Add(control); }
            for (var c = 0; c < columns.Length; c++) Cell(new TextBlock { Text = columns[c], Margin = new Thickness(8, 4), FontWeight = FontWeight.SemiBold }, 0, c + 1);
            for (var r = 0; r < data.Rows.Count; r++)
            {
                var row = data.Rows[r]; Cell(new TextBlock { Text = row.Number.ToString(), Margin = new Thickness(3, 5), FontSize = 11 }, r + 1, 0);
                for (var c = 0; c < columns.Length; c++)
                {
                    var cell = row.Cells.FirstOrDefault(x => x.Address == columns[c] + row.Number);
                    if (cell is null) continue;
                    var button = new Button { Content = new TextBlock { Text = formulas.IsChecked==true && cell.Formula is not null ? "="+cell.Formula : cell.Value, TextTrimming = TextTrimming.CharacterEllipsis },
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                        FontSize = 12, Padding = new Thickness(6, 4), BorderThickness = new Thickness(0, 0, 1, 1), Background = Brushes.Transparent };
                    button.Click += (_, _) => selected.Text = cell.Address + " · " + cell.Value + (cell.Formula is null ? "" : "\nCông thức: =" + cell.Formula);
                    Cell(button, r + 1, c + 1);
                }
            }
            copyText = string.Join("\n", data.Rows.Select(r => string.Join("\t", columns.Select(c => { var cell=r.Cells.FirstOrDefault(x=>x.Address==c+r.Number);return formulas.IsChecked==true && cell?.Formula is {} formula ? "="+formula : cell?.Value??""; }))));
            body.Content = new ScrollViewer { Content = grid, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            previous.IsEnabled = page > 0; next.IsEnabled = data.HasMore; pageText.Text = "Trang " + (page + 1);
        }
        sheets.SelectionChanged += (_, _) => { page = 0; Render(); };
        formulas.IsCheckedChanged += (_,_)=>Render();
        body.SizeChanged+=(_,e)=> { if(Math.Abs(e.NewSize.Width-e.PreviousSize.Width)>.5)Render(); };
        previous.Click += (_, _) => { page--; Render(); }; next.Click += (_, _) => { page++; Render(); };
        copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(root)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(copyText); };
        root.Children.Add(sheets); root.Children.Add(formulas); root.Children.Add(selected); root.Children.Add(body);
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { previous, pageText, next, copy } });
        Render(); return root;
    }
}
