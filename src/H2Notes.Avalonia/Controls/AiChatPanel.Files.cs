using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private readonly StackPanel _draftFiles = new() { Name = "ChatDraftFiles", Spacing = 3 };
    private async Task PreviewComposerContext()
    {
        if (_scope is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var scope = _scope; var conversation = _conversation;
        try
        {
            var context = BuildProjectContext();
            if (scope != _scope || conversation != _conversation) return;
            var turns = AiProjectContext.Prepare(conversation ?? new AiConversation(),
                new AiMessage { Content = _composer.Text ?? "", Attachments = conversation?.DraftAttachments.ToList() ?? [] }, context);
            var description = "Dự án / phạm vi: " + scope.Title + "\n"
                + (_markerMode.IsChecked == true ? "Đang chỉ lưu mốc: không gửi AI. Đây là bản xem nếu chuyển sang hỏi AI.\n\n" : "\n")
                + RequestPreview(_profiles.SelectedItem as AiProfile, turns);
            await TextPreview(owner, "Dữ liệu sẽ gửi cho AI", description, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or UriFormatException)
        { if (scope == _scope && conversation == _conversation) _status.Text = ex.Message; }
    }

    private string BuildProjectContext()
    {
        PrepareProjectContext?.Invoke();
        return _includeProject.IsChecked == true && _scope?.Project is { } p
            ? AiProjectContext.Build(p, _conversation?.Id, _includeHistory.IsChecked == true) : "";
    }

    private Task PickAttachments() => PickAttachments(false);

    private async Task PickAttachments(bool imagesOnly)
    {
        if (_preparing || _request is not null || _scope is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var target = CaptureComposerTarget();
        await ImportComposerAttachments(target, async () =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = (imagesOnly ? "Đính kèm ảnh vào " : "Đính kèm tệp vào ") + target.Scope.Title, AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(imagesOnly ? "Ảnh" : "Ảnh và tài liệu")
                    { Patterns = (imagesOnly ? [".png", ".jpg", ".jpeg", ".webp"] : AiDocuments.Extensions).Select(e => "*" + e).ToArray() }]
            });
            if (!IsCurrentComposerTarget(target) || files.Count == 0) return [];
            var paths = files.Select(f => f.TryGetLocalPath() ?? throw new InvalidDataException("Chỉ nhận tệp trên máy; hãy tải tệp về trước.")).ToArray();
            return await ReadComposerFiles(target, paths);
        });
    }

    private async Task<AiAttachment[]> ReadComposerFiles(ComposerTarget target, IReadOnlyList<string> paths)
    {
        if (!IsCurrentComposerTarget(target)) return [];
        AiComposerInputData.CheckCount(target.Conversation.DraftAttachments.Count, paths.Count);
        var localPaths = paths.Select(AiComposerInputData.LocalPath).ToArray();
        var result = await Task.Run(() => localPaths.Select(AiComposerInputData.ReadLocalFile).ToArray());
        return IsCurrentComposerTarget(target) ? result : [];
    }

    private async Task ImportComposerAttachments(ComposerTarget target, Func<Task<AiAttachment[]>> read)
    {
        if (!IsCurrentComposerTarget(target)) return;
        if (_preparing || _request is not null)
        { _status.Text = "Chờ thao tác hiện tại xong hoặc dừng AI trước khi đính kèm."; return; }
        _preparing = true;
        try
        {
            AiComposerInputData.CheckCount(target.Conversation.DraftAttachments.Count, 1);
            _status.Text = "Đang đọc tệp trên máy...";
            var result = await read();
            if (!IsCurrentComposerTarget(target)) return;
            if (result.Length == 0) { UpdateConnectionStatus(); return; }
            AiComposerInputData.CheckBudget(target.Scope.Conversations, target.Conversation, result);
            target.Conversation.DraftAttachments.AddRange(result);
            _app.SaveChatDraft(target.Scope, target.Conversation);
            Touch(target.Scope); RenderAttachments();
            _status.Text = "Đã đính kèm: " + string.Join(", ", result.Select(a => a.Name))
                + ". Bấm Gửi mới gửi AI; ảnh cần model đọc ảnh.";
        }
        catch (Exception ex) when (IsComposerInputException(ex))
        { if (IsCurrentComposerTarget(target)) _status.Text = "Không đính kèm được: " + ex.Message; }
        finally { _preparing = false; }
    }

    private static bool IsComposerInputException(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or System.Xml.XmlException
        or System.Runtime.InteropServices.ExternalException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException;

    private void ReportComposerInputError(ComposerTarget target, Exception ex)
    {
        if (IsCurrentComposerTarget(target)) _status.Text = "Chưa đọc được nội dung dán/thả: " + ex.Message;
    }

    private ComposerTarget CaptureComposerTarget()
    {
        var conversation = EnsureConversation();
        RefreshComposerState();
        return new(_scope!, conversation, _composerScopeVersion, conversation.Messages.Count);
    }

    private bool IsCurrentComposerTarget(ComposerTarget target) => target.Scope == _scope
        && target.Conversation == _conversation && target.Version == _composerScopeVersion
        && target.Conversation.Messages.Count == target.MessageCount
        && target.Scope.Conversations.Contains(target.Conversation);

    private sealed record ComposerTarget(AiChatScope Scope, AiConversation Conversation, long Version, int MessageCount);

    private void RenderAttachments()
    {
        RefreshComposerState();
        _draftFiles.Children.Clear();
        foreach (var file in _conversation?.DraftAttachments ?? [])
        {
            var scope = _scope; var conversation = _conversation;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new Button { Content = new TextBlock { Text = file.Name + $" · {file.Data.Length / 1024d:0.#} KB", TextTrimming = TextTrimming.CharacterEllipsis }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3) };
            ToolTip.SetTip(label, file.Name + "\n" + file.Notice);
            label.Click += async (_, _) => { if (TopLevel.GetTopLevel(this) is Window owner) await PreviewAttachment(owner, file); };
            var remove = AppIcon.Button(IconKind.Close, "Bỏ tệp đính kèm"); remove.Padding = new Thickness(5); remove.Width = 28;
            remove.Click += (_, _) =>
            {
                if (scope != _scope || conversation != _conversation || conversation is null || scope is null) return;
                conversation.DraftAttachments.Remove(file);
                _app.SaveChatDraft(scope, conversation);
                Touch(scope); RenderAttachments();
            };
            row.Children.Add(label); row.Children.Add(remove); Grid.SetColumn(remove, 1); _draftFiles.Children.Add(row);
        }
    }

    private void AddFileCards(ChatMessageView bubble, AiMessage message)
    {
        var scope = _scope;
        foreach (var file in message.Attachments)
        {
            var preview = new Button { Content = AppIcon.Label(IconKind.Document, file.Name, 14), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(preview, file.Notice);
            preview.Click += async (_, _) => { if (TopLevel.GetTopLevel(this) is Window owner) await PreviewAttachment(owner, file); };
            bubble.Actions.Children.Add(preview);
        }
        if (message.Role != "assistant" || message.Status != "complete") return;
        try
        {
            foreach (var file in AiArtifacts.Parse(message.Content))
            {
                bubble.Body.Text = AiArtifacts.WithoutBlocks(message.Content);
                var preview = new Button { Content = AppIcon.Label(IconKind.Document, "Bản nháp · " + file.FileName, 14), FontSize = 11 };
                preview.Click += async (_, _) => { if (TopLevel.GetTopLevel(this) is Window owner) await TextPreview(owner, "Xem bản nháp tệp", AiArtifacts.Preview(file), false); };
                var save = new Button { Content = file.LayoutSourceId is null ? "Lưu tệp…" : "Dựng Word giữ bố cục…", FontSize = 11, Name = "ChatSaveArtifact" };
                save.IsEnabled = CurrentPermission != AiPermissionMode.ReadOnly;
                save.Click += async (_, _) =>
                {
                    if (CurrentPermission == AiPermissionMode.ReadOnly || scope != _scope || TopLevel.GetTopLevel(this) is not Window owner) return;
                    save.IsEnabled = false;
                    try
                    {
                        if (file.LayoutSourceId is { } sourceId)
                        {
                            var source = _conversation?.Messages.Where(m => m.Role == "user" && !m.IsTimelineMarker)
                                .SelectMany(m => m.Attachments).FirstOrDefault(a => a.Id == sourceId);
                            if (source is null) throw new IOException("Không tìm được ID tệp nguồn trong trao đổi này. Đính kèm lại PDF/ảnh hoặc chọn Word giữ bố cục trong + / @.");
                            await ExportLayout(source, message, file.FileName); return;
                        }
                        var bytes = await Task.Run(() => AiArtifacts.Create(file));
                        if (await SaveBytes(owner, file.FileName, bytes) is { } path)
                        {
                            message.SavedFiles.Add(new(file.FileName, path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), DateTime.UtcNow));
                            Touch(scope); save.Content = "Đã lưu · Lưu bản khác…";
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
                    { await Dialogs.Message(owner, "Chưa lưu được", ex.Message); }
                    finally { save.IsEnabled = true; }
                };
                bubble.Actions.Children.Add(new Border { Padding = new Thickness(6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    BorderBrush = RichEditor.Brush("#E7CCBA"), Child = new StackPanel { Spacing = 4, Children = { preview, save } } });
            }
            foreach (var saved in message.SavedFiles)
                bubble.Actions.Children.Add(new TextBlock { Text = "Đã lưu " + saved.SavedAt.ToLocalTime().ToString("HH:mm dd/MM/yyyy") + " · " + saved.Name,
                    FontSize = 10, TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { bubble.Actions.Children.Add(new TextBlock { Text = "Bản nháp tệp chưa đúng cấu trúc. Yêu cầu AI tạo lại; chưa ghi tệp nào.", FontSize = 11, TextWrapping = TextWrapping.Wrap }); }
    }

    private static string RequestPreview(AiProfile? profile, IReadOnlyList<AiTurn> turns) =>
        (profile is null ? "Chưa chọn kết nối" : profile.ProcessingLocation + " · " + new Uri(profile.BaseUrl).Host + "\nModel: " + profile.Model)
        + "\n\nWord/Excel gửi chữ đã trích xuất, ảnh gửi nguyên ảnh. PDF theo Thiết lập AI: gửi gốc hoặc chuyển sang Markdown trước khi gửi. Bản xem này chưa chạy OCR. Các tệp liên kết chưa chọn không được đọc.\n"
        + "Mốc riêng tư và bản nháp các chat khác không gửi. Đây chỉ là bản xem trước; đóng để tiếp tục soạn. Chỉ nút Gửi ở ô soạn mới gửi AI.\n\n"
        + string.Join("\n\n", turns.Where(t => t.Role != "system").Select(t => "[" + t.Role + "]\n" + t.Content
            + string.Concat((t.Images ?? []).Select(i => $"\n[Ảnh {i.MimeType}, {i.Data.Length / 1024d:0.#} KB sẽ gửi cho model]"))
            + string.Concat((t.Files ?? []).Select(f => $"\n[PDF {f.Name}, {f.Data.Length / 1024d:0.#} KB; xử lý theo thiết lập PDF]"))));

    private static async Task<bool> TextPreview(Window owner, string title, string text, bool confirm)
    {
        var window = new Window { Title = title, Width = Math.Min(680, Math.Max(430, owner.Width)), Height = Math.Min(660, Math.Max(420, owner.Height)),
            ShowInTaskbar = false, CanMinimize = false, CanMaximize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = owner.Topmost };
        var close = new Button { Content = confirm ? "Hủy" : "Đóng", IsCancel = true, IsDefault = !confirm };
        var send = new Button { Content = "Gửi dữ liệu này", IsVisible = confirm };
        close.Click += (_, _) => window.Close(false); send.Click += (_, _) => window.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { close, send } };
        var content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(16), Children = { content, buttons } }; Grid.SetRow(buttons, 1); window.Content = root;
        return await window.ShowDialog<bool>(owner);
    }

    private static async Task PreviewAttachment(Window owner, AiAttachment file)
    {
        if (!file.IsImage) { await TextPreview(owner, file.Name, file.Notice + "\n\n" + file.Text, false); return; }
        try
        {
            using var bitmap = Bitmap.DecodeToWidth(new MemoryStream(file.Data), 1000);
            var close = new Button { Content = "Đóng", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
            var window = new Window { Title = file.Name, Width = 600, Height = 520, ShowInTaskbar = false, CanMinimize = false, CanMaximize = false, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            close.Click += (_, _) => window.Close();
            var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            if (file.HasImageOcr)
            {
                var ocr = new Button { Content = "Văn bản OCR · " + file.PdfEngine, Name = "AttachmentOcrPreview" };
                ocr.Click += async (_, _) => await TextPreview(window, "Văn bản OCR · " + file.Name, file.Notice + "\n\n" + file.Text, false);
                footer.Children.Add(ocr);
            }
            footer.Children.Add(close);
            root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform }); root.Children.Add(footer); Grid.SetRow(footer, 1); window.Content = root;
            await window.ShowDialog(owner);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        { await Dialogs.Message(owner, "Không xem được ảnh", "Ảnh không hợp lệ hoặc chưa được hỗ trợ."); }
    }

    private static async Task<string?> SaveBytes(Window owner, string name, byte[] bytes, Func<bool>? canWrite = null)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Lưu tệp từ AI", SuggestedFileName = name,
            DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true, FileTypeChoices = [new FilePickerFileType(extension) { Patterns = ["*" + extension] }] });
        if (file is null) return null;
        var path = file.TryGetLocalPath() ?? throw new IOException("Chỉ lưu vào đường dẫn trên máy.");
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new IOException("Đuôi tệp phải là " + extension);
        // Never overwrite an existing file silently, including a file that appeared after the picker.
        if (File.Exists(path) && !await Dialogs.Confirm(owner, "Thay tệp đã có?", path, "Ghi đè tệp này")) return null;
        if (canWrite is not null && !canWrite()) return null;
        await Task.Run(() => ProjectWorkspaceStore.AtomicWrite(path, bytes)); return path;
    }
}
