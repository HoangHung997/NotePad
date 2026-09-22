using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia;

[Flags]
public enum WorkAssistantContextScope
{
    None = 0,
    Application = 1,
    Document = 2,
    Session = 4,
    Selection = 8,
    All = Application | Document | Session | Selection
}

public sealed partial class WorkAssistantCompactWindow : Window
{
    private readonly WorkAssistantSettings _settings;
    private readonly TextBox _prompt;
    private readonly Button _permission;
    private H2AgentPermissionMode _permissionMode;
    private readonly TextBlock _status;
    private readonly Button _send;
    private readonly WrapPanel _contextChips;
    private readonly TextBlock _contextHint;
    private readonly Button _contextReset;
    private H2ActiveWorkContext? _capturedContext;
    private WorkAssistantContextScope _availableContextScope;
    private WorkAssistantContextScope _selectedContextScope;

    public WorkAssistantCompactWindow(WorkAssistantSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Title = "H2 Assistant";
        Width = 640; Height = 610; MinWidth = 380; MinHeight = 610;
        CanResize = true; ShowInTaskbar = false; CanMinimize = false; CanMaximize = false;
        Topmost = settings.AlwaysOnTop;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brush.Parse("#FCFAF7");

        var title = new Button { Name = "WorkAssistantThreads", Content = "H2 Assistant  ▾", FontSize = 16,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        title.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var thread in Threads)
            {
                var item = new MenuItem { Header = thread.Title + (thread.ThreadId == ConversationId ? "  ✓" : "") };
                item.Click += (_, _) => ConversationSelected?.Invoke(thread.ThreadId); menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Open(title);
        };
        var close = Controls.AppIcon.Button(Controls.IconKind.Close, "Thu gọn về bong bóng");
        close.Name = "WorkAssistantClose";
        var startNew = Controls.AppIcon.Button(Controls.IconKind.Plus, "Cuộc trò chuyện mới");
        startNew.Name = "WorkAssistantNewChat";
        startNew.Click += (_, _) => NewConversationRequested?.Invoke();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(16, 8, 8, 6) };
        var brand = new Border { Width=28, Height=28, CornerRadius=new CornerRadius(7), Background=Brush.Parse("#A4573D"),
            Child=new TextBlock { Text="H2",FontSize=15,FontWeight=FontWeight.SemiBold,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center } };
        header.Children.Add(brand); Grid.SetColumn(title,1); header.Children.Add(title);
        var pin=Controls.AppIcon.Button(Controls.IconKind.Pin,"Ghim trên cùng"); pin.Click+=(_,_)=>Topmost=!Topmost;
        var expand=Controls.AppIcon.Button(Controls.IconKind.Dock,"Mở cửa sổ đầy đủ / thu gọn"); expand.Click+=(_,_)=>SetFullMode(!FullMode);
        Grid.SetColumn(pin,2); header.Children.Add(pin); Grid.SetColumn(expand,3); header.Children.Add(expand);
        Grid.SetColumn(startNew,4); header.Children.Add(startNew); Grid.SetColumn(close,5); header.Children.Add(close);

        _contextChips = new WrapPanel { Name = "WorkAssistantContextChips", Orientation = Orientation.Horizontal };
        _contextHint = new TextBlock { Name = "WorkAssistantContextHint", FontSize = 10,
            Foreground = Brush.Parse("#796C62"), TextWrapping = TextWrapping.Wrap };
        _contextReset = new Button { Name = "WorkAssistantContextReset", Content = "Đặt lại ngữ cảnh",
            FontSize = 10, Padding = new Thickness(5, 2), IsVisible = false };
        var contextHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        contextHeader.Children.Add(_contextHint); Grid.SetColumn(_contextReset, 1); contextHeader.Children.Add(_contextReset);
        var contextArea = new StackPanel { Name = "WorkAssistantContextArea", Spacing = 3,
            Margin = new Thickness(14, 0, 14, 5), Children = { contextHeader, _contextChips } };

