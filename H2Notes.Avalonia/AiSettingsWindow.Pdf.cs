using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed partial class AiSettingsWindow
{
    internal static string PdfBridgePath => Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "convert.py");
    private sealed record PdfChoice(AiPdfEngine Engine, string Label) { public override string ToString() => Label; }

    private Control BuildPdfSettings(App app)
    {
        var settings = app.LocalSettings.Ai.Pdf;
        var choices = new[] { new PdfChoice(AiPdfEngine.Direct, "Mặc định · Gửi PDF gốc cho LLM"),
            new PdfChoice(AiPdfEngine.GotOcr, "GOT-OCR 2.0 · PDF → Markdown"),
            new PdfChoice(AiPdfEngine.MinerU, "MinerU (Magic-PDF) · PDF → Markdown"),
            new PdfChoice(AiPdfEngine.Docling, "Docling · PDF → Markdown, ưu tiên CPU") };
        var engine = new ComboBox { Name = "AiPdfEngine", ItemsSource = choices, SelectedItem = choices.FirstOrDefault(c => c.Engine == settings.Engine) ?? choices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
        var root = new TextBox { Name = "AiOcrRuntimeRoot", Text = settings.RuntimeRoot, TextWrapping = TextWrapping.Wrap };
        var pages = new NumericUpDown { Name = "AiPdfMaxPages", Minimum = 1, Maximum = 100, Value = Math.Clamp(settings.MaxPages, 1, 100), Increment = 1 };
        var seconds = new NumericUpDown { Name = "AiPdfTimeout", Minimum = 30, Maximum = 600, Value = Math.Clamp(settings.TimeoutSeconds, 30, 600), Increment = 30 };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var images = new CheckBox { Name = "AiOcrImages", IsChecked = settings.OcrImages,
            Content = new TextBlock { Text = "OCR cả ảnh đính kèm (PNG, JPEG, WebP)", TextWrapping = TextWrapping.Wrap, FontSize = 12 } };
        var readiness = new TextBlock { Name = "AiOcrReadiness", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = RichEditor.Brush("#796C62") };
        var browse = new Button { Content = "Chọn thư mục bộ OCR…", FontSize = 12 };
        var check = new Button { Content = "Kiểm tra bộ đã cài", FontSize = 12 };
        var save = new Button { Content = "Lưu thiết lập PDF / ảnh", Classes = { "accent" } };
        var local = new StackPanel { Spacing = 7 };
        AiPdfSettings Draft() => new() { Engine = ((PdfChoice)engine.SelectedItem!).Engine, OcrImages = images.IsChecked == true,
            RuntimeRoot = root.Text?.Trim() ?? "", MaxPages = (int)(pages.Value ?? 100), TimeoutSeconds = (int)(seconds.Value ?? 180) };
        void Refresh()
        {
            var choice = (PdfChoice)engine.SelectedItem!;
            local.IsVisible = choice.Engine != AiPdfEngine.Direct;
            description.Text = choice.Engine switch
            {
                AiPdfEngine.Direct => "Gửi nguyên PDF khi bạn bấm Gửi. Chỉ dùng với model/endpoint đã xác nhận hỗ trợ PDF trên OpenAI hoặc Gemini; Ollama không nhận PDF gốc. Không tự chuyển sang hãng khác.",
                AiPdfEngine.GotOcr => "Nhận dạng từng trang trên máy rồi gửi Markdown. Chạy CPU có thể chậm; cần kiểm tra dấu tiếng Việt, bảng và chữ viết tay. Không khẳng định đây là bộ tốt nhất cho mọi tài liệu.",
                AiPdfEngine.MinerU => "Phân tích bố cục, bảng và OCR sang Markdown trên CPU. Bộ cài/model khá lớn; chỉ báo sẵn sàng sau khi đã cài và thử đọc thành công.",
                _ => "Đọc cấu trúc PDF, bảng và OCR trên CPU. Là lựa chọn bổ sung cho máy này; kết quả scan/tiếng Việt vẫn cần đối chiếu bản gốc."
            };
            readiness.Text = choice.Engine == AiPdfEngine.Direct ? "PDF và ảnh có giới hạn dung lượng riêng; app báo lỗi thay vì cắt nội dung." : AiPdfProcessor.RuntimeStatus(Draft(), PdfBridgePath);
        }
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn thư mục chứa runtime.json của bộ OCR", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) { root.Text = path; Refresh(); }
        };
        engine.SelectionChanged += (_, _) => Refresh(); check.Click += (_, _) => Refresh();
        save.Click += async (_, _) =>
        {
            try
            {
                var value = Draft();
                if (value.Engine != AiPdfEngine.Direct && !Path.IsPathFullyQualified(value.RuntimeRoot)) throw new InvalidOperationException("Chọn đường dẫn đầy đủ đến bộ OCR.");
                await app.StopAiAsync(); app.LocalSettings.Ai.Pdf = value; app.LocalSettings.Save();
                Refresh(); readiness.Text = "Đã lưu lựa chọn PDF / ảnh. " + readiness.Text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { readiness.Text = "Chưa lưu được: " + ex.Message; }
        };
        local.Children.Add(images);
        local.Children.Add(new TextBlock { Text = "Bật: trích chữ/bảng trong ảnh bằng bộ OCR đã chọn, gửi Markdown cho AI; giữ ảnh gốc trong lịch sử. Tắt: ảnh mới gửi trực tiếp cho model đọc ảnh, lựa chọn PDF không tác động đến ảnh. Ảnh đã OCR giữ kết quả trong lịch sử; đính kèm lại để phân tích hình ảnh trực tiếp.", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        local.Children.Add(new TextBlock { Text = "Thư mục bộ OCR (chỉ lưu trên máy này)", FontWeight = FontWeight.SemiBold, FontSize = 12 });
        local.Children.Add(root); local.Children.Add(new WrapPanel { Children = { browse, check } });
        local.Children.Add(new TextBlock { Text = "Giới hạn số trang / thời gian chờ (giây)", FontSize = 12 });
        var limits = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") }; limits.Children.Add(pages); limits.Children.Add(seconds); Grid.SetColumn(seconds, 1); seconds.Margin = new Thickness(8, 0, 0, 0); local.Children.Add(limits);
        local.Children.Add(new TextBlock { Text = "Không tải model khi gửi chat. Bộ OCR chạy cục bộ; Markdown chỉ gửi đến kết nối AI bạn đã chọn. Dừng AI sẽ dừng cả tiến trình đọc PDF.", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        var panel = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Tài liệu PDF và OCR ảnh", FontSize = 20, FontWeight = FontWeight.SemiBold }, engine, description, local, readiness, save } };
        Refresh();
        return new Border { Name = "AiPdfSettings", BorderBrush = RichEditor.Brush("#D9CFC5"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 18, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = panel };
    }
}
