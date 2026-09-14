using Nodepad.WinForms.Models;
using Nodepad.WinForms.Services;

namespace Nodepad.WinForms;

public sealed class NodepadApplicationContext : ApplicationContext
{
    private readonly AppStorage _storage;
    private readonly AppState _state;
    private readonly Dictionary<Guid, NoteForm> _forms = [];
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _openNotesMenuItem;
    private bool _isExiting;

    public NodepadApplicationContext()
    {
        _storage = new AppStorage();
        _state = _storage.Load();

        _trayMenu = new ContextMenuStrip();
        _openNotesMenuItem = new ToolStripMenuItem("Open note");
        _openNotesMenuItem.DropDownOpening += OpenNotesMenuItem_OnDropDownOpening;

        _trayMenu.Items.Add(new ToolStripMenuItem("New note", null, (_, _) => CreateNewGeneralNote()));
        _trayMenu.Items.Add(new ToolStripMenuItem("Open project hub", null, (_, _) => ShowProjectHub(true)));
        _trayMenu.Items.Add(_openNotesMenuItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem("Show all", null, (_, _) => ShowAllDesktopNotes()));
        _trayMenu.Items.Add(new ToolStripMenuItem("Hide all", null, (_, _) => HideAllDesktopNotes()));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Nodepad Ver2",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseClick += TrayIcon_OnMouseClick;

        EnsureProjectHubExists();

        if (_state.Settings.ShowNotesOnStartup)
        {
            ShowAllDesktopNotes();
        }
    }

    private void OpenNotesMenuItem_OnDropDownOpening(object? sender, EventArgs e)
    {
        _openNotesMenuItem.DropDownItems.Clear();

        var notes = _state.Notes
            .Where(note => !note.IsArchived && !note.IsProjectHubNote)
            .OrderBy(note => note.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (notes.Count == 0)
        {
            _openNotesMenuItem.DropDownItems.Add(new ToolStripMenuItem("No notes") { Enabled = false });
            return;
        }

        foreach (var note in notes)
        {
            var item = new ToolStripMenuItem(note.DisplayTitle);
            item.Click += (_, _) => ShowNote(note, true);
            _openNotesMenuItem.DropDownItems.Add(item);
        }
    }

    private void TrayIcon_OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        BringVisibleNotesForward();
    }

    private void BringVisibleNotesForward()
    {
        var visibleNotes = _state.Notes
            .Where(note => !note.IsArchived && note.IsVisibleOnDesktop)
            .ToList();

        if (visibleNotes.Count == 0)
        {
            ShowAllDesktopNotes();
            return;
        }

        foreach (var note in visibleNotes)
        {
            ShowNote(note, true);
        }
    }

    private void ShowAllDesktopNotes()
    {
        var targetNotes = _state.Notes
            .Where(note => !note.IsArchived && (note.IsVisibleOnDesktop || note.IsProjectHubNote))
            .ToList();

        if (targetNotes.Count == 0)
        {
            targetNotes = _state.Notes.Where(note => !note.IsArchived).ToList();
        }

        foreach (var note in targetNotes)
        {
            note.IsVisibleOnDesktop = true;
            ShowNote(note, false);
        }

        PersistState();
    }

    private void HideAllDesktopNotes()
    {
        foreach (var form in _forms.Values.ToList())
        {
            form.HideToDesktop(false);
        }

        foreach (var note in _state.Notes)
        {
            note.IsVisibleOnDesktop = false;
        }

        PersistState();
    }

    private void ShowProjectHub(bool activate)
    {
        var note = EnsureProjectHubExists();
        note.IsVisibleOnDesktop = true;
        ShowNote(note, activate);
        PersistState();
    }

    private void CreateNewGeneralNote()
    {
        var note = new NoteDocument
        {
            NoteKind = NoteKinds.General,
            Title = string.Empty,
            Content = string.Empty,
            Tags = string.Empty,
            PaletteKey = _state.Settings.DefaultPaletteKey,
            FontFamilyName = _state.Settings.DefaultFontFamily,
            FontSize = _state.Settings.DefaultFontSize <= 0 ? 16 : _state.Settings.DefaultFontSize,
            IsVisibleOnDesktop = true,
            Left = 140 + (_state.Notes.Count % 4) * 28,
            Top = 120 + (_state.Notes.Count % 4) * 28,
            Width = 380,
            Height = 460,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _state.Notes.Add(note);
        ShowNote(note, true);
        PersistState();
    }

    private NoteDocument EnsureProjectHubExists()
    {
        var hub = _state.Notes.FirstOrDefault(note => note.IsProjectHubNote);
        if (hub is not null)
        {
            return hub;
        }

        hub = new NoteDocument
        {
            NoteKind = NoteKinds.ProjectHub,
            Title = "Các dự án",
            PaletteKey = _state.Settings.DefaultPaletteKey,
            FontFamilyName = _state.Settings.DefaultFontFamily,
            FontSize = _state.Settings.DefaultFontSize <= 0 ? 16 : _state.Settings.DefaultFontSize,
            Width = 560,
            Height = 700,
            Left = 180,
            Top = 120,
            IsVisibleOnDesktop = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _state.Notes.Add(hub);
        PersistState();
        return hub;
    }

    private void ShowNote(NoteDocument note, bool activate)
    {
        if (!_forms.TryGetValue(note.Id, out var form) || form.IsDisposed)
        {
            form = new NoteForm(note, PersistState);
            form.DeleteRequested += NoteForm_OnDeleteRequested;
            form.FormClosed += NoteForm_OnFormClosed;
            _forms[note.Id] = form;
        }

        form.ShowOnDesktop(activate);
    }

    private void NoteForm_OnDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is not NoteForm form)
        {
            return;
        }

        var note = form.Document;
        _state.Notes.Remove(note);
        _forms.Remove(note.Id);
        PersistState();
        form.CloseForDelete();
    }

    private void NoteForm_OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (_isExiting || sender is not NoteForm form)
        {
            return;
        }

        _forms.Remove(form.Document.Id);
    }

    private void PersistState()
    {
        _storage.Save(_state);
    }

    private void ExitApplication()
    {
        _isExiting = true;

        foreach (var form in _forms.Values.ToList())
        {
            form.CloseForAppExit();
        }

        PersistState();
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        base.ExitThreadCore();
    }
}
