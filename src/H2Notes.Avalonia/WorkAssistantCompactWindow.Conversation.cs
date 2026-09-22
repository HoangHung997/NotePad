using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed partial class WorkAssistantCompactWindow
{
    private readonly AgentChatSurface _chatSurface = new();
    private StackPanel _history => _chatSurface.Timeline;
    private ScrollViewer _historyScroll => _chatSurface.Scroll;
    private readonly Button _workspaceButton = new() { Name = "WorkAssistantWorkspace", Content = "Máy tính này · Chưa gắn dự án",
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(14, 0, 14, 4), Padding = new Thickness(8, 4), FontSize = 11 };
    private readonly ComboBox _model = new() { Name = "WorkAssistantModel", MinWidth = 80, MaxWidth = 195,
        FontSize = 11, MinHeight = 30, MaxHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _effort = new() { Name = "WorkAssistantReasoning", Width = 75, FontSize = 10,
        MinHeight = 30, MaxHeight = 34 };
    private bool _taskBusy;
    private readonly ComboBox _busySendMode = new() { Name = "WorkAssistantBusySendMode", FontSize = 11,
        ItemsSource = new[] { "Bổ sung tác vụ này", "Xếp lượt tiếp theo" }, SelectedIndex = 0, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 0, 4) };
    public bool QueueNextTurn => _busySendMode.SelectedIndex == 1;
    private IReadOnlyList<H2AgentTaskSummary> _conversation = [];
    public string? SelectedWorkspaceRoot { get; private set; }
    public Guid? SelectedModelProfileId => (_model.SelectedItem as AiProfile)?.Id;
    public string? SelectedReasoningEffort => _effort.SelectedItem as string;
    public event Action? NewConversationRequested;

    private Control BuildConversationOptions()
    {
        _historyScroll.Name = "WorkAssistantHistoryScroll";
        _history.Name = "WorkAssistantHistory";
        _busySendMode.SelectionChanged += (_, _) => RefreshSendAction();
        _add = AppIcon.Button(IconKind.Plus, "Thêm ngữ cảnh · chọn thư mục làm việc");
        _add.Name = "WorkAssistantChooseFolder"; _add.Width = _add.Height = 30; _add.Padding = new Thickness(5);
        _add.Background = Brushes.Transparent; _add.BorderThickness = new Thickness(0);
        _add.Click += (_, _) => { if (!_taskBusy) OpenAttachmentMenu(_add); };
        _workspaceButton.Click += (_, _) => OpenWorkspaceMenu(_workspaceButton);
        _model.ItemTemplate = new FuncDataTemplate<AiProfile>((p, _) => new TextBlock {
            Text = p?.Model ?? "Chọn model", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        _model.SelectionChanged += (_, _) =>
        {
            var profile = _model.SelectedItem as AiProfile;
            var options = profile is null ? [] : AiModelCapabilities.GetReasoningOptions(profile);
            _effort.ItemsSource = options; _effort.IsVisible = options.Count > 0;
            _effort.SelectedItem = profile?.ReasoningEffort == "max" ? options.LastOrDefault() : profile?.ReasoningEffort;
            if (_effort.SelectedIndex < 0 && options.Count > 0) _effort.SelectedIndex = 0;
            RefreshModelButton();
        };
        _effort.SelectionChanged += (_, _) => RefreshModelButton();
        ToolTip.SetTip(_model, "Model cho lượt gửi tiếp theo");
        ToolTip.SetTip(_effort, "Mức suy luận");
        _modelButton.Click += (_, _) => OpenModelMenu();
        _dictation.Click += (_, _) => OpenVoiceTyping();
        _dictation.Name = "WorkAssistantDictation";
        _dictation.Width = _dictation.Height = _dictation.MinHeight = 30;
        _dictation.Padding = new Thickness(7); _dictation.Background = Brushes.Transparent;
        _dictation.BorderThickness = new Thickness(0);
        _modelButton.HorizontalAlignment = HorizontalAlignment.Right;
        var row = new Grid { Name = "WorkAssistantComposerOptions", ColumnDefinitions = new ColumnDefinitions("30,Auto,*,30,34"), ColumnSpacing = 4 };
        row.Children.Add(_add); Grid.SetColumn(_permission, 1); row.Children.Add(_permission);
        Grid.SetColumn(_modelButton, 2); row.Children.Add(_modelButton);
        Grid.SetColumn(_dictation, 3); row.Children.Add(_dictation);
        Grid.SetColumn(_send, 4); row.Children.Add(_send);
        row.SizeChanged += (_, _) =>
        {
            _permissionLabel.IsVisible = row.Bounds.Width >= 490;
            _effortLabel.IsVisible = row.Bounds.Width >= 550;
            _modelButton.MaxWidth = row.Bounds.Width >= 550 ? 270 : Math.Max(80, row.Bounds.Width - 144);
        };
        DetachedFromVisualTree += (_, _) => _composerMenu?.Close();
        return row;
    }

    public void ConfigureModels(IReadOnlyList<AiProfile> profiles, Guid? selectedId)
    {
        var selected = SelectedModelProfileId ?? selectedId;
        var configured = profiles.Where(p => !string.IsNullOrWhiteSpace(p.Model)).ToArray();
        _model.ItemsSource = configured;
        _model.SelectedItem = configured.FirstOrDefault(p => p.Id == selected) ?? configured.FirstOrDefault();
        RefreshModelButton();
    }

    private void OpenWorkspaceMenu(Control anchor)
    {
        var desktop = new MenuItem { Header = "Desktop" };
        desktop.Click += (_, _) => SetWorkspaceRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var folder = new MenuItem { Header = "Chọn thư mục…" };
        folder.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
                Title = "Chọn thư mục Agent được làm việc", AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) SetWorkspaceRoot(path);
        };
        var application = new MenuItem { Header = "Dùng ứng dụng / tài liệu đang mở" };
        application.Click += (_, _) => SetWorkspaceRoot(null);
        var menu = new ContextMenu { ItemsSource = new object[] { desktop, folder, application } };
        menu.Open(anchor);
    }

    public void SetWorkspaceRoot(string? root)
    {
        if (_taskBusy) { SetStatus("Đợi tác vụ hiện tại kết thúc rồi đổi thư mục."); return; }
        if (root is not null)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!Directory.Exists(root) || root == Path.GetPathRoot(root))
            { SetStatus("Chọn một thư mục hiện có, không chọn cả ổ đĩa.", true); return; }
        }
        SelectedWorkspaceRoot = root;
        RebuildContextChips();
        _workspaceButton.Content = new TextBlock { Text = root is null ? "Máy tính này · Chưa gắn dự án" : "Thư mục · " + root,
            TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        ToolTip.SetTip(_workspaceButton, root ?? "Chọn ứng dụng hiện tại hoặc một thư mục để Agent làm việc");
        // Selecting a root supplies scope, not unattended mutation permission.
        SelectedPermissionMode = root is null ? H2AgentPermissionMode.ObserveOnly : H2AgentPermissionMode.AskBeforeChanges;
        SetStatus(root is null ? "Dùng ngữ cảnh ứng dụng đã chọn." : "Agent sẽ hỏi trước khi thay đổi tệp trong thư mục này.");
    }

    public IReadOnlyList<H2AgentChatTurn> ConversationTurns => _conversation
        .Where(t => t.Status == H2AgentTaskStatus.Completed && !string.IsNullOrWhiteSpace(t.FinalText))
        .TakeLast(6).SelectMany(t => new[] { new H2AgentChatTurn(t.TaskId + ":user", "user", t.Goal),
            new H2AgentChatTurn(t.TaskId + ":assistant", "assistant", t.FinalText!) }).ToArray();

    public void ShowHistory(IReadOnlyList<H2AgentTaskSummary> tasks, Guid? currentTaskId)
    {
        UpdateAssistantNavigation();
        _conversation = tasks.OrderBy(t => t.CreatedUtc).ToArray();
        _history.Children.Remove(_completionPanel);
        _chatSurface.PresentTasks(ApprovalAdapter, _conversation, ConversationId.ToString());
        if (!_history.Children.Contains(_completionPanel)) _history.Children.Add(_completionPanel);
    }


    private void SetTaskBusy(bool busy)
    {
        _taskBusy = busy; _send.IsEnabled = true; RefreshSendAction();
        _busySendMode.IsVisible = busy;
        _workspaceButton.IsEnabled = _permission.IsEnabled = _model.IsEnabled = _effort.IsEnabled = !busy;
        _add.IsEnabled = _modelButton.IsEnabled = !busy;
        if (busy) _composerMenu?.Close();
    }
}