        _permission = ComposerButton("WorkAssistantPermissionPreset");
        _permission.Click += (_, _) => OpenPermissionMenu();
        _prompt = new TextBox { Name = "WorkAssistantPrompt", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, Watermark = "Giao việc hoặc hỏi tiếp…", MinHeight = 50, MaxHeight = 160,
            FontSize = 14, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 6, 2, 9) };
        // The Fluent template draws a separate focus border inside the TextBox.
        _prompt.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
        _prompt.Resources["TextControlBorderThicknessFocused"] = new Thickness(0);
        _prompt.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _prompt.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        ToolTip.SetTip(_prompt, "Enter để gửi · Shift+Enter để xuống dòng");
        _status = new TextBlock { Name = "WorkAssistantCompactStatus", Text = "Sẵn sàng · chưa gửi yêu cầu.",
            FontSize = 10, Foreground = Brush.Parse("#796C62"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 3, 14, 8) };
        _send = Controls.AppIcon.Button(Controls.IconKind.ArrowUp, "Gửi yêu cầu");
        _send.Name = "WorkAssistantSendButton"; _send.Width = _send.Height = 34;
        _send.MinHeight = 34; _send.Padding = new Thickness(8); _send.CornerRadius = new CornerRadius(18);
        _send.Background = Brushes.Black; _send.Foreground = Brushes.White; _send.BorderThickness = new Thickness(0);

        var composer = new StackPanel { Spacing = 2, Children = { new ScrollViewer { MaxHeight=72,Content=_draftAttachmentRows },_prompt, BuildConversationOptions() } };
        var surface = new Border { Name = "WorkAssistantComposerSurface", CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 8), Margin = new Thickness(12, 0, 12, 0),
            Background = Brushes.White, BorderBrush = Brush.Parse("#D9D5CF"), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.Parse("#08000000") }),
            Child = composer };
        var bottom = new StackPanel { Children = { _busySendMode, surface, _status } };
        InitializeCompletionUi(_history);
        Content = BuildAssistantShell(header,contextArea,bottom);
        close.Click += (_, _) => Hide();
        _contextReset.Click += (_, _) => ResetContextScope();
        _send.Click += async (_, _) => { if (_preparingAttachments || _taskBusy && string.IsNullOrWhiteSpace(PromptText)) CancelTaskRequested?.Invoke(); else if (string.IsNullOrWhiteSpace(PromptText) && DraftAttachments.Count==0) OpenVoiceTyping(); else await SubmitPromptAsync(); };
        _prompt.PastingFromClipboard+=async(_,e)=> { e.Handled=true;await PasteAttachmentsOrText(); };
        _prompt.TextChanged += (_, _) => { RefreshSendAction(); DraftChanged?.Invoke(PromptText);if(!_taskBusy && PromptText.EndsWith('@'))OpenAttachmentMenu(_prompt); };
        RefreshSendAction(); RefreshPermissionButton();
        _prompt.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            { e.Handled = true; if (_send.IsEnabled) await SubmitPromptAsync(); }
        }, RoutingStrategies.Tunnel);
        DesktopWindowChrome.Attach(this, header);
        RebuildContextChips();
        ShowHistory([], null);
    }
    public string PromptText
    {
        get => _prompt.Text ?? "";
        set => _prompt.Text = value ?? "";
    }

    public event Func<string, string, Task>? SubmitRequested;
    public event Action<string>? DraftChanged;
    public IReadOnlyList<H2AgentThread> Threads { get; set; } = [];
    public event Action<Guid>? ConversationSelected;

    public H2ActiveWorkContext? CapturedContext => _capturedContext;
    public WorkAssistantContextScope AvailableContextScope => _availableContextScope;
    public WorkAssistantContextScope SelectedContextScope => _selectedContextScope;

    public H2AgentPermissionMode SelectedPermissionMode
    {
        get => _permissionMode;
        set
        {
            if (_taskBusy) return;
            _permissionMode = Enum.IsDefined(value) ? value : H2AgentPermissionMode.ObserveOnly;
            RefreshPermissionButton();
        }
    }

    public string SelectedContextSummary
        => BuildSelectedContextSummary(_capturedContext, _selectedContextScope);

    public void SetActiveContext(H2ActiveWorkContext? context)
    {
        _capturedContext = context;
        _availableContextScope = AvailableScope(context);
        _selectedContextScope = _availableContextScope;
        // Permission grants are per-send/session. Capturing a different foreground target must
        // never silently carry a prior mutation preset into the new context.
        SelectedPermissionMode = H2AgentPermissionMode.ObserveOnly;
        RebuildContextChips();
    }

    public void RemoveContextScope(WorkAssistantContextScope scope)
    {
        _selectedContextScope &= ~scope;
        RebuildContextChips();
    }

    public void ResetContextScope()
    {
        _selectedContextScope = _availableContextScope;
        RebuildContextChips();
    }

    public void SetStatus(string text, bool isError = false)
    {
        _status.Text = Bound(text, 600);
        _status.Foreground = Brush.Parse(isError ? "#A33A2B" : "#796C62");
    }

    public void ClearPrompt()
        => _prompt.Text = "";

    public void ApplySettings()
        => Topmost = _settings.AlwaysOnTop;

    public void OpenFromHotkey()
    {
        ApplySettings();
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        _prompt.Focus();
        _prompt.CaretIndex = _prompt.Text?.Length ?? 0;
    }

    private async Task SubmitPromptAsync()
    {
        if (_preparingAttachments) { CancelTaskRequested?.Invoke(); return; }
        var prompt = PromptText.Trim();
        if(prompt.Length==0 && DraftAttachments.Count>0)prompt="Phân tích các tệp đính kèm và tóm tắt nội dung chính.";
        if (prompt.Length == 0)
        {
            SetStatus("Nhập yêu cầu trước khi gửi.", isError: true);
            return;
        }

        var handlers = SubmitRequested?
            .GetInvocationList()
            .Cast<Func<string, string, Task>>()
            .ToArray();
        if (handlers is null || handlers.Length == 0)
        {
            SetStatus("Work Assistant chưa sẵn sàng gửi tác vụ.", isError: true);
            return;
        }

        _send.IsEnabled = false;
        SetStatus("Đang gửi tác vụ…");
        try
        {
            foreach (var handler in handlers)
                await handler(prompt, SelectedContextSummary);
        }
        finally
        {
            _send.IsEnabled = true;
        }
    }

    private void RebuildContextChips()
    {
        _contextChips.Children.Clear();

        if (SelectedWorkspaceRoot is not null)
        {
            _contextHint.Text = "Ngữ cảnh: thư mục đã chọn.";
            _contextReset.IsVisible = false;
            return;
        }

        if (_capturedContext is null || _availableContextScope == WorkAssistantContextScope.None)
        {
            _contextHint.Text = SelectedWorkspaceRoot is null ? "Chưa chọn tài liệu hoặc thư mục." : "Ngữ cảnh: thư mục đã chọn.";
            _contextReset.IsVisible = false;
            return;
        }

        _contextHint.Text = _selectedContextScope == WorkAssistantContextScope.None
            ? "Đã bỏ ngữ cảnh ứng dụng khỏi lượt gửi kế tiếp."
            : "Ngữ cảnh gửi kèm · bấm × để bỏ.";

        AddChip(
            WorkAssistantContextScope.Application,
            "WorkAssistantContextApplicationChip",
            ApplicationLabel(_capturedContext));
        AddChip(
            WorkAssistantContextScope.Document,
            "WorkAssistantContextDocumentChip",
            DocumentLabel(_capturedContext.DocumentPath));
        AddChip(
            WorkAssistantContextScope.Session,
            "WorkAssistantContextSessionChip",
            _capturedContext.DocumentSessionId);
        AddChip(
            WorkAssistantContextScope.Selection,
            "WorkAssistantContextSelectionChip",
            _capturedContext.Selection);

        _contextReset.IsVisible = _selectedContextScope != _availableContextScope;
    }

    private void AddChip(
        WorkAssistantContextScope scope,
        string name,
        string? label)
    {
        if ((_availableContextScope & scope) == 0
            || (_selectedContextScope & scope) == 0
            || string.IsNullOrWhiteSpace(label))
            return;

        var button = new Button
        {
            Name = name,
            Content = BoundChip(label) + "  ×",
            FontSize = 10,
            Padding = new Thickness(7, 3),
            Margin = new Thickness(0, 0, 5, 2),
            Background = Brush.Parse("#F8EAE2"),
            BorderBrush = Brush.Parse("#E3C6B5"),
            Foreground = Brush.Parse("#8F4A35")
        };
        button.Click += (_, _) => RemoveContextScope(scope);
        _contextChips.Children.Add(button);
    }

    private static WorkAssistantContextScope AvailableScope(H2ActiveWorkContext? context)
    {
        if (context is null) return WorkAssistantContextScope.None;

        var scope = WorkAssistantContextScope.Application;
        if (!string.IsNullOrWhiteSpace(context.DocumentPath))
            scope |= WorkAssistantContextScope.Document;
        if (!string.IsNullOrWhiteSpace(context.DocumentSessionId))
            scope |= WorkAssistantContextScope.Session;
        if (!string.IsNullOrWhiteSpace(context.Selection))
            scope |= WorkAssistantContextScope.Selection;
        return scope;
    }

    private static string BuildSelectedContextSummary(
        H2ActiveWorkContext? context,
        WorkAssistantContextScope scope)
    {
        if (context is null || scope == WorkAssistantContextScope.None)
            return "";

        var parts = new List<string>();
        if ((scope & WorkAssistantContextScope.Application) != 0)
        {
            // Do not include WindowTitle here: many apps embed document/session names in it.
            // Those belong to explicit Document/Session chips so removing those chips truly
            // removes that scope from the next-send grounding summary.
            parts.Add("App=" + context.ApplicationKind);
            parts.Add("Process=" + Bound(
                context.ProcessName + "#" + context.ProcessId, 180));
        }
        if ((scope & WorkAssistantContextScope.Document) != 0
            && !string.IsNullOrWhiteSpace(context.DocumentPath))
            parts.Add("Document=" + Bound(context.DocumentPath, 1_024));
        if ((scope & WorkAssistantContextScope.Session) != 0
            && !string.IsNullOrWhiteSpace(context.DocumentSessionId))
            parts.Add("Session=" + Bound(context.DocumentSessionId, 240));
        if ((scope & WorkAssistantContextScope.Selection) != 0
            && !string.IsNullOrWhiteSpace(context.Selection))
            parts.Add("Selection=" + Bound(context.Selection, 1_200));

        return Bound(string.Join("\n", parts), 4_000);
    }

    private static string ApplicationLabel(H2ActiveWorkContext context)
        => context.ApplicationKind is H2ApplicationKind.Unknown or H2ApplicationKind.Other
            ? string.IsNullOrWhiteSpace(context.ProcessName)
                ? context.ApplicationKind.ToString()
                : context.ProcessName
            : context.ApplicationKind.ToString();

    private static string? DocumentLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Trim().TrimEnd('\\', '/');
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return slash >= 0 && slash + 1 < value.Length
            ? value[(slash + 1)..]
            : value;
    }

    private static string BoundChip(string value)
    {
        value = Bound(value, 160);
        return value.Length <= 54 ? value : value[..51] + "…";
    }

    private static readonly WorkAssistantPermissionOption[] PermissionOptions =
    [
        new(H2AgentPermissionMode.ObserveOnly, "Chỉ quan sát"),
        new(H2AgentPermissionMode.AskBeforeChanges, "Hỏi trước khi thay đổi"),
        new(H2AgentPermissionMode.AllowScopedChanges, "Cho phép thay đổi phạm vi đã chọn"),
        new(H2AgentPermissionMode.FullAccess, "Toàn quyền tiếp cận"),
        new(H2AgentPermissionMode.UseProjectPolicy, "Dùng chính sách dự án")
    ];

    private sealed record WorkAssistantPermissionOption(
        H2AgentPermissionMode Mode,
        string Label);

    private static string Bound(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }
}
