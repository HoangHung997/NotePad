using Nodepad.WinForms.Controls;
using Nodepad.WinForms.Models;
using Nodepad.WinForms.Services;
using Timer = System.Windows.Forms.Timer;

namespace Nodepad.WinForms;

public sealed class NoteForm : Form
{
    private readonly NoteDocument _note;
    private readonly Action _persistState;
    private readonly Timer _autosaveTimer;
    private readonly ToolStrip _toolStrip;
    private readonly ToolStripTextBox _titleBox;
    private readonly ToolStripComboBox _fontFamilyComboBox;
    private readonly ToolStripComboBox _fontSizeComboBox;
    private readonly ToolStripButton _pinButton;
    private readonly ToolStripButton _starButton;
    private readonly ToolStripDropDownButton _paletteButton;
    private readonly ToolStripDropDownButton _moreButton;
    private readonly RichTextBox _bodyBox;
    private readonly Panel _projectHost;
    private readonly FlowLayoutPanel _projectFlow;
    private readonly Panel _newProjectPanel;
    private readonly TextBox _newProjectNameTextBox;
    private readonly Button _addProjectButton;
    private bool _suspendUi;
    private bool _allowClose;

    public NoteForm(NoteDocument note, Action persistState)
    {
        _note = note;
        _persistState = persistState;
        _autosaveTimer = new Timer { Interval = 360 };
        _autosaveTimer.Tick += AutosaveTimer_OnTick;

        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;

        _toolStrip = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            Dock = DockStyle.Top
        };

        _titleBox = new ToolStripTextBox
        {
            AutoSize = false,
            Width = 160
        };
        _titleBox.TextChanged += TitleBox_OnTextChanged;

        _fontFamilyComboBox = new ToolStripComboBox
        {
            AutoSize = false,
            Width = 150,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _fontFamilyComboBox.SelectedIndexChanged += FontFamilyComboBox_OnSelectedIndexChanged;

        _fontSizeComboBox = new ToolStripComboBox
        {
            AutoSize = false,
            Width = 70,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _fontSizeComboBox.SelectedIndexChanged += FontSizeComboBox_OnSelectedIndexChanged;

        _pinButton = new ToolStripButton("Pin")
        {
            CheckOnClick = true
        };
        _pinButton.CheckedChanged += PinButton_OnCheckedChanged;

        _starButton = new ToolStripButton("Star")
        {
            CheckOnClick = true
        };
        _starButton.CheckedChanged += StarButton_OnCheckedChanged;

        _paletteButton = new ToolStripDropDownButton("Color");
        _moreButton = new ToolStripDropDownButton("...");

        _toolStrip.Items.Add(new ToolStripLabel("Title"));
        _toolStrip.Items.Add(_titleBox);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_paletteButton);
        _toolStrip.Items.Add(_fontFamilyComboBox);
        _toolStrip.Items.Add(_fontSizeComboBox);
        _toolStrip.Items.Add(_pinButton);
        _toolStrip.Items.Add(_starButton);
        _toolStrip.Items.Add(_moreButton);

        _bodyBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            DetectUrls = false,
            AcceptsTab = true
        };
        _bodyBox.TextChanged += BodyBox_OnTextChanged;

        _projectHost = new Panel
        {
            Dock = DockStyle.Fill
        };

        var projectScrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        _projectFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        projectScrollPanel.Controls.Add(_projectFlow);

