using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace H2Notes.Avalonia;

public partial class App
{
    // Explicit --demo --data --bubble-evidence only. No AI calls or user document mutations.
    private async Task CaptureWorkAssistantBubbleEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        _local.WorkAssistant.Enabled = true;
        var bubble = EnsureWorkAssistantBubble();
        var compact = EnsureWorkAssistantCompact();
        bubble.ShowInTaskbar = compact.ShowInTaskbar = true;
        bubble.Title = "H2 · Bong bóng kiểm thử";
        ShowWorkAssistantBubble();
        bubble.Position = new PixelPoint(700, 650);
        var evidence = new List<object>();
        async Task Capture(string name)
        {
            await Task.Delay(400); bubble.UpdateLayout();
            var scale = bubble.RenderScaling;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(bubble.Width * scale), (int)Math.Ceiling(bubble.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(bubble); bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
            evidence.Add(new { name, bubble.Width, bubble.Height, scale, bubble.Position, bubble.IsExpanded, bubble.ActivityText, chatVisible = compact.IsVisible });
        }
        SetWorkAssistantBubbleState(WorkAssistantBubbleState.Idle); await Capture("01-idle-circle");
        SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang suy nghĩ…"); await Capture("02-working-hidden-chat");
        SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang kiểm tra nội dung và định dạng tiếng Việt trong tài liệu Word, rồi lưu bản kết quả để bạn xem lại.");
        await Capture("03-long-activity"); await Task.Delay(2600); await Capture("04-long-activity-scrolled");
        compact.OpenFromHotkey(); await Capture("05-working-open-chat");
        compact.Hide(); await Capture("06-working-chat-hidden-again");
        SetWorkAssistantBubbleState(WorkAssistantBubbleState.Completed, "Đã xong"); await Capture("07-completed-circle");
        var area = bubble.Screens.ScreenFromWindow(bubble)?.WorkingArea;
        if (area is { } workArea)
        {
            bubble.Position = new PixelPoint(workArea.Right - (int)Math.Ceiling(54 * bubble.RenderScaling) - 8, workArea.Bottom - (int)Math.Ceiling(54 * bubble.RenderScaling) - 8);
            var anchor = bubble.Position;
            SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang kiểm tra kết quả…"); await Capture("08-working-right-edge");
            compact.OpenFromHotkey(); await Capture("09-right-edge-chat-open");
            if (Math.Abs(bubble.Position.X - anchor.X) > 1 || bubble.Position.Y != anchor.Y)
                throw new InvalidOperationException("Circle anchor moved when opening chat at the right edge.");
            compact.Hide(); await Capture("10-right-edge-chat-hidden");
            SetWorkAssistantBubbleState(WorkAssistantBubbleState.Completed, "Đã xong"); await Capture("11-right-edge-completed");
            bubble.Position = new PixelPoint(700, 650);
        }
        File.WriteAllText(Path.Combine(directory, "states.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));

        var row = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        void Button(string label, Action action) { var button = new Button { Content = label }; button.Click += (_, _) => action(); row.Children.Add(button); }
        Button("Bắt đầu làm việc", () => SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang đọc và kiểm tra tài liệu Word…"));
        Button("Đổi hoạt động", () => SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang lưu tệp và kiểm tra lại nội dung tiếng Việt trong bản kết quả."));
        Button("Mở / ẩn chat", () => { if (compact.IsVisible) compact.Hide(); else compact.OpenFromHotkey(); });
        Button("Hoàn thành", () => SetWorkAssistantBubbleState(WorkAssistantBubbleState.Completed, "Đã xong"));
        var control = new Window { Title = "H2 · Kiểm tra bong bóng", Width = 350, Height = 300, Content = row, Position = new PixelPoint(30, 100) };
        control.Show();
        File.WriteAllText(Path.Combine(directory, "complete.txt"), "Native Avalonia windows; synthetic public activities. PNGs rendered at actual monitor DPI. Manual mouse interaction remains separately recorded.");
    }
}
