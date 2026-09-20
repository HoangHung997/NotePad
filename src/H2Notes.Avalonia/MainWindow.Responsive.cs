using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private bool _drawerOpen;
    private bool _wide;
    private bool _manualSidebarCollapsed;
    private bool _updatingNavigator;
    private string _navigatorSignature = "";
    private bool _wasFullscreen;
    private const string ProjectWorkspaceAgentMode = "agent";
    private const string ProjectWorkspaceTasksMode = "tasks";
    private const string ProjectWorkspaceNotesMode = "notes";
    private const string ProjectWorkspaceResourcesMode = "resources";
    private const string ProjectWorkspaceHistoryMode = "history";
    private const string ProjectWorkspaceEvidenceMode = "evidence";
    private string _projectWorkspaceMode = ProjectWorkspaceAgentMode;
    private AiChatPanel _chat = null!;
    private ProjectNavItem? _pressedProject;
    private ProjectNavItem? _targetProject;
    private Point _projectPress;
    private bool _projectDragging;
    private bool _projectDropAfter;
    private readonly Border _dragGhost = new() { IsVisible = false, IsHitTestVisible = false, Width = 260, Height = 42,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(4),
        BorderBrush = RichEditor.Brush("#A4573D"), BorderThickness = new Thickness(1), Background = RichEditor.Brush("#F8EAE2"), Opacity = .88 };
    private readonly TextBlock _ghostTitle = new() { Margin = new Thickness(12, 8), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void InitializeShell()
    {
        _chat = new AiChatPanel(_app); AiHost.Content = _chat;
        _chat.CloseRequested += () =>
        {
            if (_projectWorkspaceMode == ProjectWorkspaceAgentMode)
                ShowProjectDetail(_notesProject?.Layout.Tab == "notes"
                    ? ProjectWorkspaceNotesMode
                    : ProjectWorkspaceTasksMode);
            else
                SetAiDock("hidden");
        };
        _chat.DockRequested += SetAiDock;
        _chat.AppendNoteRequested += text => { NotesEditor.AppendText(text); _app.ScheduleSave(); };
        _chat.AddTaskRequested += text => { if (_notesProject is null) return; _notesProject.ChecklistItems.Add(new() { Text = text }); Sheet.Refresh(); UpdateSummary(); _app.ScheduleSave(); };
        _chat.ProjectActionsRequested += (project, actions) =>
        {
            if (_notesProject != project) throw new InvalidOperationException("Đã đổi dự án; hãy xem và áp dụng lại trong đúng dự án.");
            if (actions.Count != 0) throw new InvalidOperationException("Thay đổi AI phải được áp dụng trong Core trước khi làm mới giao diện.");
            FlushNotes();
            NotesEditor.Load(project.ReadNotes()); _loadedNotesSource = project.NotesRich; NotesEditor.MarkSaved();
            Sheet.Refresh(); UpdateSummary(); _app.MarkProjectDirty(project.Id); _app.ScheduleSave();
        };
        _chat.ReadContext = () => { FlushNotes(); return NotesEditor.Editor.SelectionLength > 0 ? NotesEditor.Editor.SelectedText : _notesProject?.NotesText ?? ""; };
        _chat.PrepareProjectContext = () => { FlushNotes(); Sheet.FlushDraft(); };
        _chat.DragStarted += BeginAiDrag; _chat.DragMoved += MoveAiDrag; _chat.DragFinished += EndAiDrag;
        ProjectsNavButton.Click += (_, _) =>
        {
            if (!_showCommandCenter) { ShowCommandCenter(); return; }
            if (_wide) { _manualSidebarCollapsed = !_manualSidebarCollapsed; _drawerOpen = false; }
            else _drawerOpen = !_drawerOpen;
            ApplyResponsive();
        };
        NotesNavButton.Click += (_, _) => ShowNotesMenu();
        AiNavButton.Click += (_, _) =>
        {
            if (_notesProject is null) return;
            if (_showCommandCenter) _showCommandCenter = false;
            ShowAgentWorkspace();
        };
        var aiMenu = new ContextMenu();
        var projectAi = new MenuItem { Header = "AI của dự án", Icon = new AppIcon(IconKind.Folder) };
        projectAi.Click += (_, _) => SetAiDock("floating"); aiMenu.Items.Add(projectAi);
        var standaloneAi = new MenuItem { Header = "Tách AI của dự án ra màn hình", Icon = new AppIcon(IconKind.Sparkle) };
        standaloneAi.Click += (_, _) => ShowProjectAiWindow(); aiMenu.Items.Add(standaloneAi); AiNavButton.ContextMenu = aiMenu;
        ToolTip.SetTip(AiNavButton, "AI dự án · chuột phải để mở cửa sổ riêng");
        AskAiButton.Click += (_, _) => ShowAgentWorkspace();
        SettingsNavButton.Click += (_, _) => _app.ShowSettings(this);
        ProjectPickerButton.Click += (_, _) => { _pickerTimer.Stop(); _pickerTimer.Start(); };
        _pickerTimer.Tick += (_, _) => { _pickerTimer.Stop(); _drawerOpen = !_drawerOpen; ApplyResponsive(); };
        ProjectPickerButton.DoubleTapped += (_, e) => { _pickerTimer.Stop(); RenameProject(); e.Handled = true; };
        CompactProjectPicker.Click += (_, _) => { _drawerOpen = !_drawerOpen; ApplyResponsive(); };
        ProjectMenuButton.Click += (_, _) => OpenProjectMenu();
        PriorityButton.Click += (_, _) => PrioritizeProject();
        ProjectPickerButton.ContextRequested += (_, e) => { OpenProjectMenu(); e.Handled = true; };
        CloseDrawerButton.Click += (_, _) => { _drawerOpen = false; ApplyResponsive(); };
        DrawerShade.PointerPressed += (_, _) => { _drawerOpen = false; ApplyResponsive(); };
        ProjectTitleEditor.Changed += _app.ScheduleSave;
        ProjectTitleEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { FlushNotes(); EndRename(); _app.ScheduleSave(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ProjectTitleEditor.Load(_nameBeforeEdit ?? RichDocument.Plain("")); if (_notesProject is not null && _nameBeforeEdit is not null) _notesProject.NameRich = _nameBeforeEdit; EndRename(); _app.ScheduleSave(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        ProjectList.SelectionChanged += (_, _) => { if (!_updatingNavigator && ProjectList.SelectedItem is ProjectNavItem item) OpenProjectWorkspace(_board, item.Project); };
        ProjectList.AddHandler(PointerPressedEvent, ProjectPressed, RoutingStrategies.Tunnel);
        ProjectList.AddHandler(PointerMovedEvent, ProjectMoved, RoutingStrategies.Tunnel);
        ProjectList.AddHandler(PointerReleasedEvent, ProjectReleased, RoutingStrategies.Tunnel);
        ProjectList.PointerCaptureLost += (_, _) => CancelProjectDrag();
        AgentTabButton.Click += (_, _) => ShowAgentWorkspace();
        TasksTabButton.Click += (_, _) => ShowProjectDetail(ProjectWorkspaceTasksMode);
        NotesTabButton.Click += (_, _) => ShowProjectDetail(ProjectWorkspaceNotesMode);
        ResourcesTabButton.Click += (_, _) => ShowProjectDetail(ProjectWorkspaceResourcesMode);
        HistoryTabButton.Click += (_, _) => ShowProjectDetail(ProjectWorkspaceHistoryMode);
        EvidenceTabButton.Click += (_, _) => ShowProjectDetail(ProjectWorkspaceEvidenceMode);
        CollapseTasksButton.Click += (_, _) => { if (_notesProject is not null) _notesProject.Layout.TasksCollapsed = !_notesProject.Layout.TasksCollapsed; ApplyResponsive(); _app.ScheduleSave(); };
        NotesSplitter.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_notesProject is null || !NotesSplitter.IsVisible) return;
            var total = TasksPane.Bounds.Height + NotesPane.Bounds.Height;
            if (total > 0) { _notesProject.Layout.NotesFraction = Math.Clamp(NotesPane.Bounds.Height / total, .15, .85); _notesProject.Layout.HasCustomSplit = true; }
            _app.ScheduleSave();
        }), RoutingStrategies.Bubble, true);
        AiResizeGrip.PointerPressed += (_, e) => { if (_notesProject is null || _notesProject.Layout.AiDock != "floating") return; _resizeStart = e.GetPosition(WorkAndAi); _resizeSize = AiHostBorder.Bounds.Size; e.Pointer.Capture(AiResizeGrip); e.Handled = true; };
        AiResizeGrip.PointerMoved += (_, e) =>
        {
            if (_resizeStart is not { } start || _notesProject is null) return;
            var point = e.GetPosition(WorkAndAi); _notesProject.Layout.AiWidth = Math.Max(300, _resizeSize.Width + point.X - start.X); _notesProject.Layout.AiHeight = Math.Max(350, _resizeSize.Height + point.Y - start.Y); ApplyResponsive();
        };
        AiResizeGrip.PointerReleased += (_, e) => { _resizeStart = null; e.Pointer.Capture(null); _app.ScheduleSave(); };
        AiResizeGrip.PointerCaptureLost += (_, _) => _resizeStart = null;
        _dragGhost.Child = _ghostTitle; Grid.SetColumnSpan(_dragGhost, 3); RootBody.Children.Add(_dragGhost);
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty || e.Property == WindowStateProperty) ApplyResponsive(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _drawerOpen = true; ApplyResponsive(); SearchBox.Focus(); e.Handled = true; }
            if (e.Key == Key.Escape) { _drawerOpen = false; CancelProjectDrag(); EndAiDrag(false); ApplyResponsive(); }
        }, RoutingStrategies.Tunnel);
    }

    private RichDocument? _nameBeforeEdit;
    private Point? _resizeStart;
    private Size _resizeSize;
    private void RenameProject()
    {
        if (_notesProject is null) return;
        _nameBeforeEdit = _notesProject.ReadName(); ProjectTitleEditor.Load(_nameBeforeEdit);
        ProjectTitleEditor.IsVisible = true; ProjectPickerButton.IsVisible = false; ProjectTitleEditor.FocusEditor(true);
    }
    private void EndRename() { ProjectTitleEditor.IsVisible = false; ProjectPickerButton.IsVisible = true; UpdateSummary(); }
    private void SelectCurrent(ProjectRecord? project)
    {
        if (_notesProject?.Id == project?.Id && project is not null) { UpdateSummary(); return; }
        Flush(); EndRename(); _notesProject = project; _board.SelectedProjectId = project?.Id;
        _switching = true;
        if (project is not null) Sheet.FocusProject(project);
        else Sheet.SetBoard(_board);
        NotesEditor.Load(project?.ReadNotes() ?? RichDocument.Plain("")); _loadedNotesSource = project?.NotesRich;
        NotesEditor.IsEnabled = project is not null; _notesCollapsed = project?.Layout.NotesCollapsed == true;
        _chat.SetProject(project); _switching = false; UpdateSummary(); ApplyResponsive();
        if (IsVisible || DetachedAiWindow?.IsVisible == true) _app.ScheduleSave();
    }

    private void ShowAgentWorkspace()
    {
        if (_notesProject is null) return;
        ReturnProjectAiToBoard();
        _projectWorkspaceMode = ProjectWorkspaceAgentMode;
        _drawerOpen = false;
        ApplyResponsive();
    }

    private void ShowProjectDetail(string mode)
    {
        if (_notesProject is null) return;
        _projectWorkspaceMode = mode switch
        {
            ProjectWorkspaceNotesMode => ProjectWorkspaceNotesMode,
            ProjectWorkspaceResourcesMode => ProjectWorkspaceResourcesMode,
            ProjectWorkspaceHistoryMode => ProjectWorkspaceHistoryMode,
            ProjectWorkspaceEvidenceMode => ProjectWorkspaceEvidenceMode,
            _ => ProjectWorkspaceTasksMode
        };

        if (_projectWorkspaceMode == ProjectWorkspaceNotesMode)
        {
            _notesProject.Layout.Tab = "notes";
            _notesProject.Layout.NotesCollapsed = false;
            if (!_wide) _notesProject.Layout.TasksCollapsed = true;
        }
        else if (_projectWorkspaceMode == ProjectWorkspaceTasksMode)
        {
            _notesProject.Layout.Tab = "tasks";
            _notesProject.Layout.TasksCollapsed = false;
            Sheet.SetFilter(SearchBox.Text ?? "");
        }
        else if (_projectWorkspaceMode == ProjectWorkspaceResourcesMode)
        {
            RefreshProjectResources();
        }
        else if (_projectWorkspaceMode == ProjectWorkspaceHistoryMode)
        {
            RefreshProjectHistory();
        }
        else
        {
            RefreshProjectEvidence();
        }

        ApplyResponsive();
        _app.ScheduleSave();
    }

    private void RefreshNavigator()
    {
        if (ProjectList is null || _board is null || _updatingNavigator) return;
        var query = SearchBox.Text?.Trim() ?? "";
        var items = _board.Projects.Select((p, index) => new ProjectNavItem(p, index + 1)).Where(i => query.Length == 0
            || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Project.NotesText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || i.Project.ChecklistItems.Any(t => (t.DisplayText + " " + t.CommentText).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        var signature = string.Join("|", items.Select(i => i.Project.Id + i.Title + i.Summary)) + _notesProject?.Id;
        if (signature == _navigatorSignature) return;
        _navigatorSignature = signature; _updatingNavigator = true;
        ProjectList.ItemsSource = items; ProjectList.SelectedItem = items.FirstOrDefault(i => i.Project.Id == _notesProject?.Id);
        _updatingNavigator = false;
    }

    private void ApplyResponsive()
    {
        if (RootBody is null || WorkAndAi is null) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        _wide = _wide ? width >= 880 : width >= 900;

        var layout = _notesProject?.Layout ?? new ProjectLayout();
        var projectOpen = !_showCommandCenter && _notesProject is not null;
        var primaryAgent = projectOpen
            && _projectWorkspaceMode == ProjectWorkspaceAgentMode
            && DetachedAiWindow?.IsVisible != true;
        var resourcesMode = projectOpen
            && _projectWorkspaceMode == ProjectWorkspaceResourcesMode;
        var historyMode = projectOpen
            && _projectWorkspaceMode == ProjectWorkspaceHistoryMode;
        var evidenceMode = projectOpen
            && _projectWorkspaceMode == ProjectWorkspaceEvidenceMode;

        var fullscreen = WindowState == WindowState.Maximized;
        if (!primaryAgent
            && fullscreen
            && !_wasFullscreen
            && DetachedAiWindow?.IsVisible != true
            && _notesProject is { Layout.AiExplicitlyHidden: false })
            _notesProject.Layout.AiDock = "right";
        _wasFullscreen = fullscreen;

        // Explicit legacy docking still works in detail mode. Agent-first mode ignores the
        // persisted legacy AiDock default and always presents the same AiChatPanel as primary.
        var legacyAi = projectOpen
            && !primaryAgent
            && DetachedAiWindow?.IsVisible != true
            && layout.AiDock != "hidden";
        var inlineAi = legacyAi && width < 900;
        var dockRight = legacyAi && !inlineAi && layout.AiDock == "right";
        var dockBottom = legacyAi && !inlineAi && layout.AiDock == "bottom";
        var floating = legacyAi && !inlineAi && !dockRight && !dockBottom;

        var sidebar = _wide
            && !_manualSidebarCollapsed
            && !primaryAgent
            && !(dockRight && width < 1280);
        RootBody.ColumnDefinitions[1].Width = new GridLength(sidebar ? 250 : 0);
        Sidebar.IsVisible = sidebar || _drawerOpen;
        Sidebar.Width = sidebar ? double.NaN : Math.Min(360, width - 76);
        Sidebar.HorizontalAlignment = sidebar ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetColumnSpan(Sidebar, sidebar ? 1 : 2);
        DrawerShade.IsVisible = _drawerOpen && !sidebar;
        CloseDrawerButton.IsVisible = _drawerOpen && !sidebar;

        var shortWindow = height < 730;
        var notesTab = shortWindow && layout.Tab == "notes";
        CompactTabs.IsVisible = projectOpen;
        CompactProjectPicker.IsVisible = projectOpen && !_wide;
        CompactPickerLabel.Text = "Dự án / " + (_notesProject is null
            ? ""
            : (_board.Projects.IndexOf(_notesProject) + 1).ToString());

        PriorityButton.IsEnabled = _notesProject is not null;
        ProjectTitle.FontSize = !_wide ? 26 : 22;
        Sheet.SetCompact(width - 56 - (sidebar ? 250 : 0) - (dockRight ? 345 : 0) - 32 < 600);

        CommandCenter.IsVisible = _showCommandCenter;
        WorkContent.IsVisible = projectOpen && !inlineAi;
        EditorSplit.IsVisible = projectOpen && !primaryAgent && !resourcesMode && !historyMode && !evidenceMode && !inlineAi;
        ProjectResourcesPane.IsVisible = projectOpen && resourcesMode && !inlineAi;
        ProjectHistoryPane.IsVisible = projectOpen && historyMode && !inlineAi;
        ProjectEvidencePane.IsVisible = projectOpen && evidenceMode && !inlineAi;

        // Detail mode keeps the mature task/note editor behavior. It is secondary now because
        // opening a project starts in primaryAgent; tabs expose this surface in one click.
        TasksPane.IsVisible = !notesTab;
        NotesPane.IsVisible = !shortWindow || notesTab;
        NotesSplitter.IsVisible = !primaryAgent
            && !shortWindow
            && !layout.TasksCollapsed
            && !layout.NotesCollapsed;

        var fraction = layout.HasCustomSplit && double.IsFinite(layout.NotesFraction)
            ? Math.Clamp(layout.NotesFraction, .15, .85)
            : _wide ? .52 : .44;
        EditorSplit.RowDefinitions[0].Height = notesTab
            ? new GridLength(0)
            : layout.TasksCollapsed ? new GridLength(96) : new GridLength(1 - fraction, GridUnitType.Star);
        EditorSplit.RowDefinitions[1].Height = new GridLength(NotesSplitter.IsVisible ? ResizeSplitter.HitSize : 0);
        EditorSplit.RowDefinitions[2].Height = shortWindow && !notesTab
            ? new GridLength(0)
            : layout.NotesCollapsed ? new GridLength(38) : new GridLength(fraction, GridUnitType.Star);

        Sheet.IsVisible = !layout.TasksCollapsed;
        TasksCollapseIcon.Kind = layout.TasksCollapsed ? IconKind.ChevronUp : IconKind.ChevronDown;
        NotesCollapseIcon.Kind = layout.NotesCollapsed ? IconKind.ChevronUp : IconKind.ChevronDown;
        NotesToolbar.IsVisible = NotesEditorBorder.IsVisible = !layout.NotesCollapsed;

        AgentTabButton.IsEnabled = !primaryAgent;
        TasksTabButton.IsEnabled = primaryAgent || resourcesMode || historyMode || evidenceMode || layout.Tab != "tasks";
        NotesTabButton.IsEnabled = primaryAgent || resourcesMode || historyMode || evidenceMode || layout.Tab != "notes";
        ResourcesTabButton.IsEnabled = !resourcesMode;
        HistoryTabButton.IsEnabled = !historyMode;
        EvidenceTabButton.IsEnabled = !evidenceMode;
        AgentTabButton.Foreground = RichEditor.Brush(primaryAgent ? "#FFFFFF" : "#796C62");
        TasksTabButton.Foreground = RichEditor.Brush(!primaryAgent && !resourcesMode && !historyMode && !evidenceMode && !notesTab ? "#A4573D" : "#796C62");
        NotesTabButton.Foreground = RichEditor.Brush(!primaryAgent && !resourcesMode && !historyMode && !evidenceMode && notesTab ? "#A4573D" : "#796C62");
        ResourcesTabButton.Foreground = RichEditor.Brush(resourcesMode ? "#A4573D" : "#796C62");
        HistoryTabButton.Foreground = RichEditor.Brush(historyMode ? "#A4573D" : "#796C62");
        EvidenceTabButton.Foreground = RichEditor.Brush(evidenceMode ? "#A4573D" : "#796C62");

        AiHostBorder.IsVisible = primaryAgent || legacyAi;

        if (primaryAgent)
        {
            // Wide: compact project summary on the left, Agent gets the majority on the right.
            // Narrow: keep the project header/tabs above and overlay Agent only below that header.
            WorkAndAi.ColumnDefinitions[0].Width = _wide
                ? new GridLength(310)
                : new GridLength(1, GridUnitType.Star);
            WorkAndAi.ColumnDefinitions[1].Width = new GridLength(_wide ? 12 : 0);
            WorkAndAi.ColumnDefinitions[2].Width = _wide
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            WorkAndAi.RowDefinitions[1].Height = new GridLength(0);
            WorkAndAi.RowDefinitions[2].Height = new GridLength(0);

            AiVerticalSplitter.IsVisible = false;
            AiHorizontalSplitter.IsVisible = false;
            Grid.SetColumn(AiHostBorder, _wide ? 2 : 0);
            Grid.SetRow(AiHostBorder, 0);
            Grid.SetColumnSpan(AiHostBorder, _wide ? 1 : 3);
            Grid.SetRowSpan(AiHostBorder, 3);
            AiHostBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            AiHostBorder.VerticalAlignment = VerticalAlignment.Stretch;
            AiHostBorder.Width = double.NaN;
            AiHostBorder.Height = double.NaN;
            AiHostBorder.Margin = _wide
                ? new Thickness(0)
                : new Thickness(0, 155, 0, 0);
            AiResizeGrip.IsVisible = false;
        }
        else
        {
            WorkAndAi.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkAndAi.ColumnDefinitions[2].Width = new GridLength(dockRight ? 340 : 0);
            WorkAndAi.ColumnDefinitions[1].Width = new GridLength(dockRight ? ResizeSplitter.HitSize : 0);
            WorkAndAi.RowDefinitions[2].Height = new GridLength(
                dockBottom ? Math.Clamp(height * .38, 240, 360) : 0);
            WorkAndAi.RowDefinitions[1].Height = new GridLength(dockBottom ? ResizeSplitter.HitSize : 0);

            AiVerticalSplitter.IsVisible = dockRight;
            AiHorizontalSplitter.IsVisible = dockBottom;
            Grid.SetColumn(AiHostBorder, dockRight ? 2 : 0);
            Grid.SetRow(AiHostBorder, dockBottom ? 2 : 0);
            Grid.SetColumnSpan(AiHostBorder, dockRight ? 1 : 3);
            Grid.SetRowSpan(AiHostBorder, floating || inlineAi ? 3 : 1);
            AiHostBorder.HorizontalAlignment = floating ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            AiHostBorder.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;

            var workWidth = width - 56 - (sidebar ? 250 : 0);
            var workHeight = height - 72;
            AiHostBorder.Width = floating
                ? Math.Clamp(layout.AiWidth, 300, Math.Max(300, workWidth - 24))
                : double.NaN;
            AiHostBorder.Height = floating
                ? Math.Clamp(layout.AiHeight, 350, Math.Max(350, workHeight - 24))
                : double.NaN;
            AiHostBorder.Margin = floating
                ? new Thickness(
                    Math.Clamp(layout.AiX < 0 ? workWidth - AiHostBorder.Width - 12 : layout.AiX, 0, Math.Max(0, workWidth - AiHostBorder.Width)),
                    Math.Clamp(layout.AiY < 0 ? workHeight - AiHostBorder.Height - 12 : layout.AiY, 0, Math.Max(0, workHeight - AiHostBorder.Height)),
                    0,
                    0)
                : new Thickness(0);
            AiResizeGrip.IsVisible = floating;
        }

        _chat.SetCompact(primaryAgent && !_wide || inlineAi);
    }

    private void SetAiDock(string mode)
    {
        if (mode == "desktop") { ShowProjectAiWindow(); return; }
        ReturnProjectAiToBoard();
        if (_notesProject is not null)
        {
            _notesProject.Layout.AiDock = mode;
            _notesProject.Layout.AiExplicitlyHidden = mode == "hidden";

            // Explicit docking is a detail+AI layout. The Agent-first workspace remains the
            // default when a project is opened or the user presses the Agent tab.
            if (_projectWorkspaceMode == ProjectWorkspaceAgentMode)
                _projectWorkspaceMode = _notesProject.Layout.Tab == "notes"
                    ? ProjectWorkspaceNotesMode
                    : ProjectWorkspaceTasksMode;
        }
        ApplyResponsive();
        _app.ScheduleSave();
    }

    private void OpenProjectMenu()
    {
        var p = _notesProject; if (p is null) return;
        var menu = new ContextMenu();
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Sửa tên / định dạng chữ", RenameProject);
        Add("Ưu tiên thực hiện trước", PrioritizeProject);
        Add("Xóa dự án…", async () => await DeleteRow(new(p, null, _board.Projects.IndexOf(p) + 1)));
        menu.Open(ProjectMenuButton);
    }
    private void PrioritizeProject()
    {
        if (_notesProject is null || _board.Projects.Count == 0) return;
        SheetOperations.MoveProject(_board, _notesProject.Id, _board.Projects[0].Id, false);
        UpdateSummary(); _app.ScheduleSave();
    }
    private void ShowNotesMenu()
    {
        var menu = new ContextMenu(); var add = new MenuItem { Header = "＋ Ghi chú mới" }; add.Click += (_, _) => _app.NewNote(); menu.Items.Add(add);
        foreach (var note in _app.State.Notes.Where(n => !n.IsBoard && !n.IsChat && !n.IsArchived)) { var item = new MenuItem { Header = note.Title }; item.Click += (_, _) => _app.OpenNote(note); menu.Items.Add(item); }
        menu.Open(NotesNavButton);
    }

    private static ProjectNavItem? NavItem(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as ProjectNavItem;
    private void ProjectPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ProjectList).Properties.IsLeftButtonPressed || !string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        _pressedProject = NavItem(e.Source); _projectPress = e.GetPosition(RootBody);
    }
    private void ProjectMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedProject is null || !e.GetCurrentPoint(ProjectList).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(RootBody);
        if (!_projectDragging && Math.Abs(point.X - _projectPress.X) + Math.Abs(point.Y - _projectPress.Y) < 8) return;
        if (!_projectDragging) e.Pointer.Capture(ProjectList);
        _projectDragging = true; _ghostTitle.Text = _pressedProject.Title; MoveGhost(point);
        _targetProject = null;
        foreach (var item in ProjectList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var top = item.TranslatePoint(default, RootBody); if (top is null || point.Y < top.Value.Y || point.Y > top.Value.Y + item.Bounds.Height) continue;
            _targetProject = item.DataContext as ProjectNavItem; _projectDropAfter = point.Y > top.Value.Y + item.Bounds.Height / 2;
            ProjectDragHint.Text = "Thả " + (_projectDropAfter ? "sau: " : "trước: ") + _targetProject?.Title; break;
        }
        e.Handled = true;
    }
    private void ProjectReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_projectDragging && _pressedProject is { } source && _targetProject is { } target)
        { SheetOperations.MoveProject(_board, source.Project.Id, target.Project.Id, _projectDropAfter); UpdateSummary(); _app.ScheduleSave(); e.Handled = true; }
        CancelProjectDrag();
        e.Pointer.Capture(null);
    }
    private void CancelProjectDrag() { _pressedProject = _targetProject = null; _projectDragging = false; _dragGhost.IsVisible = false; ProjectDragHint.Text = "Kéo để sắp xếp · Ctrl+K đổi dự án"; }
    private void MoveGhost(Point point) { _dragGhost.Margin = new Thickness(Math.Clamp(point.X + 12, 0, Math.Max(0, RootBody.Bounds.Width - 270)), Math.Clamp(point.Y + 12, 0, Math.Max(0, RootBody.Bounds.Height - 50)), 0, 0); _dragGhost.IsVisible = true; }
    private string? _pendingAiDock;
    private Point _pendingAiPosition;
    private void BeginAiDrag(PointerEventArgs e) { _ghostTitle.Text = "⠿  Hỏi AI"; MoveAiDrag(e); }
    private void MoveAiDrag(PointerEventArgs e)
    {
        MoveGhost(e.GetPosition(RootBody)); var point = e.GetPosition(WorkAndAi);
        _pendingAiPosition = new Point(Math.Max(0, point.X - 90), Math.Max(0, point.Y - 20));
        _pendingAiDock = point.Y > WorkAndAi.Bounds.Height - 200 ? "bottom" : point.X > WorkAndAi.Bounds.Width - 140 ? "right" : "floating";
        DockPreview.IsVisible = _pendingAiDock != "floating";
        DockPreview.HorizontalAlignment = _pendingAiDock == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DockPreview.VerticalAlignment = _pendingAiDock == "right" ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        DockPreview.Width = _pendingAiDock == "right" ? 330 : double.NaN; DockPreview.Height = _pendingAiDock == "right" ? double.NaN : 220;
        DockPreviewText.Text = _pendingAiDock == "right" ? "Thả để ghim AI bên phải" : "Thả để ghim AI ở phía dưới";
    }
    private void EndAiDrag(bool apply)
    {
        _dragGhost.IsVisible = DockPreview.IsVisible = false;
        if (apply && _pendingAiDock is not null && _notesProject is not null)
        { if (_pendingAiDock == "floating") { _notesProject.Layout.AiX = _pendingAiPosition.X; _notesProject.Layout.AiY = _pendingAiPosition.Y; } SetAiDock(_pendingAiDock); }
        _pendingAiDock = null;
    }
    private sealed record ProjectNavItem(ProjectRecord Project, int Number)
    {
        public string Title => Project.DisplayName;
        public string Summary => Project.Progress + " · " + (Project.Next?.DisplayText ?? "Đã hoàn thành");
        public double Percent => ProjectProgressCalculator.Calculate(Project).Percent;
    }
}
