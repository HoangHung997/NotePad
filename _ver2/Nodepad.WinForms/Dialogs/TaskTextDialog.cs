namespace Nodepad.WinForms.Dialogs;

public sealed class TaskTextDialog : Form
{
    private readonly TextBox _taskTextBox;

    public TaskTextDialog()
    {
        Text = "Add task";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 156);

        var label = new Label
        {
            Text = "Task text",
            AutoSize = true,
            Location = new Point(16, 16)
        };

        _taskTextBox = new TextBox
        {
            Location = new Point(16, 40),
            Width = 386
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(88, 32),
            Location = new Point(218, 100)
        };
        okButton.Click += OkButton_OnClick;

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(88, 32),
            Location = new Point(314, 100)
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.Add(label);
        Controls.Add(_taskTextBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);
    }

    public string TaskText => _taskTextBox.Text.Trim();

    private void OkButton_OnClick(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TaskText))
        {
            return;
        }

        DialogResult = DialogResult.None;
    }
}
