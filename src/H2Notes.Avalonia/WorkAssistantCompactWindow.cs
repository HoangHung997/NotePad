using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia;

public sealed class WorkAssistantCompactWindow : Window
{
    private readonly WorkAssistantSettings _settings;
    private readonly TextBox _prompt;
    private readonly TextBlock _status;

    public WorkAssistantCompactWindow(WorkAssistantSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Title = "Work Assistant";
        Width = 430;
        Height = 205;
        MinWidth = 360;
        MinHeight = 180;
        CanResize = true;
        ShowInTaskbar = false;
        CanMinimize = false;
        CanMaximize = false;
        Topmost = settings.AlwaysOnTop;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush.Parse("#FCFAF7");

        var title = new TextBlock
        {
            Text = "Work Assistant",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "×",
            Width = 30,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 8, 8, 4)
        };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        _prompt = new TextBox
        {
            Name = "WorkAssistantPrompt",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "Bạn muốn làm gì?",
            MinHeight = 72,
            Margin = new Thickness(12, 4)
        };
        _status = new TextBlock
        {
            Name = "WorkAssistantCompactStatus",
            Text = "Hotkey chỉ mở trợ lý · chưa gửi hoặc thay đổi dữ liệu.",
            FontSize = 10,
            Foreground = Brush.Parse("#796C62"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 2, 12, 10)
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        root.Children.Add(header);
        Grid.SetRow(_prompt, 1);
        root.Children.Add(_prompt);
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;

        close.Click += (_, _) => Hide();
        DesktopWindowChrome.Attach(this, header);
    }

    public string PromptText
    {
        get => _prompt.Text ?? "";
        set => _prompt.Text = value ?? "";
    }

    public void ApplySettings()
        => Topmost = _settings.AlwaysOnTop;

    public void OpenFromHotkey()
    {
        ApplySettings();
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        _prompt.Focus();
        _prompt.CaretIndex = _prompt.Text?.Length ?? 0;
    }
}
