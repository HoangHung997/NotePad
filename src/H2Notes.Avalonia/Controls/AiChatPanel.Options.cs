using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Services;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private readonly Button _permissionButton = new() { Name = "ChatPermission", Classes = { "quiet" } };
    private readonly TextBlock _permissionLabel = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly AppIcon _permissionShield = new(IconKind.Shield, 14) { Name = "ChatPermissionShield", Foreground = RichEditor.Brush("#A4573D") };
    private readonly AppIcon _permissionArrow = new(IconKind.ChevronDown, 12) { Name = "ChatPermissionChevron", Margin = new Thickness(4, 0, 0, 0) };
    private readonly ComboBox _reasoningEffort = new() { Name = "ChatReasoningEffort" };
    private readonly TextBlock _unknownReasoning = new()
    {
        Name = "ChatReasoningUnavailable", Text = "Tối đa", FontSize = 11,
        Foreground = RichEditor.Brush("#796C62"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0)
    };
    private readonly Button _dictationButton = AppIcon.Button(IconKind.Microphone, "Mở nhập giọng nói Windows · Win+H");
    private Grid? _composerOptions;
    private Grid? _composerModelActions;
    private Grid? _composerPermissionActions;
    private ContextMenu? _permissionMenu;
    private bool _refreshingComposerOptions;
    private bool _permissionPromptOpen;

    public AiPermissionMode SelectedPermissionMode => _conversation?.PermissionMode switch
    {
        AiPermissionMode.ReadOnly => AiPermissionMode.ReadOnly,
        AiPermissionMode.ProjectAccess when _scope?.Project is not null && _conversation is not null
            && _app.LocalSettings.Ai.ProjectAccessConversationIds?.Contains(_conversation.Id) == true => AiPermissionMode.ProjectAccess,
        _ => AiPermissionMode.ConfirmChanges
    };

    public AiPermissionMode CurrentPermission => SelectedPermissionMode;

    public AiProfile CreateRequestProfile(AiProfile profile)
    {
        var effort = _conversation?.ReasoningEffort;
        var supported = AiModelCapabilities.GetReasoningOptions(profile);
        var copy = AiModelCapabilities.WithReasoning(profile, effort is not null && supported.Contains(effort) ? effort : null);
        if (supported.Count == 0) copy.ReasoningEffort = "";
        return copy;
    }

    public string? SelectedReasoningEffort
    {
        get
        {
            if (_profiles.SelectedItem is not AiProfile profile) return null;
            var effort = _conversation?.ReasoningEffort;
            return effort is not null && AiModelCapabilities.GetReasoningOptions(profile).Contains(effort) ? effort : null;
        }
    }

    public IChatDictationService DictationService { get; set; } = new ChatDictationService();
    public Action<LocalConfiguration> PersistComposerPermissions { get; set; } = settings => settings.Save();
    public event Action? ComposerOptionsChanged;

    private bool ComposerOptionsBusy => _preparing || _request is not null || _permissionPromptOpen;
    private bool IsComposerCompact => Bounds.Height is > 0 and < 500;

    private Control BuildComposerOptions()
    {
        if (_composerOptions is not null) return _composerOptions;
        BuildModelPicker();

        var permissionContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        permissionContent.Children.Add(_permissionShield);
        _permissionLabel.Name = "ChatPermissionLabel";
        permissionContent.Children.Add(_permissionLabel); Grid.SetColumn(_permissionLabel, 1);
        permissionContent.Children.Add(_permissionArrow); Grid.SetColumn(_permissionArrow, 2);
        _permissionButton.Content = permissionContent;
        _permissionButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _permissionButton.HorizontalAlignment = HorizontalAlignment.Left;
        _permissionButton.MinHeight = _permissionButton.Height = 32;
        _permissionButton.MinWidth = 32;
        _permissionButton.Margin = new Thickness(0);
        _permissionButton.Padding = new Thickness(4, 3);
        _permissionButton.Foreground = RichEditor.Brush("#A4573D");
        _permissionButton.Click += (_, _) => OpenPermissionMenu();

        _dictationButton.Name = "ChatDictation";
        _dictationButton.Width = _dictationButton.Height = _dictationButton.MinHeight = 32;
        _dictationButton.Margin = new Thickness(0);
        _dictationButton.Padding = new Thickness(7);
        ToolTip.SetTip(_dictationButton, "Mở nhập giọng nói Windows (Win+H). " + ChatDictationService.AvailabilityHint);
        _dictationButton.Click += (_, _) => OpenWindowsVoiceTyping();

        foreach (var button in new[] { _send, _stop })
        {
            button.Classes.Remove("accent");
            button.Width = button.Height = button.MinHeight = 34;
            button.Margin = new Thickness(0);
            button.CornerRadius = new CornerRadius(17);
            button.Padding = new Thickness(9);
            button.Background = RichEditor.Brush("#242320");
            button.Foreground = Brushes.White;
            button.BorderThickness = new Thickness(0);
        }
        _stop.Name = "ChatStop";
        _send.Content = new AppIcon(IconKind.ArrowUp, 18) { Foreground = Brushes.White };
        _stop.Content = new AppIcon(IconKind.Stop, 17) { Foreground = Brushes.White };
        _markerMode.IsCheckedChanged += (_, _) => RefreshComposerSendIcon();
        _send.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty) RefreshComposerSendIcon();
        };

        _composerPermissionActions = new Grid { Name = "ChatPermissionActions", ColumnDefinitions = new ColumnDefinitions("32,Auto"), VerticalAlignment = VerticalAlignment.Center, Children = { _composerAdd, _permissionButton } };
        Grid.SetColumn(_permissionButton, 1);
        var sendSlot = new Grid { Name = "ChatSendSlot", Width = 34, Height = 34, Children = { _send, _stop } };
        _composerModelActions = new Grid
        {
            Name = "ChatModelActions", ColumnDefinitions = new ColumnDefinitions("*,32,34"),
            Children = { _modelPickerButton, _dictationButton, sendSlot }
        };
        Grid.SetColumn(_dictationButton, 1); Grid.SetColumn(sendSlot, 2);
        Grid.SetColumn(_composerModelActions, 1);
        _composerOptions = new Grid
        {
            Name = "ChatComposerOptions", RowDefinitions = new RowDefinitions("Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { _composerPermissionActions, _composerModelActions, _modelPickerPopup }
        };
        _composerOptions.SizeChanged += (_, _) => ArrangeComposerOptions();
        SizeChanged += (_, _) => RefreshComposerCompactLayout();
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) ToolTip.SetTip(_status, _status.Text);
        };
        DetachedFromVisualTree += (_, _) => { _permissionMenu?.Close(); CloseModelPicker(); };
        ArrangeComposerOptions(); RefreshComposerOptions();
        return _composerOptions;
    }

    private void RefreshComposerCompactLayout()
    {
        _composer.MinHeight = IsComposerCompact ? 44 : 76;
        _composer.MaxHeight = IsComposerCompact ? 80 : 180;
        _markerMode.IsVisible = false;
        _status.TextWrapping = IsComposerCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _status.TextTrimming = IsComposerCompact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        ToolTip.SetTip(_status, _status.Text);
    }

    private void RefreshComposerSendIcon()
    {
        if (_send.Content is AppIcon icon)
        {
            icon.Kind = _markerMode.IsChecked == true ? IconKind.Clipboard : IconKind.ArrowUp;
            icon.Foreground = Brushes.White;
        }
    }

    private void ArrangeComposerOptions()
    {
        if (_composerOptions is null || _composerPermissionActions is null || _composerModelActions is null) return;
        var narrow = _composerOptions.Bounds.Width < 460;
        _permissionLabel.IsVisible = _permissionArrow.IsVisible = !narrow;
        _permissionShield.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 5, 0);
        _permissionButton.Width = narrow ? 32 : double.NaN;
        _permissionButton.HorizontalContentAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        _composerModelActions.Margin = new Thickness(narrow ? 2 : 8, 0, 0, 0);
        ResizeModelPicker();
    }

    private void RefreshComposerOptions()
    {
        if (_composerOptions is null || _refreshingComposerOptions) return;
        _refreshingComposerOptions = true;
        try
        {
            var enabled = _scope is not null && !ComposerOptionsBusy;
            _permissionButton.IsEnabled = _profiles.IsEnabled = _modelPickerButton.IsEnabled = enabled;
            _dictationButton.IsEnabled = _scope is not null && !_composer.IsReadOnly && !_permissionPromptOpen && DictationService.IsSupported;
            if (!enabled) { _permissionMenu?.Close(); CloseModelPicker(); }
            if (_modelPickerPopup.IsOpen && !IsModelPickerScopeCurrent()) CloseModelPicker();
            _permissionLabel.Text = PermissionLabel(SelectedPermissionMode);
            ToolTip.SetTip(_permissionButton, PermissionDescription(SelectedPermissionMode));
            AutomationProperties.SetName(_permissionButton, "Quyền AI: " + _permissionLabel.Text);

            var profile = _profiles.SelectedItem as AiProfile;
            ToolTip.SetTip(_profiles, profile is null ? "Mở Thiết lập AI để lưu kết nối và model." : ProfileDescription(profile));
            var options = profile is null ? Array.Empty<string>() : AiModelCapabilities.GetReasoningOptions(profile);
            var choices = new[] { new ReasoningChoice(null, options.Count > 0 ? "Mặc định" : "Tối đa") }
                .Concat(options.Select(value => new ReasoningChoice(value, EffortLabel(value)))).ToArray();
            if (!(_reasoningEffort.ItemsSource?.Cast<ReasoningChoice>().SequenceEqual(choices) ?? false)) _reasoningEffort.ItemsSource = choices;
            var selected = SelectedReasoningEffort;
            _reasoningEffort.SelectedItem = _reasoningEffort.ItemsSource!.Cast<ReasoningChoice>().First(c => c.Value == selected);
            _reasoningEffort.IsEnabled = enabled && options.Count > 0;
            _reasoningEffort.IsVisible = false;
            _unknownReasoning.IsVisible = options.Count == 0;
            ToolTip.SetTip(_reasoningEffort, options.Count == 0
                ? "Tối đa: dùng khả năng mặc định của model. Chưa biết API chỉnh mức suy luận; không gửi tham số suy luận tự đặt."
                : "Chỉ hiện các mức API của model hỗ trợ. Mặc định dùng cấu hình hồ sơ; lựa chọn khác chỉ lưu cho cuộc trao đổi này.");
            ToolTip.SetTip(_unknownReasoning, "Tối đa: dùng khả năng mặc định của model. Chưa biết API chỉnh mức suy luận; không gửi tham số suy luận tự đặt.");
            RefreshModelPicker(profile, options, enabled);
        }
        finally { _refreshingComposerOptions = false; }
    }

    private void OpenPermissionMenu()
    {
        if (_scope is null || ComposerOptionsBusy) return;
        CloseModelPicker();
        _mentionPopup.IsOpen = false;
        _permissionMenu?.Close();
        _permissionMenu = new ContextMenu();
        foreach (var mode in new[] { AiPermissionMode.ReadOnly, AiPermissionMode.ConfirmChanges, AiPermissionMode.ProjectAccess })
        {
            var item = new MenuItem
            {
                Header = PermissionLabel(mode), ToggleType = MenuItemToggleType.Radio, IsChecked = SelectedPermissionMode == mode,
                IsEnabled = mode != AiPermissionMode.ProjectAccess || _scope.Project is not null
            };
            ToolTip.SetTip(item, PermissionDescription(mode));
            item.Click += async (_, _) => await SelectPermissionMode(mode);
            _permissionMenu.Items.Add(item);
        }
        _permissionMenu.Open(_permissionButton);
    }

    private async Task SelectPermissionMode(AiPermissionMode mode)
    {
        if (_scope is null || ComposerOptionsBusy || !Enum.IsDefined(mode)) return;
        var hasGrant = _conversation is not null && _app.LocalSettings.Ai.ProjectAccessConversationIds?.Contains(_conversation.Id) == true;
        if (mode == SelectedPermissionMode && mode == (_conversation?.PermissionMode ?? AiPermissionMode.ConfirmChanges)
            && (mode == AiPermissionMode.ProjectAccess || !hasGrant)) return;
        if (mode == AiPermissionMode.ProjectAccess && _scope.Project is null) return;
        var scope = _scope; var conversation = _conversation; var version = _composerScopeVersion;
        if (mode == AiPermissionMode.ProjectAccess)
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            _permissionPromptOpen = true; RefreshComposerOptions();
            bool accepted;
            try
            {
                accepted = await Dialogs.Confirm(owner, "Toàn quyền dự án", PermissionDescription(mode)
                    + "\n\nÁp dụng cho cuộc trao đổi hiện tại trong dự án: " + scope.Title + ".\nKhi bật, các thao tác được phép trong dự án sẽ tự áp dụng sau khi AI trả lời, không hỏi lại từng lần.", "Cho phép trong dự án");
            }
            finally { _permissionPromptOpen = false; RefreshComposerOptions(); }
            if (!accepted) return;
        }
        if (scope != _scope || conversation != _conversation || version != _composerScopeVersion || ComposerOptionsBusy) return;
        var selected = EnsureConversation();
        var previousGrants = _app.LocalSettings.Ai.ProjectAccessConversationIds ?? [];
        var nextGrants = previousGrants.Where(id => id != selected.Id).ToList();
        if (mode == AiPermissionMode.ProjectAccess) nextGrants.Add(selected.Id);
        if (!previousGrants.SequenceEqual(nextGrants))
        {
            _app.LocalSettings.Ai.ProjectAccessConversationIds = nextGrants;
            try { PersistComposerPermissions(_app.LocalSettings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                _app.LocalSettings.Ai.ProjectAccessConversationIds = previousGrants;
                _status.Text = "Không lưu được quyền trên máy; chưa đổi chế độ quyền. Kiểm tra quyền ghi cài đặt rồi thử lại.";
                RefreshComposerOptions(); return;
            }
        }
        selected.PermissionMode = mode;
        _mentionPopup.IsOpen = false; _composerMenu?.Close();
        Touch(scope); Render(); RefreshComposerOptions(); ComposerOptionsChanged?.Invoke();
    }

    private void OpenWindowsVoiceTyping()
    {
        if (_scope is null || _composer.IsReadOnly || _permissionPromptOpen) return;
        if (TopLevel.GetTopLevel(this) is not Window owner || !owner.IsActive || !_composer.Focus(NavigationMethod.Pointer))
        { _status.Text = "Bấm vào ô soạn trước khi mở nhập giọng nói Windows."; return; }
        _mentionPopup.IsOpen = false;
        var handle = owner.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "HWND")
        { _status.Text = "Nhập giọng nói này chỉ mở Windows Win+H; H2 Notes không có bộ ghi âm riêng."; return; }
        var result = DictationService.TryOpen(handle.Handle);
        _status.Text = result.Message;
    }

    private static string ProfileDescription(AiProfile profile) => profile.Name + " · " + profile.Model + "\n" + profile.ProcessingLocation;
    private static string PermissionLabel(AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.ReadOnly => "Chỉ đọc",
        AiPermissionMode.ProjectAccess => "Toàn quyền dự án",
        _ => "Xác nhận thay đổi"
    };
    private static string PermissionDescription(AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.ReadOnly => "Chỉ trả lời; không áp dụng thay đổi dữ liệu dự án và không lưu tệp từ AI.",
        AiPermissionMode.ProjectAccess => "Chỉ trong dự án H2 Notes đang chọn: AI được tự thêm, sửa hoặc xóa công việc; thêm, sửa hoặc xóa đúng đoạn ghi chú; và định dạng phần nội dung AI thêm/sửa. "
            + "Các thao tác hợp lệ tự áp dụng sau khi AI trả lời, không hỏi lại từng lần. Đây không phải quyền hệ điều hành của Codex: AI không chạy lệnh/shell, không truy cập toàn máy. Không tự xóa hoặc ghi đè tệp ngoài dự án. Lưu tệp ngoài dự án vẫn do bạn chọn.",
        _ => "AI có thể đề xuất thêm, sửa hoặc xóa dữ liệu trong dự án. Khi có thay đổi, H2 Notes hiện ngay nội dung cụ thể cần thay đổi; bạn bấm Đồng ý một lần để áp dụng. Không có quyền hệ điều hành/shell và không tự ghi tệp ngoài dự án."
    };
    private static string EffortLabel(string value) => value switch
    {
        "none" => "Tắt", "minimal" => "Tối thiểu", "low" => "Thấp", "medium" => "Trung bình", "high" => "Cao", "xhigh" => "Rất cao", "max" => "Tối đa", _ => value
    };
    private sealed record ReasoningChoice(string? Value, string Label);
}
