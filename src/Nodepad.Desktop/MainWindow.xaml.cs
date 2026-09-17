using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Nodepad.Desktop.Infrastructure;
using Nodepad.Desktop.Models;
using Nodepad.Desktop.Services;
using Nodepad.Desktop.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace Nodepad.Desktop;

public partial class MainWindow : Window
{
    private readonly AppStorage _storage;
    private readonly AppState _state;
    private readonly WindowsStartupService _startupService;
    private readonly Dictionary<Guid, NoteWindow> _openWindows = [];
    private readonly DispatcherTimer _explorerRefreshTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly bool _launchedFromSystemStartup;
    private SettingsWindow? _settingsWindow;
    private string _activeFilter;
    private bool _allowExit;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;

    public ObservableCollection<NoteDocument> Notes { get; }
    public ObservableCollection<NotePalette> PaletteOptions { get; }
    public ObservableCollection<string> FontOptions { get; }
    public ObservableCollection<double> FontSizeOptions { get; }
    public ObservableCollection<ProjectEntry> VisibleProjects { get; }
    public ICollectionView ExplorerNotesView { get; }
    public AppSettings Settings => _state.Settings;

    private NoteDocument? ProjectHubNote => Notes.FirstOrDefault(note => note.IsProjectHubNote && !note.IsArchived);

    public MainWindow(bool launchedFromSystemStartup = false)
    {
        InitializeComponent();

        _storage = new AppStorage();
        _state = _storage.Load();
        _startupService = new WindowsStartupService();
        _launchedFromSystemStartup = launchedFromSystemStartup;
        _activeFilter = NormalizeFilterKey(Settings.ActiveFilter);
        Settings.RunOnSystemStart = _startupService.IsEnabled();
        if (Settings.RunOnSystemStart)
        {
            _startupService.SetEnabled(true);
        }

        PaletteOptions = new ObservableCollection<NotePalette>(NotePaletteCatalog.All);
        FontOptions = new ObservableCollection<string>(
            Fonts.SystemFontFamilies.Select(font => font.Source).OrderBy(name => name));
        FontSizeOptions = new ObservableCollection<double>([12, 14, 16, 18, 20, 24, 28, 32, 36]);
        Notes = new ObservableCollection<NoteDocument>(_state.Notes);
        VisibleProjects = [];

        ExplorerNotesView = new ListCollectionView(Notes);
        ExplorerNotesView.Filter = FilterExplorerCards;
        ExplorerNotesView.SortDescriptions.Add(new SortDescription(nameof(NoteDocument.IsPinned), ListSortDirection.Descending));
        ExplorerNotesView.SortDescriptions.Add(new SortDescription(nameof(NoteDocument.IsStarred), ListSortDirection.Descending));
        ExplorerNotesView.SortDescriptions.Add(new SortDescription(nameof(NoteDocument.UpdatedAt), ListSortDirection.Descending));

        DataContext = this;

        foreach (var note in Notes)
        {
            note.PropertyChanged += Note_OnPropertyChanged;
        }

        Settings.PropertyChanged += Settings_OnPropertyChanged;
        _explorerRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _explorerRefreshTimer.Tick += ExplorerRefreshTimer_OnTick;
        _trayIcon = CreateTrayIcon();

        if (Settings.ShowNotesOnStartup || _launchedFromSystemStartup)
        {
            RestoreDesktopNotes();
        }

        RefreshExplorer();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var trayMenu = new RoundedTrayContextMenuStrip();
        trayMenu.ShowCheckMargin = false;
        trayMenu.ShowImageMargin = true;
        trayMenu.Font = new Drawing.Font("Segoe UI", 10f);
        trayMenu.Padding = new Forms.Padding(8, 6, 8, 6);
        trayMenu.BackColor = TrayMenuRenderer.MenuBackgroundColor;
        trayMenu.ForeColor = TrayMenuRenderer.MenuTextColor;
        trayMenu.RenderMode = Forms.ToolStripRenderMode.Professional;
        trayMenu.Renderer = new TrayMenuRenderer();
        trayMenu.ImageScalingSize = new Drawing.Size(18, 18);
        trayMenu.Items.Add(CreateTrayMenuItem("New Note", "\uE710", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(CreateGeneralNote), "Alt+N"));
        trayMenu.Items.Add(CreateTrayMenuItem("Project Hub", "\uE8B7", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(OpenProjectHub), "Alt+P"));
        trayMenu.Items.Add(CreateTraySeparator());
        trayMenu.Items.Add(CreateTrayMenuItem("Show All Notes", "\uE890", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(ShowAllNotes), "Alt+S"));
        trayMenu.Items.Add(CreateTrayMenuItem("Hide All Notes", "\uE7E8", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(HideAllNotes), "Alt+H"));
        trayMenu.Items.Add(CreateTraySeparator());
        trayMenu.Items.Add(CreateTrayMenuItem("Notes Explorer", "\uE8A5", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(ShowExplorer), "Alt+X"));
        trayMenu.Items.Add(CreateTrayMenuItem("Settings...", "\uE713", TrayMenuRenderer.MenuTextColor, () => Dispatcher.Invoke(ShowSettingsWindow)));
        trayMenu.Items.Add(CreateTraySeparator());
        trayMenu.Items.Add(CreateTrayMenuItem("Exit", "\uE106", Drawing.Color.FromArgb(124, 49, 46), () => Dispatcher.Invoke(ExitApplication)));

        var trayIcon = new Forms.NotifyIcon
        {
            Text = "H2 Notes",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        trayIcon.MouseUp += TrayIcon_OnMouseUp;
        trayIcon.MouseDoubleClick += TrayIcon_OnMouseDoubleClick;
        return trayIcon;
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "h2-notes-app-icon-v1.ico");
        if (File.Exists(iconPath))
        {
            return new Drawing.Icon(iconPath);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(processPath!);
            if (extractedIcon is not null)
            {
                return extractedIcon;
            }
        }

        return Drawing.SystemIcons.Application;
    }

