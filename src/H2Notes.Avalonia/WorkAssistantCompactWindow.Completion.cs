using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed record WorkAssistantProjectChoice(
    Guid ProjectId,
    string Name);

public sealed record WorkAssistantTaskPresentation(
    Guid TaskId,
    H2AgentTaskStatus Status,
    string StateText,
    string ResultText,
    bool Verified,
    bool NeedsAttention,
    IReadOnlyList<string> EvidenceLines,
    Guid? ProjectId,
    bool CanCancel,
    bool CanRetry);

public sealed partial class WorkAssistantCompactWindow
{
    private Border _completionPanel = null!;
    private TextBlock _completionState = null!;
    private TextBlock _completionResult = null!;
    private TextBlock _completionEvidence = null!;
    private Button _completionDetails = null!;
    private Button _completionCancel = null!;
    private Button _completionRetry = null!;
    private Button _completionLink = null!;
    private Button _completionOpenWorkspace = null!;
    private ComboBox _completionProjectPicker = null!;
    private WorkAssistantTaskPresentation? _completionPresentation;
    private bool _completionDetailsExpanded;

    public event Action? CancelTaskRequested;
    public event Action? RetryTaskRequested;
    public event Action<Guid>? LinkProjectRequested;
    public event Action? OpenWorkspaceRequested;

    public bool IsTaskResultVisible => _completionPanel?.IsVisible == true;
    public WorkAssistantTaskPresentation? TaskPresentation => _completionPresentation;

    private void InitializeCompletionUi(Grid root)
    {
        _completionState = new TextBlock
        {
            Name = "WorkAssistantResultState",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#A4573D"),
            TextWrapping = TextWrapping.Wrap
        };
        _completionResult = new TextBlock
        {
            Name = "WorkAssistantResultText",
            FontSize = 12,
            Foreground = Brush.Parse("#4F4842"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96
        };
        _completionEvidence = new TextBlock
        {
            Name = "WorkAssistantEvidenceDetails",
            FontSize = 10,
            Foreground = Brush.Parse("#796C62"),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            MaxHeight = 130
        };

        _completionDetails = ActionButton(
            "WorkAssistantViewDetailsButton",
            "Chi tiết / evidence");
        _completionCancel = ActionButton(
            "WorkAssistantCancelTaskButton",
            "Hủy");
        _completionRetry = ActionButton(
            "WorkAssistantRetryTaskButton",
            "Thử lại");
        _completionOpenWorkspace = ActionButton(
            "WorkAssistantOpenWorkspaceButton",
            "Mở H2");

        _completionProjectPicker = new ComboBox
        {
            Name = "WorkAssistantProjectPicker",
            MinWidth = 145,
            MaxWidth = 210,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _completionProjectPicker.ItemTemplate =
            new FuncDataTemplate<WorkAssistantProjectChoice>(
                (item, _) => new TextBlock
                {
                    Text = item?.Name ?? "",
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

        _completionLink = ActionButton(
            "WorkAssistantLinkProjectButton",
            "Gắn dự án");

        var firstActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                _completionDetails,
                _completionCancel,
                _completionRetry,
                _completionOpenWorkspace
            }
        };

        var projectRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6
        };
        projectRow.Children.Add(_completionProjectPicker);
        Grid.SetColumn(_completionLink, 1);
        projectRow.Children.Add(_completionLink);

        _completionPanel = new Border
        {
            Name = "WorkAssistantResultPanel",
            IsVisible = false,
            Background = Brush.Parse("#FFFDFC"),
            BorderBrush = Brush.Parse("#E1D9D1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(12, 4, 12, 10),
            Child = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    _completionState,
                    _completionResult,
                    firstActions,
                    _completionEvidence,
                    projectRow,
                    new TextBlock
                    {
                        Text = "Undo chỉ xuất hiện khi provider/Agent có đường hoàn tác an toàn; H2 không hứa hoàn tác chung.",
                        FontSize = 9,
                        Foreground = Brush.Parse("#9A8D82"),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };

        Grid.SetRow(_completionPanel, 5);
        root.Children.Add(_completionPanel);

        _completionDetails.Click += (_, _) =>
        {
            _completionDetailsExpanded = !_completionDetailsExpanded;
            _completionEvidence.IsVisible = _completionDetailsExpanded;
            _completionDetails.Content = _completionDetailsExpanded
                ? "Ẩn chi tiết"
                : "Chi tiết / evidence";
        };
        _completionCancel.Click += (_, _) => CancelTaskRequested?.Invoke();
        _completionRetry.Click += (_, _) => RetryTaskRequested?.Invoke();
        _completionLink.Click += (_, _) =>
        {
            if (_completionProjectPicker.SelectedItem is WorkAssistantProjectChoice project)
                LinkProjectRequested?.Invoke(project.ProjectId);
        };
        _completionOpenWorkspace.Click += (_, _) => OpenWorkspaceRequested?.Invoke();
    }

    public void ShowTaskPresentation(
        WorkAssistantTaskPresentation presentation,
        IReadOnlyList<WorkAssistantProjectChoice> projects,
        bool activate = false)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(projects);

        _completionPresentation = presentation;
        _completionPanel.IsVisible = true;
        _completionState.Text = presentation.StateText;
        _completionState.Foreground = Brush.Parse(
            presentation.NeedsAttention ? "#9A5A12"
            : presentation.Verified ? "#3F7449"
            : presentation.Status == H2AgentTaskStatus.Completed ? "#4E7654"
            : "#A4573D");
        _completionResult.Text = Bound(presentation.ResultText, 1_200);

        var lines = presentation.EvidenceLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(20)
            .Select(line => "• " + Bound(line, 500))
            .ToArray();
        _completionEvidence.Text = lines.Length == 0
            ? "Chưa có evidence."
            : string.Join(Environment.NewLine, lines);
        _completionDetails.IsEnabled = true;
        _completionCancel.IsVisible = presentation.CanCancel;
        _completionRetry.IsVisible = presentation.CanRetry;

        _completionProjectPicker.ItemsSource = projects;
        _completionProjectPicker.SelectedItem = presentation.ProjectId is { } projectId
            ? projects.FirstOrDefault(project => project.ProjectId == projectId)
            : null;
        _completionProjectPicker.IsEnabled = presentation.ProjectId is null;
        _completionLink.IsEnabled = presentation.ProjectId is null
            && projects.Count != 0;
        _completionLink.IsVisible = presentation.ProjectId is null;

        if (Height < 520)
            Height = 520;

        if (activate)
            OpenTaskResult();
    }

    public void OpenTaskResult()
    {
        if (!_completionPanel.IsVisible)
            return;

        ApplySettings();
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public void PrepareRetry(string goal)
    {
        _completionPanel.IsVisible = false;
        _completionPresentation = null;
        _completionDetailsExpanded = false;
        _completionEvidence.IsVisible = false;
        PromptText = goal ?? "";
        SetActiveContext(null);
        SelectedPermissionMode = H2AgentPermissionMode.ObserveOnly;
        SetStatus(
            "Đã nạp lại yêu cầu. Context/quyền cũ không được tái sử dụng; capture lại nếu cần rồi bấm Gửi.");
        OpenFromHotkey();
    }

    private static Button ActionButton(string name, string text)
        => new()
        {
            Name = name,
            Content = text,
            FontSize = 10,
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 5, 0)
        };
}
