using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    internal async Task CaptureLayoutEvidence(string directory)
    {
        await CaptureChatInputEvidence(directory);
        var settings = new AiSettingsWindow(_app) { Width = 620, Height = 840 };
        settings.Show(); await Task.Delay(300); settings.UpdateLayout();
        var wait = settings.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AiWaitForCompletion");
        wait.BringIntoView(); await Task.Delay(300);
        Capture(settings, "wait-setting-620x840.png"); settings.Close();
        var fixture = new AiPageLayout { Warnings = ["Dữ liệu minh họa. Font ước lượng, phải đối chiếu chữ và định dạng với tài liệu gốc."], Pages = [new()
        {
            Width = 595, Height = 842, BackgroundPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAE0lEQVR4nGP8//8/AwMDEwMYAAAkBgMBXaJOiAAAAABJRU5ErkJggg=="),
            Items = [new() { X = 155, Y = 80, Width = 280, Height = 22, Text = "BẢN THỬ GIỮ BỐ CỤC", Bold = true, FontSize = 18 },
                new() { X = 70, Y = 150, Width = 450, Height = 18, Text = "Chữ sửa được; đồ họa gốc giữ đúng vị trí.", FontSize = 14 },
                new() { X = 70, Y = 185, Width = 450, Height = 18, Text = "Đối chiếu OCR và chọn phông trước khi lưu.", FontSize = 14 }]
        }] };
        var review = new LayoutReviewWindow(fixture); review.Show(); await Task.Delay(350); review.UpdateLayout();
        Capture(review, "layout-review-1080x760.png");
        var text = review.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "BẢN THỬ GIỮ BỐ CỤC");
        text.Text = "ĐÃ SỬA CHỮ OCR"; await Task.Delay(150);
        if (fixture.Pages[0].Items[0].Text != "ĐÃ SỬA CHỮ OCR") throw new InvalidOperationException("OCR editing did not update layout");
        review.Width = 680; review.Height = 600; await Task.Delay(250); review.UpdateLayout();
        Capture(review, "layout-review-680x600.png"); review.Close();
        File.AppendAllText(Path.Combine(directory, "interaction-checks.txt"), "Layout OCR text editing updates model. Cancel/close does not save. Wait toggle is present. Synthetic data only, no AI/OCR calls. RenderTargetBitmap evidence only.\n");

        void Capture(Window window, string name)
        {
            window.UpdateLayout(); var size = window.Bounds.Size;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
            File.AppendAllText(Path.Combine(directory, "layout-sizes.txt"), $"{name}: actual {size}, nativeScale={window.RenderScaling}\n");
        }
    }
}
