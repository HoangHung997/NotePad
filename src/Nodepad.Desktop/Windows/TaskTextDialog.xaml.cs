using System.Windows;

namespace Nodepad.Desktop.Windows;

public partial class TaskTextDialog : Window
{
    public TaskTextDialog(string initialText = "")
    {
        InitializeComponent();
        TaskTextBox.Text = initialText;
        Loaded += (_, _) =>
        {
            TaskTextBox.Focus();
            TaskTextBox.SelectAll();
        };
    }

    public string TaskText => TaskTextBox.Text.Trim();

    private void OkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TaskText))
        {
            TaskTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
