using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed partial class AiChatPanel
{
    private bool _workspacePresentation;
    public event Action? ConversationsChanged;
    public event Action<H2AgentEvidence>? DocumentRequested;
    public Guid? SelectedConversationId => _conversation?.Id;
    public void MoveComposerTo(Grid? host)
    {
        if(host is null) { _chatSurface.AttachComposer(_composerFooter);return; }
        if(_composerFooter.Parent==host)return;
        (_composerFooter.Parent as Panel)?.Children.Remove(_composerFooter);
        Grid.SetRow(_composerFooter,3);host.Children.Add(_composerFooter);
    }

    public void SetWorkspacePresentation(bool primary)
    {
        _workspacePresentation = primary;
        _header.IsVisible = !primary && !_detached && _scope?.IsStandalone != true;
        _optionsPanel.IsVisible = false;
        _composer.MinHeight = 50;
        _composer.MaxHeight = Bounds.Height < 500 ? 100 : 160;
        _back.IsVisible = false;
    }

    public void NewWorkspaceConversation() => StartNewConversation();

    public void SelectWorkspaceConversation(Guid id)
    {
        if (_scope?.Conversations.FirstOrDefault(c => c.Id == id) is not { } conversation) return;
        if (_activeAgentTaskId is not null)
        { _status.Text = "Dừng lượt đang chạy trước khi đổi trao đổi."; return; }
        Flush(); _conversation = conversation; _scope.SelectedConversationId = id;
        _visibleMessages = 40; LoadDraft(); RefreshProfiles(); RefreshHistory(); Render(); ScrollToLatest(); Touch(_scope);
    }

    public void DraftWithSelection(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection)) return;
        _composer.Text = "Trao đổi về đoạn ghi chú đã chọn:\n\n" + selection + "\n\nYêu cầu: ";
        _composer.CaretIndex = _composer.Text.Length; _composer.Focus();
    }

    public async Task UseLinkedFileAsync(string path)
    {
        if (_scope is null) return;
        var target = CaptureComposerTarget();
        await ImportComposerAttachments(target, () => ReadComposerFiles(target, [path]));
        _composer.Focus();
    }

    // Only invoked by the isolated demo capture workflow. Uses the live menus.
    internal async Task CaptureComposerEvidence(string directory)
    {
        async Task Capture(Control control,string name)
        {
            await Task.Delay(350);control.UpdateLayout();
            var size=control.Bounds.Size;
            if(size.Width<1 || size.Height<1)throw new InvalidOperationException("Menu did not open: "+name);
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height)),new Vector(96,96));
            bitmap.Render(control);bitmap.Save(Path.Combine(directory,name+".png"),PngBitmapEncoderOptions.Default);
        }
        var draft=_composer.Text;
        try
        {
            OpenComposerMenu();await Capture(_composerMenu!,"18-attach-menu");_composerMenu!.Close();
            OpenPermissionMenu();await Capture(_permissionMenu!,"18-permission-menu");_permissionMenu!.Close();
            OpenModelPicker();await Capture(_modelPickerCard,"18-model-menu");CloseModelPicker();
            _composer.Text="@";_composer.CaretIndex=_composer.SelectionStart=_composer.SelectionEnd=1;_composer.Focus();
            UpdateComposerMentions();await Capture(_mentionPopup.Child!,"18-context-menu");
        }
        finally { _composerMenu?.Close();_permissionMenu?.Close();CloseModelPicker();_mentionPopup.IsOpen=false;_composer.Text=draft; }
    }
}
