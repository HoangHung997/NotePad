using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using H2Notes.Core;
using H2Notes.Avalonia.Controls;

namespace H2Notes.Avalonia;

public sealed partial class WorkAssistantCompactWindow
{
    private bool _preparingAttachments;
    public void SetAttachmentPreparation(bool preparing)
    {
        _preparingAttachments = preparing;
        _prompt.IsReadOnly = preparing;
        SetTaskBusy(preparing);
        _busySendMode.IsVisible = false;
    }
    private readonly List<AiAttachment> _draftAttachments=[];
    private readonly WrapPanel _draftAttachmentRows=new() { Orientation=Orientation.Horizontal };
    public IReadOnlyList<AiAttachment> DraftAttachments=>_draftAttachments.ToArray();
    private Guid _conversationId;
    public Guid ConversationId
    {
        get=>_conversationId;
        set
        {
            if(value==_conversationId)return;
            _conversationId=value;_draftAttachments.Clear();
            try
            {
                var path=AttachmentDraftPath;
                if(File.Exists(path) && new FileInfo(path).Length<24*1024*1024)
                {
                    var items=JsonSerializer.Deserialize<List<AiAttachment>>(File.ReadAllText(path))??[];
                    ValidateAttachments(items);_draftAttachments.AddRange(items);
                }
            }
            catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) { SetStatus("Chưa đọc được tệp nháp: "+ex.Message,true); }
            RenderAttachmentDraft();
        }
    }
    private string AttachmentDraftPath=>Path.Combine(LocalConfiguration.SettingsDirectory,"assistant-attachments",ConversationId.ToString("N")+".json");
    private static void ValidateAttachments(IReadOnlyList<AiAttachment> items)
    {
        if(items.Count>8 || items.Any(a=>a.Data.Length>AiDocuments.MaxFileBytes) || items.Sum(a=>(long)a.Data.Length)>16*1024*1024)
            throw new InvalidDataException("Tối đa 8 tệp, 8 MB mỗi tệp và 16 MB tổng cộng.");
    }
    private void SaveAttachmentDraft()
    {
        if(ConversationId==Guid.Empty)return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(AttachmentDraftPath)!);ProjectWorkspaceStore.AtomicWrite(AttachmentDraftPath,JsonSerializer.SerializeToUtf8Bytes(_draftAttachments)); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { SetStatus("Tệp nháp chưa lưu được: "+ex.Message,true); }
    }
    public void ClearAttachments() { _draftAttachments.Clear();SaveAttachmentDraft();RenderAttachmentDraft(); }
    private void RenderAttachmentDraft()
    {
        _draftAttachmentRows.Children.Clear();
        foreach(var file in _draftAttachments)
        {
            var open=new Button { Content=file.Name,FontSize=12,MaxWidth=230,Classes={"quiet"} };
            open.Click+=(_,_)=>PreviewAttachment(file);
            var remove=new Button { Content="×",FontSize=14,Classes={"quiet"} };remove.Click+=(_,_)=> { if(_taskBusy)return;_draftAttachments.Remove(file);SaveAttachmentDraft();RenderAttachmentDraft(); };
            _draftAttachmentRows.Children.Add(new StackPanel { Orientation=Orientation.Horizontal,Children={open,remove} });
        }
        RefreshSendAction();
    }
    private void PreviewAttachment(AiAttachment file)
    {
        var preview=new Window { Title="Nội dung đính kèm · "+file.Name,Width=600,Height=480,MinWidth=340,MinHeight=300 };
        var content=new StackPanel { Spacing=12,Margin=new Thickness(20) };
        content.Children.Add(new TextBlock { Text=file.Name,FontSize=18,TextWrapping=TextWrapping.Wrap });
        if(file.IsImage)
        {
            try
            {
                using var stream=new MemoryStream(file.Data);
                var bitmap=Bitmap.DecodeToWidth(stream,1200);
                content.Children.Add(new Image { Source=bitmap,Stretch=Stretch.Uniform,MaxHeight=700 });
                preview.Closed+=(_,_)=>bitmap.Dispose();
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or IOException)
            { content.Children.Add(new TextBlock { Text="Chưa xem được ảnh: "+ex.Message,TextWrapping=TextWrapping.Wrap }); }
        }
        content.Children.Add(new SelectableTextBlock { Text=string.IsNullOrWhiteSpace(file.Text) ? file.Notice : file.Text,TextWrapping=TextWrapping.Wrap });
        preview.Content=new ScrollViewer { Content=content };preview.Show(this);
    }
    private async Task ImportAttachments(Func<Task<IReadOnlyList<AiAttachment>>> read)
    {
        if(_taskBusy) { SetStatus("Đợi tác vụ kết thúc để thêm tệp vào lượt mới.");return; }
        var thread=ConversationId;
        try
        {
            var files=await read();
            if(thread!=ConversationId || _taskBusy) { SetStatus("Hội thoại đã đổi. Chọn lại tệp ở lượt hiện tại.");return; }
            var all=_draftAttachments.Concat(files).DistinctBy(a=>a.Sha256).ToArray();ValidateAttachments(all);
            _draftAttachments.Clear();_draftAttachments.AddRange(all);SaveAttachmentDraft();RenderAttachmentDraft();
        }
        catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { SetStatus("Chưa thêm được tệp: "+ex.Message,true); }
    }
    private async Task PickAttachments()
    {
        var thread=ConversationId;
        var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title="Thêm tệp vào trao đổi",AllowMultiple=true });
        if(thread!=ConversationId)return;
        await ImportAttachments(()=>Task.Run<IReadOnlyList<AiAttachment>>(()=>files.Select(f=>AiDocuments.Read(f.TryGetLocalPath()??throw new IOException("Chọn tệp đã lưu trên máy."))).ToArray()));
    }
    private async Task PasteAttachmentsOrText()
    {
        var thread=ConversationId;var draft=PromptText;var start=_prompt.SelectionStart;var end=_prompt.SelectionEnd;
        try
        {
            using var data=await Clipboard!.TryGetDataAsync();if(data is null || thread!=ConversationId)return;
            if(data.Contains(DataFormat.File))
            {
                await ImportAttachments(async()=>
                {
                    var files=await data.TryGetFilesAsync();
                    return await Task.Run(()=> (files??[]).Select(f=>AiDocuments.Read(f.TryGetLocalPath()??throw new IOException("Tệp không ở trên máy."))).ToArray());
                });
            }
            else if(data.Contains(DataFormat.Bitmap))
            {
                await ImportAttachments(async()=>
                {
                    using var bitmap=await data.TryGetBitmapAsync();if(bitmap is null)return [];
                    AiComposerInputData.CheckBitmapSize(bitmap.PixelSize.Width,bitmap.PixelSize.Height);
                    using var output=new AiComposerInputData.LimitedAttachmentStream();bitmap.Save(output,new PngBitmapEncoderOptions());
                    return new[]{AiDocuments.Read("Anh-dan-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".png",output.ToArray())};
                });
            }
            else
            {
                var text=await data.TryGetTextAsync();
                if(thread==ConversationId && draft==PromptText && start==_prompt.SelectionStart && end==_prompt.SelectionEnd)_prompt.SelectedText=text??"";
            }
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or ArgumentException) { SetStatus("Chưa dán được: "+ex.Message,true); }
    }
    private void OpenAttachmentMenu(Control anchor)
    {
        var file=new MenuItem { Header="Thêm tệp hoặc ảnh…" };file.Click+=async(_,_)=>await PickAttachments();
        var paste=new MenuItem { Header="Dán từ clipboard" };paste.Click+=async(_,_)=>await PasteAttachmentsOrText();
        var workspace=new MenuItem { Header="Chọn thư mục làm việc…" };workspace.Click+=(_,_)=>OpenWorkspaceMenu(_workspaceButton);
        var context=new MenuItem { Header="Xem ngữ cảnh gửi cho Agent" };
        context.Click+=(_,_)=>new Window { Title="Ngữ cảnh của lượt gửi",Width=600,Height=420,Content=new ScrollViewer { Content=new SelectableTextBlock {
            Text=SelectedContextSummary+"\n"+(SelectedWorkspaceRoot??"Chưa chọn thư mục")+"\n"+string.Join("\n",_draftAttachments.Select(a=>a.Name)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20) } } }.Show(this);
        _composerMenu?.Close();_composerMenu=new ContextMenu { ItemsSource=new object[]{file,paste,new Separator(),workspace,context} };_composerMenu.Open(anchor);
    }
}
