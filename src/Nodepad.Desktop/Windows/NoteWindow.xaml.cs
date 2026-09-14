using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Nodepad.Desktop.Models;
using Nodepad.Desktop.Services;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfClipboard = System.Windows.Clipboard;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfRichTextBox = System.Windows.Controls.RichTextBox;

namespace Nodepad.Desktop.Windows;

public partial class NoteWindow : Window
{
    private enum CloseReason
    {
        Hide,
        Delete,
        Archive,
        AppExit,
        Internal
    }

    private readonly NoteDocument _note;
    private readonly Action _persistState;
    private readonly Action<NoteDocument> _deleteNote;
    private readonly Func<NoteDocument, NoteDocument> _duplicateNote;
    private readonly Action<ProjectEntry> _createQuickNoteFromProject;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _topmostResetTimer;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private static readonly HashSet<NoteWindow> OpenNoteWindows = [];
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;
    private const double MinimumInactiveOpacity = 0.01d;
    private const double SnapDistance = 16d;
    private const double SnapOverlapTolerance = 42d;
    private readonly IReadOnlyList<FormatColorOption> _textColorOptions =
    [
        new("Default", null, true),
        new("Ink", "#FF241A14"),
        new("Accent", "#FFB45A36"),
        new("Blue", "#FF426B96"),
        new("Green", "#FF3E6A55"),
        new("Red", "#FF8A413F")
    ];
    private readonly IReadOnlyList<FormatColorOption> _highlightColorOptions =
    [
        new("None", null),
        new("Sun", "#FFF7E37A"),
        new("Mint", "#FFBCE8D3"),
        new("Sky", "#FFBFDDF8"),
        new("Rose", "#FFF2C4D1")
    ];
    private WpfRichTextBox? _activeSelectionEditor;
    private WpfRichTextBox? _pendingSelectionPopupEditor;
    private ChecklistItem? _draggedChecklistItem;
    private ProjectEntry? _draggedChecklistProject;
    private ProjectEntry? _draggedProject;
    private System.Windows.Point _checklistDragStartPoint;
    private System.Windows.Point _projectDragStartPoint;
    private bool _suppressProjectHeaderToggle;
    private bool _suspendRgbControls;
    private bool _suspendScheduling;
    private bool _suspendSelectionToolbar;
    private bool _pendingTopmostResetAfterDeactivate;
    private int _pendingChecklistDropIndex = -1;
    private CloseReason _closeReason = CloseReason.Hide;

    public IReadOnlyList<string> FontOptions { get; } =
        Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .OrderBy(name => name)
            .ToList();

    public IReadOnlyList<double> FontSizeOptions { get; } = [12, 14, 16, 18, 20, 24, 28, 32, 36];

    private sealed record FormatColorOption(string Label, string? Hex, bool UseEditorForeground = false);
    private sealed record ChecklistDragPayload(ProjectEntry Project, ChecklistItem Item);
    private sealed record SelectionColorSwatchTag(bool IsHighlight, FormatColorOption Option);

