using System.Security.Cryptography;
using System.Text;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private CancellationTokenSource? _pdfPreparation;
    private TaskCompletionSource? _pdfPreparationFinished;

    private async Task<IReadOnlyList<AiTurn>?> PreparePdfRequest(AiChatScope scope, AiConversation conversation,
        AiMessage user, AiProfile profile, string context)
    {
        var settings = ProjectWorkspaceStore.Clone(_app.LocalSettings.Ai.Pdf);
        var target = CaptureComposerTarget();
        using var cancel = new CancellationTokenSource();
        _pdfPreparation = cancel; _pdfPreparationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparing = true; _composer.IsReadOnly = true; _send.IsVisible = false; _stop.IsVisible = true; RefreshComposerOptions();
        try
        {
            var progress = new Progress<string>(text => { if (IsCurrentComposerTarget(target) && !cancel.IsCancellationRequested) _status.Text = text; });
            var attachments = new List<AiAttachment>();
            foreach (var item in user.Attachments)
            {
                var cached = ReusePreparedAttachment(item, settings.Engine);
                if (cached is not null)
                {
                    attachments.Add(cached);
                    if (IsCurrentComposerTarget(target)) _status.Text = "Đã dùng lại OCR đã lưu của " + item.Name + "; không chạy OCR lần nữa.";
                }
                else attachments.Add(await AiPdfProcessor.PrepareAttachmentAsync(item, settings, AiSettingsWindow.PdfBridgePath, progress, cancel.Token));
            }
            cancel.Token.ThrowIfCancellationRequested();
            user.Attachments = attachments;
            // AiProjectContext.Prepare replays old turns as text-only. Therefore only attachments
            // in this new user message can reach the OCR/native-media pipeline.
            var turns = AiProjectContext.Prepare(conversation, user, context);
            turns = await AiPdfProcessor.PrepareTurnsAsync(turns, profile, settings, AiSettingsWindow.PdfBridgePath, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (scope != _scope || conversation != _conversation || !IsCurrentComposerTarget(target)) return null;
            return turns;
        }
        catch (OperationCanceledException)
        { if (IsCurrentComposerTarget(target)) _status.Text = "Đã dừng đọc PDF/ảnh. Bản nháp và tệp gốc vẫn được giữ, chưa gửi AI."; return null; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or TimeoutException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        { if (IsCurrentComposerTarget(target)) _status.Text = "Chưa gửi PDF/ảnh: " + ex.Message; return null; }
        finally
        {
            _pdfPreparation = null; _preparing = false; _composer.IsReadOnly = false; _send.IsVisible = true; _stop.IsVisible = false;
            RefreshComposerOptions(); _pdfPreparationFinished?.TrySetResult(); _pdfPreparationFinished = null;
        }
    }

    private AiAttachment? ReusePreparedAttachment(AiAttachment source, AiPdfEngine engine)
    {
        if (engine == AiPdfEngine.Direct || string.IsNullOrWhiteSpace(source.Sha256)) return null;
        var cached = _app.State.Notes
            .SelectMany(n => n.AiConversations.Concat(n.Projects.SelectMany(p => p.Conversations)))
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Attachments)
            .FirstOrDefault(a => a.PdfEngine == engine && !string.IsNullOrWhiteSpace(a.Text)
                && (string.Equals(a.SourceSha256, source.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)));
        if (cached is null) return null;

        if (source.IsImage)
            return new AiAttachment
            {
                Id = source.Id, Name = source.Name, MimeType = source.MimeType, Data = source.Data.ToArray(), Sha256 = source.Sha256,
                Text = cached.Text, PdfEngine = engine, SourceName = source.Name, SourceSha256 = source.Sha256,
                Notice = "Ảnh dùng lại Markdown OCR đã lưu bằng " + engine + ". Không chạy OCR lại; ảnh gốc vẫn lưu trong lịch sử."
            };
        if (source.IsPdf)
        {
            var bytes = Encoding.UTF8.GetBytes(cached.Text);
            return new AiAttachment
            {
                Id = source.Id, Name = Path.GetFileNameWithoutExtension(source.Name) + ".md", MimeType = "text/markdown", Data = bytes,
                Text = cached.Text, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), PdfEngine = engine,
                SourceName = source.Name, SourceSha256 = source.Sha256,
                Notice = "PDF dùng lại Markdown OCR đã lưu bằng " + engine + ". Không chạy OCR lại."
            };
        }
        return null;
    }
}
