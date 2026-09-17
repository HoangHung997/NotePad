using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class NoteWindow : Window
{
    private readonly NoteRecord _note;
    private readonly RichEditor _editor = new();
    private readonly TextBox _title;
    public Guid NoteId => _note.Id;
    public NoteWindow(App app, NoteRecord note)
    {
        _note = note; Icon = app.Icon; Title = note.Title;
        MinWidth = 320; MinHeight = 300; Topmost = note.IsPinned;
        _title = new TextBox { Text = note.Title, FontFamily = new global::Avalonia.Media.FontFamily("Georgia"), FontSize = 23, FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(18, 12), BorderThickness = new Thickness(0), Background = global::Avalonia.Media.Brushes.Transparent };
        var close = AppIcon.Button(IconKind.Close, "Ẩn ghi chú"); close.Name = "CloseButton";
        ToolTip.SetTip(close, "Ẩn ghi chú"); close.Click += (_, _) => Close();
        var titleBar = new Grid { Name = "TitleBar", ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Background = global::Avalonia.Media.Brush.Parse("#FAF7F2") };
        titleBar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new Border { Background = RichEditor.Brush("#A4573D"), CornerRadius = new CornerRadius(3), Width = 28, Height = 28,
                Child = new TextBlock { Text = "H2", FontSize = 17, FontWeight = global::Avalonia.Media.FontWeight.Bold, Foreground = global::Avalonia.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } },
                new TextBlock { Text = "H2 Notes", FontSize = 19, VerticalAlignment = VerticalAlignment.Center } } });
        var pin = AppIcon.Button(note.IsPinned ? IconKind.PinFilled : IconKind.Pin, "Ghim trên cùng");
        pin.Click += (_, _) => { Topmost = note.IsPinned = !Topmost; ((AppIcon)pin.Content!).Kind = Topmost ? IconKind.PinFilled : IconKind.Pin; app.ScheduleSave(); };
        Grid.SetColumn(pin, 1); titleBar.Children.Add(pin); Grid.SetColumn(close, 2); titleBar.Children.Add(close);
        var layout = new Grid { RowDefinitions = new RowDefinitions("44,Auto,Auto,*,30") };
        layout.Children.Add(titleBar); Grid.SetRow(_title, 1); layout.Children.Add(_title);
        var toolbar = _editor.CreateToolbar(); Grid.SetRow(toolbar, 2); layout.Children.Add(toolbar);
        _editor.Margin = new Thickness(18, 10); Grid.SetRow(_editor, 3); layout.Children.Add(_editor);
        var footer = new TextBlock { Text = "Ghi chú riêng · Tự lưu", Margin = new Thickness(18, 4), FontSize = 11, Foreground = RichEditor.Brush("#837568") }; Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Content = new Border { BorderThickness = new Thickness(1), BorderBrush = global::Avalonia.Media.Brush.Parse("#CCC6BE"), Background = RichEditor.Brush("#FCFAF7"), Child = layout };
        DesktopWindowChrome.Attach(this, titleBar);
        _editor.Load(note.ReadContent()); _editor.Changed += app.ScheduleSave;
        WindowPlacement.Attach(this, () => _note, app, false);
        _title.TextChanged += (_, _) => { Title = _title.Text ?? ""; app.ScheduleSave(); };
        Activated += (_, _) => Opacity = 1;
        Deactivated += (_, _) => Opacity = Math.Clamp(note.NoteOpacity, .01, 1);
        Closing += (_, e) =>
        {
            if (app.IsExiting || app.IsChangingStore || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            Flush(); e.Cancel = true; _note.IsVisibleOnDesktop = false; Hide(); app.SaveNow();
        };
    }
    public void Flush()
    {
        _note.Title = _title.Text ?? "";
        if (_editor.HasChanges) { _note.ContentRich = _editor.Snapshot(); _editor.MarkSaved(); }
        if (WindowState == WindowState.Normal && IsVisible) { _note.Left = Position.X; _note.Top = Position.Y; _note.Width = Width; _note.Height = Height; }
    }
}
