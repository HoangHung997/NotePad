using Nodepad.WinForms.Dialogs;
using Nodepad.WinForms.Models;
using Nodepad.WinForms.Services;

namespace Nodepad.WinForms.Controls;

public sealed class ProjectCardControl : UserControl
{
    private readonly ProjectEntry _project;
    private readonly Label _nameLabel;
    private readonly TextBox _nameEditor;
    private readonly Button _expandButton;
    private readonly Button _removeButton;
    private readonly Label _headerLineLabel;
    private readonly Label _previewLabel;
    private readonly Panel _detailsPanel;
    private readonly Button _toggleChecklistButton;
    private readonly Label _checklistProgressLabel;
    private readonly FlowLayoutPanel _checklistPanel;
    private readonly Button _addTaskButton;
    private readonly Label _notesLabel;
    private readonly RichTextBox _notesBox;
    private bool _suspendUi;
    private string _nameBeforeEdit = string.Empty;

    public ProjectCardControl(ProjectEntry project)
    {
        _project = project;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 12);
        Padding = new Padding(12);
        BorderStyle = BorderStyle.FixedSingle;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var headerTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Margin = Padding.Empty
        };
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _expandButton = new Button
        {
            Text = _project.IsExpanded ? "▼" : "▶",
            Width = 34,
            Height = 30,
            Margin = new Padding(0, 0, 8, 0)
        };
        _expandButton.Click += ExpandButton_OnClick;

        var namePanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 32,
            Margin = Padding.Empty
        };

        _nameLabel = new Label
        {
            Text = _project.DisplayName,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _nameLabel.DoubleClick += NameLabel_OnDoubleClick;

        _nameEditor = new TextBox
        {
            Text = _project.Name,
            Visible = false,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle
        };
        _nameEditor.Leave += NameEditor_OnLeave;
        _nameEditor.KeyDown += NameEditor_OnKeyDown;

        namePanel.Controls.Add(_nameLabel);
        namePanel.Controls.Add(_nameEditor);

        _removeButton = new Button
        {
            Text = "X",
            Width = 32,
            Height = 28,
            Margin = new Padding(8, 0, 0, 0)
        };
        _removeButton.Click += RemoveButton_OnClick;

        _headerLineLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };

        _previewLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };

        _detailsPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0)
        };

        var detailsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var checklistHeaderTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        checklistHeaderTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        checklistHeaderTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _toggleChecklistButton = new Button
        {
            Text = _project.IsChecklistExpanded ? "▼ Checklist" : "▶ Checklist",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty
        };
        _toggleChecklistButton.Click += ToggleChecklistButton_OnClick;

        _checklistProgressLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(8, 6, 0, 0)
        };

        checklistHeaderTable.Controls.Add(_toggleChecklistButton, 0, 0);
        checklistHeaderTable.Controls.Add(_checklistProgressLabel, 1, 0);

        _checklistPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 8, 0, 10)
        };

        _addTaskButton = new Button
        {
            Text = "Add task",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 10)
        };
        _addTaskButton.Click += AddTaskButton_OnClick;

        _notesLabel = new Label
        {
            Text = "Notes",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _notesBox = new RichTextBox
        {
            Dock = DockStyle.Top,
            Height = 96,
            BorderStyle = BorderStyle.FixedSingle,
            DetectUrls = false
        };
        _notesBox.TextChanged += NotesBox_OnTextChanged;

        detailsTable.Controls.Add(checklistHeaderTable, 0, 0);
        detailsTable.Controls.Add(_checklistPanel, 0, 1);
        detailsTable.Controls.Add(_addTaskButton, 0, 2);
        detailsTable.Controls.Add(_notesLabel, 0, 3);
        detailsTable.Controls.Add(_notesBox, 0, 4);
        _detailsPanel.Controls.Add(detailsTable);

        headerTable.Controls.Add(_expandButton, 0, 0);
        headerTable.Controls.Add(namePanel, 1, 0);
        headerTable.Controls.Add(_removeButton, 2, 0);

        root.Controls.Add(headerTable, 0, 0);
        root.Controls.Add(_headerLineLabel, 0, 1);
        root.Controls.Add(_previewLabel, 0, 2);
        root.Controls.Add(_detailsPanel, 0, 3);

        Controls.Add(root);

        Resize += ProjectCardControl_OnResize;

        RefreshFromProject();
        RebuildChecklistItems();
    }

    public ProjectEntry Project => _project;

    public event EventHandler? ProjectChanged;

    public event EventHandler? RemoveRequested;

    public void ApplyPalette(NotePalette palette, Font contentFont, Font titleFont)
    {
        var surface = ColorHelper.FromHex(palette.SurfaceHex);
        var border = ColorHelper.FromHex(palette.BorderHex);
        var accent = ColorHelper.FromHex(palette.AccentHex);
        var foreground = ColorHelper.FromHex(palette.ForegroundHex);

        BackColor = surface;
        ForeColor = foreground;

        _nameLabel.ForeColor = foreground;
        _nameLabel.Font = titleFont;
        _nameEditor.BackColor = surface;
        _nameEditor.ForeColor = foreground;
        _nameEditor.Font = titleFont;

        _expandButton.ForeColor = foreground;
        _removeButton.ForeColor = foreground;
        _headerLineLabel.ForeColor = accent;
        _headerLineLabel.Font = new Font(contentFont, FontStyle.Bold);
        _previewLabel.ForeColor = foreground;
        _previewLabel.Font = contentFont;
        _toggleChecklistButton.ForeColor = foreground;
        _toggleChecklistButton.Font = new Font(contentFont, FontStyle.Bold);
        _checklistProgressLabel.ForeColor = accent;
        _checklistProgressLabel.Font = new Font(contentFont, FontStyle.Bold);
        _addTaskButton.ForeColor = foreground;
        _notesLabel.ForeColor = foreground;
        _notesLabel.Font = new Font(contentFont, FontStyle.Bold);
        _notesBox.BackColor = ColorHelper.FromHex(palette.BackgroundHex);
        _notesBox.ForeColor = foreground;
        _notesBox.Font = contentFont;

        foreach (var control in _checklistPanel.Controls.OfType<ChecklistItemControl>())
        {
            control.ApplyPalette(palette, contentFont);
        }

        Invalidate();
    }

    public void UpdateCardWidth(int width)
    {
        if (width <= 0)
        {
            return;
        }

        Width = width;
        UpdateChecklistItemWidths();
    }

    private void RefreshFromProject()
    {
        _suspendUi = true;
        _nameLabel.Text = _project.DisplayName;
        if (!_nameEditor.Focused)
        {
            _nameEditor.Text = _project.Name;
        }

        _expandButton.Text = _project.IsExpanded ? "▼" : "▶";
        _toggleChecklistButton.Text = _project.IsChecklistExpanded ? "▼ Checklist" : "▶ Checklist";
        _headerLineLabel.Text = _project.HeaderLine;
        _previewLabel.Text = _project.Preview;
        _checklistProgressLabel.Text = _project.ProgressLabel;
        _detailsPanel.Visible = _project.IsExpanded;
        _checklistPanel.Visible = _project.IsExpanded && _project.IsChecklistExpanded;
        _addTaskButton.Visible = _project.IsExpanded && _project.IsChecklistExpanded;
        _notesLabel.Visible = _project.IsExpanded;
        _notesBox.Visible = _project.IsExpanded;
        if (!_notesBox.Focused)
        {
            _notesBox.Text = RichTextDocumentSerializer.ExtractPlainText(_project.Notes);
        }

        _suspendUi = false;
    }

    private void RebuildChecklistItems()
    {
        _checklistPanel.SuspendLayout();
        _checklistPanel.Controls.Clear();

        foreach (var item in _project.ChecklistItems)
        {
            var control = new ChecklistItemControl(item);
            control.ItemChanged += ChecklistItemControl_OnItemChanged;
            control.RemoveRequested += ChecklistItemControl_OnRemoveRequested;
            control.UpdateCardWidth(Math.Max(120, _checklistPanel.ClientSize.Width - 6));
            _checklistPanel.Controls.Add(control);
        }

        _checklistPanel.ResumeLayout(true);
        UpdateChecklistItemWidths();
    }

    private void UpdateChecklistItemWidths()
    {
        var width = Math.Max(120, _checklistPanel.ClientSize.Width - 6);
        foreach (var control in _checklistPanel.Controls.OfType<ChecklistItemControl>())
        {
            control.Width = width;
        }
    }

    private void ExpandButton_OnClick(object? sender, EventArgs e)
    {
        _project.IsExpanded = !_project.IsExpanded;
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void ToggleChecklistButton_OnClick(object? sender, EventArgs e)
    {
        _project.IsChecklistExpanded = !_project.IsChecklistExpanded;
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void AddTaskButton_OnClick(object? sender, EventArgs e)
    {
        using var dialog = new TaskTextDialog
        {
            StartPosition = FormStartPosition.CenterParent
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _project.ChecklistItems.Add(new ChecklistItem
        {
            Text = dialog.TaskText,
            IsCompleted = false
        });
        _project.TouchSummary();
        RebuildChecklistItems();
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void NotesBox_OnTextChanged(object? sender, EventArgs e)
    {
        if (_suspendUi)
        {
            return;
        }

        _project.Notes = _notesBox.Text;
        _project.TouchSummary();
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void ChecklistItemControl_OnItemChanged(object? sender, EventArgs e)
    {
        _project.TouchSummary();
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void ChecklistItemControl_OnRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not ChecklistItemControl control)
        {
            return;
        }

        _project.ChecklistItems.Remove(control.Item);
        _project.TouchSummary();
        RebuildChecklistItems();
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void RemoveButton_OnClick(object? sender, EventArgs e)
    {
        RemoveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NameLabel_OnDoubleClick(object? sender, EventArgs e)
    {
        _nameBeforeEdit = _project.Name;
        _nameLabel.Visible = false;
        _nameEditor.Visible = true;
        _nameEditor.Focus();
        _nameEditor.SelectAll();
    }

    private void NameEditor_OnLeave(object? sender, EventArgs e)
    {
        CommitNameEdit();
    }

    private void NameEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            CommitNameEdit();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode != Keys.Escape)
        {
            return;
        }

        _project.Name = _nameBeforeEdit;
        _nameEditor.Text = _nameBeforeEdit;
        CancelNameEdit();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void CommitNameEdit()
    {
        _project.Name = string.IsNullOrWhiteSpace(_nameEditor.Text)
            ? "Untitled Project"
            : _nameEditor.Text.Trim();
        CancelNameEdit();
        _project.TouchSummary();
        RefreshFromProject();
        RaiseProjectChanged();
    }

    private void CancelNameEdit()
    {
        _nameEditor.Visible = false;
        _nameLabel.Visible = true;
    }

    private void RaiseProjectChanged()
    {
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ProjectCardControl_OnResize(object? sender, EventArgs e)
    {
        UpdateChecklistItemWidths();
    }
}
