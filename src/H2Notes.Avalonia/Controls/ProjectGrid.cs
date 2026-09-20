using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Avalonia.Threading;
using H2Notes.Core;
using NeraSpreadSheet.DataGrid.Core;
using NeraSpreadSheet.Scrolling;

namespace H2Notes.Avalonia.Controls;

public sealed class ProjectGrid : Control
{
    private double HeaderHeight => Compact ? 0 : 39;
    public bool Compact { get; private set; }
    public void SetCompact(bool value)
    {
        if (Compact == value) return;
        Compact = value; _layoutWidth = 0; InvalidateArrange(); Refresh();
    }
    private static readonly IBrush Ink = RichEditor.Brush("#302E2B");
    private static readonly IBrush Accent = RichEditor.Brush("#A4573D");
    private static readonly Pen Line = new(RichEditor.Brush("#E1DDD7"), 1);
    private readonly ContinuousScrollController _scroll = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _tipTimer = new();
    private readonly Stopwatch _clock = new();
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical, Width = 15, AllowAutoHide = false };
    private readonly RichEditor _editor = new() { IsVisible = false, Background = Brushes.White, BorderBrush = Accent, BorderThickness = new Thickness(1.5) };
    private readonly GridColumnDefinition[] _columns = [
        new("number", "STT", typeof(int), true, 64), new("title", "Dự án / Công việc", typeof(string), false, 440),
        new("progress", "Tiến độ", typeof(string), true, 145),
        new("comment", "Ghi chú", typeof(string), false, 270)];
    private readonly List<LayoutRow> _layout = [];
    private double[] _widths = [64, 440, 145, 270];
    private double[] _edges = [0, 64, 504, 649, 919];
    public SheetPreferences Preferences { get; set; } = new();
    private (Guid? Row, int Column) _hoverCell;
    private double _contentHeight;
    private double _layoutWidth;
    private bool _manualWidths;
    private bool _syncBar;
    private NoteRecord? _board;
    private string _filter = "";
    private SheetRow? _selection;
    private SheetRow? _editingRow;
    private int _editingColumn;
    private RichDocument? _editOriginal;
    private bool _draftFlushed;
    private Rect _editorBounds;
    private Point _press;
    private SheetRow? _pressedRow;
    private SheetRow? _dropTarget;
    private bool _dragging;
    private bool _after;
    private Point _ghost;
    private int _resizeColumn = -1;
    private double _resizeStartWidth;
    private ProjectRecord? _focusedProject;
    public bool TasksOnly => _focusedProject is not null;
    public void FocusProject(ProjectRecord project)
    {
        CommitEdit(); _focusedProject = project; _layoutWidth = 0; _manualWidths = false;
        _selection = null; _scroll.Reset(); Refresh(); InvalidateArrange();
    }

    public event Action<SheetRow?>? SelectionChanged;
    public event Action? DataChanged;
    public event Action<SheetRow>? DeleteRequested;
    public SheetRow? SelectedRow => _selection;
    public bool IsEditing => _editor.IsVisible;
    public bool HasDraft => _editor.IsVisible && _editor.HasChanges;
    public event Action? DraftChanged;
    public event Action? EditStarting;

    public ProjectGrid()
    {
        Focusable = true; ClipToBounds = true;
        LogicalChildren.Add(_bar); LogicalChildren.Add(_editor);
        VisualChildren.Add(_bar); VisualChildren.Add(_editor);
        _bar.ValueChanged += (_, _) => { if (!_syncBar) { CommitEdit(); _scroll.ScrollTo(0, _bar.Value, false); InvalidateVisual(); } };
        _editor.Changed += () => DraftChanged?.Invoke();
        _editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { CommitEdit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Tab)
            { CommitEdit(); Focus(); MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        _timer.Tick += (_, _) =>
        {
            var elapsed = _clock.Elapsed; _clock.Restart();
            if (_dragging)
            {
                var delta = _ghost.Y < HeaderHeight + 25 ? -10 : _ghost.Y > Bounds.Height - 28 ? 10 : 0;
                if (delta != 0) _scroll.QueueDelta(new ScrollDelta(0, delta, ScrollInputKind.Precision));
            }
            var result = _scroll.AdvanceFrame(elapsed, new ScrollBounds(0, MaxScroll));
            if (result.Changed) { ClearCellTip(); SyncBar(); InvalidateVisual(); if (_dragging) UpdateDrop(_ghost); }
            if (!_scroll.HasPendingMotion && !_dragging) { _timer.Stop(); _clock.Stop(); }
        };
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            if (_hoverCell.Row is not null && IsPointerOver && !_dragging) ToolTip.SetIsOpen(this, true);
        };
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        DetachedFromVisualTree += (_, _) => { _timer.Stop(); ClearCellTip(); ClearLayouts(); };
        AttachedToVisualTree += (_, _) => Refresh();
    }

    private double MaxScroll => Math.Max(0, _contentHeight - Math.Max(0, Bounds.Height - HeaderHeight));
    public void SetBoard(NoteRecord board)
    {
        CommitEdit(); _board = board; _focusedProject = null; _selection = null; _scroll.Reset(); Refresh();
        Select(_layout.FirstOrDefault()?.Row);
    }
    public void SetFilter(string value) { CommitEdit(); _filter = value.Trim(); _scroll.Reset(); Refresh(); }
    public void Refresh()
    {
        if (_board is null) return;
        ClearCellTip();
        ClearLayouts();
        var y = 0d;
        var rows = _focusedProject is { } selected
            ? selected.ChecklistItems
                .Where(task => _filter.Length == 0
                    || task.DisplayText.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                    || task.CommentText.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .Select(task => new SheetRow(selected, task, _board.Projects.IndexOf(selected) + 1))
                .ToList()
            : SheetOperations.Rows(_board, _filter);
        foreach (var row in rows)
        {
            var doc = row.Task?.ReadText() ?? row.Project.ReadName();
            var title = MakeCell(doc, _widths[1] - (TasksOnly ? 20 : row.IsProject ? 50 : 59), row.IsProject);
            var commentDoc = row.Task?.ReadComment() ?? row.Project.ReadNotes();
            var comment = MakeCell(commentDoc, (Compact ? _widths[1] : _widths[3]) - 24);
            var progressText = row.IsProject ? row.Project.Progress : "";
            if (row.IsProject && !row.Project.IsExpanded && row.Project.Next is { } next)
                progressText += "\nNext: " + next.DisplayText;
            var progressDoc = new RichDocument { Runs = [new(progressText, new TextStyle(Size: 12))] };
            var progress = MakeCell(progressDoc, _widths[2] - 20);
            // Even a fixed-height row must fit its first line when a larger font is selected.
            var height = Math.Max(40, Math.Max(title.TextLines[0].Height, comment.TextLines[0].Height) + 16);
            if (Preferences.AutoHeightTitle) height = Math.Max(height, title.Height + 16);
            if (Preferences.AutoHeightComment) height = Math.Max(height, comment.Height + 16);
            if (Preferences.AutoHeightProgress && !TasksOnly) height = Math.Max(height, progress.Height + 16);
            if (Compact)
            {
                title = LimitText(title, doc, title.TextLines[0].Height + 16, Preferences.AutoHeightTitle);
                comment = LimitText(comment, commentDoc, comment.TextLines[0].Height + 16, Preferences.AutoHeightComment);
                height = Math.Max(58, title.Height + (commentDoc.Text.Length > 0 ? comment.Height + 4 : 0) + 20);
            }
            title = LimitText(title, doc, height, Preferences.AutoHeightTitle, row.IsProject);
            comment = LimitText(comment, commentDoc, height, Preferences.AutoHeightComment);
            progress = LimitText(progress, progressDoc, height, Preferences.AutoHeightProgress);
            _layout.Add(new(row, y, height, title, progress, comment));
            y += height;
        }
        _contentHeight = y;
        if (_selection is not null)
        {
            var updated = _layout.FirstOrDefault(x => x.Row.Id == _selection.Id)?.Row;
            _selection = updated ?? _layout.FirstOrDefault(x => x.Row.Project.Id == _selection.Project.Id)?.Row;
        }
        _scroll.ScrollTo(0, Math.Min(_scroll.Snapshot.OffsetY, MaxScroll), false);
        SyncBar(); InvalidateArrange(); InvalidateVisual();
    }
    private void ClearLayouts()
    {
        foreach (var row in _layout) { row.Title.Dispose(); row.Comment.Dispose(); row.Progress.Dispose(); }
        _layout.Clear();
    }

    public void SelectProject(ProjectRecord project)
    {
        Select(_layout.FirstOrDefault(x => x.Row.IsProject && x.Row.Project.Id == project.Id)?.Row);
        EnsureSelectedVisible();
    }

    private void Select(SheetRow? row)
    {
        if (_selection?.Id != row?.Id) CommitEdit();
        _selection = row; InvalidateVisual(); SelectionChanged?.Invoke(row);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _bar.Measure(availableSize); _editor.Measure(new Size(Math.Max(0, availableSize.Width), 60));
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 1000,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 450);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var width = Math.Max(TasksOnly ? 200 : 650, size.Width - 15);
        if (Math.Abs(_layoutWidth - width) > 0.5)
        {
            _layoutWidth = width;
            if (Compact) _widths = [40, width - 40, 0, 0];
            else if (TasksOnly) _widths = [40, (width - 40) * .58, 0, (width - 40) * .42];
            else if (!_manualWidths) _widths = [64, (width - 209) * .61, 145, (width - 209) * .39];
            else
            {
                // Keep manually resized columns inside the viewport when the note gets narrower.
                var fixedWidth = _widths[0] + _widths[2];
                if (fixedWidth > width - 220)
                {
                    var factor = (width - 220) / fixedWidth;
                    _widths[0] *= factor; _widths[2] *= factor;
                    fixedWidth = width - 220;
                }
                var ratio = Math.Clamp(_widths[1] / (_widths[1] + _widths[3]), .3, .75);
                _widths[1] = (width - fixedWidth) * ratio;
                _widths[3] = width - fixedWidth - _widths[1];
            }
            RebuildEdges(); Refresh();
        }
        _bar.Arrange(new Rect(size.Width - 15, HeaderHeight, 15, Math.Max(0, size.Height - HeaderHeight)));
        if (_editor.IsVisible && _editingRow is not null)
        {
            var row = _layout.FirstOrDefault(x => x.Row.Id == _editingRow.Id);
            if (row is not null)
            {
                var indent = _editingColumn == 1 ? (TasksOnly ? 0 : _editingRow.IsProject ? 32 : 40) : 0;
                _editorBounds = new Rect(_edges[_editingColumn] + indent, HeaderHeight + row.Y - _scroll.Snapshot.OffsetY,
                    Math.Max(30, _widths[_editingColumn] - indent), Math.Max(42, row.Height));
                if (Compact) _editorBounds = new Rect(40, row.Y - _scroll.Snapshot.OffsetY + (_editingColumn == 3 ? row.Title.Height + 12 : 0), _widths[1], Math.Max(42, row.Height - (_editingColumn == 3 ? row.Title.Height + 12 : 0)));
                _editor.Arrange(_editorBounds);
            }
        }
        return size;
    }
    private void RebuildEdges()
    {
        _edges = new double[_columns.Length + 1];
        for (var i = 0; i < _columns.Length; i++) _edges[i + 1] = _edges[i] + _widths[i];
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(RichEditor.Brush("#FFFDFC"), new Rect(Bounds.Size));
        var width = Bounds.Width - 15;
        using (context.PushClip(new Rect(0, HeaderHeight, Math.Max(0, width), Math.Max(0, Bounds.Height - HeaderHeight))))
        {
            var offset = _scroll.Snapshot.OffsetY;
            for (var index = FirstVisibleIndex(offset); index < _layout.Count; index++)
            {
                var layout = _layout[index];
                var y = HeaderHeight + layout.Y - offset;
                if (y + layout.Height < HeaderHeight) continue;
                if (y > Bounds.Height) break;
                var row = layout.Row;
                var selected = _selection?.Id == row.Id;
                var background = selected ? "#F9EAE2" : row.IsProject ? "#FBF8F3" : "#FFFDFC";
                context.FillRectangle(RichEditor.Brush(background), new Rect(0, y, width, layout.Height));
                if (selected) context.DrawRectangle(Accent, null, new Rect(1, y + 2, 6, layout.Height - 4), 2, 2);
                if (row.IsProject)
                {
                    context.DrawText(MakeText(row.Ordinal.ToString(), 50, 15), new Point(25, y + 11));
                    context.DrawText(MakeText(row.Project.IsExpanded || _filter.Length > 0 ? "▾" : "▸", 25, 18), new Point(_edges[1] + 13, y + 8));
                }
                else
                {
                    var box = new Rect((_widths[0] - 19) / 2, y + 10, 19, 19);
                    context.DrawRectangle(row.Task!.IsCompleted ? Accent : Brushes.White, new Pen(row.Task.IsCompleted ? Accent : Ink, 1), box, 3, 3);
                    if (row.Task.IsCompleted)
                    {
                        context.DrawLine(new Pen(Brushes.White, 2), box.TopLeft + new Vector(4, 10), box.TopLeft + new Vector(8, 14));
                        context.DrawLine(new Pen(Brushes.White, 2), box.TopLeft + new Vector(8, 14), box.TopLeft + new Vector(16, 5));
                    }
                    if (!TasksOnly && row.Project.Next?.Id == row.Task.Id)
                        context.DrawRectangle(Accent, null, new Rect(_edges[1] + 28, y + 12, 3, layout.Height - 24), 1, 1);
                }
                if (!_editor.IsVisible || _editingRow?.Id != row.Id || _editingColumn != 1)
                {
                    using var clip = context.PushClip(new Rect(_edges[1], y, _widths[1], layout.Height));
                    layout.Title.Draw(context, new Point(_edges[1] + (TasksOnly ? 8 : row.IsProject ? 38 : 47), y + 8));
                }
                using (context.PushClip(new Rect(_edges[2], y, _widths[2], layout.Height)))
                    layout.Progress.Draw(context, new Point(_edges[2] + 10, y + 8));
                if (!_editor.IsVisible || _editingRow?.Id != row.Id || _editingColumn != 3)
                {
                    using var clip = context.PushClip(new Rect(Compact ? _edges[1] : _edges[3], y, Compact ? _widths[1] : _widths[3], layout.Height));
                    layout.Comment.Draw(context, new Point((Compact ? _edges[1] + 8 : _edges[3] + 12), y + (Compact ? layout.Title.Height + 12 : 8)));
                }
                context.DrawLine(Line, new Point(0, y + layout.Height), new Point(width, y + layout.Height));
            }
            if (!Compact) for (var i = TasksOnly ? 3 : 1; i < _columns.Length; i++) context.DrawLine(Line, new Point(_edges[i], HeaderHeight), new Point(_edges[i], Bounds.Height));
            if (_layout.Count == 0)
                context.DrawText(MakeText(TasksOnly ? "Chưa có công việc. Nhấn + để thêm." : _filter.Length > 0 ? "Không tìm thấy dự án hoặc công việc." : "Chưa có dự án. Nhấn + Dự án để bắt đầu.", width - 60, 16), new Point(30, 72));
            if (_dragging && _dropTarget is not null)
            {
                var target = _layout.First(x => x.Row.Id == _dropTarget.Id);
                var targetY = HeaderHeight + target.Y + (_after ? target.Height : 0) - offset;
                if (_pressedRow?.IsProject == true && _after)
                {
                    var last = _layout.Last(x => x.Row.Project.Id == _dropTarget.Project.Id);
                    targetY = HeaderHeight + last.Y + last.Height - offset;
                }
                context.DrawLine(new Pen(Accent, 3), new Point(5, targetY), new Point(width - 5, targetY));
            }
        }
        context.FillRectangle(RichEditor.Brush("#FAF7F2"), new Rect(0, 0, Bounds.Width, HeaderHeight));
        for (var i = 0; i < _columns.Length; i++)
        {
            if (Compact || TasksOnly && i is 0 or 2) continue;
            context.DrawText(MakeText(TasksOnly && i == 1 ? "Công việc" : _columns[i].Header, _widths[i] - 16, 14), new Point(_edges[i] + (i == 0 ? 20 : 10), 9));
            context.DrawLine(Line, new Point(_edges[i], 0), new Point(_edges[i], HeaderHeight));
        }
        context.DrawLine(Line, new Point(0, HeaderHeight), new Point(Bounds.Width, HeaderHeight));
        if (_dragging && _pressedRow is not null)
        {
            var ghost = new Rect(Math.Clamp(_ghost.X + 14, 0, Math.Max(0, width - 330)), Math.Clamp(_ghost.Y + 14, HeaderHeight, Math.Max(HeaderHeight, Bounds.Height - 55)), 320, 44);
            using (context.PushOpacity(.9))
            {
                context.DrawRectangle(RichEditor.Brush("#F6E6DD"), new Pen(Accent, 1), ghost, 5, 5);
                var text = MakeText(_pressedRow.Title, 294, 14, _pressedRow.IsProject); text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(text, ghost.TopLeft + new Vector(12, 11));
            }
        }
    }

    private static FormattedText MakeText(string text, double width, double size, bool bold = false) => new(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI", FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, Ink)
        { MaxTextWidth = Math.Max(10, width) };
    private static TextLayout LimitText(TextLayout text, RichDocument doc, double height, bool autoHeight, bool project = false)
    {
        if (autoHeight || text.Height <= height - 16) return text;
        var available = height - 16;
        var usedHeight = 0d; var prefixLength = 0;
        foreach (var line in text.TextLines)
        {
            if (usedHeight + line.Height > available) break;
            usedHeight += line.Height;
            prefixLength = line.FirstTextSourceIndex + line.Length - line.NewLineLength;
        }
        // Explicit newlines can bypass framework ellipsis. Trim a display-only prefix,
        // preserving rich spans and Unicode grapheme boundaries; the model stays intact.
        var source = doc.Text;
        prefixLength = source[..Math.Min(prefixLength, source.Length)].TrimEnd().Length;
        var boundaries = StringInfo.ParseCombiningCharacters(source[..prefixLength]).Append(prefixLength).ToArray();
        var low = 0; var high = boundaries.Length - 1;
        TextLayout? best = null;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = MakeCell(DisplayPrefix(doc, boundaries[mid]), text.MaxWidth, project);
            if (candidate.Height <= available && candidate.Width <= text.MaxWidth + .1)
            { best?.Dispose(); best = candidate; low = mid + 1; }
            else { candidate.Dispose(); high = mid - 1; }
        }
        best ??= MakeCell(RichDocument.Plain("…"), text.MaxWidth, project);
        text.Dispose(); return best;
    }
    private static RichDocument DisplayPrefix(RichDocument doc, int length)
    {
        var display = new RichDocument(); var remaining = length;
        foreach (var run in doc.Runs)
        {
            var take = Math.Min(remaining, run.Text.Length);
            if (take > 0) display.Runs.Add(new(run.Text[..take], run.Style));
            remaining -= take; if (remaining == 0) break;
        }
        display.Runs.Add(new("…", doc.StyleAt(Math.Max(0, length - 1))));
        return display;
    }
    private int FirstVisibleIndex(double offset)
    {
        var low = 0; var high = _layout.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_layout[mid].Y + _layout[mid].Height < offset) low = mid + 1; else high = mid;
        }
        return low;
    }
    private static TextLayout MakeCell(RichDocument doc, double width, bool project = false, double maxHeight = double.PositiveInfinity)
    {
        List<ValueSpan<TextRunProperties>> overrides = [];
        var position = 0;
        foreach (var run in doc.Runs)
        {
            if (run.Text.Length == 0) continue;
            var decorations = new TextDecorationCollection();
            if (run.Style.Underline) decorations.Add(TextDecorations.Underline[0]);
            if (run.Style.Strike) decorations.Add(TextDecorations.Strikethrough[0]);
            overrides.Add(new(position, run.Text.Length, new GenericTextRunProperties(
                new Typeface(run.Style.Font, run.Style.Italic ? FontStyle.Italic : FontStyle.Normal,
                    run.Style.Bold || project ? FontWeight.Bold : FontWeight.Normal), run.Style.Size,
                decorations, RichEditor.Brush(run.Style.Color), run.Style.Highlight is null ? null : RichEditor.Brush(run.Style.Highlight))));
            position += run.Text.Length;
        }
        return new TextLayout(doc.Text, new Typeface("Segoe UI"), 16, Ink, textWrapping: TextWrapping.Wrap,
            textTrimming: double.IsPositiveInfinity(maxHeight) ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(10, width), maxHeight: maxHeight, textStyleOverrides: overrides);
    }

    private SheetRow? Hit(Point point) => point.Y < HeaderHeight ? null : _layout.FirstOrDefault(x =>
        point.Y - HeaderHeight + _scroll.Snapshot.OffsetY >= x.Y && point.Y - HeaderHeight + _scroll.Snapshot.OffsetY < x.Y + x.Height)?.Row;
    private int ColumnAt(Point point)
    {
        if (Compact && point.X >= _edges[1])
        {
            var row = Hit(point); var layout = _layout.FirstOrDefault(r => r.Row.Id == row?.Id);
            return layout is not null && point.Y + _scroll.Snapshot.OffsetY > layout.Y + layout.Title.Height + 10 ? 3 : 1;
        }
        for (var i = 0; i < _columns.Length; i++) if (point.X >= _edges[i] && point.X < _edges[i + 1]) return i; return -1;
    }
    private int ResizeColumnAt(Point point)
    {
        if (Compact || point.Y < 0 || point.Y >= HeaderHeight || point.X < 0 || point.X >= Bounds.Width - 15
            || (_editor.IsVisible && _editorBounds.Contains(point))) return -1;
        for (var i = 0; i < _columns.Length - 1; i++)
        {
            if (TasksOnly && i is 0 or 2) continue;
            if (Math.Abs(point.X - _edges[i + 1]) < 6) return i;
        }
        return -1;
    }
    private void UpdateResizeCursor(Point point)
    {
        if (_resizeColumn >= 0 || ResizeColumnAt(point) >= 0) Cursor = new Cursor(StandardCursorType.SizeWestEast);
        else ClearValue(CursorProperty);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        ClearCellTip();
        if (point.X >= Bounds.Width - 15 || (_editor.IsVisible && _editorBounds.Contains(point))) return;
        var properties = e.GetCurrentPoint(this).Properties;
        var resizeColumn = ResizeColumnAt(point);
        if (resizeColumn >= 0 && properties.IsLeftButtonPressed)
        {
            CommitEdit(); _resizeColumn = resizeColumn; _press = point; _resizeStartWidth = _widths[resizeColumn];
            UpdateResizeCursor(point); e.Pointer.Capture(this); e.Handled = true; return;
        }
        var row = Hit(point);
        if (row is null) return;
        CommitEdit(); Select(row); Focus();
        if (properties.IsRightButtonPressed) { OpenRowMenu(row); e.Handled = true; return; }
        if (!properties.IsLeftButtonPressed) return;
        var column = ColumnAt(point);
        if (row.IsProject && column == 1 && point.X < _edges[1] + 34)
        { row.Project.IsExpanded = !row.Project.IsExpanded; Changed(); e.Handled = true; return; }
        if (!row.IsProject && column == 0)
        { row.Task!.IsCompleted = !row.Task.IsCompleted; Changed(); e.Handled = true; return; }
        if (e.ClickCount == 2 && column is 1 or 3) { BeginEdit(row, column); e.Handled = true; return; }
        _press = point; _pressedRow = row; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var point = e.GetPosition(this);
        UpdateResizeCursor(point);
        if (_pressedRow is null && _resizeColumn < 0 && !(_editor.IsVisible && _editorBounds.Contains(point))) UpdateCellTip(point);
        else ClearCellTip();
        if (_resizeColumn >= 0)
        {
            var newWidth = Math.Clamp(_resizeStartWidth + point.X - _press.X, 50, 700);
            var delta = newWidth - _widths[_resizeColumn];
            if (_widths[3] - delta < 100) return;
            _widths[_resizeColumn] = newWidth; _widths[3] -= delta; _manualWidths = true; RebuildEdges(); Refresh(); return;
        }
        if (_pressedRow is null || _filter.Length > 0) return;
        if (!_dragging && Math.Abs(point.X - _press.X) + Math.Abs(point.Y - _press.Y) > 8) { _dragging = true; StartMotion(); }
        if (_dragging) { _ghost = point; UpdateDrop(point); InvalidateVisual(); }
    }
    private void UpdateDrop(Point point)
    {
        _dropTarget = Hit(point);
        if (_dropTarget is null || _pressedRow is null) return;
        if (_pressedRow.IsProject)
            _dropTarget = _layout.First(x => x.Row.IsProject && x.Row.Project.Id == _dropTarget.Project.Id).Row;
        else if (_dropTarget.IsProject || _dropTarget.Project.Id != _pressedRow.Project.Id) { _dropTarget = null; return; }
        var layout = _layout.First(x => x.Row.Id == _dropTarget.Id);
        _after = point.Y - HeaderHeight + _scroll.Snapshot.OffsetY > layout.Y + layout.Height / 2;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var source = _pressedRow; var target = _dropTarget;
        var apply = _dragging && source is not null && target is not null && _board is not null;
        var after = _after;
        ResetDrag(); e.Pointer.Capture(null);
        if (apply)
        {
            var moved = source!.IsProject ? SheetOperations.MoveProject(_board!, source.Id, target!.Project.Id, after)
                : SheetOperations.MoveTask(source.Project, source.Id, target!.Id, after);
            if (moved) Changed();
        }
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); ResetDrag(); }
    private void ResetDrag() { _pressedRow = null; _dropTarget = null; _dragging = false; _resizeColumn = -1; ClearValue(CursorProperty); InvalidateVisual(); }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ClearCellTip();
        if (_editor.IsVisible && _editorBounds.Contains(e.GetPosition(this))) return;
        CommitEdit(); _scroll.QueueDelta(new ScrollDelta(0, -e.Delta.Y * 60, ScrollInputKind.Wheel)); StartMotion(); e.Handled = true;
    }
    private void StartMotion() { if (_timer.IsEnabled) return; _clock.Restart(); _timer.Start(); }
    private void SyncBar()
    {
        _syncBar = true; _bar.Maximum = MaxScroll; _bar.ViewportSize = Math.Max(0, Bounds.Height - HeaderHeight);
        _bar.Value = _scroll.Snapshot.OffsetY; _syncBar = false;
    }

    public void BeginEdit(SheetRow row, int column = 1)
    {
        CommitEdit(); EditStarting?.Invoke(); _editingRow = row; _editingColumn = column;
        _editOriginal = column == 3 ? row.Task is null ? row.Project.ReadNotes() : row.Task.ReadComment()
            : row.Task?.ReadText() ?? row.Project.ReadName();
        _draftFlushed = false; _editor.Load(_editOriginal);
        _editor.IsVisible = true; InvalidateArrange(); InvalidateVisual();
        Dispatcher.UIThread.Post(() => _editor.FocusEditor(true));
    }
    public void CommitEdit()
    {
        if (!_editor.IsVisible || _editingRow is null) return;
        var row = _editingRow;
        var changed = _editor.HasChanges || _draftFlushed;
        if (changed)
        {
            var value = _editor.Snapshot();
            if (_editingColumn == 3)
            {
                if (row.Task is null) row.Project.NotesRich = value; else SetComment(row.Task, value);
            }
            else if (!string.IsNullOrWhiteSpace(value.Text))
            {
                if (row.Task is null) row.Project.NameRich = value; else row.Task.TextRich = value;
            }
        }
        _editor.IsVisible = false; _editingRow = null; _editOriginal = null; _draftFlushed = false;
        if (changed) Changed(); else InvalidateVisual();
    }
    public void FlushDraft()
    {
        if (!_editor.IsVisible || !_editor.HasChanges || _editingRow is null) return;
        var row = _editingRow; var value = _editor.Snapshot();
        if (_editingColumn == 3) { if (row.Task is null) row.Project.NotesRich = value; else SetComment(row.Task, value); }
        else if (!string.IsNullOrWhiteSpace(value.Text)) { if (row.Task is null) row.Project.NameRich = value; else row.Task.TextRich = value; }
        _draftFlushed = true; _editor.MarkSaved(); DataChanged?.Invoke();
    }
    public void CancelEdit()
    {
        if (_draftFlushed && _editingRow is { } row && _editOriginal is { } original)
        {
            if (_editingColumn == 3) { if (row.Task is null) row.Project.NotesRich = original; else SetComment(row.Task, original); }
            else { if (row.Task is null) row.Project.NameRich = original; else row.Task.TextRich = original; }
        }
        var changed = _draftFlushed;
        _editor.IsVisible = false; _editingRow = null; _editOriginal = null; _draftFlushed = false;
        Focus(); if (changed) Changed(); else InvalidateVisual();
    }
    private void Changed() { Refresh(); DataChanged?.Invoke(); SelectionChanged?.Invoke(_selection); }
    private static void SetComment(TaskRecord task, RichDocument value) { task.CommentRich = value; task.Comment = value.Text; }

    private void UpdateCellTip(Point point)
    {
        var row = Hit(point); var column = ColumnAt(point);
        if (_hoverCell == (row?.Id, column)) return;
        ClearCellTip(); _hoverCell = (row?.Id, column);
        var text = column switch
        {
            0 when row?.Task is { } task => task.IsCompleted ? "Đã hoàn thành" : "Chưa hoàn thành",
            1 => row?.Title,
            2 when row?.IsProject == true => row.Project.Progress + (row.Project.Next is { } next ? "\nNext: " + next.DisplayText : ""),
            3 => row?.Comment,
            _ => null
        };
        if (!string.IsNullOrEmpty(text))
        {
            ToolTip.SetTip(this, new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = text, MaxWidth = 520, TextWrapping = TextWrapping.Wrap } });
            // One drawing surface contains many cells, so each cell needs its own hover delay.
            _tipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolTip.GetShowDelay(this)));
            _tipTimer.Start();
        }
    }
    private void ClearCellTip() { _tipTimer.Stop(); ToolTip.SetIsOpen(this, false); ToolTip.SetTip(this, null); _hoverCell = (null, -1); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); ClearCellTip(); if (_resizeColumn < 0) ClearValue(CursorProperty); }

    private void OpenRowMenu(SheetRow row)
    {
        var menu = new ContextMenu();
        void Add(string title, Action action, bool enabled = true)
        { var item = new MenuItem { Header = title, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        if (row.IsProject)
        {
            Add("↑  Ưu tiên thực hiện trước", () =>
            {
                if (_board is not null && SheetOperations.MoveProject(_board, row.Id, _board.Projects[0].Id, false))
                { _scroll.Reset(); Changed(); SelectProject(row.Project); }
            }, _board?.Projects.FirstOrDefault()?.Id != row.Id);
            Add(row.Project.IsExpanded ? "Thu gọn dự án" : "Mở rộng dự án", () => { row.Project.IsExpanded = !row.Project.IsExpanded; Changed(); });
        }
        else Add(row.Task!.IsCompleted ? "Đánh dấu chưa xong" : "Đánh dấu đã xong", () => { row.Task.IsCompleted = !row.Task.IsCompleted; Changed(); });
        Add("Sửa tên / định dạng chữ", () => BeginEdit(row));
        Add("Sửa ghi chú", () => BeginEdit(row, 3));
        menu.Items.Add(new Separator()); Add(row.IsProject ? "Xóa dự án…" : "Xóa công việc…", () => DeleteRequested?.Invoke(row));
        menu.Open(this);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (_editor.IsVisible || e.Handled) return;
        switch (e.Key)
        {
            case Key.Escape: ResetDrag(); break;
            case Key.Down: MoveSelection(1); break;
            case Key.Up: MoveSelection(-1); break;
            case Key.F2: case Key.Enter: if (_selection is not null) BeginEdit(_selection); break;
            case Key.Space: if (_selection is { Task: { } task }) { task.IsCompleted = !task.IsCompleted; Changed(); } break;
            case Key.Left: case Key.Right:
                if (_selection is not null) { _selection.Project.IsExpanded = e.Key == Key.Right; Changed(); } break;
            case Key.Delete: if (_selection is not null) DeleteRequested?.Invoke(_selection); break;
            default: return;
        }
        e.Handled = true;
    }
    private void MoveSelection(int delta)
    {
        if (_layout.Count == 0) return;
        var index = _layout.FindIndex(x => x.Row.Id == _selection?.Id);
        Select(_layout[Math.Clamp(index + delta, 0, _layout.Count - 1)].Row); EnsureSelectedVisible();
    }
    private void EnsureSelectedVisible()
    {
        var row = _layout.FirstOrDefault(x => x.Row.Id == _selection?.Id); if (row is null) return;
        var offset = _scroll.Snapshot.OffsetY;
        if (row.Y < offset) offset = row.Y;
        else if (row.Y + row.Height > offset + Bounds.Height - HeaderHeight) offset = row.Y + row.Height - Bounds.Height + HeaderHeight;
        _scroll.ScrollTo(0, Math.Clamp(offset, 0, MaxScroll), false); SyncBar(); InvalidateVisual();
    }
    private sealed record LayoutRow(SheetRow Row, double Y, double Height, TextLayout Title, TextLayout Progress, TextLayout Comment);
}
