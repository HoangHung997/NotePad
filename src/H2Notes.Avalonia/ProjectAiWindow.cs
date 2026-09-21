using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class ProjectAiWindow : Window
{
    private readonly ProjectAiWindowState _placement;
    private readonly ContentControl _host = new() { Name = "ProjectAiHost" };
    private readonly TextBlock _projectTitle = new() { Name = "AiProjectTitle", FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _saveStatus = new() { Text = "Lịch sử lưu trong dự án", FontSize = 10, Margin = new Thickness(12, 2), Foreground = RichEditor.Brush("#837568") };
    public Guid WindowId => _placement.Id;
    public Guid? ProjectId { get; private set; }

    public ProjectAiWindow(App app, MainWindow main, ProjectAiWindowState placement)
    {
        _placement = placement;
        Title = "H2 Notes · Agent dự án"; Icon = app.Icon; MinWidth = 340; MinHeight = 420; Topmost = placement.IsPinned;
        var titleBar = new Grid { Name = "TitleBar", ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), Height = 40, Background = RichEditor.Brush("#FAF7F2") };
        titleBar.Children.Add(new AppIcon(IconKind.SparkleFilled, 20) { Foreground = RichEditor.Brush("#A4573D"), Margin = new Thickness(12, 0, 8, 0) });
        var title = new TextBlock { Text = "Agent dự án", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); titleBar.Children.Add(title);
        void Add(Button button, int column) { button.Padding = new Thickness(6); Grid.SetColumn(button, column); titleBar.Children.Add(button); }
        var settings = AppIcon.Button(IconKind.Settings, "Thiết lập AI"); settings.Click += async (_, _) => { if (_host.Content is AiChatPanel chat) await chat.ShowSettings(); }; Add(settings, 2);
        var dock = AppIcon.Button(IconKind.Dock, "Ghép Agent về Project Workspace"); dock.Name = "DockProjectAiButton";
        dock.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (mode, label) in new[] { ("floating", "Nổi trong bảng dự án"), ("right", "Ghim bên phải bảng dự án"), ("bottom", "Ghim phía dưới bảng dự án") })
            { var item = new MenuItem { Header = label }; item.Click += (_, _) => { main.DockProjectAi(mode); app.ShowMain(); }; menu.Items.Add(item); }
            menu.Open(dock);
        }; Add(dock, 3);
        var pin = AppIcon.Button(Topmost ? IconKind.PinFilled : IconKind.Pin, "Ghim Agent trên màn hình"); pin.Name = "PinChatButton";
        pin.Click += (_, _) => { Topmost = placement.IsPinned = !Topmost; ((AppIcon)pin.Content!).Kind = Topmost ? IconKind.PinFilled : IconKind.Pin; app.ScheduleSave(); }; Add(pin, 4);
        var close = AppIcon.Button(IconKind.Close, "Ẩn Agent, giữ lịch sử dự án"); close.Name = "CloseButton"; close.Click += (_, _) => Close(); Add(close, 5);
        var pickerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        pickerContent.Children.Add(new AppIcon(IconKind.Folder, 17) { Margin = new Thickness(0, 0, 8, 0) });
        Grid.SetColumn(_projectTitle, 1); pickerContent.Children.Add(_projectTitle);
        var chevron = new AppIcon(IconKind.ChevronDown, 14) { Margin = new Thickness(6, 0, 0, 0) }; Grid.SetColumn(chevron, 2); pickerContent.Children.Add(chevron);
        var picker = new Button { Name = "AiProjectPicker", Content = pickerContent, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(10, 0, 10, 6), Padding = new Thickness(8, 6) };
        ToolTip.SetTip(picker, "Đổi dự án · dùng cùng Agent thread và bản nháp với Project Workspace");
        picker.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var project in main.AiProjects)
            {
                var item = new MenuItem { Header = project.DisplayName, ToggleType = MenuItemToggleType.Radio, IsChecked = project.Id == ProjectId };
                item.Click += (_, _) => main.SelectAiProject(project.Id); menu.Items.Add(item);
            }
            menu.Open(picker);
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("40,Auto,*,22") };
        layout.Children.Add(titleBar); Grid.SetRow(picker, 1); layout.Children.Add(picker);
        Grid.SetRow(_host, 2); layout.Children.Add(_host); Grid.SetRow(_saveStatus, 3); layout.Children.Add(_saveStatus);
        Content = new Border { Background = RichEditor.Brush("#FCFAF7"), BorderBrush = RichEditor.Brush("#CCC6BE"), BorderThickness = new Thickness(1), Child = layout };
        DesktopWindowChrome.Attach(this, titleBar);
        WindowPlacement.Attach(this, app, placement.Width, placement.Height, () => new PixelPoint(placement.Left, placement.Top));
        Closing += (_, e) =>
        {
            Flush();
            if (app.IsExiting || app.IsChangingStore || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            e.Cancel = true; main.CancelAi(); main.DockProjectAi("hidden"); app.SaveNow();
        };
    }
    internal void AttachChat(AiChatPanel chat) => _host.Content = chat;
    internal void ReleaseChat() => MainWindow.DetachChatHost(_host);
    internal void UpdateProject(ProjectRecord? project)
    {
        ProjectId = project?.Id; _projectTitle.Text = project?.DisplayName ?? "Chưa có dự án · thêm ở bảng dự án";
        ToolTip.SetTip(_projectTitle, _projectTitle.Text); Title = "H2 Notes · Agent · " + (project?.DisplayName ?? "Chọn dự án");
    }
    internal void SetSaveStatus(string text) => _saveStatus.Text = text + " · Lịch sử trong dự án";
    public void Flush()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _placement.Left = Position.X; _placement.Top = Position.Y; _placement.Width = Width; _placement.Height = Height;
    }
}
