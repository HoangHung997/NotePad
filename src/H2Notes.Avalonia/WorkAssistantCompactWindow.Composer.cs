using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;
using H2Notes.Avalonia.Services;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed partial class WorkAssistantCompactWindow
{
    private Button _add = null!;
    private readonly TextBlock _permissionLabel = new() { Name = "WorkAssistantPermissionLabel", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly AppIcon _permissionShield = new(IconKind.Shield, 15);
    private readonly Button _modelButton = ComposerButton("WorkAssistantModelPicker");
    private readonly TextBlock _modelLabel = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _effortLabel = new() { FontSize = 12, Foreground = Brush.Parse("#919191"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _dictation = AppIcon.Button(IconKind.Microphone, "Nhập giọng nói Windows · Win+H");
    private ContextMenu? _composerMenu;
    public IChatDictationService DictationService { get; set; } = new ChatDictationService();

    private static Button ComposerButton(string name) => new()
    {
        Name = name, Classes = { "quiet" }, Height = 32, MinHeight = 32, MinWidth = 0,
        Padding = new Thickness(4, 3), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    private void RefreshPermissionButton()
    {
        var label = PermissionOptions.FirstOrDefault(p => p.Mode == _permissionMode)?.Label ?? "Chỉ quan sát";
        _permissionLabel.Text = label;
        var brush = Brush.Parse(_permissionMode == H2AgentPermissionMode.FullAccess ? "#F65B16" : "#796C62");
        _permissionLabel.Foreground = _permissionShield.Foreground = brush;
        if (_permission.Content is null)
            _permission.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5,
                Children = { _permissionShield, _permissionLabel } };
        ToolTip.SetTip(_permission, label + "\n" + PermissionDescription(_permissionMode));
        AutomationProperties.SetName(_permission, "Quyền: " + label);
    }

    private static string PermissionDescription(H2AgentPermissionMode mode) => mode switch
    {
        H2AgentPermissionMode.FullAccess => "Agent được đọc, sửa tệp và chạy lệnh trên máy, dùng mạng theo quyền tài khoản Windows; không hỏi lại từng thao tác. Áp dụng cho tác vụ gửi tiếp theo, tối đa 1 giờ. Không tự nâng quyền quản trị.",
        H2AgentPermissionMode.AskBeforeChanges => "Hiện thao tác cụ thể để bạn đồng ý hoặc từ chối trước mỗi thay đổi trong phạm vi đã chọn.",
        H2AgentPermissionMode.AllowScopedChanges => "Tự thay đổi trong thư mục hoặc tài liệu đã chọn. Không chạy lệnh tự do hay sửa ngoài phạm vi này.",
        H2AgentPermissionMode.UseProjectPolicy => "Dùng quyền của dự án khi tác vụ đã gắn dự án.",
        _ => "Đọc và trả lời; không sửa dữ liệu hoặc chạy lệnh trên máy."
    };

    private void OpenPermissionMenu()
    {
        if (_taskBusy) return;
        var items = PermissionOptions.Where(p => p.Mode != H2AgentPermissionMode.UseProjectPolicy).Select(option =>
        {
            var item = new MenuItem { Name = "WorkAssistantPermission" + option.Mode,
                Header = new StackPanel { MaxWidth = 290, Spacing = 4, Children = {
                    new TextBlock { Text = (option.Mode == _permissionMode ? "✓  " : "") + option.Label, FontSize = 13 },
                    new TextBlock { Text = PermissionDescription(option.Mode), FontSize = 11,
                        Foreground = Brush.Parse("#796C62"), TextWrapping = TextWrapping.Wrap } } } };
            item.Click += (_, _) => { SelectedPermissionMode = option.Mode; SetStatus(option.Label + " · áp dụng cho lượt gửi tiếp theo."); };
            return item;
        }).ToArray();
        _composerMenu?.Close(); _composerMenu = new ContextMenu { ItemsSource = items };
        _composerMenu.Open(_permission);
    }

    private void RefreshModelButton()
    {
        var profile = _model.SelectedItem as AiProfile;
        _modelLabel.Text = profile?.Model ?? "Chọn model";
        _effortLabel.Text = EffortLabel(SelectedReasoningEffort);
        if (_modelButton.Content is null)
        {
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 5 };
            content.Children.Add(_modelLabel); Grid.SetColumn(_effortLabel, 1); content.Children.Add(_effortLabel);
            var arrow = new AppIcon(IconKind.ChevronDown, 11) { Foreground = Brush.Parse("#919191") };
            Grid.SetColumn(arrow, 2); content.Children.Add(arrow); _modelButton.Content = content;
        }
        ToolTip.SetTip(_modelButton, (profile is null ? "Thêm kết nối AI trong cài đặt H2 Notes." : profile.Name + " · " + profile.Model + "\n" + profile.ProcessingLocation) + "\n" + _effortLabel.Text);
        AutomationProperties.SetName(_modelButton, "Model: " + _modelLabel.Text + " · " + _effortLabel.Text);
    }

    private static string EffortLabel(string? value) => value switch
    {
        "low" => "Nhanh", "medium" => "Cân bằng", "high" => "Chuyên sâu", "xhigh" or "max" => "Tối đa",
        "minimal" => "Tối thiểu", "none" => "Tắt suy luận", _ => value ?? ""
    };

    private void OpenModelMenu()
    {
        if (_taskBusy) return;
        var items = new List<object>();
        foreach (var profile in (_model.ItemsSource?.Cast<AiProfile>() ?? []))
        {
            var item = new MenuItem { Header = (SelectedModelProfileId == profile.Id ? "✓  " : "") + profile.Model + " · " + profile.Name };
            item.Click += (_, _) => { if (!_taskBusy) _model.SelectedItem = profile; };
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "Chưa cấu hình model trong H2 Notes", IsEnabled = false });
        var options = _effort.ItemsSource?.Cast<string>().ToArray() ?? [];
        if (options.Length > 0)
        {
            items.Add(new Separator());
            foreach (var effort in options)
            {
                var item = new MenuItem { Header = (SelectedReasoningEffort == effort ? "✓  " : "") + EffortLabel(effort) };
                item.Click += (_, _) => { if (!_taskBusy) _effort.SelectedItem = effort; };
                items.Add(item);
            }
        }
        _composerMenu?.Close(); _composerMenu = new ContextMenu { ItemsSource = items }; _composerMenu.Open(_modelButton);
    }

    private void RefreshSendAction()
    {
        var empty = _preparingAttachments || string.IsNullOrWhiteSpace(PromptText) && DraftAttachments.Count==0;
        _send.Content = new AppIcon(empty ? (_taskBusy ? IconKind.Stop : IconKind.Waveform) : IconKind.ArrowUp, 18) { Foreground = Brushes.White };
        var label = empty ? (_taskBusy ? "Dừng tác vụ" : "Nhập giọng nói Windows · Win+H") : (_taskBusy ? (QueueNextTurn ? "Xếp lượt tiếp theo" : "Bổ sung vào tác vụ đang chạy") : "Gửi yêu cầu");
        ToolTip.SetTip(_send, label); AutomationProperties.SetName(_send, label);
    }

    private void OpenVoiceTyping()
    {
        if (!DictationService.IsSupported) { SetStatus("Nhập giọng nói cần Windows hỗ trợ Win+H."); return; }
        if (!IsActive || !_prompt.Focus(NavigationMethod.Pointer)) { SetStatus("Bấm vào ô soạn trước khi mở nhập giọng nói."); return; }
        _composerMenu?.Close();
        var handle = TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "HWND") { SetStatus("Nhập giọng nói Windows chưa khả dụng."); return; }
        SetStatus(DictationService.TryOpen(handle.Handle).Message);
    }
}