    private static Forms.ToolStripSeparator CreateTraySeparator()
    {
        return new Forms.ToolStripSeparator
        {
            AutoSize = false,
            Height = 12,
            Margin = new Forms.Padding(0, 4, 0, 4)
        };
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(string text, string glyph, Drawing.Color glyphColor, Action onClick, string? shortcutText = null)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            AutoSize = false,
            Size = new Drawing.Size(320, 34),
            Height = 34,
            Margin = new Forms.Padding(0, 2, 0, 2),
            Padding = new Forms.Padding(8, 6, 14, 6),
            Image = TrayMenuIconFactory.Create(glyph, glyphColor),
            ImageScaling = Forms.ToolStripItemImageScaling.None,
            TextImageRelation = Forms.TextImageRelation.ImageBeforeText,
            ForeColor = TrayMenuRenderer.MenuTextColor,
            ShowShortcutKeys = !string.IsNullOrWhiteSpace(shortcutText),
            ShortcutKeyDisplayString = shortcutText ?? string.Empty
        };

        item.Click += (_, _) => onClick();
        return item;
    }

    private static string NormalizeFilterKey(string? key)
    {
        return key switch
        {
            "desktop" or "notes" or "projects" or "starred" or "hidden" or "archived" => key,
            _ => "desktop"
        };
    }

    private static string NormalizeProjectViewMode(string? mode)
    {
        return mode switch
        {
            "board" or "list" => mode,
            _ => "list"
        };
    }

    private bool FilterExplorerCards(object item)
    {
        if (item is not NoteDocument note)
        {
            return false;
        }

        if (_activeFilter == "projects")
        {
            return false;
        }

        if (!note.Matches(Settings.SearchText))
        {
            return false;
        }

        return _activeFilter switch
        {
            "desktop" => note.IsVisibleOnDesktop && !note.IsArchived,
            "notes" => !note.IsProjectHubNote && !note.IsArchived,
            "starred" => note.IsStarred && !note.IsArchived,
            "hidden" => !note.IsVisibleOnDesktop && !note.IsArchived,
            "archived" => note.IsArchived,
            _ => !note.IsArchived
        };
    }

    private void RestoreDesktopNotes()
    {
        foreach (var note in Notes.Where(note => note.IsVisibleOnDesktop && !note.IsArchived))
        {
            OpenNote(note, true);
        }
    }

    private NoteDocument CreateGeneralNote()
    {
        var note = CreateBaseNote(NoteKinds.General, "Untitled", string.Empty);
        OpenNote(note);
        return note;
    }

    private NoteDocument OpenProjectHub(ProjectEntry? focusProject = null)
    {
        _activeFilter = "projects";
        var hub = GetOrCreateProjectHubNote();

        if (focusProject is not null)
        {
            focusProject.IsExpanded = true;
        }

        OpenNote(hub);
        PersistState();
        return hub;
    }

    private NoteDocument GetOrCreateProjectHubNote()
    {
        var existingHub = Notes.FirstOrDefault(note => note.IsProjectHubNote);
        if (existingHub is not null)
        {
            existingHub.IsVisibleOnDesktop = true;
            existingHub.IsArchived = false;
            return existingHub;
        }

        var hub = CreateBaseNote(NoteKinds.ProjectHub, "Project Hub", string.Empty);
        hub.Width = 520;
        hub.Height = 680;
        hub.IsVisibleOnDesktop = true;
        hub.UpdatedAt = DateTime.UtcNow;
        PersistState();
        return hub;
    }

    private NoteDocument CreateBaseNote(string noteKind, string title, string content)
    {
        var offset = Notes.Count % 6;
        var note = new NoteDocument
        {
            Id = Guid.NewGuid(),
            NoteKind = noteKind,
            Title = title,
            ProjectName = string.Empty,
            Content = content,
            Tags = string.Empty,
            PaletteKey = Settings.DefaultPaletteKey,
            FontFamilyName = Settings.DefaultFontFamily,
            FontSize = Settings.DefaultFontSize,
            NoteOpacity = 1.0,
            IsPinned = false,
            IsStarred = false,
            IsArchived = false,
            IsVisibleOnDesktop = true,
            Left = 180 + (offset * 24),
            Top = 120 + (offset * 20),
            Width = noteKind == NoteKinds.ProjectHub ? 520 : 380,
            Height = noteKind == NoteKinds.ProjectHub ? 680 : 460,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        note.PropertyChanged += Note_OnPropertyChanged;
        Notes.Add(note);
        PersistState();
        return note;
    }

    private void OpenNote(NoteDocument note, bool restoringFromStartup = false)
    {
        if (_openWindows.TryGetValue(note.Id, out var existingWindow))
        {
            existingWindow.BringForward();
            return;
        }

        note.IsVisibleOnDesktop = true;

        var noteWindow = new NoteWindow(
            note,
            PersistState,
            DeleteNoteFromWindow,
            DuplicateNote,
            project => CreateQuickNoteFromProject(project))
        {
            ShowInTaskbar = false
        };

        noteWindow.Closed += (_, _) => _openWindows.Remove(note.Id);
        _openWindows[note.Id] = noteWindow;
        noteWindow.Show();
        noteWindow.BringForward();

        if (!restoringFromStartup)
        {
            PersistState();
        }
    }

    private NoteDocument DuplicateNote(NoteDocument source)
    {
        if (source.IsProjectHubNote)
        {
            OpenNote(source);
            return source;
        }

        var duplicate = CreateBaseNote(source.NoteKind, source.DisplayTitle, source.Content);
        duplicate.Tags = source.Tags;
        duplicate.PaletteKey = source.PaletteKey;
        duplicate.FontFamilyName = source.FontFamilyName;
        duplicate.FontSize = source.FontSize;
        duplicate.NoteOpacity = source.NoteOpacity;
        duplicate.IsPinned = source.IsPinned;
        duplicate.IsStarred = false;
        duplicate.ChecklistItems = new ObservableCollection<ChecklistItem>(
            source.ChecklistItems.Select(item => new ChecklistItem
            {
                Text = item.Text,
                IsCompleted = item.IsCompleted
            }));
        duplicate.Left = source.Left + 24;
        duplicate.Top = source.Top + 24;
        duplicate.Width = source.Width;
        duplicate.Height = source.Height;
        PersistState();
        OpenNote(duplicate);
        return duplicate;
    }

    private NoteDocument CreateQuickNoteFromProject(ProjectEntry project)
    {
        var nextAction = project.DisplayNextAction.Trim();
        if (string.Equals(nextAction, "No next action", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(nextAction))
        {
            return CreateGeneralNote();
        }

        var note = CreateBaseNote(NoteKinds.General, project.DisplayName, nextAction);
        note.Tags = project.DisplayName;
        note.UpdatedAt = DateTime.UtcNow;
        PersistState();
        OpenNote(note);
        return note;
    }

    private void CompleteProjectNextAction(ProjectEntry project)
    {
        var currentItem = project.CurrentChecklistItem;
        if (currentItem is null)
        {
            return;
        }

        currentItem.IsCompleted = true;
        project.TouchSummary();
        PersistState();
    }

    private void MoveProjectNextActionToNote(ProjectEntry project)
    {
        var nextAction = project.DisplayNextAction.Trim();
        if (string.Equals(nextAction, "No next action", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(nextAction))
        {
            return;
        }

        CreateQuickNoteFromProject(project);
        project.TouchSummary();
        PersistState();
    }

    private void DeleteNoteFromWindow(NoteDocument note)
    {
        if (_openWindows.TryGetValue(note.Id, out var window))
        {
            _openWindows.Remove(note.Id);
            window.CloseWithoutPersist();
        }

        note.PropertyChanged -= Note_OnPropertyChanged;
        Notes.Remove(note);
        PersistState();
    }

    private void PersistState()
    {
        Settings.ActiveFilter = _activeFilter;
        Settings.ProjectExplorerViewMode = NormalizeProjectViewMode(Settings.ProjectExplorerViewMode);
        _state.Notes = Notes.ToList();
        _storage.Save(_state);
        RefreshExplorer();
    }

    private void RefreshVisibleProjects()
    {
        VisibleProjects.Clear();

        var hub = ProjectHubNote;
        if (hub is null)
        {
            return;
        }

        foreach (var project in hub.Projects.Where(project => project.Matches(Settings.SearchText)))
        {
            VisibleProjects.Add(project);
        }
    }

    private void RefreshExplorer()
    {
        RefreshVisibleProjects();
        ExplorerNotesView.Refresh();

        WorkspaceTitleText.Text = _activeFilter switch
        {
            "desktop" => "Desktop Notes",
            "notes" => "General Notes",
            "projects" => "Project Hub",
            "starred" => "Starred Notes",
            "hidden" => "Hidden Notes",
            "archived" => "Archived Notes",
            _ => "Nodepad"
        };

        WorkspaceSubtitleText.Text = _activeFilter switch
        {
            "projects" => "All projects now live inside one sticky note, and each project can be expanded or collapsed there.",
            "desktop" => "These are the notes currently living on your desktop.",
            "hidden" => "Notes are safe here when you want a cleaner desktop.",
            _ => "Open any card to bring the full sticky note back to the front."
        };

        DesktopCountText.Text = Notes.Count(note => note.IsVisibleOnDesktop && !note.IsArchived).ToString();
        NotesCountText.Text = Notes.Count(note => !note.IsProjectHubNote && !note.IsArchived).ToString();
        ProjectsCountText.Text = (ProjectHubNote?.ProjectCount ?? 0).ToString();
        StarredCountText.Text = Notes.Count(note => note.IsStarred && !note.IsArchived).ToString();
        HiddenCountText.Text = Notes.Count(note => !note.IsVisibleOnDesktop && !note.IsArchived).ToString();
        ArchivedCountText.Text = Notes.Count(note => note.IsArchived).ToString();

        CardsPane.Visibility = _activeFilter == "projects" ? Visibility.Collapsed : Visibility.Visible;
        ProjectsPane.Visibility = _activeFilter == "projects" ? Visibility.Visible : Visibility.Collapsed;
        LaunchProjectHubButton.Visibility = _activeFilter == "projects" ? Visibility.Visible : Visibility.Collapsed;
        ProjectListViewButton.Visibility = _activeFilter == "projects" ? Visibility.Visible : Visibility.Collapsed;
        ProjectBoardViewButton.Visibility = _activeFilter == "projects" ? Visibility.Visible : Visibility.Collapsed;
        ProjectsListPane.Visibility = _activeFilter == "projects" && NormalizeProjectViewMode(Settings.ProjectExplorerViewMode) == "list"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProjectsBoardPane.Visibility = _activeFilter == "projects" && NormalizeProjectViewMode(Settings.ProjectExplorerViewMode) == "board"
            ? Visibility.Visible
            : Visibility.Collapsed;

        var visibleCount = _activeFilter == "projects"
            ? VisibleProjects.Count
            : ExplorerNotesView.Cast<object>().Count();
        EmptyStateText.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = _activeFilter switch
        {
            "projects" when ProjectHubNote is null => "No project hub yet. Open it once and add your projects inside.",
            "projects" => "No projects match this view yet.",
            "hidden" => "Nothing is hidden right now.",
            "archived" => "No archived notes yet.",
            _ => "Nothing matches this view yet."
        };

        UpdateFilterButtons();
        UpdateProjectViewButtons();
    }

    private void UpdateFilterButtons()
    {
        UpdateFilterButtonVisual(DesktopFilterButton, _activeFilter == "desktop");
        UpdateFilterButtonVisual(NotesFilterButton, _activeFilter == "notes");
        UpdateFilterButtonVisual(ProjectsFilterButton, _activeFilter == "projects");
        UpdateFilterButtonVisual(StarredFilterButton, _activeFilter == "starred");
        UpdateFilterButtonVisual(HiddenFilterButton, _activeFilter == "hidden");
        UpdateFilterButtonVisual(ArchivedFilterButton, _activeFilter == "archived");
    }

    private void UpdateFilterButtonVisual(WpfButton button, bool isActive)
    {
        button.Background = isActive
            ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush")
            : (System.Windows.Media.Brush)FindResource("PanelAltBrush");
        button.Foreground = isActive
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("TextBrush");
        button.BorderBrush = button.Background;
    }

    private void UpdateProjectViewButtons()
    {
        UpdateFilterButtonVisual(ProjectListViewButton, NormalizeProjectViewMode(Settings.ProjectExplorerViewMode) == "list");
        UpdateFilterButtonVisual(ProjectBoardViewButton, NormalizeProjectViewMode(Settings.ProjectExplorerViewMode) == "board");
    }

    private void ShowExplorer()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings, PaletteOptions, FontOptions, FontSizeOptions);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void RevealOpenNotes()
    {
        var noteWindows = _openWindows.Values
            .Where(window => window.IsLoaded && window.Visibility == Visibility.Visible)
            .ToList();
        var auxiliaryWindows = System.Windows.Application.Current.Windows
            .OfType<Window>()
            .Where(window =>
                window != this
                && window is not NoteWindow
                && window.IsLoaded
                && window.Visibility == Visibility.Visible)
            .ToList();

        if (noteWindows.Count == 0 && auxiliaryWindows.Count == 0)
        {
            return;
        }

        for (var index = 0; index < noteWindows.Count; index++)
        {
            var activate = index == noteWindows.Count - 1 && auxiliaryWindows.Count == 0;
            noteWindows[index].RevealFromTray(activate);
        }

        for (var index = 0; index < auxiliaryWindows.Count; index++)
        {
            RevealGenericWindowFromTray(auxiliaryWindows[index], index == auxiliaryWindows.Count - 1);
        }
    }

    internal void PrepareForAppExit()
    {
        _allowExit = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _settingsWindow?.Close();

        foreach (var window in _openWindows.Values.ToList())
        {
            window.CloseForAppExit();
        }
    }

    private void ExitApplication()
    {
        PrepareForAppExit();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void ShowAllNotes()
    {
        foreach (var note in Notes.Where(note => !note.IsArchived))
        {
            OpenNote(note);
        }
    }

    private void HideAllNotes()
    {
        foreach (var note in Notes.Where(note => !note.IsArchived))
        {
            note.IsVisibleOnDesktop = false;
        }

        foreach (var window in _openWindows.Values.ToList())
        {
            window.HideToDesktop();
        }

        PersistState();
    }

    private void ToggleDesktopVisibility(NoteDocument note)
    {
        if (note.IsArchived)
        {
            return;
        }

        if (note.IsVisibleOnDesktop && _openWindows.TryGetValue(note.Id, out var window))
        {
            window.HideToDesktop();
            return;
        }

        note.IsVisibleOnDesktop = true;
        PersistState();
        OpenNote(note);
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        PersistState();
    }

    private void SettingsControl_OnChanged(object sender, RoutedEventArgs e)
    {
        PersistState();
    }

    private void Settings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.SearchText) or nameof(AppSettings.ActiveFilter))
        {
            return;
        }

        if (e.PropertyName is nameof(AppSettings.RunOnSystemStart))
        {
            _startupService.SetEnabled(Settings.RunOnSystemStart);
        }

        PersistState();
    }

    private void Note_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteDocument.NoteOpacity)
            or nameof(NoteDocument.Left)
            or nameof(NoteDocument.Top)
            or nameof(NoteDocument.Width)
            or nameof(NoteDocument.Height))
        {
            return;
        }

        ScheduleExplorerRefresh();
    }

    private void FilterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string filterKey)
        {
            return;
        }

        _activeFilter = NormalizeFilterKey(filterKey);
        PersistState();
    }

    private void NewGeneralNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        CreateGeneralNote();
    }

    private void TrayIcon_OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left || e.Clicks != 1)
        {
            return;
        }

        QueueTrayReveal();
    }

    private void TrayIcon_OnMouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowExplorer));
    }

    private void OpenProjectHubButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenProjectHub();
    }

    private void LaunchProjectHubButton_OnClick(object sender, RoutedEventArgs e)
    {
        var project = sender is FrameworkElement element ? element.DataContext as ProjectEntry : null;
        OpenProjectHub(project);
    }

    private void ProjectViewModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string mode)
        {
            return;
        }

        Settings.ProjectExplorerViewMode = NormalizeProjectViewMode(mode);
        PersistState();
    }

    private void NoteCard_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not FrameworkElement element || element.DataContext is not NoteDocument note)
        {
            return;
        }

        OpenNote(note);
    }

    private void CardOpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NoteDocument note)
        {
            OpenNote(note);
        }
    }

    private void CardToggleDesktopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NoteDocument note)
        {
            ToggleDesktopVisibility(note);
        }
    }

    private void ProjectExpander_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.DataContext is ProjectEntry project)
        {
            project.IsExpanded = true;
            PersistState();
        }
    }

    private void ProjectExpander_OnCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.DataContext is ProjectEntry project)
        {
            project.IsExpanded = false;
            PersistState();
        }
    }

    private void ProjectDoneNextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ProjectEntry project)
        {
            CompleteProjectNextAction(project);
        }
    }

    private void ProjectMoveToNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ProjectEntry project)
        {
            MoveProjectNextActionToNote(project);
        }
    }

    private void ShowAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowAllNotes();
    }

    private void HideAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideAllNotes();
    }

    private void MainWindow_OnClosing(object sender, CancelEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void QueueTrayReveal()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RevealOpenNotes));
    }

    private void ScheduleExplorerRefresh()
    {
        _explorerRefreshTimer.Stop();
        _explorerRefreshTimer.Start();
    }

    private void ExplorerRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _explorerRefreshTimer.Stop();
        RefreshExplorer();
    }

    private static void RevealGenericWindowFromTray(Window window, bool activate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        if (handle != IntPtr.Zero)
        {
            ShowWindowAsync(handle, SwRestore);
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        }

        if (activate)
        {
            window.Activate();
            window.Focus();
        }

        if (handle != IntPtr.Zero)
        {
            DispatcherTimer timer = new()
            {
                Interval = TimeSpan.FromMilliseconds(2600)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            };
            timer.Start();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
