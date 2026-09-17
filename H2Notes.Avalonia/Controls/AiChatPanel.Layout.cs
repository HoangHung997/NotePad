using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private CancellationTokenSource? _layoutCancellation;
    private TaskCompletionSource? _layoutFinished;

    private async Task ExportLayout(AiAttachment? source = null, AiMessage? record = null, string? requestedName = null)
    {
        if (ComposerOptionsBusy || CurrentPermission == AiPermissionMode.ReadOnly || TopLevel.GetTopLevel(this) is not Window owner || _scope is null) return;
        var target = CaptureComposerTarget();
        try
        {
            if (source is null || !source.IsPdf && !source.IsImage)
            {
                if (source is not null)
                    await Dialogs.Message(owner, "Chọn lại tệp gốc", "Tệp trong lịch sử đã chuyển thành Markdown, không còn tọa độ chữ. Chọn lại đúng PDF/ảnh gốc; app kiểm tra mã nội dung trước khi dựng Word.");
                var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = "Chọn PDF/ảnh để dựng Word bằng MinerU", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("PDF và ảnh tài liệu") { Patterns = ["*.pdf", "*.png", "*.jpg", "*.jpeg", "*.webp"] }] });
                if (files.Count == 0 || !IsCurrentComposerTarget(target)) return;
                var path = files[0].TryGetLocalPath() ?? throw new IOException("Chỉ đọc tệp trên máy.");
                var original = await Task.Run(() => AiDocuments.Read(path));
                if (source?.SourceSha256 is { Length: > 0 } hash && !hash.Equals(original.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Tệp đã chọn không khớp tệp gốc trong lịch sử. Chưa tạo Word.");
                source = original;
            }
            if (!IsCurrentComposerTarget(target) || CurrentPermission == AiPermissionMode.ReadOnly) return;
            if (!await Dialogs.Confirm(owner, "Word giữ bố cục · Thử nghiệm", "Dùng MinerU đã cài để đọc PDF/ảnh trên máy, không gửi AI. Tối đa 10 trang. Chữ sửa được; bảng, dấu/chữ ký và vùng chưa nhận dạng giữ ảnh. Phông ước lượng và OCR có thể sai dấu/số. Bạn đối chiếu và sửa trước khi lưu. Không thay tệp gốc.", "Dựng bản thử")) return;
            if (!IsCurrentComposerTarget(target)) return;
            _preparing = true; RefreshComposerOptions();
            using var cancel = new CancellationTokenSource(); _layoutCancellation = cancel;
            _layoutFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var status = new TextBlock { Text = "Đang chuẩn bị bố cục…", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
            var stop = new Button { Content = "Dừng", HorizontalAlignment = HorizontalAlignment.Right };
            var progress = new Window { Title = "Đang dựng Word trên máy", Width = 460, Height = 190, ShowInTaskbar = false, CanMinimize = false, CanMaximize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { status, stop } } };
            var completed = false;
            stop.Click += (_, _) => cancel.Cancel(); progress.Closing += (_, _) => { if (!completed) cancel.Cancel(); };
            var progressClosed = progress.ShowDialog(owner);
            AiPageLayout layout;
            try
            {
                layout = await AiPdfProcessor.PrepareLayoutAsync(source, _app.LocalSettings.Ai.Pdf, AiSettingsWindow.PdfBridgePath,
                    new Progress<string>(s => status.Text = s), cancel.Token);
            }
            finally
            {
                completed = true; progress.Close(); await progressClosed;
                // App shutdown waits for the child process, not for an interactive save dialog.
                _layoutFinished?.TrySetResult(); _layoutFinished = null;
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (!IsCurrentComposerTarget(target) || CurrentPermission == AiPermissionMode.ReadOnly) return;
            var review = new LayoutReviewWindow(layout);
            using var closeReview = cancel.Token.Register(() => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => review.Close(false)));
            if (!await review.ShowDialog<bool>(owner) || !IsCurrentComposerTarget(target) || CurrentPermission == AiPermissionMode.ReadOnly) return;
            cancel.Token.ThrowIfCancellationRequested();
            var bytes = await Task.Run(layout.CreateWord);
            var name = requestedName ?? Path.GetFileNameWithoutExtension(source.Name) + "-giu-bo-cuc.docx";
            if (await SaveBytes(owner, name, bytes, () => !cancel.IsCancellationRequested && IsCurrentComposerTarget(target) && CurrentPermission != AiPermissionMode.ReadOnly) is not { } saved) return;
            if (!IsCurrentComposerTarget(target)) return;
            if (record is null)
            {
                record = new AiMessage { Role = "user", IsTimelineMarker = true, Content = "Đã dựng Word giữ bố cục trên máy từ " + source.Name + ". Chữ OCR/phông cần đối chiếu bản gốc; không gửi AI.", CreatedAt = DateTime.UtcNow };
                target.Conversation.Messages.Add(record);
            }
            record.SavedFiles.Add(new(Path.GetFileName(saved), saved, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), DateTime.UtcNow));
            Touch(target.Scope); Render(); _status.Text = "Đã lưu Word. Mở trong Word để kiểm tra bố cục và chữ trước khi dùng.";
        }
        catch (OperationCanceledException) { _status.Text = "Đã dừng dựng Word. Không thay tệp nguồn."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or System.Xml.XmlException)
        { await Dialogs.Message(owner, "Chưa dựng được Word", ex.Message); }
        finally
        {
            _preparing = false; _layoutCancellation = null; _layoutFinished?.TrySetResult(); _layoutFinished = null; RefreshComposerOptions();
        }
    }
}
