using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    internal async Task CaptureChatInputEvidence(string directory)
    {
        await CaptureDocumentEvidence(directory);
        SetAiDock("right");
        var message = _board.Projects[2].Conversations[0].Messages.Last();
        var original = message.Content;
        var bubble = _chat.GetVisualDescendants().OfType<ChatMessageView>().Single(b => b.Message.Id == message.Id);
        bubble.Actions.Children.Clear();
        message.Status = "streaming"; message.Content = "";
        bubble.Refresh(); bubble.SetThinking(string.Join("\n", Enumerable.Range(1, 80)
            .Select(i => $"Dòng {i:00}/80 · Tiến trình minh họa, không phải suy nghĩ của model thật.")));
        var thinking = bubble.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "MessageThinking");
        thinking.IsExpanded = true;
        async Task Capture(Window window, string name, double width, double height)
        {
            window.WindowState = WindowState.Normal; window.Show();
            window.Width = width; window.Height = height;
            for (var retry = 0; retry < 30; retry++)
            {
                await Task.Delay(100); window.UpdateLayout();
                if (Math.Abs(window.Bounds.Width - width) < 1 && Math.Abs(window.Bounds.Height - height) < 1) break;
            }
            if (Math.Abs(window.Bounds.Width - width) >= 1 || Math.Abs(window.Bounds.Height - height) >= 1)
            {
                File.AppendAllText(Path.Combine(directory, "layout-sizes.txt"), $"SKIPPED {name}: requested {width}x{height}, actual {window.Bounds.Size}\n");
                return;
            }
            if (window == this) ApplyResponsive();
            window.UpdateLayout();
            File.AppendAllText(Path.Combine(directory, "layout-sizes.txt"), $"{name}: {window.Bounds.Size}; native scaling {window.RenderScaling}\n");
            if (name.StartsWith("thinking-"))
            {
                var scroll = (ScrollViewer)thinking.Content!;
                File.AppendAllText(Path.Combine(directory, "thinking-scroll.txt"), $"{name}: offset={scroll.Offset.Y}; end={scroll.Extent.Height - scroll.Viewport.Height}; latestLine=80/80\n");
            }
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, name + "-render96.png"), PngBitmapEncoderOptions.Default);
        }
        await Capture(this, "thinking-docked-1440x860", 1440, 860);
        bubble.SetThinking(string.Join("\n", Enumerable.Range(81, 80)
            .Select(i => $"Dòng {i:000}/160 · Tiến trình minh họa sau cập nhật, không gọi model.")));
        await Capture(this, "thinking-compact-560x820", 560, 820);
        message.Content = AiArtifacts.WithoutBlocks(original); message.Status = "complete"; bubble.Refresh();
        var composer = _chat.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ChatComposer");
        composer.Focus(); composer.Text = "@"; composer.CaretIndex = 1;
        await Capture(this, "commands-compact-560x820", 560, 820);
        var popup = _chat.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.Popup>().FirstOrDefault(p => p.Name == "ChatMentionPopup");
        if (popup?.IsOpen == true && popup.Child is { } popupContent)
        {
            popupContent.UpdateLayout(); var size = popupContent.Bounds.Size;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
            bitmap.Render(popupContent); bitmap.Save(Path.Combine(directory, "commands-popup-render96.png"), PngBitmapEncoderOptions.Default); popup.IsOpen = false;
        }
        var settings = new AiSettingsWindow(_app);
        await Capture(settings, "lan-settings-620x840", 620, 840);
        var pdfEngine = settings.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AiPdfEngine");
        pdfEngine.SelectedIndex = 2;
        settings.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AiOcrImages").IsChecked = true;
        settings.UpdateLayout(); await Task.Delay(200);
        ((ScrollViewer)settings.Content!).ScrollToEnd();
        await Capture(settings, "pdf-image-settings-mineru-620x840", 620, 840); settings.Close();
        var fixtureProfile = new AiProfile { Name = "OpenAI · mẫu giao diện, không gọi mạng", Model = "gpt-5.2", Protocol = AiProtocol.OpenAiResponses, BaseUrl = "https://api.openai.com/v1" };
        _board.Projects[2].Conversations[0].ProfileId = fixtureProfile.Id;
        _board.Projects[2].Conversations[0].ReasoningEffort = "high";
        _app.LocalSettings.Ai.Profiles.Add(fixtureProfile); _chat.RefreshConnections();
        composer.Text = "Tóm tắt công việc còn lại và đề xuất thứ tự thực hiện.";
        ShowProjectAiWindow();
        await Capture(DetachedAiWindow!, "composer-wide-900x620", 900, 620);
        await Capture(DetachedAiWindow!, "composer-minimum-340x420", 340, 420);
        _chat.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ChatModelPicker")
            .RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(200);
        var modelPopup = _chat.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.Popup>().Single(c => c.Name == "ChatModelPickerPopup");
        if (modelPopup.IsOpen && modelPopup.Child is { } modelCard)
        {
            modelCard.UpdateLayout(); var size = modelCard.Bounds.Size;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
            bitmap.Render(modelCard); bitmap.Save(Path.Combine(directory, "model-reasoning-popup-render96.png"), PngBitmapEncoderOptions.Default);
            modelPopup.IsOpen = false;
        }
        composer.Text = "@moc"; composer.Focus(); composer.CaretIndex = composer.SelectionStart = composer.SelectionEnd = composer.Text.Length;
        await Task.Delay(200);
        var markerPopup = _chat.GetVisualDescendants().OfType<global::Avalonia.Controls.Primitives.Popup>().Single(c => c.Name == "ChatMentionPopup");
        if (markerPopup.IsOpen && markerPopup.Child is { } markerCard)
        {
            markerCard.UpdateLayout(); var size = markerCard.Bounds.Size;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
            bitmap.Render(markerCard); bitmap.Save(Path.Combine(directory, "marker-context-popup-render96.png"), PngBitmapEncoderOptions.Default); markerPopup.IsOpen = false;
        }
        File.WriteAllText(Path.Combine(directory, "capture-info.txt"), "Actual Release controls with isolated synthetic fixtures, no model requests or live data changes. 96 DPI RenderTargetBitmap, NOT native screenshots.\nNative render scaling: " + RenderScaling);
    }

    internal async Task CaptureDocumentEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        var profile = new AiProfile { Name = "AI minh họa · không gọi mạng", Model = "vision-demo" };
        _app.LocalSettings.Ai = new() { Profiles = [profile], SelectedId = profile.Id };
        var xlsx = new AiArtifact { FileName = "Cong-viec.xlsx", Sheets = [new() { Name = "Cong viec", Rows = [["Công việc", "Tình trạng"], ["Đối chiếu khối lượng", "Đang làm"], ["Gửi hồ sơ", "Chưa xong"]] }] };
        var docx = new AiArtifact { FileName = "Bao-cao.docx", Text = "Báo cáo dự án\nĐã hoàn thành hai công việc.\nCần gửi hồ sơ bổ sung trước thứ Sáu." };
        var conversation = new AiConversation { Title = "Tài liệu dự án · dữ liệu mẫu", ProfileId = profile.Id, Draft = "Trích xuất các công việc từ tệp này giúp tôi.",
            DraftAttachments = [AiDocuments.Read(docx.FileName, AiArtifacts.Create(docx))], Messages =
            [new() { Content = "Tóm tắt toàn bộ dự án và tạo bảng theo dõi Excel.", CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
             new() { Role = "assistant", Model = "Minh họa UI", Content = "Dự án có 3 việc; còn hồ sơ bổ sung cần xử lý. Đây là bản nháp bảng theo dõi, bạn có thể xem trước rồi lưu.\n```h2-file\n"
                 + System.Text.Json.JsonSerializer.Serialize(xlsx) + "\n```", CreatedAt = DateTime.UtcNow.AddMinutes(-1) }] };
        var project = _board.Projects[2]; project.Conversations = [conversation]; project.SelectedAiConversationId = conversation.Id;
        SelectCurrent(_board.Projects[0]); SelectCurrent(project);
        async Task Capture(Window window, string name, double width, double height)
        {
            window.Width = width; window.Height = height; window.Show(); await Task.Delay(650);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, name + "-render96.png"), PngBitmapEncoderOptions.Default);
        }
        SetAiDock("right"); await Capture(this, "files-docked-1440x860", 1440, 860);
        await Capture(this, "files-compact-560x820", 560, 820);
        ShowProjectAiWindow(); await Capture(DetachedAiWindow!, "files-desktop-420x700", 420, 700);
        await Capture(DetachedAiWindow!, "files-minimum-340x420", 340, 420);
        File.WriteAllText(Path.Combine(directory, "capture-info.txt"), "Actual Release controls; 96 DPI RenderTargetBitmap, NOT native screenshots. Isolated demo-only fixture, no AI calls.\nNative scaling: " + RenderScaling);
    }

    internal async Task CaptureAiErrorEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        var cloud = new AiProfile { Name = "Ollama Cloud (minh họa)", Model = "minimax-m3:cloud" };
        var google = new AiProfile { Name = "Google Gemini (minh họa)", Model = "gemini-2.5-flash-lite", Protocol = AiProtocol.Gemini, BaseUrl = "https://generativelanguage.googleapis.com/v1beta" };
        // Demo-only fixture. Random profile IDs have no saved credentials; never call AI here.
        _app.LocalSettings.Ai = new() { Profiles = [cloud, google], SelectedId = cloud.Id };
        var project = _board.Projects[2];
        var message = new AiMessage { Role = "assistant", Model = cloud.Model, Status = "error", CreatedAt = DateTime.UtcNow,
            ErrorText = AiFailure.FromStatus(System.Net.HttpStatusCode.PaymentRequired, "").Message };
        var conversation = new AiConversation { ProfileId = cloud.Id, Messages = [new() { Content = "Câu thử kết nối (dữ liệu minh họa)", CreatedAt = message.CreatedAt }, message] };
        project.Conversations = [conversation]; project.SelectedAiConversationId = conversation.Id;
        SelectCurrent(_board.Projects[0]); SelectCurrent(project); ShowProjectAiWindow();
        async Task Capture(Window window, string filename, double width, double height)
        {
            window.Width = width; window.Height = height; window.Show(); await Task.Delay(600);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, filename), PngBitmapEncoderOptions.Default);
        }
        await Capture(DetachedAiWindow!, "cloud-payment-420x700-render96.png", 420, 700);
        message.Model = google.Model; message.ErrorText = AiFailure.FromStatus(System.Net.HttpStatusCode.NotFound, "no longer available to new users").Message;
        conversation.ProfileId = google.Id; _app.LocalSettings.Ai.SelectedId = google.Id;
        SelectCurrent(_board.Projects[0]); SelectCurrent(project);
        await Capture(DetachedAiWindow!, "gemini-model-error-420x700-render96.png", 420, 700);
        var settings = new AiSettingsWindow(_app);
        await Capture(settings, "ai-settings-test-620x840-render96.png", 620, 840); settings.Close();
        File.WriteAllText(Path.Combine(directory, "capture-info.txt"), "Demo-only synthetic HTTP failures and random credential-free profiles. No network calls from captures.\nActual Release controls; 96 DPI RenderTargetBitmap, not a native screenshot. Native render scaling: " + RenderScaling);
    }

    internal async Task CaptureChatEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        var morning = new DateTime(2026, 9, 15, 1, 30, 0, DateTimeKind.Utc);
        var conversation = new AiConversation { Title = "Lịch sử mẫu · không gọi API", Messages =
        [
            new() { Role = "user", Content = "Tôi đang rà soát hồ sơ, cần kiểm tra những gì?", CreatedAt = morning.AddDays(-1) },
            new() { Role = "assistant", Content = "Đối chiếu khối lượng, bản vẽ và các phụ lục trước khi gửi.", Model = "Minh họa UI", CreatedAt = morning.AddDays(-1).AddMinutes(1) },
            new() { Role = "user", Content = "Bắt đầu kiểm tra phụ lục khối lượng.", IsTimelineMarker = true, CreatedAt = morning },
            new() { Role = "user", Content = "Nhắc tôi thứ tự rà soát hồ sơ.", CreatedAt = morning.AddMinutes(3) },
            new() { Role = "assistant", Content = "Kiểm tra danh mục trước, sau đó đối chiếu các số liệu chưa thống nhất.", Model = "Minh họa UI", CreatedAt = morning.AddMinutes(4) }
        ] };
        var project = _board.Projects[2]; project.Conversations = [ProjectWorkspaceStore.Clone(conversation)];
        project.SelectedAiConversationId = project.Conversations[0].Id;
        SelectCurrent(_board.Projects[0]); SelectCurrent(project);
        async Task Capture(Window window, string name, double width, double height)
        {
            window.Width = width; window.Height = height; window.Show();
            await Task.Delay(600);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, name + "-render96.png"), PngBitmapEncoderOptions.Default);
        }
        SetAiDock("floating"); await Capture(this, "project-floating-1040x760", 1040, 760);
        await Capture(this, "project-compact-560x820", 560, 820);
        ShowProjectAiWindow();
        await Capture(DetachedAiWindow!, "project-desktop-420x760", 420, 760);
        await Capture(DetachedAiWindow!, "project-desktop-minimum-340x420", 340, 420);
        SelectCurrent(_board.Projects[0]);
        await Capture(DetachedAiWindow!, "project-desktop-switch-420x660", 420, 660);
        SelectCurrent(project); SetAiDock("right");
        await Capture(this, "project-redocked-1440x860", 1440, 860);
        File.WriteAllText(Path.Combine(directory, "capture-info.txt"), "Release Avalonia controls with isolated, explicitly fabricated chat fixture. No live AI requests.\n96 DPI RenderTargetBitmap output; not native input/DPI/desktop certification.\nNative render scaling: " + RenderScaling);
    }

    // Explicit demo-only diagnostics: render the actual application controls,
    // not a second mockup. This is not a native desktop/input/DPI certification.
    internal async Task CaptureEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        if (_board.Projects.Count < 3) throw new InvalidOperationException("Evidence needs the isolated demo fixture.");
        var p = _board.Projects[2];
        var notes = RichDocument.Plain("Tạm hoàn thành HSHC, chờ chủ đầu tư chốt.\nCập nhật khối lượng khi có xác nhận mới.\n\nCần lưu ý\nKiểm tra phụ lục trước khi gửi.");
        notes.Format(0, notes.Text.Length, s => s with { Font = "Cambria", Color = "#302E2B" });
        var highlighted = notes.Text.IndexOf("Cập nhật", StringComparison.Ordinal);
        notes.Format(highlighted, "Cập nhật khối lượng khi có xác nhận mới.".Length, s => s with { Highlight = "#FFDDC7" });
        p.NotesRich = notes; p.Layout.NotesFraction = .44; SelectCurrent(p);
        async Task Capture(string id, double width, double height, string dock = "hidden", bool drawer = false, bool collapseTasks = false, string tab = "tasks")
        {
            WindowState = WindowState.Normal; Width = width; Height = height;
            p.Layout.AiDock = dock; p.Layout.TasksCollapsed = collapseTasks; p.Layout.NotesCollapsed = false; p.Layout.Tab = tab;
            _drawerOpen = drawer; ApplyResponsive();
            await Task.Delay(550); Opacity = 1;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
            bitmap.Render(this); bitmap.Save(Path.Combine(directory, id + "-" + width + "x" + height + "-render96.png"), PngBitmapEncoderOptions.Default);
        }
        await Capture("UI-01", 560, 820);
        await Capture("UI-02", 560, 820, drawer: true);
        await Capture("UI-03", 560, 820, collapseTasks: true, tab: "notes");
        await Capture("UI-04", 560, 820, "floating");
        await Capture("UI-05", 1040, 760);
        await Capture("UI-06", 1040, 760, "floating");
        await Capture("UI-07", 1440, 860, "right");
        await Capture("UI-08-docked", 1160, 820, "bottom");
        await Capture("UI-10", 560, 600);
        var note = new NoteRecord { Title = "Ghi chú trong ngày", NoteKind = "note", ContentRich = notes, Width = 560, Height = 600 };
        var noteWindow = new NoteWindow(_app, note); noteWindow.Show(); await Task.Delay(350); noteWindow.Opacity = 1;
        using (var bitmap = new RenderTargetBitmap(new PixelSize(560, 600), new Vector(96, 96)))
        { bitmap.Render(noteWindow); bitmap.Save(Path.Combine(directory, "UI-09-560x600-render96.png"), PngBitmapEncoderOptions.Default); }
        noteWindow.Hide(); await Capture("UI-05-final", 1040, 760); Activate();
        File.WriteAllText(Path.Combine(directory, "capture-info.txt"), "Actual Avalonia controls rendered by Release app with isolated demo data.\nRenderTargetBitmap: 96 DPI, logical DIP dimensions in filenames.\nNative screen scaling: " + RenderScaling + "\nNot native screen captures; no Windows input, IME, multimonitor or tray verification implied.\nAI content intentionally empty: no real API was called.\n");
    }
}
