using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private readonly Button _modelPickerButton = new()
    {
        Name = "ChatModelPicker", Classes = { "quiet" }, MinWidth = 0, MinHeight = 32, Height = 32,
        Padding = new Thickness(5, 3), Margin = new Thickness(0), CornerRadius = new CornerRadius(8),
        HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 300, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _modelPickerLabel = new()
    {
        Name = "ChatModelPickerLabel", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Popup _modelPickerPopup = new()
    {
        Name = "ChatModelPickerPopup", Placement = PlacementMode.TopEdgeAlignedRight,
        IsLightDismissEnabled = true, VerticalOffset = -6
    };
    private readonly TextBlock _modelPickerLocation = new()
    {
        Name = "ChatModelPickerLocation", FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = RichEditor.Brush("#796C62")
    };
    private readonly TextBlock _reasoningCurrent = new()
    {
        Name = "ChatReasoningCurrent", FontSize = 12, FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap, Foreground = RichEditor.Brush("#A4573D")
    };
    private readonly Slider _reasoningSlider = new()
    {
        Name = "ChatReasoningSlider", Minimum = 0, Maximum = 1, SmallChange = 1, LargeChange = 1,
        TickFrequency = 1, IsSnapToTickEnabled = true, TickPlacement = TickPlacement.BottomRight,
        MinWidth = 0, Margin = new Thickness(0, 4)
    };
    private readonly Button _reasoningReset = new()
    {
        Name = "ChatReasoningReset", Content = new AppIcon(IconKind.Undo, 15), Classes = { "quiet" },
        Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(6), Foreground = RichEditor.Brush("#796C62")
    };
    private readonly TextBlock _reasoningMaximum = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _reasoningHint = new()
    {
        Name = "ChatReasoningHint", FontSize = 11, Foreground = RichEditor.Brush("#796C62"), TextWrapping = TextWrapping.Wrap
    };
    private Grid? _reasoningScale;
    private Border? _modelPickerCard;
    private ModelPickerScope? _modelPickerScope;

    private void BuildModelPicker()
    {
        // Move the same selector once; its original persistence/cancel handlers stay attached.
        _optionsPanel.Children.Remove(_profiles);
        _profiles.MinWidth = 0;
        _profiles.MinHeight = _profiles.Height = 36;
        _profiles.FontSize = 12;
        _profiles.Padding = new Thickness(8, 5);
        _profiles.Background = Brushes.Transparent;
        _profiles.BorderThickness = new Thickness(0);
        _profiles.BorderBrush = RichEditor.Brush("#D9CFC5");
        _profiles.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _profiles.SelectionBoxItemTemplate = new FuncDataTemplate<AiProfile>((profile, _) => new TextBlock
        {
            Text = ModelLabel(profile), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        _profiles.ItemTemplate = new FuncDataTemplate<AiProfile>((profile, _) => new StackPanel
        {
            MaxWidth = 270, Spacing = 2, Children =
            {
                new TextBlock { Text = profile?.Name, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = profile?.Model, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = profile?.ProcessingLocation, FontSize = 11, Foreground = RichEditor.Brush("#796C62"), TextWrapping = TextWrapping.Wrap }
            }
        });
        AutomationProperties.SetName(_profiles, "Model và kết nối AI đã lưu");
        _profiles.SelectionChanged += (_, _) => { CloseModelPicker(); RefreshComposerOptions(); };

        // Retain the existing discrete selection model; only the slider is shown.
        _reasoningEffort.IsVisible = false;
        _reasoningEffort.SelectionChanged += (_, _) =>
        {
            if (_refreshingComposerOptions || _loading) return;
            if (!CanChangeModelPickerEffort()) { RefreshComposerOptions(); return; }
            if (_reasoningEffort.SelectedItem is not ReasoningChoice choice) return;
            SelectModelPickerReasoning(choice.Value);
        };
        _reasoningSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _refreshingComposerOptions || _loading) return;
            if (!CanChangeModelPickerEffort()) { RefreshComposerOptions(); return; }
            var index = (int)Math.Round(_reasoningSlider.Value);
            if (index >= 0 && index < _reasoningEffort.ItemCount) _reasoningEffort.SelectedIndex = index;
        };
        _reasoningReset.Click += (_, _) => ResetModelPickerReasoning();
        ToolTip.SetTip(_reasoningReset, "Xóa lựa chọn riêng cho cuộc trao đổi, dùng lại mặc định của hồ sơ. Không sửa cấu hình model.");
        AutomationProperties.SetName(_reasoningReset, "Đặt lại mức suy luận về mặc định hồ sơ");

        var buttonContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonContent.Children.Add(_modelPickerLabel);
        var arrow = new AppIcon(IconKind.ChevronDown, 12) { Margin = new Thickness(4, 0, 0, 0) };
        buttonContent.Children.Add(arrow); Grid.SetColumn(arrow, 1);
        _modelPickerButton.Content = buttonContent;
        _modelPickerButton.Click += (_, _) =>
        {
            if (_modelPickerPopup.IsOpen) CloseModelPicker(); else OpenModelPicker();
        };

        _reasoningCurrent.VerticalAlignment = VerticalAlignment.Center;
        var reasoningTitle = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children =
        { _reasoningCurrent, _unknownReasoning, _reasoningReset } };
        Grid.SetColumn(_reasoningReset, 1);
        _reasoningScale = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children =
        {
            new TextBlock { Text = "Mặc định", FontSize = 11 }, _reasoningMaximum
        } };
        Grid.SetColumn(_reasoningMaximum, 1);
        var body = new StackPanel { Spacing = 5, Children =
        { reasoningTitle, _profiles, _reasoningSlider, _reasoningScale } };
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        body.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CloseModelPicker(); _modelPickerButton.Focus(NavigationMethod.Tab); e.Handled = true;
        };
        _modelPickerCard = new Border
        {
            Name = "ChatModelPickerCard", Width = 310, Padding = new Thickness(14), CornerRadius = new CornerRadius(16),
            Background = RichEditor.Brush("#FFFCF8"), BorderBrush = RichEditor.Brush("#D9CFC5"), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 16, Color = Color.Parse("#245C4634") }),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body }
        };
        // Scope Fluent accent resources to this card, not the rest of the application.
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
            _modelPickerCard.Resources[key] = Color.Parse("#A4573D");
        _modelPickerPopup.PlacementTarget = _modelPickerButton;
        _modelPickerPopup.Child = _modelPickerCard;
        _modelPickerPopup.Closed += (_, _) =>
        {
            _profiles.IsDropDownOpen = _reasoningEffort.IsDropDownOpen = false;
            _modelPickerScope = null;
        };
    }

    private void OpenModelPicker()
    {
        if (_scope is null || ComposerOptionsBusy) return;
        RefreshComposerOptions();
        _mentionPopup.IsOpen = false; _composerMenu?.Close(); _permissionMenu?.Close();
        _modelPickerScope = CaptureModelPickerScope();
        ResizeModelPicker();
        _modelPickerPopup.IsOpen = true;
        if (_reasoningSlider.IsVisible) _reasoningSlider.Focus(NavigationMethod.Directional);
        else _profiles.Focus(NavigationMethod.Directional);
    }

    private void CloseModelPicker()
    {
        _profiles.IsDropDownOpen = _reasoningEffort.IsDropDownOpen = false;
        _modelPickerPopup.IsOpen = false;
        _modelPickerScope = null;
    }

    private void ResizeModelPicker()
    {
        if (_modelPickerCard is null) return;
        var size = TopLevel.GetTopLevel(this)?.ClientSize ?? Bounds.Size;
        _modelPickerCard.Width = Math.Min(310, Math.Max(160, size.Width - 24));
        _modelPickerCard.MaxHeight = Math.Max(120, size.Height - 32);
    }

    private bool CanChangeModelPickerEffort() => _scope is not null && !ComposerOptionsBusy
        && _profiles.SelectedItem is AiProfile && _modelPickerPopup.IsOpen && IsModelPickerScopeCurrent();

    private void ResetModelPickerReasoning() => SelectModelPickerReasoning(null);

    private void SelectModelPickerReasoning(string? effort)
    {
        if (!CanChangeModelPickerEffort()) { RefreshComposerOptions(); return; }
        var options = AiModelCapabilities.GetReasoningOptions((AiProfile)_profiles.SelectedItem!);
        if (options.Count == 0 || effort is not null && !options.Contains(effort))
        { RefreshComposerOptions(); return; }
        if (_conversation?.ReasoningEffort == effort) return;
        EnsureConversation().ReasoningEffort = effort;
        // The first choice may create a conversation; keep this open scope in sync.
        _modelPickerScope = CaptureModelPickerScope();
        Touch(_scope); RefreshComposerOptions(); ComposerOptionsChanged?.Invoke();
    }

    private ModelPickerScope CaptureModelPickerScope()
    {
        var profile = _profiles.SelectedItem as AiProfile;
        return new(_scope, _conversation, _composerScopeVersion, profile, profile?.Model, profile?.BaseUrl,
            profile?.Protocol, profile?.ReasoningEffort);
    }

    private bool IsModelPickerScopeCurrent() => _modelPickerScope == CaptureModelPickerScope();

    private void RefreshModelPicker(AiProfile? profile, IReadOnlyList<string> options, bool enabled)
    {
        var selected = SelectedReasoningEffort;
        var configured = profile?.ReasoningEffort?.Trim().ToLowerInvariant();
        var inherited = configured == "max" ? options.LastOrDefault() : options.FirstOrDefault(value => value == configured);
        var effortLabel = options.Count == 0 ? "Tối đa" : selected is not null ? EffortLabel(selected)
            : inherited is not null ? "Mặc định · " + EffortLabel(inherited) : "Mặc định";
        _modelPickerLabel.Text = ModelLabel(profile) + " · " + effortLabel;
        var description = (profile is null ? "Mở Thiết lập AI để lưu kết nối và model." : ProfileDescription(profile))
            + "\nMức suy luận: " + effortLabel;
        ToolTip.SetTip(_modelPickerButton, description);
        AutomationProperties.SetName(_modelPickerButton, "Model và suy luận: " + _modelPickerLabel.Text);
        _modelPickerLocation.Text = profile is null ? "Chưa có kết nối. Mở Thiết lập AI để thêm model." : profile.ProcessingLocation;
        _reasoningCurrent.Text = effortLabel;
        _reasoningCurrent.IsVisible = options.Count > 0;
        _reasoningEffort.IsVisible = false;
        _reasoningSlider.IsVisible = _reasoningScale!.IsVisible = options.Count > 0;
        _reasoningSlider.IsEnabled = enabled && options.Count > 0;
        _reasoningSlider.Maximum = Math.Max(1, options.Count);
        _reasoningSlider.Value = Math.Max(0, _reasoningEffort.SelectedIndex);
        AutomationProperties.SetName(_reasoningSlider, "Mức suy luận: " + effortLabel);
        ToolTip.SetTip(_reasoningSlider, "Mặc định hoặc các mức model hỗ trợ: " + string.Join(" · ", options.Select(EffortLabel)));
        _reasoningMaximum.Text = options.Count > 0 ? EffortLabel(options[^1]) : "Tối đa";
        _reasoningReset.IsVisible = options.Count > 0;
        _reasoningReset.IsEnabled = enabled && _conversation?.ReasoningEffort is not null;
        _reasoningHint.Text = options.Count == 0
            ? "Dùng khả năng mặc định của model. Chưa xác nhận API chỉnh mức suy luận; không gửi tham số tự đặt."
            : "Mặc định dùng cấu hình hồ sơ. Các nấc còn lại chỉ áp dụng cho cuộc trao đổi này.";
        ToolTip.SetTip(_reasoningCurrent, _reasoningHint.Text);
        ToolTip.SetTip(_reasoningSlider, _reasoningHint.Text + "\nMặc định hoặc: " + string.Join(" · ", options.Select(EffortLabel)));
    }

    private static string ModelLabel(AiProfile? profile) => string.IsNullOrWhiteSpace(profile?.Model)
        ? profile?.Name ?? "Chọn model" : profile.Model;

    private sealed record ModelPickerScope(AiChatScope? Scope, AiConversation? Conversation, long Version,
        AiProfile? Profile, string? Model, string? BaseUrl, AiProtocol? Protocol, string? ProfileEffort);
}
