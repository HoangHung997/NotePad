using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private readonly Button _composerAdd = AppIcon.Button(IconKind.Plus, "Đính kèm, ngữ cảnh và gợi ý yêu cầu");
    private readonly Popup _mentionPopup = new() { Name = "ChatMentionPopup", Placement = PlacementMode.TopEdgeAlignedLeft, IsLightDismissEnabled = true };
    private readonly ListBox _mentionList = new() { Name = "ChatMentionList", MaxHeight = 200 };
    private ContextMenu? _composerMenu;
    private Grid? _composerRow;
    private bool _composerInputInitialized;
    private bool _mentionRefreshQueued;
    private string? _dismissedMentionText;
    private AiChatScope? _composerKnownScope;
    private AiConversation? _composerKnownConversation;
    private long _composerScopeVersion;
    private ComposerTarget? _mentionTarget;
    private Task _composerPaste = Task.CompletedTask;

    private Control BuildComposer()
    {
        if (_composerRow is not null) return _composerRow;
        _composerAdd.Name = "ChatAttach";
        _composerAdd.Width = _composerAdd.Height = _composerAdd.MinHeight = 32;
        _composerAdd.Padding = new Thickness(5);
        _composerAdd.Margin = new Thickness(0);
        _composerAdd.VerticalAlignment = VerticalAlignment.Center;
        _composerAdd.Classes.Add("quiet");
        _composerAdd.Foreground = RichEditor.Brush("#A4573D");
        _composerAdd.Click += (_, _) => OpenComposerMenu();
        _composer.InnerLeftContent = null;
        _composer.CornerRadius = new CornerRadius(14);
        _composer.Background = Brushes.Transparent;
        _composer.BorderThickness = new Thickness(0);
        _composer.Padding = new Thickness(5, 5, 5, 8);
        _composer.MinHeight = 76;
        _composer.MaxHeight = 180;
        _composer.FontSize = 14;
        ToolTip.SetTip(_composer, "Ctrl+V để dán ảnh hoặc văn bản; thả tệp/file:// trên máy. Gõ @ để chọn ngữ cảnh hoặc gợi ý. Ctrl+Enter để gửi.");
        var options = BuildComposerOptions();
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), Children = { _composer, options } };
        Grid.SetRow(options, 1);
        var surface = new Border
        {
            Name = "ChatComposerSurface", CornerRadius = new CornerRadius(22), Padding = new Thickness(10, 9, 10, 8),
            Background = RichEditor.Brush("#FFFCF8"), BorderBrush = RichEditor.Brush("#E4DBD2"), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.Parse("#0A5C4634") }), Child = body
        };
        _composerRow = new Grid { Name = "ChatComposerRow", Children = { surface, _mentionPopup } };
        _mentionPopup.PlacementTarget = _composer;
        _mentionList.ItemTemplate = new FuncDataTemplate<ComposerAction>((action, _) => new StackPanel
        {
            Spacing = 2, Margin = new Thickness(4, 5), Children =
            {
                new TextBlock { Text = action?.Label, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = action?.Detail, FontSize = 10, Foreground = RichEditor.Brush("#796C62"), TextWrapping = TextWrapping.Wrap }
            }
        });
        _mentionPopup.Child = new Border
        {
            Background = RichEditor.Brush("#FFFCF8"), BorderBrush = RichEditor.Brush("#D9CFC5"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = new StackPanel
            {
                Spacing = 4, Children =
                {
                    new TextBlock { Text = "@ Ngữ cảnh và gợi ý", FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Thickness(5, 3) },
                    _mentionList,
                    new TextBlock { Text = "↑↓ chọn · Enter/Tab chèn · Esc đóng\nChưa gửi AI, chưa mở hay lưu tệp.", FontSize = 10,
                        Foreground = RichEditor.Brush("#796C62"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 2) }
                }
            }
        };
        _mentionList.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && _mentionList.SelectedItem is ComposerAction action && _mentionTarget is { } target)
            { e.Handled = true; await ChooseComposerAction(action, target, true); }
        };
        _mentionPopup.Closed += (_, _) => _dismissedMentionText = _composer.Text;
        return _composerRow;
    }

    private void InitializeComposerInput()
    {
        if (_composerInputInitialized) return;
        _composerInputInitialized = true;
        _composer.PastingFromClipboard += (_, e) =>
        {
            // Handled must be set before the first await or TextBox also pastes its own copy.
            if (_scope is null || _composer.IsReadOnly) return;
            e.Handled = true;
            var target = CaptureComposerTarget();
            _composerPaste = PasteComposerClipboard(_composerPaste, target, CaptureComposerInsertion());
        };
        _composer.AddHandler(KeyDownEvent, ComposerKeyDown, RoutingStrategies.Tunnel);
        _composer.GotFocus += (_, _) => QueueMentionRefresh();
        _composer.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty && e.Property != TextBox.CaretIndexProperty
                && e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty) return;
            if (_loading) { _mentionPopup.IsOpen = false; return; }
            QueueMentionRefresh();
        };
        _markerMode.IsCheckedChanged += (_, _) => RefreshComposerPlaceholder();
        var menu = new ContextMenu();
        void Add(string name, Action action)
        { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Cắt", _composer.Cut); Add("Sao chép", _composer.Copy);
        Add("Dán ảnh / tệp / văn bản", _composer.Paste);
        menu.Items.Add(new Separator());
        Add("Chọn tất cả", _composer.SelectAll); Add("Hoàn tác", _composer.Undo); Add("Làm lại", _composer.Redo);
        _composer.ContextMenu = menu;
        var dropTarget = (Control?)_composerRow ?? _composer;
        DragDrop.SetAllowDrop(dropTarget, true);
        dropTarget.AddHandler(DragDrop.DragOverEvent, ComposerDragOver, RoutingStrategies.Bubble);
        dropTarget.AddHandler(DragDrop.DropEvent, ComposerDrop, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => { _mentionPopup.IsOpen = false; _composerMenu?.Close(); };
        RefreshComposerState(); RefreshComposerPlaceholder();
    }

    private void RefreshComposerPlaceholder() => _composer.PlaceholderText = _markerMode.IsChecked == true
        ? "Ghi mốc công việc, không gửi AI..." : "Hỏi AI... @ ngữ cảnh";

    // Called by RenderAttachments for every scope/conversation load, including empty drafts.
    private void RefreshComposerState()
    {
        if (_scope != _composerKnownScope || _conversation != _composerKnownConversation)
        {
            _composerKnownScope = _scope; _composerKnownConversation = _conversation; _composerScopeVersion++;
            _mentionPopup.IsOpen = false; _composerMenu?.Close(); _mentionTarget = null;
            _permissionMenu?.Close(); _profiles.IsDropDownOpen = _reasoningEffort.IsDropDownOpen = false;
        }
        _composerAdd.IsEnabled = _scope is not null;
        RefreshComposerOptions();
    }

    private ComposerInsertion CaptureComposerInsertion() => new(_composer.Text ?? "", _composer.SelectionStart, _composer.SelectionEnd);

    private sealed record ComposerInsertion(string Text, int Start, int End);

    private async Task PasteComposerClipboard(Task previous, ComposerTarget target, ComposerInsertion insertion)
    {
        try
        {
            await previous;
            if (!IsCurrentComposerTarget(target) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            using var data = await clipboard.TryGetDataAsync();
            if (!IsCurrentComposerTarget(target) || data is null) return;
            await PasteComposerDataAt(target, data, insertion);
        }
        catch (Exception ex) when (IsComposerInputException(ex)) { ReportComposerInputError(target, ex); }
    }

    private Task PasteComposerData(ComposerTarget target, IAsyncDataTransfer data)
        => PasteComposerDataAt(target, data, CaptureComposerInsertion());

    private async Task PasteComposerDataAt(ComposerTarget target, IAsyncDataTransfer data, ComposerInsertion insertion)
    {
        if (!IsCurrentComposerTarget(target)) return;
        if (data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap))
        {
            await ImportComposerAttachments(target, async () =>
            {
                if (data.Contains(DataFormat.File))
                {
                    var files = await data.TryGetFilesAsync();
                    if (!IsCurrentComposerTarget(target)) return [];
                    if (files is not { Length: > 0 }) throw new InvalidDataException("Clipboard không có tệp trên máy đọc được.");
                    return await ReadComposerFiles(target, files.Select(f => f.TryGetLocalPath()
                        ?? throw new InvalidDataException("Không tự tải tệp từ xa; hãy tải về máy trước.")).ToArray());
                }
                using var bitmap = await data.TryGetBitmapAsync();
                if (!IsCurrentComposerTarget(target)) return [];
                if (bitmap is null) throw new InvalidDataException("Không đọc được ảnh trong clipboard; thử chụp hoặc sao chép lại.");
                AiComposerInputData.CheckBitmapSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                var name = "Anh-dan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                var image = await Task.Run(() =>
                {
                    using var bytes = new AiComposerInputData.LimitedAttachmentStream();
                    bitmap.Save(bytes, new PngBitmapEncoderOptions());
                    return AiDocuments.Read(name, bytes.ToArray());
                });
                return IsCurrentComposerTarget(target) ? [image] : [];
            });
            return;
        }
        var text = await data.TryGetTextAsync();
        if (!IsCurrentComposerTarget(target) || string.IsNullOrEmpty(text) || _composer.IsReadOnly) return;
        if (CaptureComposerInsertion() != insertion)
        {
            _status.Text = "Bản nháp hoặc vùng chọn đã đổi khi đọc clipboard. Chưa dán nội dung; bấm Ctrl+V để dán tại vị trí mới.";
            return;
        }
        // Use the text editing API to preserve selection/undo. URLs remain text, never network requests.
        _composer.SelectedText = text;
    }

    private void ComposerDragOver(object? sender, DragEventArgs e)
    {
        var supported = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text);
        e.DragEffects = _scope is not null && !_preparing && _request is null && supported ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ComposerDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true; e.DragEffects = DragDropEffects.None;
        if (_scope is null || _preparing || _request is not null) return;
        var target = CaptureComposerTarget();
        try
        {
            // Native drop data is only valid during this event: take paths/text before awaiting.
            var files = e.DataTransfer.TryGetFiles();
            var text = files is { Length: > 0 } ? null : e.DataTransfer.TryGetText();
            var paths = files is { Length: > 0 }
                ? files.Select(f => f.TryGetLocalPath() ?? throw new InvalidDataException("Chỉ thả tệp trên máy.")).ToArray()
                : AiComposerInputData.LocalPathsFromText(text);
            if (paths is { Length: > 0 })
            {
                e.DragEffects = DragDropEffects.Copy;
                await ImportComposerAttachments(target, () => ReadComposerFiles(target, paths));
            }
            else if (!string.IsNullOrEmpty(text) && IsCurrentComposerTarget(target) && !_composer.IsReadOnly)
            {
                _composer.SelectedText = text; e.DragEffects = DragDropEffects.Copy;
                _status.Text = "Đã chèn văn bản/liên kết. Không tự tải URL hoặc đọc tệp từ xa.";
            }
        }
        catch (Exception ex) when (IsComposerInputException(ex)) { ReportComposerInputError(target, ex); }
    }

    private IEnumerable<ComposerAction> ComposerActions()
    {
        yield return new("image", "Đính kèm ảnh...", "Chọn PNG, JPG, WEBP trên máy", "anh image screenshot paste", "Đính kèm");
        yield return new("file", "Đính kèm tệp...", "Word, Excel, TXT, MD, CSV, JSON; tối đa 4 tệp", "tep file attach document", "Đính kèm");
        yield return new("preview", "Xem dữ liệu gửi", "Xem trước, không gửi AI", "preview context du lieu", "Ngữ cảnh");
        yield return new("marker", "Chỉ lưu mốc, không hỏi AI", _markerMode.IsChecked == true
            ? "Đang bật · chọn để trở lại hỏi AI" : "Đang tắt · chọn để lưu mốc riêng tư, không gọi AI", "moc marker timeline rieng tu context ngu canh", "Ngữ cảnh");
        if (_scope?.Project is not null)
        {
            yield return new("project", "Dự án · " + _scope.Title, "Kèm toàn bộ dự án hiện tại", "du an project context", "Ngữ cảnh",
                "@[Dự án hiện tại] Dựa trên dữ liệu dự án đang chọn, ", true);
            yield return new("tasks", "Công việc dự án", "Tập trung công việc; vẫn kèm toàn bộ dự án", "cong viec task checklist next", "Ngữ cảnh",
                "@[Công việc dự án] Phân tích các việc đã xong, chưa xong và việc tiếp theo; ", true);
            yield return new("notes", "Ghi chú dự án", "Tập trung ghi chú; vẫn kèm toàn bộ dự án", "ghi chu note context", "Ngữ cảnh",
                "@[Ghi chú dự án] Dựa trên ghi chú hiện tại, ", true);
            yield return new("history", "Lịch sử dự án", "Kèm các trao đổi khác, không kèm mốc riêng tư/bản nháp", "lich su history chat context", "Ngữ cảnh",
                "@[Lịch sử dự án] Đối chiếu các trao đổi đã lưu với dữ liệu hiện tại; ", true, true);
        }
        if (SelectedPermissionMode != AiPermissionMode.ReadOnly)
        {
            yield return new("word", "Bản nháp Word .docx", "AI đề xuất nội dung; bạn xem và bấm Lưu tệp", "word docx document bao cao", "Yêu cầu bản nháp",
                "@[Bản nháp Word] Hãy soạn tệp .docx theo định dạng h2-file để tôi xem và tự lưu. Nội dung cần: ");
            yield return new("excel", "Bản nháp Excel .xlsx", "Bảng dữ liệu tĩnh, không chạy macro/công thức", "excel xlsx spreadsheet bang tinh", "Yêu cầu bản nháp",
                "@[Bản nháp Excel] Hãy soạn tệp .xlsx theo định dạng h2-file, các ô là giá trị tĩnh, để tôi xem và tự lưu. Các cột cần: ");
            yield return new("csv", "Bản nháp bảng CSV", "AI đề xuất bảng, chỉ lưu khi bạn chọn", "csv bang table", "Yêu cầu bản nháp",
                "@[Bản nháp CSV] Hãy soạn bảng .csv theo định dạng h2-file để tôi xem và tự lưu. Yêu cầu: ");
            yield return new("word-layout", "Word giữ bố cục từ PDF/ảnh…", "MinerU local · chữ sửa được, hình gốc · đối chiếu trước khi lưu", "word docx pdf anh layout bo cuc font", "Yêu cầu bản nháp");
        }
        yield return new("compare", "Đối chiếu các tệp đã chọn", "Chỉ dùng nội dung đã đính kèm, không tự tìm tệp", "compare doi chieu so sanh", "Yêu cầu bản nháp",
            "@[Đối chiếu tệp] So sánh nội dung các tệp đã đính kèm, nêu khác biệt và phần thiếu dữ liệu. Tập trung: ");
        var attachments = (_conversation?.DraftAttachments ?? []).Concat((_conversation?.Messages ?? [])
            .Where(m => !m.IsTimelineMarker && m.Status == "complete" && m.Role is "user" or "assistant").SelectMany(m => m.Attachments));
        foreach (var file in attachments.DistinctBy(a => a.Id))
            yield return new("reference-" + file.Id, "Tệp · " + file.Name, "Nhắc tới tệp đã kèm trong trao đổi, không tự mở tệp", file.Name + " file tep", "Tệp trong trao đổi",
                "@[Tệp " + System.Text.Json.JsonSerializer.Serialize(file.Name, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "] ");
    }

    private void OpenComposerMenu()
    {
        if (_scope is null) return;
        var target = CaptureComposerTarget(); _mentionPopup.IsOpen = false;
        _composerMenu = new ContextMenu();
        MenuItem Item(ComposerAction action)
        {
            var item = new MenuItem
            {
                Header = action.Label,
                Icon = action.Id is "image" or "file" ? new AppIcon(action.Id == "image" ? IconKind.Link : IconKind.Document) : null,
                ToggleType = action.Id == "marker" ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
                IsChecked = action.Id == "marker" && _markerMode.IsChecked == true,
                IsEnabled = action.Id is not ("file" or "image" or "marker") || !ComposerOptionsBusy
            };
            ToolTip.SetTip(item, action.Detail);
            item.Click += async (_, _) => await ChooseComposerAction(action, target, false);
            return item;
        }
        foreach (var group in ComposerActions().GroupBy(a => a.Group))
        {
            if (group.Key == "Đính kèm")
            {
                foreach (var action in group) _composerMenu.Items.Add(Item(action));
                _composerMenu.Items.Add(new Separator());
                continue;
            }
            var category = new MenuItem { Header = group.Key };
            foreach (var action in group) category.Items.Add(Item(action));
            _composerMenu.Items.Add(category);
        }
        if (IsComposerCompact)
        {
            _composerMenu.Items.Add(new Separator());
            var history = new MenuItem { Header = "Lịch sử trao đổi", IsEnabled = !ComposerOptionsBusy };
            foreach (var conversation in target.Scope.Conversations)
            {
                var item = new MenuItem { Header = conversation.Title, ToggleType = MenuItemToggleType.Radio, IsChecked = conversation == _conversation };
                item.Click += (_, _) =>
                {
                    if (!IsCurrentComposerTarget(target) || ComposerOptionsBusy) return;
                    _history.SelectedItem = (_history.ItemsSource?.Cast<ConversationItem>() ?? []).FirstOrDefault(c => c.Value == conversation);
                };
                history.Items.Add(item);
            }
            _composerMenu.Items.Add(history);
            var start = new MenuItem { Header = "Trao đổi mới", Icon = new AppIcon(IconKind.Plus), IsEnabled = !ComposerOptionsBusy };
            start.Click += (_, _) => { if (IsCurrentComposerTarget(target) && !ComposerOptionsBusy) StartNewConversation(); };
            _composerMenu.Items.Add(start);
            if (_scope.Project is not null)
            {
                void ContextToggle(CheckBox source, string label)
                {
                    var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, IsChecked = source.IsChecked == true,
                        IsEnabled = !ComposerOptionsBusy };
                    item.Click += (_, _) => { if (IsCurrentComposerTarget(target) && !ComposerOptionsBusy) source.IsChecked = source.IsChecked != true; };
                    _composerMenu.Items.Add(item);
                }
                ContextToggle(_includeProject, "Kèm toàn bộ dự án");
                ContextToggle(_includeHistory, "Kèm các trao đổi khác của dự án");
            }
        }
        _composerMenu.Open(_composerAdd);
    }

    private void QueueMentionRefresh()
    {
        if (_mentionRefreshQueued) return;
        _mentionRefreshQueued = true;
        Dispatcher.UIThread.Post(() => { _mentionRefreshQueued = false; UpdateComposerMentions(); });
    }

    private void UpdateComposerMentions()
    {
        if (_scope is null || _loading || !_composer.IsFocused || _composer.Text == _dismissedMentionText
            || AiComposerInputData.FindMention(_composer.Text, _composer.CaretIndex, _composer.SelectionStart, _composer.SelectionEnd) is not { } mention)
        { _mentionPopup.IsOpen = false; return; }
        var query = AiComposerInputData.SearchKey(mention.Query);
        var actions = ComposerActions().Where(a => AiComposerInputData.SearchKey(a.Label + " " + a.Keywords).Contains(query, StringComparison.Ordinal)).ToArray();
        if (actions.Length == 0) { _mentionPopup.IsOpen = false; return; }
        var selected = (_mentionList.SelectedItem as ComposerAction)?.Id;
        _mentionTarget = CaptureComposerTarget();
        _mentionList.ItemsSource = actions;
        _mentionList.SelectedIndex = Math.Max(0, Array.FindIndex(actions, a => a.Id == selected));
        _mentionPopup.Width = Math.Clamp(_composer.Bounds.Width, 180, 330);
        _mentionList.MaxHeight = Math.Clamp(Bounds.Height * .4, 90, 200);
        _mentionPopup.IsOpen = true;
    }

    private async void ComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_mentionPopup.IsOpen)
        {
            // Handle before TextBox consumes Enter; choosing a mention still takes precedence.
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { e.Handled = true; await SendOrSave(); }
            return;
        }
        if (e.Key == Key.Escape) { e.Handled = true; _mentionPopup.IsOpen = false; return; }
        if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            var count = _mentionList.ItemCount;
            if (count > 0) { _mentionList.SelectedIndex = (_mentionList.SelectedIndex + (e.Key == Key.Down ? 1 : count - 1)) % count; _mentionList.ScrollIntoView(_mentionList.SelectedItem!); }
        }
        else if (e.Key is Key.Enter or Key.Tab && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Control))
        {
            e.Handled = true;
            if (_mentionList.SelectedItem is ComposerAction action && _mentionTarget is { } target)
                await ChooseComposerAction(action, target, true);
        }
    }

    private async Task ChooseComposerAction(ComposerAction action, ComposerTarget target, bool fromMention)
    {
        if (!IsCurrentComposerTarget(target)) { _mentionPopup.IsOpen = false; return; }
        if (action.Id == "marker" && ComposerOptionsBusy) return;
        var mention = fromMention ? AiComposerInputData.FindMention(_composer.Text, _composer.CaretIndex, _composer.SelectionStart, _composer.SelectionEnd) : null;
        if (fromMention && mention is null) return;
        _mentionPopup.IsOpen = false; _composerMenu?.Close();
        if (mention is { } range) { _composer.SelectionStart = range.Start; _composer.SelectionEnd = range.Start + range.Length; }
        if (action.Prompt is not null)
        {
            if (action.Project) _includeProject.IsChecked = true;
            if (action.History) _includeHistory.IsChecked = true;
            _composer.SelectedText = action.Prompt;
        }
        else
        {
            if (mention is not null) _composer.SelectedText = "";
            if (action.Id == "image") await PickAttachments(true);
            else if (action.Id == "word-layout") await ExportLayout();
            else if (action.Id == "file") await PickAttachments();
            else if (action.Id == "preview") await PreviewComposerContext();
            else if (action.Id == "marker") _markerMode.IsChecked = _markerMode.IsChecked != true;
        }
        if (IsCurrentComposerTarget(target)) _composer.Focus();
    }

    private sealed record ComposerAction(string Id, string Label, string Detail, string Keywords, string Group,
        string? Prompt = null, bool Project = false, bool History = false);
}
