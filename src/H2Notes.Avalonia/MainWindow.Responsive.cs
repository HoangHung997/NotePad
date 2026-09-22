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
        AiNavButton.Click += (_, _) => _app.ShowWorkAssistantFull();
        var aiMenu = new ContextMenu();
        var projectAi = new MenuItem { Header = "Agent của dự án", Icon = new AppIcon(IconKind.Folder) };
        projectAi.Click += (_, _) => ShowAgentWorkspace(); aiMenu.Items.Add(projectAi);
        var standaloneAi = new MenuItem { Header = "Tách Agent dự án ra màn hình", Icon = new AppIcon(IconKind.Sparkle) };
        standaloneAi.Click += (_, _) => ShowProjectAiWindow(); aiMenu.Items.Add(standaloneAi); AiNavButton.ContextMenu = aiMenu;
        ToolTip.SetTip(AiNavButton, "Mở H2 Assistant · chuột phải để mở Agent của dự án");
        AskAiButton.Click += (_, _) => ShowAgentWorkspace();
        SettingsNavButton.Click += (_, _) => _app.ShowSettings(this);
        ProjectPickerButton.Click += (_, _) => { _drawerOpen = !_drawerOpen; ApplyResponsive(); };
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
            if (total > 0)
            {
                var localLayout = _app.LocalSettings.GetProjectLayout(_notesProject.Id);
                localLayout.NotesFraction = Math.Clamp(NotesPane.Bounds.Height / total, .15, .85);
                localLayout.HasCustomSplit = true;
                _app.LocalSettings.Save();
            }
        }), RoutingStrategies.Bubble, true);
        AiResizeGrip.PointerPressed += (_, e) => { if (_notesProject is null || _notesProject.Layout.AiDock != "floating") return; _resizeStart = e.GetPosition(WorkAndAi); _resizeSize = AiHostBorder.Bounds.Size; e.Pointer.Capture(AiResizeGrip); e.Handled = true; };
        AiResizeGrip.PointerMoved += (_, e) =>
        {
            if (_resizeStart is not { } start || _notesProject is null) return;
            var point = e.GetPosition(WorkAndAi);
            var localLayout = _app.LocalSettings.GetProjectLayout(_notesProject.Id);
            localLayout.AiWidth = Math.Max(300, _resizeSize.Width + point.X - start.X);
            localLayout.AiHeight = Math.Max(350, _resizeSize.Height + point.Y - start.Y);
            ApplyResponsive();
        };
        AiResizeGrip.PointerReleased += (_, e) => { _resizeStart = null; e.Pointer.Capture(null); _app.LocalSettings.Save(); };
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
        _documentInspector.IsVisible = false; _openDocument = null; _documentInspector.Child = null;
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
        _showCommandCenter = false;
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
            || i.Project.Conversations.Any(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            || i.Project.ChecklistItems.Any(t => (t.DisplayText + " " + t.CommentText).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        var signature = string.Join("|", items.Select(i => i.Project.Id + i.Title + i.Summary + i.Project.SelectedAiConversationId + string.Join(";", i.Project.Conversations.Select(c => c.Id + c.Title)))) + _notesProject?.Id;
        if (signature == _navigatorSignature) return;
        _navigatorSignature = signature; _updatingNavigator = true;
        ProjectList.ItemsSource = items; ProjectList.SelectedItem = items.FirstOrDefault(i => i.Project.Id == _notesProject?.Id);
        _updatingNavigator = false;
    }

    private void ApplyResponsive()
    {
        if (RootBody is null || WorkAndAi is null || _chat is null) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        _wide = width >= 980;
        var projectOpen = !_showCommandCenter && _notesProject is not null;
        var primaryAgent = projectOpen && _projectWorkspaceMode == ProjectWorkspaceAgentMode;
        var tasks = projectOpen && _projectWorkspaceMode == ProjectWorkspaceTasksMode;
        var notes = projectOpen && _projectWorkspaceMode == ProjectWorkspaceNotesMode;
        var files = projectOpen && _projectWorkspaceMode == ProjectWorkspaceResourcesMode;
        var history = projectOpen && _projectWorkspaceMode == ProjectWorkspaceHistoryMode;
        var evidence = projectOpen && _projectWorkspaceMode == ProjectWorkspaceEvidenceMode;
        var document = projectOpen && _documentInspector.IsVisible && (primaryAgent || files || history || evidence);
        var documentPage = document && width < 980;
        var sidebar = projectOpen && width >= 1280 && !_manualSidebarCollapsed;
        RootBody.ColumnDefinitions[1].Width = new GridLength(sidebar ? 220 : 0);
        Sidebar.IsVisible = sidebar || _drawerOpen;
        Sidebar.Width = sidebar ? double.NaN : Math.Min(390, width - 76);
        Sidebar.HorizontalAlignment = sidebar ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetColumnSpan(Sidebar, sidebar ? 1 : 2);
        DrawerShade.IsVisible = _drawerOpen && !sidebar;
        CloseDrawerButton.IsVisible = _drawerOpen && !sidebar;
        var dock = projectOpen && (tasks || notes) && width >= 1200 && _notesProject?.Layout.AiExplicitlyHidden != true && DetachedAiWindow?.IsVisible != true;
        var narrow = width < 700;
        var shortWindow = height <= 650;
        CompactProjectPicker.IsVisible = false;
        ProjectTitle.FontSize = narrow ? 22 : 24;
        ProjectProgress.IsVisible = !shortWindow;
        ProjectProgress.MaxLines = 1; ProjectProgress.TextTrimming = TextTrimming.CharacterEllipsis;
        ProjectNextSummary.IsVisible = false;
        PriorityButton.IsVisible = false;
        CompactTabs.IsVisible = projectOpen;
        NotesTabButton.IsVisible = !shortWindow || !narrow;
        ResourcesTabButton.IsVisible = HistoryTabButton.IsVisible = !narrow;
        EvidenceTabButton.IsVisible = false;
        TabsOverflowButton.IsVisible = true;
        foreach (var (button, selected) in new[] { (AgentTabButton,primaryAgent), (TasksTabButton,tasks), (NotesTabButton,notes),
            (ResourcesTabButton,files), (HistoryTabButton,history), (EvidenceTabButton,evidence) })
        { button.IsEnabled = true; button.Classes.Set("selected",selected); button.Foreground = RichEditor.Brush(selected ? "#A4573D" : "#495363"); }
        CommandCenter.IsVisible = _showCommandCenter;
        WorkContent.IsVisible = projectOpen && !documentPage;
        EditorSplit.IsVisible = tasks || notes;
        ProjectResourcesPane.IsVisible = files;
        ProjectHistoryPane.IsVisible = history;
        ProjectEvidencePane.IsVisible = evidence;
        TasksPane.IsVisible = tasks; NotesPane.IsVisible = notes;
        NotesSplitter.IsVisible = false;
        EditorSplit.RowDefinitions[0].Height = tasks ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        EditorSplit.RowDefinitions[1].Height = new GridLength(0);
        EditorSplit.RowDefinitions[2].Height = notes ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        Sheet.IsVisible = true; NotesToolbar.IsVisible = NotesEditorBorder.IsVisible = true;
        Sheet.SetCompact(width - 56 - (sidebar ? 220 : 0) - (dock ? 360 : 0) - 32 < 600);
        AiHostBorder.IsVisible = !documentPage && (primaryAgent || dock) && DetachedAiWindow?.IsVisible != true;
        AskAiButton.IsVisible = !primaryAgent;
        var targetParent = primaryAgent ? WorkContent : WorkAndAi;
        if (AiHostBorder.Parent != targetParent)
        { (AiHostBorder.Parent as Panel)?.Children.Remove(AiHostBorder); targetParent.Children.Add(AiHostBorder); }
        Grid.SetColumn(AiHostBorder,primaryAgent ? 0 : 2);
        Grid.SetRow(AiHostBorder,primaryAgent ? 2 : 0);
        Grid.SetColumnSpan(AiHostBorder,1); Grid.SetRowSpan(AiHostBorder,1);
        AiHostBorder.HorizontalAlignment = HorizontalAlignment.Stretch; AiHostBorder.VerticalAlignment = VerticalAlignment.Stretch;
        AiHostBorder.Width = AiHostBorder.Height = double.NaN; AiHostBorder.Margin = new Thickness(0);
        AiHostBorder.BorderThickness = new Thickness(primaryAgent ? 0 : 1,0,0,0); AiHostBorder.CornerRadius = new CornerRadius(0);
        AiResizeGrip.IsVisible = AiVerticalSplitter.IsVisible = AiHorizontalSplitter.IsVisible = false;
        var inspectorWidth = width < 1280 ? 360 : 400;
        WorkAndAi.ColumnDefinitions[0].Width = new GridLength(1,GridUnitType.Star);
        WorkAndAi.ColumnDefinitions[1].Width = new GridLength(0);
        WorkAndAi.ColumnDefinitions[2].Width = new GridLength(document && !documentPage ? inspectorWidth : dock ? 360 : 0);
        WorkAndAi.RowDefinitions[1].Height = WorkAndAi.RowDefinitions[2].Height = new GridLength(0);
        Grid.SetColumn(_documentInspector,documentPage ? 0 : 2);
        Grid.SetRowSpan(_documentInspector,3);
        _documentInspector.IsVisible = document;
        _chat.MoveComposerTo(dock ? WorkContent : null);
        _chat.SetWorkspacePresentation(primaryAgent);
        _chat.SetCompact(false);
        UpdateOverviewCardWidth(width);
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
        {
            if (_pendingAiDock == "floating")
            {
                var localLayout = _app.LocalSettings.GetProjectLayout(_notesProject.Id);
                localLayout.AiX = _pendingAiPosition.X;
                localLayout.AiY = _pendingAiPosition.Y;
                _app.LocalSettings.Save();
            }
            SetAiDock(_pendingAiDock);
        }
        _pendingAiDock = null;
    }
    private sealed record ProjectNavItem(ProjectRecord Project, int Number)
    {
        public string Title => Project.DisplayName;
        public string Summary => Project.Progress + " · " + (Project.Next?.DisplayText ?? "Đã hoàn thành");
        public double Percent => ProjectProgressCalculator.Calculate(Project).Percent;
    }
}
