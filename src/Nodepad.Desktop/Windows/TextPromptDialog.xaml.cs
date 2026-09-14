using System.Windows;

namespace Nodepad.Desktop.Windows;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string label, string initialText = "")
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = label;
        ValueTextBox.Text = initialText;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string ValueText => ValueTextBox.Text.Trim();

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueText))
        {
            ValueTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
