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
                attachments.Add(await AiPdfProcessor.PrepareAttachmentAsync(item, settings, AiSettingsWindow.PdfBridgePath, progress, cancel.Token));
            cancel.Token.ThrowIfCancellationRequested();
            user.Attachments = attachments;
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
}
