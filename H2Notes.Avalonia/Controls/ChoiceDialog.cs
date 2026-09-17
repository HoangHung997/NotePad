using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

public static class ChoiceDialog
{
    public static Task<string?> Show(Window owner, string title, string message, params (string Key, string Label, string Hint)[] choices)
    {
        var window = new Window { Title = title, Width = 560, SizeToContent = SizeToContent.Height, MaxHeight = 760,
            CanResize = false, ShowInTaskbar = false, CanMinimize = false, CanMaximize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        foreach (var choice in choices)
        {
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new TextBlock { Text = choice.Label, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = choice.Hint, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .8 });
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => window.Close(choice.Key); panel.Children.Add(button);
        }
        var cancel = new Button { Content = "Hủy", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => window.Close(null); panel.Children.Add(cancel);
        window.Content = new ScrollViewer { Content = panel };
        return window.ShowDialog<string?>(owner);
    }
}
