using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class App : Application
{
    public SheetState State { get; private set; } = new();
    public WindowIcon Icon { get; private set; } = null!;
    public bool IsExiting { get; private set; }
    private INoteStorage _storage = null!;
    private LocalConfiguration _local = new();
    private readonly LocalChatDraftStore _draftStore = new();
    public LocalConfiguration LocalSettings => _local;
    public string DeviceId => _local.DeviceId;
    public bool IsChangingStore { get; private set; }
    public bool UsesProjectFiles => _storage is ProjectWorkspaceStore;
    public string DataFolder => _storage is ProjectWorkspaceStore project ? project.Root : Path.GetDirectoryName(DataPath)!;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<Guid, NoteWindow> _notes = [];
    private readonly Dictionary<Guid, AiChatWindow> _aiWindows = [];
    private readonly List<Window> _windowOrder = [];
    private MainWindow? _main;
    private TrayIcon? _tray;
    private bool _saving;
    private bool _demo;
    private FileStream? _instanceLock;
    private bool _storageReady;
    private bool _restoring;
    private readonly HashSet<Guid> _dirtyProjects = [];
    public void MarkProjectDirty(Guid id) => _dirtyProjects.Add(id);
    public Task StopAiAsync() => Task.WhenAll(_aiWindows.Values.Select(w => w.StopAiAsync()).Append(_main?.StopAiAsync() ?? Task.CompletedTask));
    private void CancelAi() { _main?.CancelAi(); foreach (var window in _aiWindows.Values) window.CancelAi(); }
    public void RefreshAiConnections() { _main?.RefreshAiConnections(); foreach (var window in _aiWindows.Values) window.RefreshAiConnections(); }
    private DateTime _lastTrayClick;
    public string DataPath => _storage.FilePath;
    public IEnumerable<Window> OpenWindows => _notes.Values.Cast<Window>().Concat(_aiWindows.Values).Concat(_main is null ? [] : new Window[] { _main })
        .Concat(_main?.DetachedAiWindow is { } ai ? new Window[] { ai } : []);

    internal void LoadChatDraft(AiChatScope scope, AiConversation conversation) => _draftStore.Load(scope.Id, conversation);
    internal void SaveChatDraft(AiChatScope scope, AiConversation conversation) => _draftStore.Save(scope.Id, conversation);
    internal void DeleteChatDraft(AiChatScope scope, AiConversation conversation) => _draftStore.Delete(scope.Id, conversation.Id);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var args = desktop.Args ?? [];
            _demo = args.Contains("--demo");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes");
            var dataPath = Path.Combine(folder, _demo ? "project-sheet-demo.json" : "project-sheet-v1.json");
            var dataIndex = Array.IndexOf(args, "--data");
            if (dataIndex >= 0 && dataIndex + 1 < args.Length) dataPath = Path.GetFullPath(args[dataIndex + 1]);
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://H2Notes.Avalonia/Assets/app.ico")));
            try
            {
                _local = LocalConfiguration.Read();
                if (_demo || dataIndex >= 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
                    _instanceLock = new FileStream(dataPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    _storage = new SheetStorage(dataPath);
                }
                else
                {
                    var projectStore = new ProjectWorkspaceStore(_local.DataFolder ?? LocalConfiguration.DefaultDataFolder, writerId: _local.DeviceId);
                    _instanceLock = projectStore.AcquireLock(); _storage = projectStore;
                }
            }
            catch (IOException ex) { ShowStartupError(desktop, "Không mở được thư mục hoặc bản H2 Notes khác trên máy này đang dùng cùng kho.\n" + ex.Message); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { ShowStartupError(desktop, "Không mở được kho dữ liệu. Không thay đổi file gốc.\n" + ex.Message); return; }
            try
            {
                State = _demo ? File.Exists(dataPath) ? SheetStorage.Read(dataPath) : SheetStorage.Demo()
                    : _storage.LoadOrImport(UsesProjectFiles && File.Exists(dataPath) ? dataPath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nodepad", "state.json"));
                if (UsesProjectFiles && _local.DesktopSession is not null)
                    State.DesktopSession = ProjectWorkspaceStore.Clone(_local.DesktopSession);
                _storageReady = true;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
            { ShowStartupError(desktop, "Không mở được dữ liệu. Không ghi đè file gốc.\n" + ex.Message); return; }
            _restoring = true;
            var plan = DesktopRestorePlan.Create(State, args.Contains("--startup"));
            // OnExplicitShutdown owns the lifetime; assigning MainWindow would auto-show a hidden board.
            _main = new MainWindow(this, plan.Board); TrackWindow(_main);
            _saveTimer.Tick += (_, _) => SaveNow();
            _syncTimer.Tick += (_, _) => RefreshSharedWorkspace();
            BuildTray();
            RestoreWindows(plan);
            _restoring = false;
            if (UsesProjectFiles) _syncTimer.Start();
            if (args.Contains("--show")) ShowMain();
            if (args.Contains("--show-ai")) ShowProjectAiWindow();
            ScheduleSave();
            var evidenceIndex = Array.IndexOf(args, "--ui-evidence");
            if (_demo && dataIndex >= 0 && evidenceIndex >= 0 && evidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    try { ShowMain(); await _main.CaptureEvidence(Path.GetFullPath(args[evidenceIndex + 1])); }
                    catch (Exception ex) { await Dialogs.Message(_main, "UI evidence failed", ex.Message); }
                });
            var chatEvidenceIndex = Array.IndexOf(args, "--chat-evidence");
            if (_demo && dataIndex >= 0 && chatEvidenceIndex >= 0 && chatEvidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    try { ShowMain(); await _main.CaptureChatEvidence(Path.GetFullPath(args[chatEvidenceIndex + 1])); }
                    catch (Exception ex) { await Dialogs.Message(_main, "Chat evidence failed", ex.Message); }
                });
            desktop.ShutdownRequested += (_, e) => { CancelAi(); SaveNow(); if (LastSaveError is not null) e.Cancel = true; };
            var aiErrorEvidenceIndex = Array.IndexOf(args, "--ai-error-evidence");
            if (_demo && dataIndex >= 0 && aiErrorEvidenceIndex >= 0 && aiErrorEvidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    try { ShowMain(); await _main.CaptureAiErrorEvidence(Path.GetFullPath(args[aiErrorEvidenceIndex + 1])); }
                    catch (Exception ex) { await Dialogs.Message(_main, "AI error evidence failed", ex.Message); }
                });
            desktop.Exit += (_, _) => { IsExiting = true; _saveTimer.Stop(); _syncTimer.Stop(); _tray?.Dispose(); _instanceLock?.Dispose(); };
            var documentsEvidenceIndex = Array.IndexOf(args, "--documents-evidence");
            if (_demo && dataIndex >= 0 && documentsEvidenceIndex >= 0 && documentsEvidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    try { ShowMain(); await _main.CaptureDocumentEvidence(Path.GetFullPath(args[documentsEvidenceIndex + 1])); ExitApp(); }
                    catch (Exception ex) { await Dialogs.Message(_main, "Document evidence failed", ex.Message); }
                });
            var inputEvidenceIndex = Array.IndexOf(args, "--chat-input-evidence");
            if (_demo && dataIndex >= 0 && inputEvidenceIndex >= 0 && inputEvidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    try { ShowMain(); await _main.CaptureChatInputEvidence(Path.GetFullPath(args[inputEvidenceIndex + 1])); ExitApp(); }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(Path.GetFullPath(args[inputEvidenceIndex + 1]), "capture-error.txt"), ex.ToString());
                        ExitApp();
                    }
                });
            var layoutEvidenceIndex = Array.IndexOf(args, "--layout-evidence");
            if (_demo && dataIndex >= 0 && layoutEvidenceIndex >= 0 && layoutEvidenceIndex + 1 < args.Length)
                Dispatcher.UIThread.Post(async () =>
                {
                    var directory = Path.GetFullPath(args[layoutEvidenceIndex + 1]);
                    try { ShowMain(); await _main.CaptureLayoutEvidence(directory); ExitApp(); }
                    catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "capture-error.txt"), ex.ToString()); ExitApp(); }
                });
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, string text)
    {
        var window = new Window { Title = "H2 Notes", ShowInTaskbar = false, CanMinimize = false, CanMaximize = false, Width = 490, Height = 200, Content = new TextBlock { Text = text, Margin = new Thickness(24), TextWrapping = global::Avalonia.Media.TextWrapping.Wrap } };
        window.Closed += (_, _) => desktop.Shutdown(); desktop.MainWindow = window; window.Show();
    }

    private void BuildTray()
    {
        var menu = new NativeMenu();
        void Add(string text, Action action) { var item = new NativeMenuItem(text); item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("H2 Notes · Mở bảng dự án", ShowMain);
        Add("＋ Ghi chú mới", NewNote);
        Add("AI dự án · cửa sổ riêng", ShowProjectAiWindow);
        var legacyChats = State.Notes.Where(n => n.IsChat && !n.IsArchived).ToList();
        if (legacyChats.Count > 0)
        {
            var history = new NativeMenuItem("Chat ngoài dự án đã lưu ở bản trước") { Menu = new NativeMenu() };
            foreach (var note in legacyChats) { var item = new NativeMenuItem(note.Title); item.Click += (_, _) => OpenChat(note); history.Menu.Items.Add(item); }
            menu.Items.Add(history);
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Đưa cửa sổ đang mở lên trước", RaiseOpenWindows);
        Add("Hiện tất cả ghi chú và AI", () => { ShowMain(); foreach (var n in State.Notes.Where(n => !n.IsBoard && !n.IsArchived)) OpenNote(n); });
        Add("Cài đặt…", () => { ShowMain(); ShowSettings(_main!); });
        menu.Items.Add(new NativeMenuItemSeparator()); Add("Thoát", ExitApp);
        _tray = new TrayIcon { Icon = Icon, ToolTipText = "H2 Notes · Bảng dự án", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var doubleClickMilliseconds = OperatingSystem.IsWindows() ? GetDoubleClickTime() : 500;
            if ((now - _lastTrayClick).TotalMilliseconds <= doubleClickMilliseconds) { _lastTrayClick = default; ShowMain(); }
            else { _lastTrayClick = now; RaiseOpenWindows(); }
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    public void ScheduleSave()
    {
        if (_saving || !_storageReady || _restoring || IsExiting) return;
        if (_main?.SelectedProjectId is { } id) MarkProjectDirty(id);
        _main?.SetSaveStatus("Đang soạn…"); _saveTimer.Stop(); _saveTimer.Start();
        foreach (var window in _aiWindows.Values) window.SetSaveStatus("Đang soạn…");
    }

    public void SaveNow()
    {
        if (_saving || !_storageReady || _restoring || IsExiting) return;
        _saving = true; _saveTimer.Stop();
        try
        {
            _main?.Flush(); foreach (var window in _notes.Values) window.Flush();
            foreach (var window in _aiWindows.Values) window.Flush();
            CaptureDesktopSession();
            if (UsesProjectFiles)
            {
                _local.DesktopSession = State.DesktopSession is null ? null : ProjectWorkspaceStore.Clone(State.DesktopSession);
                _local.Save();
            }
            if (_storage is ProjectWorkspaceStore projectStore) projectStore.SaveIncremental(State, _dirtyProjects);
            else _storage.Save(State);
            _dirtyProjects.Clear(); LastSaveError = null;
            var merged = _storage is ProjectWorkspaceStore p && p.LastMergeConflicts.Count > 0;
            _main?.SetSaveStatus(merged ? "Đã lưu · đã gộp thay đổi từ máy khác" : "Đã lưu");
            _main?.RefreshAfterSave();
            foreach (var window in _aiWindows.Values) { window.SetSaveStatus(merged ? "Đã lưu · đã gộp thay đổi" : "Đã lưu"); window.RefreshFromModel(); }
        }
        catch (IOException ex) when (ex.Message.StartsWith("Kho dữ liệu đang được thiết bị khác ghi", StringComparison.Ordinal))
        {
            LastSaveError = null;
            _main?.SetSaveStatus("NAS đang bận · sẽ tự lưu lại");
            foreach (var window in _aiWindows.Values) window.SetSaveStatus("NAS đang bận · sẽ tự lưu lại");
            _saveTimer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            _main?.SetSaveStatus("Lỗi lưu · xem cài đặt");
            LastSaveError = ex.Message;
            foreach (var window in _aiWindows.Values) window.SetSaveStatus("Chưa lưu được · kiểm tra thư mục dữ liệu");
        }
        finally { _saving = false; }
    }

    private void RefreshSharedWorkspace()
    {
        if (_saving || !_storageReady || _restoring || IsExiting || _storage is not ProjectWorkspaceStore projectStore) return;
        try
        {
            if (!projectStore.RefreshFromDisk(State, _dirtyProjects)) return;
            LastSaveError = null;
            _main?.RefreshAfterExternalSync();
            foreach (var window in _aiWindows.Values) window.RefreshFromModel();
            var text = projectStore.LastMergeConflicts.Count > 0 ? "Đã đồng bộ · giữ bản đang sửa khi trùng trường" : "Đã đồng bộ thay đổi từ máy khác";
            _main?.SetSaveStatus(text);
            foreach (var window in _aiWindows.Values) window.SetSaveStatus(text);
        }
        catch (IOException ex) when (ex.Message.StartsWith("Kho dữ liệu đang được thiết bị khác ghi", StringComparison.Ordinal))
        {
            _main?.SetSaveStatus("NAS đang có máy khác ghi · vẫn tiếp tục soạn");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            _main?.SetSaveStatus("Chưa đồng bộ được NAS · vẫn giữ bản đang soạn");
        }
    }

    public string? LastSaveError { get; private set; }

    public void SwitchWorkspace(ProjectWorkspaceStore destination, FileStream destinationLock, SheetState existing,
        WorkspaceTransfer transfer, WorkspaceTransferMode mode, IReadOnlyDictionary<Guid, ConflictResolution>? choices)
    {
        SaveNow();
        if (LastSaveError is not null) throw new IOException(LastSaveError);
        if (!transfer.MatchesSource(State)) throw new IOException("Dữ liệu vừa thay đổi trong lúc xem trước. Hãy chọn lại thư mục để tạo bản xem trước mới.");
        var next = transfer.Prepare(mode, choices);
        if (mode == WorkspaceTransferMode.UseExisting && File.Exists(destination.FilePath)) next = destination.Read();
        var oldRoot = _local.DataFolder;
        if (_storage is ProjectWorkspaceStore old) old.BackupSnapshot(State);
        if (mode != WorkspaceTransferMode.UseExisting) destination.BackupSnapshot(existing);
        // Machine preferences are not imported from the selected data store.
        next.SheetPreferences = State.SheetPreferences;
        if (mode != WorkspaceTransferMode.UseExisting) destination.Save(next);
        try { _local.DataFolder = destination.Root; _local.Save(); }
        catch { _local.DataFolder = oldRoot; if (mode != WorkspaceTransferMode.UseExisting) destination.Save(existing); throw; }
        _restoring = IsChangingStore = true; _saveTimer.Stop(); _syncTimer.Stop();
        try
        {
            foreach (var window in OpenWindows.ToArray()) window.Close();
            _notes.Clear(); _aiWindows.Clear(); _windowOrder.Clear();
            _instanceLock?.Dispose(); _instanceLock = destinationLock; _storage = destination; State = next;
            if (_local.DesktopSession is not null) State.DesktopSession = ProjectWorkspaceStore.Clone(_local.DesktopSession);
            var plan = DesktopRestorePlan.Create(State, false);
            _main = new MainWindow(this, plan.Board); TrackWindow(_main);
            ShowMain();
            RestoreWindows(plan); BuildTrayAfterStoreChange();
        }
        finally { _restoring = IsChangingStore = false; }
        LastSaveError = null; _syncTimer.Start();
        if (mode != WorkspaceTransferMode.UseExisting) ScheduleSave();
    }

    public LegacyImportRecord ImportLegacy(LegacyImportPreview preview)
    {
        if (!_storageReady || _saving) throw new InvalidOperationException("App chưa sẵn sàng nhập dữ liệu.");
        _saving = true; _saveTimer.Stop();
        try
        {
            _main?.Flush(); foreach (var window in _notes.Values) window.Flush();
            foreach (var window in _aiWindows.Values) window.Flush();
            CaptureDesktopSession();
            var imported = _storage.ImportCopies(State, preview);
            LastSaveError = null; _main?.SetSaveStatus("Đã nhập và lưu");
            return imported;
        }
        catch
        {
            _main?.SetSaveStatus("Chưa nhập được dữ liệu");
            _saveTimer.Start();
            throw;
        }
        finally { _saving = false; }
    }

    public void ShowImported(LegacyImportRecord imported)
    {
        var board = State.Notes.FirstOrDefault(n => imported.NoteIds.Contains(n.Id) && n.IsBoard && !n.IsArchived);
        if (board is not null) { _main?.SetBoard(board); ShowMain(); }
        else
        {
            var note = State.Notes.FirstOrDefault(n => imported.NoteIds.Contains(n.Id) && !n.IsArchived);
            if (note is not null) OpenNote(note);
        }
    }

    public void ShowMain()
    {
        if (_main is null) return;
        _main.MarkOpen(); _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate(); ScheduleSave();
    }

    internal void TrackWindow(Window window)
    {
        _windowOrder.Add(window);
        window.Activated += (_, _) =>
        {
            _windowOrder.Remove(window); _windowOrder.Add(window); ScheduleSave();
        };
    }

    private void CaptureDesktopSession()
    {
        if (State.DesktopSession?.ProjectAiWindow is { } placement) placement.IsVisible = _main?.DetachedAiWindow?.IsVisible == true;
        State.DesktopSession = new DesktopSessionState
        {
            SelectedBoardId = _main?.BoardId,
            ProjectAiWindow = State.DesktopSession?.ProjectAiWindow,
            OpenWindowIds = _windowOrder.Where(w => w.IsVisible)
                .Select(w => w switch { MainWindow main => main.BoardId, ProjectAiWindow projectAi => projectAi.WindowId, AiChatWindow ai => ai.NotebookId, NoteWindow note => note.NoteId, _ => throw new InvalidOperationException("Unknown session window.") }).ToList()
        };
    }

    public void RaiseOpenWindows()
    {
        var windows = _windowOrder.Where(w => w.IsVisible).ToList();
        foreach (var window in windows)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            var pinned = window.Topmost;
            window.Topmost = true; window.Topmost = pinned;
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { } handle)
            {
                SetWindowPos(handle.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                if (!pinned) SetWindowPos(handle.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
        }
        windows.LastOrDefault()?.Activate();
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    public void NewNote()
    {
        var note = new NoteRecord { Title = "Ghi chú mới", NoteKind = "general", Width = 380, Height = 430 };
        State.Notes.Add(note); OpenNote(note); ScheduleSave();
    }

    public void OpenNote(NoteRecord note)
    {
        if (note.IsChat) { OpenChat(note); return; }
        if (!_notes.TryGetValue(note.Id, out var window)) { window = new NoteWindow(this, note); _notes.Add(note.Id, window); TrackWindow(window); }
        note.IsVisibleOnDesktop = true; window.Show(); window.WindowState = WindowState.Normal; window.Activate(); ScheduleSave();
    }

    public void ShowProjectAiWindow() => _main?.ShowProjectAiWindow();

    private void RestoreWindows(DesktopRestorePlan plan)
    {
        var notes = plan.OpenWindows.ToDictionary(n => n.Id);
        var ids = notes.Keys.Concat(plan.ProjectAiWindowId is { } ai ? new[] { ai } : []).ToList();
        var order = State.DesktopSession?.OpenWindowIds ?? [];
        foreach (var id in ids.OrderBy(id => order.IndexOf(id)))
        {
            if (id == plan.ProjectAiWindowId) ShowProjectAiWindow();
            else if (notes[id].IsBoard) ShowMain();
            else OpenNote(notes[id]);
        }
    }

    private void BuildTrayAfterStoreChange() { _tray?.Dispose(); BuildTray(); }

    private void OpenChat(NoteRecord notebook)
    {
        if (!_aiWindows.TryGetValue(notebook.Id, out var window)) { window = new AiChatWindow(this, notebook); _aiWindows.Add(notebook.Id, window); TrackWindow(window); }
        notebook.IsVisibleOnDesktop = true; window.Show(); window.WindowState = WindowState.Normal; window.Activate(); ScheduleSave();
    }

    public void ExitApp()
    {
        CancelAi();
        SaveNow();
        if (LastSaveError is not null) return;
        IsExiting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public async void ShowSettings(Window owner) => await new SettingsWindow(this).ShowDialog(owner);
}
