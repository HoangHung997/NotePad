using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

public sealed class WorkAssistantCompactWindow : Window
{
    private readonly WorkAssistantSettings _settings;
    private readonly TextBox _prompt;
    private readonly TextBlock _status;
    private readonly WrapPanel _contextChips;
    private readonly TextBlock _contextHint;
    private readonly Button _contextReset;
    private H2ActiveWorkContext? _capturedContext;
    private WorkAssistantContextScope _availableContextScope;
    private WorkAssistantContextScope _selectedContextScope;

    public WorkAssistantCompactWindow(WorkAssistantSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Title = "Work Assistant";
        Width = 460;
        Height = 270;
        MinWidth = 380;
        MinHeight = 230;
        CanResize = true;
        ShowInTaskbar = false;
        CanMinimize = false;
        CanMaximize = false;
        Topmost = settings.AlwaysOnTop;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush.Parse("#FCFAF7");

        var title = new TextBlock
        {
            Text = "Work Assistant",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "×",
            Width = 30,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 8, 8, 4)
        };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        _contextChips = new WrapPanel
        {
            Name = "WorkAssistantContextChips",
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _contextHint = new TextBlock
        {
            Name = "WorkAssistantContextHint",
            Text = "Không có ngữ cảnh ứng dụng.",
            FontSize = 10,
            Foreground = Brush.Parse("#796C62"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _contextReset = new Button
        {
            Name = "WorkAssistantContextReset",
            Content = "Đặt lại scope",
            FontSize = 10,
            Padding = new Thickness(7, 3),
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var contextHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 2, 12, 0)
        };
        contextHeader.Children.Add(_contextHint);
        Grid.SetColumn(_contextReset, 1);
        contextHeader.Children.Add(_contextReset);

        var contextArea = new StackPanel
        {
            Name = "WorkAssistantContextArea",
            Spacing = 4,
            Children = { contextHeader, _contextChips }
        };

        _prompt = new TextBox
        {
            Name = "WorkAssistantPrompt",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Watermark = "Bạn muốn làm gì?",
            MinHeight = 72,
            Margin = new Thickness(12, 4)
        };
        _status = new TextBlock
        {
            Name = "WorkAssistantCompactStatus",
            Text = "Hotkey chỉ mở trợ lý · chưa gửi hoặc thay đổi dữ liệu.",
            FontSize = 10,
            Foreground = Brush.Parse("#796C62"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 2, 12, 10)
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
        root.Children.Add(header);
        Grid.SetRow(contextArea, 1);
        root.Children.Add(contextArea);
        Grid.SetRow(_prompt, 2);
        root.Children.Add(_prompt);
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;

        close.Click += (_, _) => Hide();
        _contextReset.Click += (_, _) => ResetContextScope();
        DesktopWindowChrome.Attach(this, header);
        RebuildContextChips();
    }

    public string PromptText
    {
        get => _prompt.Text ?? "";
        set => _prompt.Text = value ?? "";
    }

    public H2ActiveWorkContext? CapturedContext => _capturedContext;
    public WorkAssistantContextScope AvailableContextScope => _availableContextScope;
    public WorkAssistantContextScope SelectedContextScope => _selectedContextScope;

    public string SelectedContextSummary
        => BuildSelectedContextSummary(_capturedContext, _selectedContextScope);

    public void SetActiveContext(H2ActiveWorkContext? context)
    {
        _capturedContext = context;
        _availableContextScope = AvailableScope(context);
        _selectedContextScope = _availableContextScope;
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

    private void RebuildContextChips()
    {
        _contextChips.Children.Clear();

        if (_capturedContext is null || _availableContextScope == WorkAssistantContextScope.None)
        {
            _contextHint.Text = "Không có ngữ cảnh ứng dụng.";
            _contextReset.IsVisible = false;
            return;
        }

        _contextHint.Text = _selectedContextScope == WorkAssistantContextScope.None
            ? "Đã bỏ toàn bộ context khỏi lượt gửi kế tiếp."
            : "Context cho lượt gửi kế tiếp · bấm × để bỏ bớt scope.";

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

    private static string Bound(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }
}
