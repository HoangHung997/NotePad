using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    static AiChatPanel()
    {
        InputElement.KeyDownEvent.AddClassHandler<AiChatPanel>((panel, e) => panel.HandleComposerEnter(e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.GotFocusEvent.AddClassHandler<AiChatPanel>((panel, e) => panel.RefreshComposerKeyHints(e),
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private async void HandleComposerEnter(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !ReferenceEquals(e.Source, _composer)) return;

        // Shift+Enter always belongs to the multiline editor. If an @ suggestion happens to be
        // open, close only the suggestion so TextBox can insert its normal newline.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (_mentionPopup.IsOpen) _mentionPopup.IsOpen = false;
            return;
        }

        // While @ autocomplete is open, Enter keeps its conventional "choose suggestion" role.
        // The existing composer handler performs that selection. Ctrl+Enter also stays available
        // for compatibility with existing users/tests; plain Enter is now the primary send gesture.
        if (_mentionPopup.IsOpen || e.KeyModifiers != KeyModifiers.None) return;

        e.Handled = true;
        await SendOrSave();
    }

    private void RefreshComposerKeyHints(GotFocusEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _composer)) return;
        ToolTip.SetTip(_composer,
            "Enter để gửi · Shift+Enter để xuống dòng. Ctrl+V để dán ảnh hoặc văn bản; thả tệp/file:// trên máy. Gõ @ để chọn ngữ cảnh hoặc gợi ý.");
        ToolTip.SetTip(_send, "Gửi · Enter");
    }
}
