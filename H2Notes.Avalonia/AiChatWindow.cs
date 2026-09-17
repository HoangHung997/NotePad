using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class AiChatWindow : Window
{
    private readonly NoteRecord _notebook;
    private readonly AiChatPanel _chat;
    private readonly TextBlock _saveStatus = new() { Text = "Tự lưu", FontSize = 10, Foreground = RichEditor.Brush("#837568"), Margin = new Thickness(12, 2) };
    public Guid NotebookId => _notebook.Id;
    public AiChatWindow(App app, NoteRecord notebook)
    {
        if (!notebook.IsChat) throw new ArgumentException("Expected an independent AI notebook.", nameof(notebook));
        _notebook = notebook; _chat = new AiChatPanel(app);
        Title = "H2 Notes · Chat ngoài dự án đã lưu ở bản trước"; Icon = app.Icon;
        MinWidth = 340; MinHeight = 420; Topmost = notebook.IsPinned;
        var titleBar = new Grid { Name = "TitleBar", ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Height = 44, Background = RichEditor.Brush("#FAF7F2") };
        titleBar.Children.Add(new AppIcon(IconKind.SparkleFilled, 22) { Foreground = RichEditor.Brush("#A4573D"), Margin = new Thickness(12, 0, 8, 0) });
        var title = new TextBlock { Text = "Chat cũ · ngoài dự án", FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, "Giữ lại hội thoại của bản trước; không phải cửa sổ AI của dự án đang chọn.");
        Grid.SetColumn(title, 1); titleBar.Children.Add(title);
        var settings = AppIcon.Button(IconKind.Settings, "Thiết lập AI"); settings.Padding = new Thickness(7);
        settings.Click += async (_, _) => await _chat.ShowSettings(); Grid.SetColumn(settings, 2); titleBar.Children.Add(settings);
        var pin = AppIcon.Button(Topmost ? IconKind.PinFilled : IconKind.Pin, "Ghim AI trên màn hình"); pin.Name = "PinChatButton"; pin.Padding = new Thickness(7);
        pin.Click += (_, _) => { Topmost = _notebook.IsPinned = !Topmost; ((AppIcon)pin.Content!).Kind = Topmost ? IconKind.PinFilled : IconKind.Pin; app.ScheduleSave(); };
        Grid.SetColumn(pin, 3); titleBar.Children.Add(pin);
        var close = AppIcon.Button(IconKind.Close, "Ẩn AI, giữ lịch sử"); close.Name = "CloseButton"; close.Padding = new Thickness(7);
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 4); titleBar.Children.Add(close);
        var layout = new Grid { RowDefinitions = new RowDefinitions("44,*,22") };
        layout.Children.Add(titleBar); Grid.SetRow(_chat, 1); layout.Children.Add(_chat); Grid.SetRow(_saveStatus, 2); layout.Children.Add(_saveStatus);
        Content = new Border { Background = RichEditor.Brush("#FCFAF7"), BorderBrush = RichEditor.Brush("#CCC6BE"), BorderThickness = new Thickness(1), Child = layout };
        DesktopWindowChrome.Attach(this, titleBar); WindowPlacement.Attach(this, () => _notebook, app, false);
        _chat.SetStandalone(notebook);
        Closing += (_, e) =>
        {
            _chat.Cancel(); Flush();
            if (app.IsExiting || app.IsChangingStore || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            e.Cancel = true; _notebook.IsVisibleOnDesktop = false; Hide(); app.SaveNow();
        };
    }
    public Task StopAiAsync() => _chat.StopAsync();
    public void CancelAi() => _chat.Cancel();
    public void RefreshAiConnections() => _chat.RefreshConnections();
    public void SetSaveStatus(string text) => _saveStatus.Text = text;
    public void Flush()
    {
        _chat.Flush();
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _notebook.Left = Position.X; _notebook.Top = Position.Y; _notebook.Width = Width; _notebook.Height = Height;
    }
}
