using Nodepad.WinForms.Models;
using Nodepad.WinForms.Services;

namespace Nodepad.WinForms.Controls;

public sealed class ChecklistItemControl : UserControl
{
    private readonly ChecklistItem _item;
    private readonly CheckBox _checkBox;
    private readonly TextBox _textBox;
    private readonly Button _removeButton;

    public ChecklistItemControl(ChecklistItem item)
    {
        _item = item;

        Height = 56;
        Margin = new Padding(0, 0, 0, 8);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _checkBox = new CheckBox
        {
            Checked = _item.IsCompleted,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 18, 8, 0),
            AutoSize = true
        };
        _checkBox.CheckedChanged += CheckBox_OnCheckedChanged;

        _textBox = new TextBox
        {
            Text = _item.Text,
            Multiline = true,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ScrollBars = ScrollBars.Vertical
        };
        _textBox.TextChanged += TextBox_OnTextChanged;

        _removeButton = new Button
        {
            Text = "X",
            Width = 32,
            Height = 28,
            Margin = new Padding(8, 14, 0, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _removeButton.Click += RemoveButton_OnClick;

        layout.Controls.Add(_checkBox, 0, 0);
        layout.Controls.Add(_textBox, 1, 0);
        layout.Controls.Add(_removeButton, 2, 0);

        Controls.Add(layout);
    }

    public ChecklistItem Item => _item;

    public event EventHandler? ItemChanged;

    public event EventHandler? RemoveRequested;

    public void ApplyPalette(NotePalette palette, Font contentFont)
    {
        var surface = ColorHelper.FromHex(palette.SurfaceHex);
        var border = ColorHelper.FromHex(palette.BorderHex);
        var foreground = ColorHelper.FromHex(palette.ForegroundHex);

        BackColor = Color.Transparent;
        _checkBox.ForeColor = foreground;
        _textBox.BackColor = surface;
        _textBox.ForeColor = foreground;
        _textBox.BorderStyle = BorderStyle.FixedSingle;
        _textBox.Font = contentFont;
        _removeButton.BackColor = surface;
        _removeButton.ForeColor = foreground;
        _removeButton.FlatStyle = FlatStyle.Standard;
    }

    public void UpdateCardWidth(int width)
    {
        Width = Math.Max(120, width);
    }

    private void CheckBox_OnCheckedChanged(object? sender, EventArgs e)
    {
        _item.IsCompleted = _checkBox.Checked;
        ItemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TextBox_OnTextChanged(object? sender, EventArgs e)
    {
        _item.Text = _textBox.Text;
        ItemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveButton_OnClick(object? sender, EventArgs e)
    {
        RemoveRequested?.Invoke(this, EventArgs.Empty);
    }
}
