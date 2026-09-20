using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow : Window
{
    private readonly App _app;
    private NoteRecord _board;
    private ProjectRecord? _notesProject;
    private RichDocument? _loadedNotesSource;
    private bool _notesCollapsed;
    private bool _switching;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    public Guid BoardId => _board.Id;
    public Guid? SelectedProjectId => _notesProject?.Id;
    public Task StopAiAsync() => _chat.StopAsync();
    public void CancelAi() => _chat.Cancel();
    public void RefreshAiConnections() => _chat.RefreshConnections();
    public MainWindow() : this(Application.Current as App ?? new App(), new NoteRecord()) { }
    public MainWindow(App app, NoteRecord board)
    {
        _app = app; _board = board;
        InitializeComponent();
        Icon = app.Icon;
        BrandImage.Source = new Bitmap(AssetLoader.Open(new Uri("avares://H2Notes.Avalonia/Assets/app.png")));
        NotesToolbar.Content = NotesEditor.CreateToolbar();
        InitializeShell();
        InitializeCommandCenter();
        InitializeProjectResources();
        InitializeProjectHistory();
        InitializeProjectEvidenceInspector();
        Sheet.SelectionChanged += OnSelection;
        Sheet.DataChanged += () =>
        {
            if (_switching) return;
            if (Sheet.SelectedRow is { } changed)
            {
                var now = DateTime.UtcNow;
                changed.Project.UpdatedAtUtc = now;
                if (changed.Task is { } task)
                {
                    task.UpdatedAtUtc = now;
                    // Only timestamp completion for records created by the new timestamp-aware app.
                    // Legacy completed tasks intentionally remain unknown instead of being backfilled with "now".
                    if (task.CreatedAtUtc is not null)
                        task.CompletedAtUtc = task.IsCompleted ? task.CompletedAtUtc ?? now : null;
                }
                _app.MarkProjectDirty(changed.Project.Id);
            }
            UpdateSummary(); _app.ScheduleSave();
        };
        Sheet.DraftChanged += _app.ScheduleSave;
        Sheet.EditStarting += FlushNotes;
        Sheet.DeleteRequested += async row => await DeleteRow(row);
        NotesEditor.Changed += _app.ScheduleSave;
        SearchBox.TextChanged += (_, _) =>
        {
            if (!_showCommandCenter && _projectWorkspaceMode == ProjectWorkspaceTasksMode)
                Sheet.SetFilter(SearchBox.Text ?? "");
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshNavigator();
            if (!_showCommandCenter && _projectWorkspaceMode == "tasks")
                Sheet.SetFilter(SearchBox.Text ?? "");
        };
        AddProjectButton.Click += async (_, _) => await AddProject();
        AddTaskButton.Click += async (_, _) => await AddTask();
        CollapseNotesButton.Click += (_, _) => ToggleNotes();
        PinButton.Click += (_, _) => { Topmost = !Topmost; _board.IsPinned = Topmost; UpdatePin(); _app.ScheduleSave(); };
        CloseButton.Click += (_, _) => Close();
        MenuButton.Click += (_, _) => OpenMenu();
        DesktopWindowChrome.Attach(this, TitleBar);
        Activated += (_, _) => Opacity = 1;
        Deactivated += (_, _) => { Opacity = Math.Clamp(_board.NoteOpacity, .01, 1); _app.ScheduleSave(); };
        Closing += (_, e) =>
        {
            if (DetachedAiWindow?.IsVisible != true) _chat.Cancel();
            if (_app.IsExiting || _app.IsChangingStore || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            Flush(); e.Cancel = true; _board.IsVisibleOnDesktop = false; Hide(); _app.SaveNow();
        };
        SetBoard(board);
        ShowCommandCenter();
        WindowPlacement.Attach(this, () => _board, app, true);
        Opened += (_, _) => { _board.IsVisibleOnDesktop = true; _app.ScheduleSave(); };
    }
    public void SetSaveStatus(string text) { SaveLabel.Text = text; DetachedAiWindow?.SetSaveStatus(text); }
    public void MarkOpen() => _board.IsVisibleOnDesktop = true;
    public void SetBoard(NoteRecord board)
    {
        Flush();
        if (_board.Id != board.Id) _board.IsVisibleOnDesktop = false;
        _board = board; _switching = true; _notesProject = null;
        if (IsVisible) { board.IsVisibleOnDesktop = true; _app.ScheduleSave(); }
        SearchBox.Text = ""; BoardTitle.Text = board.Title; Topmost = board.IsPinned;
        Sheet.Preferences = _app.State.SheetPreferences;
        Sheet.SetBoard(board); _switching = false; SelectCurrent(board.Projects.FirstOrDefault(p => p.Id == board.SelectedProjectId) ?? board.Projects.FirstOrDefault()); UpdatePin(); UpdateSummary(); RefreshCommandCenter();
    }
    private void UpdatePin() { PinIcon.Kind = Topmost ? IconKind.PinFilled : IconKind.Pin; }
    private void OnSelection(SheetRow? row)
    {
        if (_switching || row is null) return;
        if (_notesProject?.Id != row?.Project.Id)
        {
            SelectCurrent(row?.Project); return;
        }
        else if (_notesProject is not null && !NotesEditor.HasChanges && !NotesEditor.IsKeyboardFocusWithin
                 && !ReferenceEquals(_loadedNotesSource, _notesProject.NotesRich))
        { NotesEditor.Load(_notesProject.ReadNotes()); _loadedNotesSource = _notesProject.NotesRich; }
        UpdateSummary();
    }
    private void UpdateSummary()
    {
        NotesTitle.Text = "Ghi chú";
        NextLabel.Text = _notesProject?.Next is { } next ? "Tiếp theo: " + next.DisplayText : _notesProject is null ? "" : "Không còn công việc chưa hoàn thành.";
        StatusLabel.Text = $"{_board.Projects.Count} dự án  ·  {_notesProject?.ChecklistItems.Count ?? 0} công việc trong dự án đang chọn";
        AddTaskButton.IsEnabled = _notesProject is not null;
        ProjectTitle.Text = _notesProject is null ? "Chọn dự án" : _notesProject.DisplayName;
        ProjectProgress.Text = _notesProject is null ? "" : $"{_notesProject.Progress} công việc hoàn thành";
        ProjectNextSummary.Text = _notesProject?.Next is { } summaryNext
            ? "Tiếp theo: " + summaryNext.DisplayText
            : _notesProject is null ? "" : "Tiếp theo: Đã hoàn thành";
        var projectAttention = _notesProject is null || _commandCenterQuery is null
            ? 0
            : _commandCenterQuery.GetNeedsAttention(
                [_notesProject],
                H2WorkspaceHealthSnapshot.Healthy).Count;
        ProjectAttentionSummary.Text = _notesProject is null
            ? ""
            : projectAttention == 0
                ? "Không có việc cần bạn xử lý"
                : $"⚠ {projectAttention} cần bạn xem";
        TasksTitle.Text = $"Công việc  {_notesProject?.Progress ?? "0/0"}";
        DetachedAiWindow?.UpdateProject(_notesProject);
        RefreshNavigator();
        RefreshCommandCenter();
        if (_projectWorkspaceMode == ProjectWorkspaceResourcesMode)
            RefreshProjectResources();
        if (_projectWorkspaceMode == ProjectWorkspaceHistoryMode)
            RefreshProjectHistory();
        if (_projectWorkspaceMode == ProjectWorkspaceEvidenceMode)
            RefreshProjectEvidence();
    }
    public void FlushNotes()
    {
        if (_notesProject is not null && ProjectTitleEditor.HasChanges && !string.IsNullOrWhiteSpace(ProjectTitleEditor.Snapshot().Text))
        {
            _notesProject.NameRich = ProjectTitleEditor.Snapshot();
            _notesProject.UpdatedAtUtc = DateTime.UtcNow;
            ProjectTitleEditor.MarkSaved(); _app.MarkProjectDirty(_notesProject.Id);
        }
        if (_notesProject is not null && NotesEditor.HasChanges)
        {
            _notesProject.NotesRich = NotesEditor.Snapshot();
            _notesProject.UpdatedAtUtc = DateTime.UtcNow;
            _loadedNotesSource = _notesProject.NotesRich; NotesEditor.MarkSaved(); _app.MarkProjectDirty(_notesProject.Id);
        }
    }
    public void Flush()
    {
        if (Sheet is null) return;
        _chat?.Flush();
        DetachedAiWindow?.Flush();
        FlushNotes(); Sheet.FlushDraft();
        if (WindowState == WindowState.Normal && IsVisible)
        { _board.SheetLeft = Position.X; _board.SheetTop = Position.Y; _board.SheetWidth = Width; _board.SheetHeight = Height; }
    }
    public void RefreshAfterSave()
    {
        if (!Sheet.IsEditing) Sheet.Refresh();
        UpdateSummary();
    }
    private async Task AddProject()
    {
        Sheet.CommitEdit(); FlushNotes();
        var name = await Dialogs.Prompt(this, "Thêm dự án", "Tên dự án");
        if (string.IsNullOrWhiteSpace(name)) return;
        var now = DateTime.UtcNow;
        var project = new ProjectRecord { Name = name.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now };
        _board.UpdatedAtUtc = now;
        _board.Projects.Add(project); SearchBox.Text = ""; OpenProjectWorkspace(_board, project); _app.ScheduleSave();
    }
    private async Task AddTask()
    {
        Sheet.CommitEdit(); FlushNotes(); var project = _notesProject; if (project is null) return;
        var text = await Dialogs.Prompt(this, "Thêm công việc", project.DisplayName);
        if (string.IsNullOrWhiteSpace(text)) return;
        var now = DateTime.UtcNow;
        project.ChecklistItems.Add(new TaskRecord { Text = text.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now });
        project.UpdatedAtUtc = now; project.IsExpanded = true;
        Sheet.Refresh(); UpdateSummary(); _app.ScheduleSave();
    }
    private async Task DeleteRow(SheetRow row)
    {
        Flush();
        if (!await Dialogs.Confirm(this, "Xác nhận xóa", row.IsProject ? "Xóa dự án và các công việc bên trong?\n" + row.Title : "Xóa công việc này?\n" + row.Title)) return;
        var now = DateTime.UtcNow;
        if (row.Task is null) { _board.Projects.Remove(row.Project); _board.UpdatedAtUtc = now; _notesProject = null; }
        else { row.Project.ChecklistItems.Remove(row.Task); row.Project.UpdatedAtUtc = now; }
        if (row.IsProject) SelectCurrent(_board.Projects.FirstOrDefault());
        else { Sheet.Refresh(); UpdateSummary(); }
        _app.SaveNow();
    }
    private void ToggleNotes()
    {
        _notesCollapsed = !_notesCollapsed;
        if (_notesProject is not null) _notesProject.Layout.NotesCollapsed = _notesCollapsed;
        ApplyResponsive();
        NotesCollapseIcon.Kind = _notesCollapsed ? IconKind.ChevronUp : IconKind.ChevronDown;
        _app.ScheduleSave();
    }
    private void OpenMenu()
    {
        var menu = new ContextMenu();
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("＋ Ghi chú thường", _app.NewNote);
        Add("Tách AI của dự án ra màn hình", ShowProjectAiWindow);
        Add(WindowState == WindowState.Maximized ? "Trở về kích thước trước" : "Lấp đầy màn hình", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Add("AI: Ghim bên phải", () => SetAiDock("right"));
        Add("AI: Ghim phía dưới", () => SetAiDock("bottom"));
        Add("AI: Cửa sổ nổi", () => SetAiDock("floating"));
        Add("Khôi phục bố cục mặc định", () => { if (_notesProject is not null) { _notesProject.Layout = new(); _app.LocalSettings.ResetProjectLayout(_notesProject.Id); _app.LocalSettings.Save(); _app.ScheduleSave(); } _manualSidebarCollapsed = false; _notesCollapsed = false; ApplyResponsive(); });
        foreach (var board in _app.State.Notes.Where(n => n.IsBoard && !n.IsArchived))
            Add("▦ " + board.Title, () => { Sheet.CommitEdit(); SetBoard(board); ShowCommandCenter(); });
        var otherNotes = _app.State.Notes.Where(n => !n.IsBoard && !n.IsChat && !n.IsArchived).ToList();
        if (otherNotes.Count > 0)
        {
            var notesMenu = new MenuItem { Header = "Các ghi chú thường" };
            foreach (var note in otherNotes) { var item = new MenuItem { Header = note.Title }; item.Click += (_, _) => _app.OpenNote(note); notesMenu.Items.Add(item); }
            menu.Items.Add(notesMenu);
        }
        menu.Items.Add(new Separator());
        Add("Cài đặt…", () => _app.ShowSettings(this));
        Add("Về bản thử này", async () => await Dialogs.Message(this, "H2 Notes · Project Sheet", "Avalonia · bảng dự án nhẹ.\nTận dụng bộ cuộn và cấu trúc cột NeraSpreadSheet.\nDữ liệu thử được lưu riêng, không ghi đè bản WPF."));
        Add("Thoát ứng dụng", _app.ExitApp);
        menu.Open(MenuButton);
    }
}