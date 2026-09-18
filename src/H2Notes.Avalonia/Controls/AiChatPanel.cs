using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel : UserControl
{
    private readonly App _app;
    private readonly Func<AiClient> _createClient;
    private readonly StackPanel _messages = new() { Name = "ChatMessages", Spacing = 10, Margin = new Thickness(12) };
    private readonly Dictionary<Guid, ChatMessageView> _bubbles = [];
    private readonly ScrollViewer _scroll;
    private readonly TextBox _composer = new() { Name = "ChatComposer", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Hỏi AI…", MinHeight = 48, MaxHeight = 140 };
    private readonly ComboBox _profiles = new() { Name = "ChatProfile", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _history = new() { Name = "ChatHistory", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _includeProject = new() { Name = "ChatIncludeProject", Content = "Toàn bộ dự án: ghi chú, việc, liên kết", FontSize = 11, IsChecked = true };
    private readonly CheckBox _includeHistory = new() { Content = "Kèm các cuộc trao đổi khác của dự án", FontSize = 11, IsChecked = true };
    private readonly CheckBox _markerMode = new() { Name = "ChatMarkerMode", Content = "Chỉ lưu mốc, không hỏi AI", IsVisible = false };
    private readonly TextBlock _status = new() { Name = "ChatConnectionStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = RichEditor.Brush("#796C62") };
    private readonly Button _send = AppIcon.Button(IconKind.Send, "Gửi · Ctrl+Enter", "accent");
    private readonly Button _stop = AppIcon.Button(IconKind.Stop, "Dừng trả lời");
    private readonly Button _back = AppIcon.Button(IconKind.ChevronLeft, "Quay lại dự án");
    private readonly TextBlock _titleText = new() { Text = "Hỏi AI", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _title = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll) };
    private readonly Grid _header;
    private readonly StackPanel _optionsPanel;
    private AiChatScope? _scope;
    private AiConversation? _conversation;
    private CancellationTokenSource? _request;
    private AiMessage? _activeAnswer;
    private Action? _flushStreaming;
    private string _streamingReasoning = "";
    private TaskCompletionSource? _streamFinished;
    private bool _loading;
    private bool _detached;
    private bool _followLatest = true;
    private bool _preparing;
    private int _visibleMessages = 40;
    private bool _dragging;
    private Point? _press;
    public event Action? CloseRequested;
    public event Action<string>? DockRequested;
    public event Action<string>? AppendNoteRequested;
    public event Action<string>? AddTaskRequested;
    public event Action<PointerEventArgs>? DragStarted;
    public event Action<PointerEventArgs>? DragMoved;
    public event Action<bool>? DragFinished;
    public Func<string>? ReadContext { get; set; }
    public Action? PrepareProjectContext { get; set; }

    public AiChatPanel(App app, Func<AiClient>? createClient = null)
    {
        _app = app;
        _createClient = createClient ?? (() => new AiClient());
        _back.IsVisible = _stop.IsVisible = false;
        _send.Name = "ChatSend"; _send.Width = 40; _send.Height = 40; _send.Padding = new Thickness(9);
        _stop.Width = _stop.Height = 40; _stop.Padding = new Thickness(9);
        _title.Children.Add(new AppIcon(IconKind.Grip, 16) { Margin = new Thickness(0, 0, 7, 0) });
        _title.Children.Add(_titleText); Grid.SetColumn(_titleText, 1);
        ToolTip.SetTip(_title, "Kéo để di chuyển hoặc ghim khung AI");
        _header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(10, 4), Height = 36 };
        var settings = AppIcon.Button(IconKind.Settings, "Thiết lập AI");
        var dock = AppIcon.Button(IconKind.Pin, "Vị trí khung AI"); var close = AppIcon.Button(IconKind.Close, "Ẩn AI");
        settings.Padding = dock.Padding = close.Padding = new Thickness(7);
        _header.Children.Add(_back); _header.Children.Add(_title); Grid.SetColumn(_title, 1);
        _header.Children.Add(settings); Grid.SetColumn(settings, 2); _header.Children.Add(dock); Grid.SetColumn(dock, 3); _header.Children.Add(close); Grid.SetColumn(close, 4);
        close.Click += (_, _) => CloseRequested?.Invoke(); _back.Click += (_, _) => CloseRequested?.Invoke();
        settings.Click += async (_, _) => await ShowSettings();
        dock.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (mode, label) in new[] { ("floating", "Nổi trong bảng dự án"), ("right", "Ghim bên phải"), ("bottom", "Ghim phía dưới") })
            { var item = new MenuItem { Header = label }; item.Click += (_, _) => DockRequested?.Invoke(mode); menu.Items.Add(item); }
            menu.Items.Add(new Separator());
            var independent = new MenuItem { Header = "Tách AI của dự án ra màn hình", Icon = new AppIcon(IconKind.Sparkle) };
            independent.Click += (_, _) => DockRequested?.Invoke("desktop"); menu.Items.Add(independent); menu.Open(dock);
        };
        _title.PointerPressed += (_, e) => { if (e.GetCurrentPoint(_title).Properties.IsLeftButtonPressed) { _press = e.GetPosition(this); e.Pointer.Capture(_title); } };
        _title.PointerMoved += (_, e) =>
        {
            if (_press is null) return;
            if (!_dragging && Math.Abs(e.GetPosition(this).X - _press.Value.X) + Math.Abs(e.GetPosition(this).Y - _press.Value.Y) > 8) { _dragging = true; DragStarted?.Invoke(e); }
            if (_dragging) DragMoved?.Invoke(e);
        };
        _title.PointerReleased += (_, e) => { var apply = _dragging; _press = null; _dragging = false; e.Pointer.Capture(null); if (apply) DragFinished?.Invoke(true); };
        _title.PointerCaptureLost += (_, _) => { if (_dragging) DragFinished?.Invoke(false); _press = null; _dragging = false; };
        var newChat = new Button { Name = "ChatNew", Content = AppIcon.Label(IconKind.Plus, "Trao đổi mới", 14), FontSize = 12 };
        newChat.Click += (_, _) => StartNewConversation();
        var deleteChat = new Button { Content = "Xóa cuộc trao đổi…", FontSize = 12 };
        deleteChat.Click += async (_, _) =>
        {
            var conversation = _conversation; var scope = _scope;
            if (conversation is null || scope is null || TopLevel.GetTopLevel(this) is not Window owner) return;
            if (!await Dialogs.Confirm(owner, "Xóa cuộc trao đổi", "Xóa lịch sử và bản nháp cuộc trao đổi này khỏi H2 Notes?\nKhông yêu cầu xóa bản lưu trên máy chủ AI.", "Xóa lịch sử này")) return;
            await StopAsync(); _app.DeleteChatDraft(scope, conversation); scope.Conversations.Remove(conversation); Touch(scope);
            if (_scope == scope) { _conversation = scope.Conversations.LastOrDefault(); scope.SelectedConversationId = _conversation?.Id; LoadDraft(); RefreshHistory(); Render(); }
        };
        _profiles.SelectionChanged += (_, _) => { if (!_loading && _profiles.SelectedItem is AiProfile p) { if (_conversation is not null) { Cancel(); _conversation.ProfileId = p.Id; Touch(_scope); } UpdateConnectionStatus(); } };
        _history.SelectionChanged += (_, _) =>
        {
            if (_loading || _history.SelectedItem is not ConversationItem item) return;
            Cancel(); _conversation = item.Value; if (_scope is not null) _scope.SelectedConversationId = _conversation.Id;
            _visibleMessages = 40; LoadDraft(); RefreshProfiles(); Render(); ScrollToLatest(); Touch(_scope);
        };
        var details = new Expander { Header = "Lịch sử và ngữ cảnh", FontSize = 12, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 4, Children = { _history, newChat, deleteChat, _includeProject, _includeHistory } } };
        var options = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 12, 8), Children = { _profiles, details } };
        _optionsPanel = options;
        _scroll = new ScrollViewer { Content = _messages, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta != default || e.ViewportDelta != default)
            { if (_followLatest) ScrollToLatest(); }
            else if (e.OffsetDelta != default) _followLatest = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y < 40;
        };
        _messages.SizeChanged += (_, _) => UpdateBubbleWidths();
        var composerRow = BuildComposer();
        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(12), Children = { _status,
            new ScrollViewer { MaxHeight = 74, Content = _draftFiles, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, _markerMode, composerRow } };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(_header); root.Children.Add(options); Grid.SetRow(options, 1);
        root.Children.Add(_scroll); Grid.SetRow(_scroll, 2); root.Children.Add(footer); Grid.SetRow(footer, 3); Content = root;
        _markerMode.IsCheckedChanged += (_, _) =>
        {
            _composer.PlaceholderText = _markerMode.IsChecked == true ? "Ghi mốc công việc tại thời điểm này…" : "Hỏi AI…";
            _send.Content = new AppIcon(_markerMode.IsChecked == true ? IconKind.Clipboard : IconKind.Send);
            var tip = _markerMode.IsChecked == true ? "Lưu mốc, không gửi AI · Ctrl+Enter" : "Gửi AI · Ctrl+Enter";
            ToolTip.SetTip(_send, tip); global::Avalonia.Automation.AutomationProperties.SetName(_send, tip);
            if (!_loading && _conversation is not null) { _conversation.MarkerOnlyMode = _markerMode.IsChecked == true; Touch(_scope); }
        };
        _composer.TextChanged += (_, _) =>
        {
            if (_loading || _scope is null) return;
            if (_conversation is null && string.IsNullOrEmpty(_composer.Text)) return;
            var conversation = EnsureConversation(); conversation.Draft = _composer.Text ?? ""; _app.SaveChatDraft(_scope, conversation);
        };
        _send.Click += async (_, _) => await SendOrSave(); _stop.Click += (_, _) => Cancel();
        InitializeComposerInput();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Height < 500; _optionsPanel.IsVisible = !compact;
            _markerMode.IsVisible = false;
            _composer.MinHeight = compact ? 44 : 76; _composer.MaxHeight = compact ? 80 : 180;
            _status.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            _status.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        };
        RefreshProfiles(); Render();
    }

    public async Task ShowSettings()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await StopAsync(); await new AiSettingsWindow(_app).ShowDialog(owner); RefreshConnections();
    }
    private void StartNewConversation()
    {
        Cancel(); _conversation = null; if (_scope is not null) _scope.SelectedConversationId = null;
        _loading = true; _composer.Text = ""; _loading = false; Render(); RefreshHistory();
    }
    public void RefreshConnections() { RefreshProfiles(); UpdateConnectionStatus(); }
    public void RefreshFromModel()
    {
        if (_scope is null) return;
        var id = _conversation?.Id;
        _conversation = id is null ? _scope.Conversations.LastOrDefault() : _scope.Conversations.FirstOrDefault(c => c.Id == id) ?? _scope.Conversations.LastOrDefault();
        RefreshHistory(); Render(); if (_followLatest) ScrollToLatest();
    }
    public void SetCompact(bool compact) { _back.IsVisible = compact; _titleText.Text = compact ? "Hỏi AI · " + (_scope?.Title ?? "") : "Hỏi AI"; }
    public void SetDetached(bool detached) { _detached = detached; _header.IsVisible = !detached && _scope?.IsStandalone != true; }
    public void Cancel()
    {
        _pdfPreparation?.Cancel();
        _layoutCancellation?.Cancel();
        _flushStreaming?.Invoke();
        _streamingReasoning = "";
        if (_request is not null && _activeAnswer is not null)
        {
            _activeAnswer.Status = "interrupted";
            if (_bubbles.TryGetValue(_activeAnswer.Id, out var bubble)) bubble.Refresh();
        }
        _request?.Cancel();
    }
    public async Task StopAsync() { Cancel(); if (_pdfPreparationFinished is { } preparation) await preparation.Task; if (_streamFinished is { } completion) await completion.Task; if (_layoutFinished is { } layout) await layout.Task; }
    public void Flush() => _flushStreaming?.Invoke();
    public void SetProject(ProjectRecord? project) => SetScope(project is null ? null : AiChatScope.ForProject(project));
    public void SetStandalone(NoteRecord notebook) => SetScope(AiChatScope.ForNotebook(notebook));
    private void SetScope(AiChatScope? scope)
    {
        if (_scope?.Id == scope?.Id) return;
        Cancel(); _visibleMessages = 40; _scope = scope;
        if (scope is not null)
        {
            var unfinished = scope.Conversations.SelectMany(c => c.Messages)
                .Where(m => m.Status == "streaming" && (string.IsNullOrWhiteSpace(m.DeviceId) || m.DeviceId == _app.DeviceId)).ToArray();
            foreach (var message in unfinished) message.Status = "interrupted";
            if (unfinished.Length > 0) Touch(scope);
        }
        _conversation = scope?.Conversations.FirstOrDefault(c => c.Id == scope.SelectedConversationId) ?? scope?.Conversations.LastOrDefault();
        if (scope is not null) scope.SelectedConversationId = _conversation?.Id;
        LoadDraft(); _includeProject.IsChecked = _includeHistory.IsChecked = true;
        _includeProject.IsVisible = _includeHistory.IsVisible = scope?.Project is not null;
        _header.IsVisible = !_detached && scope?.IsStandalone != true;
        RefreshHistory(); RefreshProfiles(); Render(); ScrollToLatest();
    }
    private void LoadDraft()
    {
        if (_scope is not null && _conversation is not null) _app.LoadChatDraft(_scope, _conversation);
        _loading = true; _composer.Text = _conversation?.Draft ?? ""; _markerMode.IsChecked = _conversation?.MarkerOnlyMode == true; _loading = false; RenderAttachments();
    }
    private void Touch(AiChatScope? scope) { if (scope?.Project is { } p) _app.MarkProjectDirty(p.Id); _app.ScheduleSave(); }
    private AiConversation EnsureConversation()
    {
        if (_conversation is not null) return _conversation;
        if (_scope is null) throw new InvalidOperationException("Chọn dự án hoặc mở AI độc lập trước.");
        _conversation = new AiConversation { ProfileId = (_profiles.SelectedItem as AiProfile)?.Id, MarkerOnlyMode = _markerMode.IsChecked == true };
        _scope.Conversations.Add(_conversation); _scope.SelectedConversationId = _conversation.Id; RefreshHistory(); return _conversation;
    }
    private void RefreshProfiles()
    {
        _loading = true; var profiles = _app.LocalSettings.Ai.Profiles;
        _profiles.ItemsSource = profiles.ToArray();
        _profiles.SelectedItem = profiles.FirstOrDefault(p => p.Id == (_conversation?.ProfileId ?? _app.LocalSettings.Ai.SelectedId)) ?? profiles.FirstOrDefault();
        _loading = false;
    }
    private void RefreshHistory()
    {
        _loading = true; var items = _scope?.Conversations.Select(c => new ConversationItem(c)).ToArray() ?? [];
        _history.ItemsSource = items; _history.SelectedItem = items.FirstOrDefault(c => c.Value == _conversation); _history.IsVisible = items.Length > 0; _loading = false;
    }
    private void ScrollToLatest() { _followLatest = true; Dispatcher.UIThread.Post(() => { if (_followLatest) _scroll.ScrollToEnd(); }); }
    private void UpdateBubbleWidths()
    {
        var width = Math.Max(100, _messages.Bounds.Width * .88);
        foreach (var bubble in _bubbles.Values) bubble.MaxWidth = width;
    }
    private void Render()
    {
        _messages.Children.Clear(); _bubbles.Clear();
        if (_conversation is null || _conversation.Messages.Count == 0)
            _messages.Children.Add(new TextBlock { Text = (_scope?.IsStandalone == true ? "AI độc lập · không kèm dữ liệu dự án.\n\n" : "AI của dự án đang chọn.\n\n")
                + "Chọn kết nối và đặt câu hỏi, hoặc bật ‘Chỉ lưu mốc’ để ghi lại công việc mà không gọi AI.\n\nCtrl+Enter để gửi; Enter để xuống dòng.", TextWrapping = TextWrapping.Wrap, Foreground = RichEditor.Brush("#796C62") });
        else
        {
            if (_conversation.Messages.Count > _visibleMessages)
            {
                var older = new Button { Content = $"Xem 40 tin trước · {_conversation.Messages.Count} tin được lưu", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
                older.Click += (_, _) =>
                {
                    var oldHeight = _scroll.Extent.Height; var offset = _scroll.Offset;
                    _followLatest = false; _visibleMessages += 40; Render();
                    Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(offset.X, offset.Y + Math.Max(0, _scroll.Extent.Height - oldHeight)));
                };
                _messages.Children.Add(older);
            }
            DateTime? previous = null; var first = true;
            foreach (var message in _conversation.Messages.TakeLast(_visibleMessages))
            {
                var time = AiHistory.LocalTime(message);
                if (first || time?.Date != previous?.Date || time.HasValue && previous.HasValue && time.Value - previous.Value >= TimeSpan.FromMinutes(15))
                    _messages.Children.Add(ChatMessageView.TimeDivider(message));
                AddMessage(message); previous = time; first = false;
            }
        }
        RenderAttachments(); UpdateBubbleWidths();
        UpdateConnectionStatus();
    }
    private void UpdateConnectionStatus() => _status.Text = _profiles.SelectedItem is AiProfile p ? p.ProcessingLocation + " · " + p.Model : "Chưa thiết lập AI · vẫn lưu mốc được.";
    private void AddMessage(AiMessage message)
    {
        var bubble = new ChatMessageView(message);
        if (message == _activeAnswer) bubble.SetThinking(_streamingReasoning);
        AddFileCards(bubble, message);
        AddProjectActions(bubble, message);
        if (_scope?.Project is not null && message.Role == "assistant" && message.Content.Length > 0 && message.Status != "streaming")
        {
            var append = new Button { Content = "Thêm vào ghi chú", FontSize = 11 }; var task = new Button { Content = "Tạo công việc…", FontSize = 11 };
            var targetScope = _scope; var targetConversation = _conversation;
            append.IsEnabled = task.IsEnabled = CurrentPermission != AiPermissionMode.ReadOnly;
            append.Click += async (_, _) =>
            {
                if (CurrentPermission == AiPermissionMode.ReadOnly || _scope != targetScope || _conversation != targetConversation
                    || TopLevel.GetTopLevel(this) is not Window owner) return;
                var text = bubble.Body.Text ?? message.Content;
                if (CurrentPermission == AiPermissionMode.ConfirmChanges
                    && !await Dialogs.Confirm(owner, "Thêm vào ghi chú?", text, "Thêm vào ghi chú")) return;
                if (_scope == targetScope && _conversation == targetConversation && CurrentPermission != AiPermissionMode.ReadOnly)
                { AppendNoteRequested?.Invoke(text); Touch(targetScope); }
            };
            task.Click += async (_, _) =>
            {
                if (CurrentPermission == AiPermissionMode.ReadOnly || TopLevel.GetTopLevel(this) is not Window owner) return;
                var result = await Dialogs.Prompt(owner, "Tạo công việc từ AI", "Nội dung công việc", bubble.Body.Text ?? message.Content);
                if (!string.IsNullOrWhiteSpace(result) && targetScope == _scope && targetConversation == _conversation && CurrentPermission != AiPermissionMode.ReadOnly)
                { AddTaskRequested?.Invoke(result); Touch(targetScope); }
            };
            bubble.Actions.Children.Add(new WrapPanel { Children = { append, task } });
        }
        _bubbles.Add(message.Id, bubble); _messages.Children.Add(bubble);
    }
    private async Task SendOrSave()
    {
        if (_request is not null || _preparing || _scope is null || string.IsNullOrWhiteSpace(_composer.Text) && _conversation?.DraftAttachments.Count is not > 0) return;
        if (_markerMode.IsChecked != true) { await Send(); return; }
        var conversation = EnsureConversation(); var text = _composer.Text?.Trim() ?? "";
        SetTitle(conversation, text);
        conversation.Messages.Add(new AiMessage { Role = "user", Content = text, CreatedAt = DateTime.UtcNow, IsTimelineMarker = true,
            DeviceId = _app.DeviceId, Attachments = conversation.DraftAttachments.ToList() });
        ClearDraft(conversation); Touch(_scope); RefreshHistory(); Render(); ScrollToLatest(); _composer.Focus();
    }
    private static void SetTitle(AiConversation conversation, string text)
    { if (conversation.Messages.Count == 0) conversation.Title = text.Length > 42 ? text[..42] + "…" : text; }
    private void ClearDraft(AiConversation conversation)
    {
        if (_scope is not null) _app.DeleteChatDraft(_scope, conversation);
        conversation.Draft = ""; conversation.DraftAttachments.Clear(); _loading = true; _composer.Text = ""; _loading = false; RenderAttachments();
    }
    private async Task Send()
    {
        if (_scope is null) return;
        if (_profiles.SelectedItem is not AiProfile profile || string.IsNullOrWhiteSpace(profile.Model)) { _status.Text = "Mở Thiết lập AI để chọn model và lưu kết nối trước."; return; }
        var owner = TopLevel.GetTopLevel(this) as Window; if (owner is null) return;
        var scope = _scope; var prompt = _composer.Text?.Trim() ?? ""; string key;
        if (prompt.Length == 0) prompt = "Phân tích các tệp đính kèm và tóm tắt nội dung chính.";
        try { key = SecretVault.Read(profile.Id); AiClient.Endpoint(profile, "models"); }
        catch (Exception) { _status.Text = "Không đọc được kết nối hoặc khóa. Mở Thiết lập AI để kiểm tra."; return; }
        var conversation = EnsureConversation(); var sentPermission = CurrentPermission;
        profile = CreateRequestProfile(profile);
        string context; IReadOnlyList<AiTurn> turns;
        var runId = Guid.NewGuid();
        var user = new AiMessage { Role = "user", Content = prompt, Provider = profile.Name, Model = profile.Model, CreatedAt = DateTime.UtcNow,
            AiRunId = runId, DeviceId = _app.DeviceId, Attachments = conversation.DraftAttachments.ToList() };
        try { context = BuildProjectContext(); turns = AiProjectContext.Prepare(conversation, user, context); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { _status.Text = ex.Message; return; }
        if (AiPdfProcessor.NeedsPreparation(turns, _app.LocalSettings.Ai.Pdf))
        {
            var prepared = await PreparePdfRequest(scope, conversation, user, profile, context);
            if (prepared is null) return;
            turns = prepared;
        }
        conversation.ProfileId = profile.Id; SetTitle(conversation, prompt);
        user.Context = context;
        conversation.Messages.Add(user);
        var answer = new AiMessage { Role = "assistant", ParentId = user.Id, Model = profile.Model, Provider = profile.Name, Status = "streaming",
            CreatedAt = DateTime.UtcNow, AiRunId = runId, DeviceId = _app.DeviceId };
        conversation.Messages.Add(answer); ClearDraft(conversation);
        RefreshHistory(); Render(); ScrollToLatest();
        var cts = new CancellationTokenSource(); _request = cts; _activeAnswer = answer; _streamFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _send.IsVisible = false; _stop.IsVisible = true; RefreshComposerOptions(); Touch(scope);
        var buffer = new StringBuilder(); var reasoning = new StringBuilder(); var lastSave = DateTime.UtcNow;
        var reasoningClipped = false;
        _streamingReasoning = "";
        _flushStreaming = () => { answer.Content = buffer.ToString(); if (scope.Project is { } p) _app.MarkProjectDirty(p.Id); };
        var paint = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        paint.Tick += (_, _) =>
        {
            var follow = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y < 40;
            answer.Content = buffer.ToString();
            _streamingReasoning = answer.Content.Length == 0 ? (reasoningClipped ? "… Tiến trình gần nhất (rút gọn hiển thị)\n" : "") + reasoning : "";
            if (_conversation == conversation && _bubbles.TryGetValue(answer.Id, out var bubble))
            { bubble.Refresh(); bubble.SetThinking(_streamingReasoning); if (follow) ScrollToLatest(); }
            if (DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(2)) { lastSave = DateTime.UtcNow; Touch(scope); }
        };
        paint.Start(); string? error = null;
        try
        {
            using var client = _createClient();
            await foreach (var part in client.StreamEvents(profile, key, turns, cts.Token))
            {
                if (part.Kind is AiStreamEventKind.Reasoning or AiStreamEventKind.ReasoningSummary)
                {
                    if (buffer.Length == 0)
                    {
                        var firstProgress = reasoning.Length == 0;
                        reasoning.Append(part.Text);
                        if (reasoning.Length > 24_000) { reasoning.Remove(0, reasoning.Length - 24_000); reasoningClipped = true; }
                        if (firstProgress)
                        {
                            _streamingReasoning = (reasoningClipped ? "… Tiến trình gần nhất (rút gọn hiển thị)\n" : "") + reasoning;
                            if (_conversation == conversation && _bubbles.TryGetValue(answer.Id, out var thinkingBubble)) thinkingBubble.SetThinking(_streamingReasoning);
                        }
                    }
                    continue;
                }
                if (buffer.Length + part.Text.Length > 600_000) throw new InvalidOperationException("Phản hồi vượt giới hạn an toàn; đã giữ phần nhận được.");
                var firstAnswer = buffer.Length == 0;
                buffer.Append(part.Text); reasoning.Clear();
                if (firstAnswer)
                {
                    answer.Content = buffer.ToString(); _streamingReasoning = "";
                    if (_conversation == conversation && _bubbles.TryGetValue(answer.Id, out var replyBubble)) replyBubble.Refresh();
                }
            }
            answer.Status = cts.IsCancellationRequested ? "interrupted" : "complete";
        }
        catch (OperationCanceledException)
        {
            answer.Status = "interrupted";
            error = cts.IsCancellationRequested ? "Đã dừng theo yêu cầu. Phần trả lời đã nhận được giữ lại."
                : "Kết nối AI đã im lặng quá lâu. Bật Chờ AI hoàn tất trong Thiết lập AI nếu máy xử lý chậm. Phần đã nhận được giữ lại; không tự gửi lại.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        { answer.Status = "error"; error = AiFailure.Describe(ex); }
        finally
        {
            paint.Stop(); answer.Content = buffer.ToString(); reasoning.Clear(); _streamingReasoning = "";
            cts.Dispose(); _request = null; _activeAnswer = null; _flushStreaming = null; _send.IsVisible = true; _stop.IsVisible = false;
            RefreshComposerOptions();
            answer.ErrorText = error ?? "";
            ApplyAutomaticProjectActions(scope, conversation, answer, sentPermission);
            if (_conversation == conversation) { Render(); if (error is not null) _status.Text = "Gửi chưa hoàn tất · xem chi tiết trong tin nhắn phía trên."; }
            Touch(scope); _streamFinished?.TrySetResult(); _streamFinished = null;
        }
    }
    private sealed record ConversationItem(AiConversation Value) { public override string ToString() => Value.Title; }
}