    public NoteWindow(
        NoteDocument note,
        Action persistState,
        Action<NoteDocument> deleteNote,
        Func<NoteDocument, NoteDocument> duplicateNote,
        Action<ProjectEntry> createQuickNoteFromProject)
    {
        _note = note;
        _persistState = persistState;
        _deleteNote = deleteNote;
        _duplicateNote = duplicateNote;
        _createQuickNoteFromProject = createQuickNoteFromProject;
        _suspendScheduling = true;

        InitializeComponent();
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Hyperlink_OnRequestNavigate));

        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _autosaveTimer.Tick += AutosaveTimer_OnTick;
        _topmostResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _topmostResetTimer.Tick += TopmostResetTimer_OnTick;

        DataContext = this;

        Left = _note.Left;
        Top = _note.Top;
        Width = _note.Width;
        Height = _note.Height;

        ConfigureBindings();
        ApplyPalette();
        RefreshProjectHubSummary();
        _suspendScheduling = false;

        _note.PropertyChanged += Note_OnPropertyChanged;
        _note.Projects.CollectionChanged += Projects_OnCollectionChanged;
        OpenNoteWindows.Add(this);

        LocationChanged += WindowBounds_OnChanged;
        SizeChanged += WindowBounds_OnChanged;
        Activated += NoteWindow_OnActivated;
        Deactivated += NoteWindow_OnDeactivated;
        Closing += NoteWindow_OnClosing;
        Closed += NoteWindow_OnClosed;
    }

    public void HideToDesktop()
    {
        _closeReason = CloseReason.Hide;
        Close();
    }

    public void CloseForAppExit()
    {
        _closeReason = CloseReason.AppExit;
        Close();
    }

    public void CloseWithoutPersist()
    {
        _closeReason = CloseReason.Internal;
        Close();
    }

    public void BringForward()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        if (handle != IntPtr.Zero)
        {
            ShowWindowAsync(handle, SwRestore);
        }

        ForceTopOfZOrder();
        Activate();
        Focus();
        TryActivateWindow(handle);

        ApplyWindowOpacity();
    }

    public void RevealFromTray(bool activate)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        if (handle != IntPtr.Zero)
        {
            ShowWindowAsync(handle, SwRestore);
        }

        ForceTopOfZOrder(activate ? 3200 : 4200);
        if (activate)
        {
            _pendingTopmostResetAfterDeactivate = !_note.IsPinned;
            Activate();
            Focus();
            TryActivateWindow(handle);
        }

        ApplyWindowOpacity();
    }

    private void ConfigureBindings()
    {
        TitleBox.Text = _note.IsProjectHubNote
            ? (string.IsNullOrWhiteSpace(_note.Title) ? "Project Hub" : _note.Title)
            : _note.Title;
        TagsBox.Text = _note.Tags;
        LoadEditorContent(BodyBox, _note.Content);
        FontFamilyComboBox.SelectedItem = _note.FontFamilyName;
        FontSizeComboBox.SelectedItem = _note.FontSize;
        OpacitySlider.Value = GetInactiveOpacity();
        PinCheckBox.IsChecked = _note.IsPinned;
        StarCheckBox.IsChecked = _note.IsStarred;
        ProjectHubPanel.Visibility = _note.IsProjectHubNote ? Visibility.Visible : Visibility.Collapsed;
        QuickNoteBodyPanel.Visibility = _note.IsProjectHubNote ? Visibility.Collapsed : Visibility.Visible;
        ProjectEntriesControl.ItemsSource = _note.Projects;
        RefreshProjectOrdering();
        BuildSelectionColorButtons();
        SetSelectionRgbPreviewFromColor(System.Windows.Media.Colors.Black);
        DuplicateButton.IsEnabled = !_note.IsProjectHubNote;
        DuplicateButton.ToolTip = _note.IsProjectHubNote ? "There is only one shared project hub note." : null;

        MinWidth = _note.IsProjectHubNote ? 380 : 300;
        MinHeight = _note.IsProjectHubNote ? 560 : 320;

        ApplyEditorBaseStyle(BodyBox, _note.FontSize);
    }

    private void LoadEditorContent(WpfRichTextBox editor, string? storedValue)
    {
        editor.Document = RichTextDocumentSerializer.CreateDocument(storedValue);
        RichTextDocumentSerializer.NormalizeDocument(editor.Document);
    }

    private void ApplyEditorBaseStyle(WpfRichTextBox editor, double fontSize)
    {
        editor.FontFamily = new WpfFontFamily(_note.FontFamilyName);
        editor.FontSize = fontSize;
        editor.CaretBrush = editor.Foreground;

        if (editor.Document is not null)
        {
            RichTextDocumentSerializer.NormalizeDocument(editor.Document);
            editor.Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            editor.Document.LineHeight = Math.Max(18d, Math.Round(fontSize * 1.35d));
        }
    }

    private void ApplyProjectTitleEditorStyle(WpfRichTextBox editor)
    {
        editor.FontFamily = new WpfFontFamily("Georgia");
        editor.FontSize = 18;
        editor.FontWeight = FontWeights.Bold;
        editor.CaretBrush = editor.Foreground;
        editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        if (editor.Document is not null)
        {
            RichTextDocumentSerializer.NormalizeDocument(editor.Document);
            editor.Document.PagePadding = new Thickness(0);
            editor.Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            editor.Document.LineHeight = 22d;
        }
    }

    private string ReadEditorContent(WpfRichTextBox editor)
    {
        return RichTextDocumentSerializer.Serialize(editor.Document);
    }

    private static string ReadEditorPlainText(WpfRichTextBox editor)
    {
        var range = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd);
        return range.Text
            .Replace("\r\n", Environment.NewLine)
            .TrimEnd('\r', '\n');
    }

    private static string ReadSelectionText(WpfRichTextBox editor)
    {
        return editor.Selection.IsEmpty
            ? string.Empty
            : editor.Selection.Text.Trim();
    }

    private IEnumerable<WpfRichTextBox> EnumerateProjectEditors()
    {
        return FindVisualChildren<WpfRichTextBox>(ProjectHubPanel)
            .Where(editor => !ReferenceEquals(editor, BodyBox));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void BuildPaletteButtons()
    {
        PalettePanel.Children.Clear();

        foreach (var palette in NotePaletteCatalog.All)
        {
            var swatch = new WpfButton
            {
                Tag = palette.Key,
                ToolTip = palette.Label,
                Background = BrushFromHex(palette.BackgroundHex),
                BorderBrush = BrushFromHex(palette.Key == _note.PaletteKey ? palette.AccentHex : palette.BorderHex),
                BorderThickness = new Thickness(palette.Key == _note.PaletteKey ? 3 : 1.5),
                Style = (Style)System.Windows.Application.Current.Resources["SwatchButtonStyle"]
            };
            swatch.Click += PaletteButton_OnClick;
            PalettePanel.Children.Add(swatch);
        }
    }

    private void BuildSelectionColorButtons()
    {
        SelectionTextColorPanel.Children.Clear();
        SelectionHighlightColorPanel.Children.Clear();

        foreach (var option in _textColorOptions)
        {
            SelectionTextColorPanel.Children.Add(CreateSelectionColorSwatch(option, false));
        }

        foreach (var option in _highlightColorOptions)
        {
            SelectionHighlightColorPanel.Children.Add(CreateSelectionColorSwatch(option, true));
        }
    }

    private WpfButton CreateSelectionColorSwatch(FormatColorOption option, bool isHighlight)
    {
        var button = new WpfButton
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(0),
            Tag = new SelectionColorSwatchTag(isHighlight, option),
            ToolTip = option.Label,
            BorderThickness = new Thickness(1.1),
            Style = (Style)System.Windows.Application.Current.Resources["SelectionToolbarButtonStyle"]
        };

        if (option.Hex is null)
        {
            button.Content = isHighlight ? "X" : "A";
            button.Background = WpfBrushes.Transparent;
        }
        else
        {
            button.Content = string.Empty;
            button.Background = BrushFromHex(option.Hex);
        }

        button.Click += SelectionColorSwatchButton_OnClick;
        return button;
    }

    private void ApplyPalette()
    {
        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        var background = BrushFromHex(palette.BackgroundHex);
        var surface = BrushFromHex(palette.SurfaceHex);
        var border = BrushFromHex(palette.BorderHex);
        var accent = BrushFromHex(palette.AccentHex);
        var foreground = BrushFromHex(palette.ForegroundHex);

        Background = background;
        NoteShell.Background = background;
        NoteShell.BorderBrush = border;
        DragHandleIndicator.Background = border;
        TitleBox.Foreground = foreground;
        HideButton.Foreground = foreground;
        MenuButton.Foreground = foreground;
        BodyBox.Background = surface;
        BodyBox.Foreground = foreground;
        BodyBox.BorderBrush = border;
        ApplyEditorBaseStyle(BodyBox, _note.FontSize);
        ProjectHubPanel.Background = background;
        ProjectHubSummaryText.Foreground = accent;
        NoteMenuCard.Background = surface;
        NoteMenuCard.BorderBrush = border;
        SelectionFormatCard.Background = surface;
        SelectionFormatCard.BorderBrush = border;
        SelectionRgbPreview.BorderBrush = border;
        DragPreviewCard.Background = surface;
        DragPreviewCard.BorderBrush = border;
        DragPreviewTitleText.Foreground = foreground;
        DragPreviewBodyText.Foreground = foreground;
        TagsBox.Background = background;
        TagsBox.Foreground = foreground;
        TagsBox.BorderBrush = border;
        PinCheckBox.Foreground = foreground;
        StarCheckBox.Foreground = foreground;
        NewProjectNameTextBox.Background = background;
        NewProjectNameTextBox.Foreground = foreground;
        NewProjectNameTextBox.BorderBrush = border;

        foreach (var editor in EnumerateProjectEditors())
        {
            editor.Foreground = foreground;

            switch (editor.Tag as string)
            {
                case "ProjectTitleEditor":
                    editor.Background = WpfBrushes.Transparent;
                    editor.BorderBrush = WpfBrushes.Transparent;
                    ApplyProjectTitleEditorStyle(editor);
                    break;
                case "ChecklistItemEditor":
                    editor.Background = WpfBrushes.Transparent;
                    editor.BorderBrush = WpfBrushes.Transparent;
                    ApplyEditorBaseStyle(editor, _note.FontSize);
                    break;
                default:
                    editor.Background = surface;
                    editor.BorderBrush = border;
                    ApplyEditorBaseStyle(editor, _note.FontSize);
                    break;
            }
        }

        Topmost = _note.IsPinned;
        if (!_note.IsPinned)
        {
            _topmostResetTimer.Stop();
        }

        ApplyWindowOpacity();
        RefreshOpacityLabel();
        BuildPaletteButtons();
    }

    private double GetInactiveOpacity()
    {
        return Math.Clamp(_note.NoteOpacity, MinimumInactiveOpacity, 1d);
    }

    private void ApplyWindowOpacity()
    {
        Opacity = IsActive ? 1d : GetInactiveOpacity();
    }

    private void RefreshOpacityLabel()
    {
        OpacityLabel.Text = $"Inactive opacity {(int)Math.Round(GetInactiveOpacity() * 100)}%";
    }

    private void ForceTopOfZOrder(int resetDelayMilliseconds = 450)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _topmostResetTimer.Stop();
        Topmost = true;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        if (!_note.IsPinned)
        {
            _topmostResetTimer.Interval = TimeSpan.FromMilliseconds(resetDelayMilliseconds);
            _topmostResetTimer.Start();
            return;
        }

        Topmost = true;
    }

    private void ResetToNormalZOrder()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        Topmost = false;
    }

    private void TopmostResetTimer_OnTick(object? sender, EventArgs e)
    {
        _topmostResetTimer.Stop();
        if (_note.IsPinned)
        {
            Topmost = true;
            return;
        }

        ResetToNormalZOrder();
    }

    private void RefreshProjectHubSummary()
    {
        ProjectHubSummaryText.Text = _note.IsProjectHubNote ? _note.ProgressLabel : string.Empty;
    }

    private void RefreshProjectOrdering()
    {
        for (var index = 0; index < _note.Projects.Count; index++)
        {
            _note.Projects[index].OrderNumber = index + 1;
        }
    }

    private void ShowDragPreview(string title, string? body, System.Windows.Point position)
    {
        DragPreviewTitleText.Text = title;
        DragPreviewBodyText.Text = string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();
        DragPreviewBodyText.Visibility = string.IsNullOrWhiteSpace(DragPreviewBodyText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateDragPreview(position);
        DragPreviewPopup.IsOpen = true;
    }

    private void UpdateDragPreview(System.Windows.Point position)
    {
        DragPreviewPopup.HorizontalOffset = position.X + 18;
        DragPreviewPopup.VerticalOffset = position.Y + 18;
    }

    private void HideDragPreview()
    {
        DragPreviewPopup.IsOpen = false;
    }

    private void Projects_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshProjectOrdering();
    }

    private void SchedulePersist()
    {
        if (_suspendScheduling)
        {
            return;
        }

        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void PersistNow(bool preserveDesktopVisibility)
    {
        _autosaveTimer.Stop();
        CaptureWindowBounds();

        _suspendScheduling = true;
        _note.Title = _note.IsProjectHubNote
            ? (string.IsNullOrWhiteSpace(TitleBox.Text) ? "Project Hub" : TitleBox.Text.Trim())
            : TitleBox.Text.Trim();
        _note.Content = _note.IsProjectHubNote
            ? string.Empty
            : ReadEditorContent(BodyBox);
        _note.Tags = TagsBox.Text.Trim();
        _note.FontFamilyName = FontFamilyComboBox.SelectedItem as string ?? _note.FontFamilyName;
        _note.FontSize = FontSizeComboBox.SelectedItem is double fontSize ? fontSize : _note.FontSize;
        _note.NoteOpacity = OpacitySlider.Value;
        _note.IsPinned = PinCheckBox.IsChecked == true;
        _note.IsStarred = StarCheckBox.IsChecked == true;
        if (_note.IsProjectHubNote)
        {
            SyncProjectEditorsToModel();
        }

        if (!preserveDesktopVisibility)
        {
            _note.IsVisibleOnDesktop = false;
        }

        _note.UpdatedAt = DateTime.UtcNow;
        _note.TouchSummary();
        _suspendScheduling = false;

        RefreshProjectHubSummary();
        _persistState();
    }

    private void SyncProjectEditorsToModel()
    {
        var touchedProjects = new HashSet<ProjectEntry>();

        foreach (var editor in EnumerateProjectEditors())
        {
            switch (editor.Tag as string)
            {
                case "ProjectTitleEditor" when editor.DataContext is ProjectEntry titleProject:
                    titleProject.Name = ReadEditorContent(editor);
                    touchedProjects.Add(titleProject);
                    break;
                case "ChecklistItemEditor" when editor.DataContext is ChecklistItem checklistItem:
                    checklistItem.Text = ReadEditorContent(editor);
                    if (FindProjectForChecklistItem(checklistItem) is { } checklistProject)
                    {
                        touchedProjects.Add(checklistProject);
                    }
                    break;
                default:
                    if (editor.DataContext is ProjectEntry notesProject)
                    {
                        notesProject.Notes = ReadEditorContent(editor);
                        touchedProjects.Add(notesProject);
                    }
                    break;
            }
        }

        foreach (var project in touchedProjects)
        {
            project.TouchSummary();
        }
    }

    private ProjectEntry? FindProjectForChecklistItem(ChecklistItem checklistItem)
    {
        return _note.Projects.FirstOrDefault(project => project.ChecklistItems.Contains(checklistItem));
    }

    private void CaptureWindowBounds()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        _suspendScheduling = true;
        _note.Left = Left;
        _note.Top = Top;
        _note.Width = Width;
        _note.Height = Height;
        _suspendScheduling = false;
    }

    private void Note_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        if (e.PropertyName is nameof(NoteDocument.PaletteKey)
            or nameof(NoteDocument.FontFamilyName)
            or nameof(NoteDocument.FontSize)
            or nameof(NoteDocument.IsPinned)
            or nameof(NoteDocument.IsStarred))
        {
            ApplyPalette();
        }

        if (e.PropertyName is nameof(NoteDocument.NoteOpacity))
        {
            ApplyWindowOpacity();
            RefreshOpacityLabel();
        }

        if (e.PropertyName is nameof(NoteDocument.ProgressLabel)
            or nameof(NoteDocument.CompletedChecklistCount)
            or nameof(NoteDocument.TotalChecklistCount)
            or nameof(NoteDocument.ProjectCount))
        {
            RefreshProjectHubSummary();
        }

        SchedulePersist();
    }

    private void AutosaveTimer_OnTick(object? sender, EventArgs e)
    {
        PersistNow(true);
    }

    private void DragSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
            SnapToNearbyBounds();
            CaptureWindowBounds();
            SchedulePersist();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SnapToNearbyBounds()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        var currentLeft = Left;
        var currentTop = Top;
        var width = Width;
        var height = Height;
        var bestLeft = currentLeft;
        var bestTop = currentTop;
        var bestHorizontalDelta = SnapDistance + 0.1d;
        var bestVerticalDelta = SnapDistance + 0.1d;

        var handle = new WindowInteropHelper(this).Handle;
        var workArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var screenBounds = new Rect(workArea.Left, workArea.Top, workArea.Width, workArea.Height);

        TrySnapHorizontal(currentLeft, width, screenBounds.Left, ref bestLeft, ref bestHorizontalDelta);
        TrySnapHorizontal(currentLeft + width, width, screenBounds.Right, ref bestLeft, ref bestHorizontalDelta, alignRightEdge: true);
        TrySnapVertical(currentTop, height, screenBounds.Top, ref bestTop, ref bestVerticalDelta);
        TrySnapVertical(currentTop + height, height, screenBounds.Bottom, ref bestTop, ref bestVerticalDelta, alignBottomEdge: true);

        foreach (var other in OpenNoteWindows)
        {
            if (ReferenceEquals(other, this)
                || !other.IsLoaded
                || other.Visibility != Visibility.Visible
                || other.WindowState != WindowState.Normal)
            {
                continue;
            }

            var otherBounds = new Rect(other.Left, other.Top, other.Width, other.Height);
            if (RangesOverlapOrNear(currentTop, currentTop + height, otherBounds.Top, otherBounds.Bottom))
            {
                TrySnapHorizontal(currentLeft, width, otherBounds.Left, ref bestLeft, ref bestHorizontalDelta);
                TrySnapHorizontal(currentLeft, width, otherBounds.Right, ref bestLeft, ref bestHorizontalDelta);
                TrySnapHorizontal(currentLeft + width, width, otherBounds.Left, ref bestLeft, ref bestHorizontalDelta, alignRightEdge: true);
                TrySnapHorizontal(currentLeft + width, width, otherBounds.Right, ref bestLeft, ref bestHorizontalDelta, alignRightEdge: true);
            }

            if (RangesOverlapOrNear(currentLeft, currentLeft + width, otherBounds.Left, otherBounds.Right))
            {
                TrySnapVertical(currentTop, height, otherBounds.Top, ref bestTop, ref bestVerticalDelta);
                TrySnapVertical(currentTop, height, otherBounds.Bottom, ref bestTop, ref bestVerticalDelta);
                TrySnapVertical(currentTop + height, height, otherBounds.Top, ref bestTop, ref bestVerticalDelta, alignBottomEdge: true);
                TrySnapVertical(currentTop + height, height, otherBounds.Bottom, ref bestTop, ref bestVerticalDelta, alignBottomEdge: true);
            }
        }

        if (!bestLeft.Equals(currentLeft))
        {
            Left = bestLeft;
        }

        if (!bestTop.Equals(currentTop))
        {
            Top = bestTop;
        }
    }

    private static bool RangesOverlapOrNear(double startA, double endA, double startB, double endB)
    {
        return endA >= startB - SnapOverlapTolerance && endB >= startA - SnapOverlapTolerance;
    }

    private static void TrySnapHorizontal(
        double edgeToCompare,
        double width,
        double targetEdge,
        ref double bestLeft,
        ref double bestDelta,
        bool alignRightEdge = false)
    {
        var delta = Math.Abs(edgeToCompare - targetEdge);
        if (delta > SnapDistance || delta >= bestDelta)
        {
            return;
        }

        bestDelta = delta;
        bestLeft = alignRightEdge ? targetEdge - width : targetEdge;
    }

    private static void TrySnapVertical(
        double edgeToCompare,
        double height,
        double targetEdge,
        ref double bestTop,
        ref double bestDelta,
        bool alignBottomEdge = false)
    {
        var delta = Math.Abs(edgeToCompare - targetEdge);
        if (delta > SnapDistance || delta >= bestDelta)
        {
            return;
        }

        bestDelta = delta;
        bestTop = alignBottomEdge ? targetEdge - height : targetEdge;
    }

    private void MenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        NoteMenuPopup.IsOpen = !NoteMenuPopup.IsOpen;
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideToDesktop();
    }

    private void PaletteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button && button.Tag is string paletteKey)
        {
            _note.PaletteKey = paletteKey;
            ApplyPalette();
            SchedulePersist();
        }
    }

    private void FontFamilyComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendScheduling || FontFamilyComboBox.SelectedItem is not string fontFamily)
        {
            return;
        }

        _note.FontFamilyName = fontFamily;
        ApplyPalette();
        SchedulePersist();
    }

    private void FontSizeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendScheduling || FontSizeComboBox.SelectedItem is not double fontSize)
        {
            return;
        }

        _note.FontSize = fontSize;
        ApplyPalette();
        SchedulePersist();
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        _note.NoteOpacity = e.NewValue;
        ApplyWindowOpacity();
        RefreshOpacityLabel();
        SchedulePersist();
    }

    private void TagsBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        SchedulePersist();
    }

    private void TitleBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        SchedulePersist();
    }

    private void BodyBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendScheduling && sender is WpfRichTextBox)
        {
            return;
        }

        if (_note.IsProjectHubNote)
        {
            return;
        }

        SchedulePersist();
    }

    private void ProjectNotesEditor_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRichTextBox editor || editor.DataContext is not ProjectEntry project)
        {
            return;
        }

        var previousState = _suspendScheduling;
        _suspendScheduling = true;
        LoadEditorContent(editor, project.Notes);
        _suspendScheduling = previousState;
        // chỉnh khoảng cách dòng ở đây
        editor.Document.PagePadding = new Thickness(0);

        foreach (Block block in editor.Document.Blocks)
        {
            if (block is Paragraph p)
            {
                p.Margin = new Thickness(0);
                p.LineHeight = 16; // thử 14, 16, 18
            }
        }
        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        editor.Background = BrushFromHex(palette.SurfaceHex);
        editor.Foreground = BrushFromHex(palette.ForegroundHex);
        editor.BorderBrush = BrushFromHex(palette.BorderHex);
        ApplyEditorBaseStyle(editor, _note.FontSize);
    }

    private void ProjectNameEditor_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRichTextBox editor || editor.DataContext is not ProjectEntry project)
        {
            return;
        }

        var previousState = _suspendScheduling;
        _suspendScheduling = true;
        LoadEditorContent(editor, project.Name);
        _suspendScheduling = previousState;

        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        editor.Background = WpfBrushes.Transparent;
        editor.Foreground = BrushFromHex(palette.ForegroundHex);
        editor.BorderBrush = WpfBrushes.Transparent;
        ApplyProjectTitleEditorStyle(editor);
    }

    private void ProjectNameEditor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendScheduling || sender is not WpfRichTextBox)
        {
            return;
        }

        SchedulePersist();
    }

    private void ProjectNameEditor_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
        }
    }

    private void ChecklistItemEditor_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfRichTextBox editor || editor.DataContext is not ChecklistItem item)
        {
            return;
        }

        var previousState = _suspendScheduling;
        _suspendScheduling = true;
        LoadEditorContent(editor, item.Text);
        _suspendScheduling = previousState;
        editor.Padding = new Thickness(0);
        editor.Document.PagePadding = new Thickness(0);

        foreach (Block block in editor.Document.Blocks)
        {
            if (block is Paragraph p)
            {
                p.Margin = new Thickness(0);
                p.LineHeight = 16;
            }
        }
        var palette = NotePaletteCatalog.Get(_note.PaletteKey);
        editor.Background = WpfBrushes.Transparent;
        editor.Foreground = BrushFromHex(palette.ForegroundHex);
        editor.BorderBrush = WpfBrushes.Transparent;
        ApplyEditorBaseStyle(editor, _note.FontSize);
    }

    private void ProjectNotesEditor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendScheduling || sender is not WpfRichTextBox)
        {
            return;
        }

        SchedulePersist();
    }

    private void PinCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        _note.IsPinned = PinCheckBox.IsChecked == true;
        ApplyPalette();
        SchedulePersist();
    }

    private void StarCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        _note.IsStarred = StarCheckBox.IsChecked == true;
        SchedulePersist();
    }

    private void AddProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewProjectNameTextBox.Text))
        {
            return;
        }

        _note.Projects.Add(new ProjectEntry
        {
            Name = NewProjectNameTextBox.Text.Trim(),
            NextAction = string.Empty,
            Notes = string.Empty,
            References = string.Empty,
            IsExpanded = true,
            ChecklistItems = []
        });
        NewProjectNameTextBox.Clear();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void RemoveProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Remove project '{project.DisplayName}' from the hub?",
            "Remove project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _note.Projects.Remove(project);
        RefreshProjectOrdering();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void AddProjectChecklistItemButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        var dialog = new TaskTextDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        project.ChecklistItems.Add(new ChecklistItem
        {
            Text = dialog.TaskText,
            IsCompleted = false
        });
        project.TouchSummary();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void RemoveProjectChecklistItemButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.DataContext is not ChecklistItem item
            || element.Tag is not ProjectEntry project)
        {
            return;
        }

        project.ChecklistItems.Remove(item);
        project.TouchSummary();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void ProjectChecklistItemState_OnChanged(object sender, RoutedEventArgs e)
    {
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void ChecklistItemEditor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendScheduling || sender is not WpfRichTextBox)
        {
            return;
        }

        SchedulePersist();
    }

    private void TypingEditor_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suspendScheduling)
        {
            return;
        }

        PersistNow(true);
    }

    private void ChecklistItemContainer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        BeginChecklistDrag(sender, e);
    }

    private void ChecklistItemContainer_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ContinueChecklistDrag(sender, e);
    }

    private void ChecklistItemContainer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearChecklistDragState();
        HideDragPreview();
    }

    private void ChecklistItemDragHandle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginChecklistDrag(sender, e);
    }

    private void BeginChecklistDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not FrameworkElement element
            || element.DataContext is not ChecklistItem item
            || element.Tag is not ProjectEntry project)
        {
            return;
        }

        ClearProjectDragState();
        _draggedChecklistItem = item;
        _draggedChecklistProject = project;
        _checklistDragStartPoint = e.GetPosition(this);
    }

    private void ChecklistItemDragHandle_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ContinueChecklistDrag(sender, e);
    }

    private void ContinueChecklistDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || _draggedChecklistItem is null
            || _draggedChecklistProject is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _checklistDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _checklistDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ShowDragPreview(_draggedChecklistItem.DisplayText, _draggedChecklistProject.DisplayName, currentPosition);
        var payload = new System.Windows.DataObject(typeof(ChecklistDragPayload), new ChecklistDragPayload(_draggedChecklistProject, _draggedChecklistItem));
        DragDrop.DoDragDrop(element, payload, System.Windows.DragDropEffects.Move);
        ClearChecklistDragState();
        HideDragPreview();
    }

    private void ChecklistItemDragHandle_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearChecklistDragState();
        HideDragPreview();
    }

    private void ProjectCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressProjectHeaderToggle = false;

        if (e.ChangedButton != MouseButton.Left
            || sender is not FrameworkElement element
            || element.DataContext is not ProjectEntry project
            || IsInteractiveSource(e.OriginalSource as DependencyObject)
            || IsWithinChecklistItemSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _draggedProject = project;
        _projectDragStartPoint = e.GetPosition(this);
    }

    private void ProjectCard_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || _draggedProject is null
            || _draggedChecklistItem is not null
            || IsWithinChecklistItemSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _projectDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _projectDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ShowDragPreview($"{_draggedProject.DisplayOrderLabel} {_draggedProject.DisplayName}", _draggedProject.HeaderLine, currentPosition);
        var payload = new System.Windows.DataObject(typeof(ProjectEntry), _draggedProject);
        var effect = DragDrop.DoDragDrop(element, payload, System.Windows.DragDropEffects.Move);
        if (effect == System.Windows.DragDropEffects.Move)
        {
            _suppressProjectHeaderToggle = true;
        }

        ClearProjectDragState();
        HideDragPreview();
    }

    private void ProjectCard_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearProjectDragState();
        HideDragPreview();
    }

    private void ProjectCard_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryResolveProjectDropTarget(sender, e.Data, out var draggedProject, out var targetProject))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        UpdateDragPreview(e.GetPosition(NoteShell));
        var sourceIndex = _note.Projects.IndexOf(draggedProject);
        var targetIndex = _note.Projects.IndexOf(targetProject);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dropTarget = sender as FrameworkElement;
        var placeAfter = dropTarget is not null && e.GetPosition(dropTarget).Y > dropTarget.ActualHeight / 2;
        var insertIndex = placeAfter ? targetIndex + 1 : targetIndex;
        if (sourceIndex < insertIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, _note.Projects.Count - 1);
        if (insertIndex != sourceIndex)
        {
            _note.Projects.Move(sourceIndex, insertIndex);
            RefreshProjectOrdering();
            RefreshProjectHubSummary();
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void ProjectCard_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryResolveProjectDropTarget(sender, e.Data, out _, out _))
        {
            return;
        }

        RefreshProjectOrdering();
        RefreshProjectHubSummary();
        SchedulePersist();
        HideDragPreview();
        e.Handled = true;
    }

    private void ChecklistItemContainer_OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryResolveChecklistDropTarget(sender, e.Data, out var project, out var draggedItem, out var targetItem))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            _pendingChecklistDropIndex = -1;
            e.Handled = true;
            return;
        }

        UpdateDragPreview(e.GetPosition(NoteShell));
        var sourceIndex = project.ChecklistItems.IndexOf(draggedItem);
        var targetIndex = project.ChecklistItems.IndexOf(targetItem);
        if (sourceIndex >= 0 && targetIndex >= 0)
        {
            var dropTarget = sender as FrameworkElement;
            var placeAfter = dropTarget is not null && e.GetPosition(dropTarget).Y > dropTarget.ActualHeight / 2;
            var insertIndex = placeAfter ? targetIndex + 1 : targetIndex;
            if (sourceIndex < insertIndex)
            {
                insertIndex--;
            }

            _pendingChecklistDropIndex = Math.Clamp(insertIndex, 0, project.ChecklistItems.Count - 1);
        }
        else
        {
            _pendingChecklistDropIndex = -1;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void ChecklistItemContainer_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryResolveChecklistDropTarget(sender, e.Data, out var project, out var draggedItem, out var targetItem))
        {
            _pendingChecklistDropIndex = -1;
            return;
        }

        var sourceIndex = project.ChecklistItems.IndexOf(draggedItem);
        var targetIndex = project.ChecklistItems.IndexOf(targetItem);
        var insertIndex = _pendingChecklistDropIndex;
        if (insertIndex < 0 && sourceIndex >= 0 && targetIndex >= 0)
        {
            var dropTarget = sender as FrameworkElement;
            var placeAfter = dropTarget is not null && e.GetPosition(dropTarget).Y > dropTarget.ActualHeight / 2;
            insertIndex = placeAfter ? targetIndex + 1 : targetIndex;
            if (sourceIndex < insertIndex)
            {
                insertIndex--;
            }

            insertIndex = Math.Clamp(insertIndex, 0, project.ChecklistItems.Count - 1);
        }

        if (sourceIndex >= 0 && insertIndex >= 0 && insertIndex != sourceIndex)
        {
            project.ChecklistItems.Move(sourceIndex, insertIndex);
        }

        _pendingChecklistDropIndex = -1;
        project.TouchSummary();
        RefreshProjectHubSummary();
        SchedulePersist();
        HideDragPreview();
        e.Handled = true;
    }

    private bool TryResolveChecklistDropTarget(object? sender, System.Windows.IDataObject data, out ProjectEntry project, out ChecklistItem draggedItem, out ChecklistItem targetItem)
    {
        project = null!;
        draggedItem = null!;
        targetItem = null!;

        if (!data.GetDataPresent(typeof(ChecklistDragPayload))
            || data.GetData(typeof(ChecklistDragPayload)) is not ChecklistDragPayload payload
            || sender is not FrameworkElement element
            || element.DataContext is not ChecklistItem target
            || element.Tag is not ProjectEntry targetProject
            || !ReferenceEquals(payload.Project, targetProject)
            || ReferenceEquals(payload.Item, target))
        {
            return false;
        }

        project = targetProject;
        draggedItem = payload.Item;
        targetItem = target;
        return true;
    }

    private bool TryResolveProjectDropTarget(object? sender, System.Windows.IDataObject data, out ProjectEntry draggedProject, out ProjectEntry targetProject)
    {
        draggedProject = null!;
        targetProject = null!;

        if (!data.GetDataPresent(typeof(ProjectEntry))
            || data.GetData(typeof(ProjectEntry)) is not ProjectEntry payload
            || sender is not FrameworkElement element
            || element.DataContext is not ProjectEntry target
            || ReferenceEquals(payload, target))
        {
            return false;
        }

        draggedProject = payload;
        targetProject = target;
        return true;
    }

    private void ClearChecklistDragState()
    {
        _draggedChecklistItem = null;
        _draggedChecklistProject = null;
        _checklistDragStartPoint = default;
        _pendingChecklistDropIndex = -1;
    }

    private void ClearProjectDragState()
    {
        _draggedProject = null;
        _projectDragStartPoint = default;
    }

    private void ChecklistSectionHeader_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not FrameworkElement element
            || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        if (IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        project.IsChecklistExpanded = !project.IsChecklistExpanded;
        SchedulePersist();
        e.Handled = true;
    }

    private void ProjectField_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void RichTextEditor_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not WpfRichTextBox editor || !TryOpenSelectionPopup(editor))
        {
            return;
        }
    }

    private void RichTextEditor_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfRichTextBox editor || editor.Selection.IsEmpty)
        {
            _pendingSelectionPopupEditor = null;
            return;
        }

        var clickedPosition = editor.GetPositionFromPoint(e.GetPosition(editor), true);
        if (clickedPosition is null || !IsPositionInsideSelection(editor, clickedPosition))
        {
            _pendingSelectionPopupEditor = null;
            return;
        }

        _pendingSelectionPopupEditor = editor;
        editor.Focus();
        e.Handled = true;
    }

    private void RichTextEditor_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfRichTextBox editor || !ReferenceEquals(_pendingSelectionPopupEditor, editor))
        {
            _pendingSelectionPopupEditor = null;
            return;
        }

        _pendingSelectionPopupEditor = null;
        editor.Focus();
        TryOpenSelectionPopup(editor);
        e.Handled = true;
    }

    private bool TryOpenSelectionPopup(WpfRichTextBox editor)
    {
        if (editor.Selection.IsEmpty)
        {
            return false;
        }

        _activeSelectionEditor = editor;
        RefreshSelectionPopup(editor);
        SelectionFormatPopup.PlacementTarget = editor;
        SelectionFormatPopup.IsOpen = true;
        return true;
    }

    private static bool IsPositionInsideSelection(WpfRichTextBox editor, TextPointer position)
    {
        if (editor.Selection.IsEmpty)
        {
            return false;
        }

        var startToPosition = editor.Selection.Start.CompareTo(position);
        var endToPosition = editor.Selection.End.CompareTo(position);
        return startToPosition <= 0 && endToPosition >= 0;
    }

    private void SelectionFormatPopup_OnClosed(object sender, EventArgs e)
    {
        _activeSelectionEditor = null;
        _pendingSelectionPopupEditor = null;
        _suspendSelectionToolbar = true;
        SelectionFontFamilyComboBox.SelectedItem = null;
        SelectionFontSizeComboBox.SelectedItem = null;
        _suspendSelectionToolbar = false;
    }

    private void RefreshSelectionPopup(WpfRichTextBox editor)
    {
        _suspendSelectionToolbar = true;
        SelectionStylePanel.IsEnabled = !editor.Selection.IsEmpty;
        SelectionParagraphPanel.IsEnabled = !editor.Selection.IsEmpty;
        SelectionFontFamilyComboBox.SelectedItem = ResolveSelectionFontFamily(editor);
        SelectionFontSizeComboBox.SelectedItem = ResolveSelectionFontSize(editor);
        _suspendSelectionToolbar = false;
        var textColor = ResolveSelectionTextColor(editor);
        var highlightColor = ResolveSelectionHighlightColor(editor);
        SetSelectionRgbPreviewFromColor(textColor);
        SelectionPastePlainButton.IsEnabled = WpfClipboard.ContainsText();
        SelectionSearchGoogleLabel.Text = BuildGoogleSearchLabel(ReadSelectionText(editor));
        SelectionQuickTextColorButton.Foreground = new SolidColorBrush(textColor);
        SelectionQuickTextColorButton.BorderBrush = new SolidColorBrush(textColor);
        SelectionQuickHighlightButton.Background = highlightColor.A == 0
            ? (WpfBrush)System.Windows.Application.Current.Resources["PanelBrush"]
            : new SolidColorBrush(highlightColor);
        SelectionQuickHighlightButton.BorderBrush = highlightColor.A == 0
            ? (WpfBrush)System.Windows.Application.Current.Resources["PanelBorderBrush"]
            : new SolidColorBrush(highlightColor);
        RefreshSelectionToolStates(editor);
    }

    private string ResolveSelectionFontFamily(WpfRichTextBox editor)
    {
        var selectedFontFamily = editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty);
        return selectedFontFamily is WpfFontFamily fontFamily
            ? fontFamily.Source
            : _note.FontFamilyName;
    }

    private double ResolveSelectionFontSize(WpfRichTextBox editor)
    {
        var selectedFontSize = editor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
        if (selectedFontSize is double fontSize && fontSize > 0)
        {
            return FontSizeOptions.OrderBy(option => Math.Abs(option - fontSize)).First();
        }

        return FontSizeOptions.OrderBy(option => Math.Abs(option - editor.FontSize)).First();
    }

    private void ApplySelectionStyle(Action<WpfRichTextBox> action)
    {
        if (_activeSelectionEditor is null || _activeSelectionEditor.Selection.IsEmpty)
        {
            return;
        }

        action(_activeSelectionEditor);
        _activeSelectionEditor.Focus();
        RefreshSelectionPopup(_activeSelectionEditor);
    }

    private void ExecuteSelectionCommand(RoutedUICommand command)
    {
        ApplySelectionStyle(editor => command.Execute(null, editor));
    }

    private void CloseSelectionPopup()
    {
        SelectionFormatPopup.IsOpen = false;
    }

    private void AdjustSelectionFontSize(int direction)
    {
        if (_activeSelectionEditor is null)
        {
            return;
        }

        var orderedSizes = FontSizeOptions.OrderBy(size => size).ToList();
        var currentSize = ResolveSelectionFontSize(_activeSelectionEditor);
        var currentIndex = orderedSizes
            .Select((size, index) => new { size, index })
            .OrderBy(item => Math.Abs(item.size - currentSize))
            .First()
            .index;

        var nextIndex = Math.Clamp(currentIndex + direction, 0, orderedSizes.Count - 1);
        SelectionFontSizeComboBox.SelectedItem = orderedSizes[nextIndex];
    }

    private void ReplaceSelectedText(Func<string, string> transform)
    {
        if (_activeSelectionEditor is null || _activeSelectionEditor.Selection.IsEmpty)
        {
            return;
        }

        var editor = _activeSelectionEditor;
        var originalText = editor.Selection.Text;
        var replacementText = transform(originalText);
        if (string.Equals(originalText, replacementText, StringComparison.Ordinal))
        {
            return;
        }

        var start = editor.Selection.Start;
        editor.Selection.Text = replacementText;
        var end = start.GetPositionAtOffset(replacementText.Length, LogicalDirection.Forward) ?? editor.CaretPosition;
        editor.Selection.Select(start, end);
        editor.Focus();
        RefreshSelectionPopup(editor);
    }

    private void PrefixSelectionLines(Func<int, string> prefixFactory)
    {
        ReplaceSelectedText(text =>
        {
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].TrimStart();
                lines[index] = string.IsNullOrWhiteSpace(trimmed)
                    ? prefixFactory(index)
                    : $"{prefixFactory(index)}{trimmed}";
            }

            return string.Join(Environment.NewLine, lines);
        });
    }

    private void SelectionFontFamilyComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSelectionToolbar || SelectionFontFamilyComboBox.SelectedItem is not string fontFamily)
        {
            return;
        }

        ApplySelectionStyle(editor =>
            editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new WpfFontFamily(fontFamily)));
    }

    private void SelectionFontSizeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendSelectionToolbar || SelectionFontSizeComboBox.SelectedItem is not double fontSize)
        {
            return;
        }

        ApplySelectionStyle(editor => editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, fontSize));
    }

    private void SelectionDecreaseFontButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustSelectionFontSize(-1);
    }

    private void SelectionIncreaseFontButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdjustSelectionFontSize(1);
    }

    private System.Windows.Media.Color ResolveSelectionTextColor(WpfRichTextBox editor)
    {
        var selectedForeground = editor.Selection.GetPropertyValue(TextElement.ForegroundProperty);
        if (selectedForeground is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return editor.Foreground is SolidColorBrush editorBrush
            ? editorBrush.Color
            : System.Windows.Media.Colors.Black;
    }

    private static System.Windows.Media.Color ResolveSelectionHighlightColor(WpfRichTextBox editor)
    {
        var selectedBackground = editor.Selection.GetPropertyValue(TextElement.BackgroundProperty);
        if (selectedBackground is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return System.Windows.Media.Colors.Transparent;
    }

    private void RefreshSelectionToolStates(WpfRichTextBox editor)
    {
        SetSelectionToolButtonState(SelectionBoldButton, HasSelectionFontWeight(editor, FontWeights.Bold));
        SetSelectionToolButtonState(SelectionItalicButton, HasSelectionFontStyle(editor, FontStyles.Italic));
        SetSelectionToolButtonState(SelectionUnderlineButton, HasSelectionDecoration(editor, TextDecorationLocation.Underline));
        SetSelectionToolButtonState(SelectionStrikethroughButton, HasSelectionDecoration(editor, TextDecorationLocation.Strikethrough));
        SetSelectionToolButtonState(SelectionCaseButton, false);
    }

    private static bool HasSelectionFontWeight(WpfRichTextBox editor, FontWeight weight)
    {
        var selectedWeight = editor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        return selectedWeight is FontWeight selected && selected == weight;
    }

    private static bool HasSelectionFontStyle(WpfRichTextBox editor, System.Windows.FontStyle style)
    {
        var selectedStyle = editor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
        return selectedStyle is System.Windows.FontStyle selected && selected == style;
    }

    private static bool HasSelectionDecoration(WpfRichTextBox editor, TextDecorationLocation location)
    {
        var selectedDecorations = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        return selectedDecorations is TextDecorationCollection collection
            && collection.Cast<TextDecoration>().Any(decoration => decoration.Location == location);
    }

    private static void SetSelectionToolButtonState(WpfButton button, bool isActive)
    {
        var panelBrush = (WpfBrush)System.Windows.Application.Current.Resources["PanelBrush"];
        var accentSoftBrush = (WpfBrush)System.Windows.Application.Current.Resources["AccentSoftBrush"];
        var panelBorderBrush = (WpfBrush)System.Windows.Application.Current.Resources["PanelBorderBrush"];
        var accentBrush = (WpfBrush)System.Windows.Application.Current.Resources["AccentBrush"];

        button.Background = isActive ? accentSoftBrush : panelBrush;
        button.BorderBrush = isActive ? accentBrush : panelBorderBrush;
    }

    private static string BuildGoogleSearchLabel(string selectionText)
    {
        var normalized = string.Join(" ", selectionText
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Search at Google";
        }

        if (normalized.Length > 28)
        {
            normalized = $"{normalized[..28]}...";
        }

        return $"Search at Google for '{normalized}'";
    }

    private void SetSelectionRgbPreviewFromColor(System.Windows.Media.Color color)
    {
        _suspendRgbControls = true;
        SelectionRedSlider.Value = color.R;
        SelectionGreenSlider.Value = color.G;
        SelectionBlueSlider.Value = color.B;
        _suspendRgbControls = false;
        SelectionRgbPreview.Background = new SolidColorBrush(color);
    }

    private System.Windows.Media.Color GetSelectionRgbColor()
    {
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(SelectionRedSlider.Value),
            (byte)Math.Round(SelectionGreenSlider.Value),
            (byte)Math.Round(SelectionBlueSlider.Value));
    }

    private void SelectionRgbSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendRgbControls)
        {
            return;
        }

        SelectionRgbPreview.Background = new SolidColorBrush(GetSelectionRgbColor());
    }

    private void SelectionApplyTextRgbButton_OnClick(object sender, RoutedEventArgs e)
    {
        var brush = new SolidColorBrush(GetSelectionRgbColor());
        ApplySelectionStyle(editor => editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush));
    }

    private void SelectionApplyHighlightRgbButton_OnClick(object sender, RoutedEventArgs e)
    {
        var brush = new SolidColorBrush(GetSelectionRgbColor());
        ApplySelectionStyle(editor => editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, brush));
    }

    private void SelectionClearHighlightButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySelectionStyle(editor =>
            editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, WpfBrushes.Transparent));
    }

    private void SelectionColorSwatchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not SelectionColorSwatchTag tag)
        {
            return;
        }

        if (tag.IsHighlight)
        {
            var brush = tag.Option.Hex is null ? WpfBrushes.Transparent : BrushFromHex(tag.Option.Hex);
            ApplySelectionStyle(editor => editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, brush));
            return;
        }

        ApplySelectionStyle(editor =>
        {
            var brush = tag.Option.UseEditorForeground
                ? editor.Foreground
                : BrushFromHex(tag.Option.Hex!);
            editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
        });
    }

    private void SelectionBoldButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.ToggleBold);
    }

    private void SelectionItalicButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.ToggleItalic);
    }

    private void SelectionUnderlineButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.ToggleUnderline);
    }

    private void SelectionStrikethroughButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySelectionStyle(editor =>
        {
            var currentDecorations = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            var hasStrikethrough = currentDecorations is TextDecorationCollection collection
                && collection.Cast<TextDecoration>().Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough);

            editor.Selection.ApplyPropertyValue(
                Inline.TextDecorationsProperty,
                hasStrikethrough ? new TextDecorationCollection() : TextDecorations.Strikethrough);
        });
    }

    private void SelectionToggleCaseButton_OnClick(object sender, RoutedEventArgs e)
    {
        ReplaceSelectedText(text =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            return text.Any(char.IsLower)
                ? text.ToUpper()
                : text.ToLower();
        });
    }

    private void SelectionClearFormattingButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySelectionStyle(editor =>
        {
            editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, editor.FontFamily);
            editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, editor.FontSize);
            editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, editor.Foreground);
            editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, WpfBrushes.Transparent);
        });
        CloseSelectionPopup();
    }

    private void SelectionAlignLeftButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.AlignLeft);
    }

    private void SelectionAlignCenterButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.AlignCenter);
    }

    private void SelectionAlignRightButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.AlignRight);
    }

    private void SelectionAlignJustifyButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.AlignJustify);
    }

    private void SelectionDecreaseIndentButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.DecreaseIndentation);
    }

    private void SelectionIncreaseIndentButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.IncreaseIndentation);
    }

    private void SelectionBulletsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.ToggleBullets);
    }

    private void SelectionNumberingButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(EditingCommands.ToggleNumbering);
    }

    private void SelectionInsertBulletTextButton_OnClick(object sender, RoutedEventArgs e)
    {
        PrefixSelectionLines(_ => "• ");
        CloseSelectionPopup();
    }

    private void SelectionInsertChecklistButton_OnClick(object sender, RoutedEventArgs e)
    {
        PrefixSelectionLines(_ => "☐ ");
        CloseSelectionPopup();
    }

    private void SelectionInsertNumberedTextButton_OnClick(object sender, RoutedEventArgs e)
    {
        PrefixSelectionLines(index => $"{index + 1}. ");
        CloseSelectionPopup();
    }

    private void SelectionInsertLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeSelectionEditor is null || _activeSelectionEditor.Selection.IsEmpty)
        {
            return;
        }

        var initialUrl = WpfClipboard.ContainsText() && Uri.TryCreate(WpfClipboard.GetText(), UriKind.Absolute, out _)
            ? WpfClipboard.GetText()
            : string.Empty;

        var prompt = new TextPromptDialog("Insert Link", "URL", initialUrl)
        {
            Owner = this
        };

        if (prompt.ShowDialog() != true
            || !Uri.TryCreate(prompt.ValueText, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            ApplySelectionStyle(editor =>
            {
                var hyperlink = new Hyperlink(editor.Selection.Start, editor.Selection.End)
                {
                    NavigateUri = uri
                };
                hyperlink.ToolTip = uri.AbsoluteUri;
            });
        }
        catch
        {
            ReplaceSelectedText(text => $"{text} ({uri.AbsoluteUri})");
        }

        CloseSelectionPopup();
    }

    private void SelectionUndoButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.Undo);
        CloseSelectionPopup();
    }

    private void SelectionCutButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.Cut);
        CloseSelectionPopup();
    }

    private void SelectionCopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.Copy);
        CloseSelectionPopup();
    }

    private void SelectionPasteButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.Paste);
        CloseSelectionPopup();
    }

    private void SelectionPastePlainButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeSelectionEditor is null || !WpfClipboard.ContainsText())
        {
            return;
        }

        _activeSelectionEditor.Selection.Text = WpfClipboard.GetText();
        _activeSelectionEditor.Focus();
        CloseSelectionPopup();
    }

    private void SelectionDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.Delete);
        CloseSelectionPopup();
    }

    private void SelectionSelectAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        ExecuteSelectionCommand(ApplicationCommands.SelectAll);
        CloseSelectionPopup();
    }

    private void SelectionFindReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeSelectionEditor is null)
        {
            return;
        }

        var editor = _activeSelectionEditor;
        CloseSelectionPopup();
        var dialog = new FindReplaceWindow(editor)
        {
            Owner = this
        };
        dialog.ShowDialog();
        editor.Focus();
    }

    private void SelectionSearchGoogleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeSelectionEditor is null)
        {
            return;
        }

        var query = string.Join(" ", ReadSelectionText(_activeSelectionEditor)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(query)}")
        {
            UseShellExecute = true
        });
        CloseSelectionPopup();
    }

    private void PrioritizeProjectMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not MenuItem { DataContext: ProjectEntry project })
        {
            return;
        }

        var sourceIndex = _note.Projects.IndexOf(project);
        if (sourceIndex <= 0)
        {
            return;
        }

        // Commit drafts before Move can unload and recreate the card's editors.
        PersistNow(true);
        _note.Projects.Move(sourceIndex, 0);
        _persistState();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsLoaded)
            {
                ProjectEntriesScrollViewer.ScrollToTop();
            }
        }));
    }

    private void ProjectHeader_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressProjectHeaderToggle)
        {
            _suppressProjectHeaderToggle = false;
            e.Handled = true;
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        if (IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        project.IsExpanded = !project.IsExpanded;
        RefreshProjectHubSummary();
        SchedulePersist();
        e.Handled = true;
    }

    private void CompleteNextActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        var currentItem = project.CurrentChecklistItem;
        if (currentItem is null)
        {
            return;
        }

        currentItem.IsCompleted = true;
        project.TouchSummary();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void MoveNextActionToNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ProjectEntry project)
        {
            return;
        }

        var nextAction = project.DisplayNextAction.Trim();
        if (string.Equals(nextAction, "No next action", StringComparison.OrdinalIgnoreCase))
        {
            nextAction = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(nextAction))
        {
            return;
        }

        _createQuickNoteFromProject(project);
        project.TouchSummary();
        RefreshProjectHubSummary();
        SchedulePersist();
    }

    private void ProjectExpander_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.DataContext is ProjectEntry project)
        {
            project.IsExpanded = true;
            SchedulePersist();
        }
    }

    private void ProjectExpander_OnCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && expander.DataContext is ProjectEntry project)
        {
            project.IsExpanded = false;
            SchedulePersist();
        }
    }

    private void NewProjectNameTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        AddProjectButton_OnClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void DuplicateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_note.IsProjectHubNote)
        {
            return;
        }

        PersistNow(true);
        _duplicateNote(_note);
        NoteMenuPopup.IsOpen = false;
    }

    private void ArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _closeReason = CloseReason.Archive;
        Close();
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            $"Delete '{_note.DisplayTitle}' permanently?",
            "Delete note",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _closeReason = CloseReason.Delete;
        _deleteNote(_note);
    }

    private void Hyperlink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private void WindowBounds_OnChanged(object? sender, EventArgs e)
    {
        CaptureWindowBounds();
        SchedulePersist();
    }

    private void NoteWindow_OnActivated(object? sender, EventArgs e)
    {
        ApplyWindowOpacity();
    }

    private void NoteWindow_OnDeactivated(object? sender, EventArgs e)
    {
        if (_pendingTopmostResetAfterDeactivate && !_note.IsPinned)
        {
            _pendingTopmostResetAfterDeactivate = false;
            _topmostResetTimer.Stop();
            ResetToNormalZOrder();
        }

        ApplyWindowOpacity();
    }

    private void NoteWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        switch (_closeReason)
        {
            case CloseReason.Delete:
            case CloseReason.Internal:
                return;
            case CloseReason.Archive:
                _note.IsArchived = true;
                PersistNow(false);
                return;
            case CloseReason.AppExit:
                PersistNow(true);
                return;
            default:
                PersistNow(false);
                return;
        }
    }

    private void NoteWindow_OnClosed(object? sender, EventArgs e)
    {
        _note.PropertyChanged -= Note_OnPropertyChanged;
        _note.Projects.CollectionChanged -= Projects_OnCollectionChanged;
        OpenNoteWindows.Remove(this);
        Activated -= NoteWindow_OnActivated;
        Deactivated -= NoteWindow_OnDeactivated;
        _autosaveTimer.Stop();
        _topmostResetTimer.Stop();
        _pendingTopmostResetAfterDeactivate = false;
    }

    private static System.Windows.Media.Brush BrushFromHex(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.RichTextBox
                or System.Windows.Controls.CheckBox
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.Slider)
            {
                return true;
            }

            source = source switch
            {
                Visual => VisualTreeHelper.GetParent(source),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return false;
    }

    private static bool IsWithinChecklistItemSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is ChecklistItem)
            {
                return true;
            }

            source = source switch
            {
                Visual => VisualTreeHelper.GetParent(source),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return false;
    }

    private static void TryActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0u
            : GetWindowThreadProcessId(foregroundWindow, out _);

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }

            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(foregroundThreadId, currentThreadId, false);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