        _newProjectPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0)
        };

        _newProjectNameTextBox = new TextBox
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Left = 0,
            Top = 8,
            Width = 320
        };
        _newProjectNameTextBox.KeyDown += NewProjectNameTextBox_OnKeyDown;

        _addProjectButton = new Button
        {
            Text = "Add project",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Width = 112,
            Height = 28
        };
        _addProjectButton.Click += AddProjectButton_OnClick;

        _newProjectPanel.Controls.Add(_newProjectNameTextBox);
        _newProjectPanel.Controls.Add(_addProjectButton);
        _projectHost.Controls.Add(projectScrollPanel);
        _projectHost.Controls.Add(_newProjectPanel);

        Controls.Add(_bodyBox);
        Controls.Add(_projectHost);
        Controls.Add(_toolStrip);

        Move += NoteForm_OnBoundsChanged;
        Resize += NoteForm_OnBoundsChanged;
        Resize += NoteForm_OnResize;
        FormClosing += NoteForm_OnFormClosing;
        FormClosed += NoteForm_OnFormClosed;

        BuildToolbarMenus();
        InitializeFontChoices();
        LoadFromNote();
    }

    public NoteDocument Document => _note;

    public event EventHandler? DeleteRequested;

    public void ShowOnDesktop(bool activate)
    {
        _note.IsVisibleOnDesktop = true;
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        TopMost = _note.IsPinned;
        if (activate)
        {
            BringToFront();
            Activate();
        }

        PersistNow();
    }

    public void HideToDesktop(bool persist = true)
    {
        _note.IsVisibleOnDesktop = false;
        Hide();
        if (persist)
        {
            PersistNow();
        }
    }

    public void CloseForAppExit()
    {
        _allowClose = true;
        Close();
    }

    public void CloseForDelete()
    {
        _allowClose = true;
        Close();
    }

    private void BuildToolbarMenus()
    {
        _paletteButton.DropDownItems.Clear();
        foreach (var palette in NotePaletteCatalog.All)
        {
            var item = new ToolStripMenuItem(palette.Label)
            {
                Tag = palette.Key
            };
            item.Click += PaletteMenuItem_OnClick;
            _paletteButton.DropDownItems.Add(item);
        }

        var hideItem = new ToolStripMenuItem("Hide");
        hideItem.Click += (_, _) => HideToDesktop();
        _moreButton.DropDownItems.Add(hideItem);

        var deleteItem = new ToolStripMenuItem("Delete")
        {
            Enabled = !_note.IsProjectHubNote
        };
        deleteItem.Click += DeleteItem_OnClick;
        _moreButton.DropDownItems.Add(deleteItem);
    }

    private void InitializeFontChoices()
    {
        _fontFamilyComboBox.Items.Clear();
        foreach (var fontName in FontFamily.Families.Select(font => font.Name).Distinct().OrderBy(name => name))
        {
            _fontFamilyComboBox.Items.Add(fontName);
        }

        _fontSizeComboBox.Items.Clear();
        foreach (var size in new[] { 11d, 12d, 13d, 14d, 16d, 18d, 20d, 24d, 28d, 32d })
        {
            _fontSizeComboBox.Items.Add(size.ToString("0"));
        }
    }

    private void LoadFromNote()
    {
        _suspendUi = true;

        MinimumSize = _note.IsProjectHubNote ? new Size(520, 620) : new Size(340, 360);
        Bounds = new Rectangle((int)Math.Round(_note.Left), (int)Math.Round(_note.Top), (int)Math.Round(_note.Width), (int)Math.Round(_note.Height));

        _titleBox.Text = _note.IsProjectHubNote
            ? (string.IsNullOrWhiteSpace(_note.Title) ? "Project Hub" : _note.Title)
            : _note.Title;
        _fontFamilyComboBox.SelectedItem = _note.FontFamilyName;
        _fontSizeComboBox.SelectedItem = _note.FontSize.ToString("0");
        _pinButton.Checked = _note.IsPinned;
        _starButton.Checked = _note.IsStarred;

        _bodyBox.Visible = !_note.IsProjectHubNote;
        _projectHost.Visible = _note.IsProjectHubNote;
        _bodyBox.Text = RichTextDocumentSerializer.ExtractPlainText(_note.Content);

        RefreshProjectCards();
        ApplyPalette();
        RefreshCaption();
        _suspendUi = false;
    }

    private void RefreshProjectCards()
    {
        _projectFlow.SuspendLayout();
        _projectFlow.Controls.Clear();

        foreach (var project in _note.Projects)
        {
            var card = new ProjectCardControl(project);
            card.ProjectChanged += ProjectCard_OnProjectChanged;
            card.RemoveRequested += ProjectCard_OnRemoveRequested;
            ApplyPaletteToProjectCard(card);
            _projectFlow.Controls.Add(card);
        }

        _projectFlow.ResumeLayout(true);
        UpdateProjectCardWidths();
    }

    private void ApplyPalette()
    {
        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        var background = ColorHelper.FromHex(palette.BackgroundHex);
        var surface = ColorHelper.FromHex(palette.SurfaceHex);
        var foreground = ColorHelper.FromHex(palette.ForegroundHex);

        BackColor = background;
        ForeColor = foreground;
        _toolStrip.BackColor = surface;
        _toolStrip.ForeColor = foreground;
        _bodyBox.BackColor = surface;
        _bodyBox.ForeColor = foreground;
        _bodyBox.Font = BuildContentFont();
        _projectHost.BackColor = background;
        _projectFlow.BackColor = background;
        _newProjectPanel.BackColor = background;
        _newProjectNameTextBox.BackColor = surface;
        _newProjectNameTextBox.ForeColor = foreground;
        _newProjectNameTextBox.Font = BuildContentFont();
        _addProjectButton.BackColor = surface;
        _addProjectButton.ForeColor = foreground;
        _titleBox.BackColor = surface;
        _titleBox.ForeColor = foreground;
        _fontFamilyComboBox.ComboBox.BackColor = surface;
        _fontFamilyComboBox.ComboBox.ForeColor = foreground;
        _fontSizeComboBox.ComboBox.BackColor = surface;
        _fontSizeComboBox.ComboBox.ForeColor = foreground;

        foreach (var card in _projectFlow.Controls.OfType<ProjectCardControl>())
        {
            ApplyPaletteToProjectCard(card);
        }

        TopMost = _note.IsPinned;
    }

    private void ApplyPaletteToProjectCard(ProjectCardControl card)
    {
        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        card.ApplyPalette(palette, BuildContentFont(), BuildTitleFont());
        card.UpdateCardWidth(GetProjectCardWidth());
    }

    private Font BuildContentFont()
    {
        return TryCreateFont(_note.FontFamilyName, (float)Math.Clamp(_note.FontSize, 11d, 36d), FontStyle.Regular);
    }

    private Font BuildTitleFont()
    {
        return TryCreateFont("Georgia", (float)Math.Clamp(_note.FontSize + 4d, 13d, 40d), FontStyle.Bold);
    }

    private static Font TryCreateFont(string familyName, float size, FontStyle style)
    {
        try
        {
            return new Font(familyName, size, style);
        }
        catch
        {
            return new Font("Segoe UI", size, style);
        }
    }

    private void RefreshCaption()
    {
        Text = _note.DisplayTitle;
    }

    private void SchedulePersist()
    {
        if (_suspendUi)
        {
            return;
        }

        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void PersistNow()
    {
        _autosaveTimer.Stop();
        CaptureBounds();

        _note.Title = _note.IsProjectHubNote
            ? (string.IsNullOrWhiteSpace(_titleBox.Text) ? "Project Hub" : _titleBox.Text.Trim())
            : _titleBox.Text.Trim();
        _note.Content = _note.IsProjectHubNote ? string.Empty : _bodyBox.Text;
        _note.IsPinned = _pinButton.Checked;
        _note.IsStarred = _starButton.Checked;
        _note.UpdatedAt = DateTime.UtcNow;
        _note.TouchSummary();
        RefreshCaption();
        _persistState();
    }

    private void CaptureBounds()
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }

        _note.Left = Left;
        _note.Top = Top;
        _note.Width = Width;
        _note.Height = Height;
    }

    private int GetProjectCardWidth()
    {
        return Math.Max(300, _projectHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 20);
    }

    private void UpdateProjectCardWidths()
    {
        var width = GetProjectCardWidth();
        foreach (var card in _projectFlow.Controls.OfType<ProjectCardControl>())
        {
            card.UpdateCardWidth(width);
        }

        _newProjectNameTextBox.Width = Math.Max(120, _newProjectPanel.ClientSize.Width - _addProjectButton.Width - 12);
        _addProjectButton.Left = _newProjectNameTextBox.Right + 8;
        _addProjectButton.Top = 8;
    }

    private void TitleBox_OnTextChanged(object? sender, EventArgs e)
    {
        _note.Title = _titleBox.Text.Trim();
        RefreshCaption();
        SchedulePersist();
    }

    private void BodyBox_OnTextChanged(object? sender, EventArgs e)
    {
        if (_suspendUi || _note.IsProjectHubNote)
        {
            return;
        }

        _note.Content = _bodyBox.Text;
        RefreshCaption();
        SchedulePersist();
    }

    private void FontFamilyComboBox_OnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suspendUi || _fontFamilyComboBox.SelectedItem is not string fontName)
        {
            return;
        }

        _note.FontFamilyName = fontName;
        ApplyPalette();
        SchedulePersist();
    }

    private void FontSizeComboBox_OnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suspendUi || _fontSizeComboBox.SelectedItem is not string sizeText || !double.TryParse(sizeText, out var fontSize))
        {
            return;
        }

        _note.FontSize = fontSize;
        ApplyPalette();
        SchedulePersist();
    }

    private void PinButton_OnCheckedChanged(object? sender, EventArgs e)
    {
        if (_suspendUi)
        {
            return;
        }

        _note.IsPinned = _pinButton.Checked;
        TopMost = _note.IsPinned;
        SchedulePersist();
    }

    private void StarButton_OnCheckedChanged(object? sender, EventArgs e)
    {
        if (_suspendUi)
        {
            return;
        }

        _note.IsStarred = _starButton.Checked;
        SchedulePersist();
    }

    private void PaletteMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem item || item.Tag is not string paletteKey)
        {
            return;
        }

        _note.PaletteKey = paletteKey;
        ApplyPalette();
        SchedulePersist();
    }

    private void DeleteItem_OnClick(object? sender, EventArgs e)
    {
        if (_note.IsProjectHubNote)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Delete '{_note.DisplayTitle}' permanently?",
            "Delete note",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddProjectButton_OnClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_newProjectNameTextBox.Text))
        {
            return;
        }

        _note.Projects.Add(new ProjectEntry
        {
            Name = _newProjectNameTextBox.Text.Trim(),
            IsExpanded = true,
            IsChecklistExpanded = true
        });
        _newProjectNameTextBox.Clear();
        RefreshProjectCards();
        SchedulePersist();
    }

    private void NewProjectNameTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        AddProjectButton_OnClick(sender, EventArgs.Empty);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void ProjectCard_OnProjectChanged(object? sender, EventArgs e)
    {
        RefreshCaption();
        SchedulePersist();
    }

    private void ProjectCard_OnRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is not ProjectCardControl card)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Remove project '{card.Project.DisplayName}' from the hub?",
            "Remove project",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        _note.Projects.Remove(card.Project);
        RefreshProjectCards();
        SchedulePersist();
    }

    private void NoteForm_OnBoundsChanged(object? sender, EventArgs e)
    {
        if (_suspendUi)
        {
            return;
        }

        CaptureBounds();
        SchedulePersist();
    }

    private void NoteForm_OnResize(object? sender, EventArgs e)
    {
        UpdateProjectCardWidths();
    }

    private void AutosaveTimer_OnTick(object? sender, EventArgs e)
    {
        PersistNow();
    }

    private void NoteForm_OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            PersistNow();
            return;
        }

        e.Cancel = true;
        HideToDesktop();
    }

    private void NoteForm_OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Dispose();
    }
}
