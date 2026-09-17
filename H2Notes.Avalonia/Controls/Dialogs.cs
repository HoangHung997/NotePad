using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace H2Notes.Avalonia.Controls;

public static class Dialogs
{
    public static async Task<string?> ChooseColor(Window owner)
    {
        var window = Create(owner, "Chọn màu RGB");
        var preview = new Border { Height = 70, CornerRadius = new CornerRadius(6) };
        var red = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 0, FormatString = "0" };
        var green = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 143, FormatString = "0" };
        var blue = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 153, FormatString = "0" };
        var hex = new TextBox { Text = "#008F99" };
        var ok = new Button { Content = "Chọn màu", IsDefault = true };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        var syncing = false;
        void UpdatePreview()
        {
            if (syncing) return;
            syncing = true;
            var color = global::Avalonia.Media.Color.FromRgb((byte)(red.Value ?? 0), (byte)(green.Value ?? 0), (byte)(blue.Value ?? 0));
            preview.Background = new global::Avalonia.Media.SolidColorBrush(color);
            hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; ok.IsEnabled = true; syncing = false;
        }
        red.ValueChanged += (_, _) => UpdatePreview(); green.ValueChanged += (_, _) => UpdatePreview(); blue.ValueChanged += (_, _) => UpdatePreview();
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (!global::Avalonia.Media.Color.TryParse(hex.Text, out var color)) { ok.IsEnabled = false; return; }
            syncing = true; red.Value = color.R; green.Value = color.G; blue.Value = color.B;
            preview.Background = new global::Avalonia.Media.SolidColorBrush(color); ok.IsEnabled = true; syncing = false;
        };
        var channels = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10 };
        var controls = new[] { red, green, blue }; var labels = new[] { "Đỏ · R", "Xanh lá · G", "Xanh dương · B" };
        for (var i = 0; i < 3; i++) { var panel = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = labels[i] }, controls[i] } }; Grid.SetColumn(panel, i); channels.Children.Add(panel); }
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 15, Children = { preview, channels, hex,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } } } };
        ok.Click += (_, _) => window.Close(hex.Text); cancel.Click += (_, _) => window.Close(null); UpdatePreview();
        return await window.ShowDialog<string?>(owner);
    }
    public static async Task<string?> Prompt(Window owner, string title, string hint, string value = "")
    {
        var window = Create(owner, title);
        var input = new TextBox { Text = value, AcceptsReturn = true, MinHeight = 80, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 85 };
        var cancel = new Button { Content = "Hủy", IsCancel = true, MinWidth = 85 };
        ok.Click += (_, _) => window.Close(input.Text);
        cancel.Click += (_, _) => window.Close(null);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children =
        {
            new TextBlock { Text = hint, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }, input,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } }
        } };
        window.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }
    public static async Task Message(Window owner, string title, string text)
    {
        var window = Create(owner, title);
        var ok = new Button { Content = "Đóng", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => window.Close();
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children = { new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap } }, ok } };
        await window.ShowDialog(owner);
    }
    public static async Task<bool> Confirm(Window owner, string title, string text, string confirmLabel = "Xóa")
    {
        var window = Create(owner, title);
        var ok = new Button { Content = confirmLabel, MinWidth = 80 };
        var cancel = new Button { Content = "Hủy", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => window.Close(true); cancel.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 18, Children = { new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } } };
        return await window.ShowDialog<bool>(owner);
    }
    private static Window Create(Window owner, string title) => new()
    {
        Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
        CanMinimize = false, CanMaximize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = owner.Topmost, ShowInTaskbar = false
    };
}
