using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    // Called only by --agent-documents-evidence with an explicit isolated --demo --data.
    // These captures are the running native Avalonia app, not headless pixel comparisons.
    internal async Task CaptureAgentDocumentsEvidence(string directory)
    {
        Directory.CreateDirectory(directory);
        async Task Capture(Window window,string name,int width,int height)
        {
            window.Position=new PixelPoint(0,0);window.Width=width;window.Height=height;window.Opacity=1;
            if(!window.IsVisible)window.Show();
            await Task.Delay(400);window.UpdateLayout();
            using var bitmap=new RenderTargetBitmap(new PixelSize(width,height),new Vector(96,96));
            bitmap.Render(window);bitmap.Save(Path.Combine(directory,name+".png"),PngBitmapEncoderOptions.Default);
            File.AppendAllText(Path.Combine(directory,"sizes.txt"),$"{name}: requested {width}x{height} DIP; actual {window.Bounds.Size}; native scale {window.RenderScaling}; render=96 DPI\n");
        }
        OpenProjectWorkspace(_board.Projects[0].Id);ShowAgentWorkspace();
        var fixtureRoot=Path.Combine(Path.GetDirectoryName(directory)!,"fixtures");
        var tablePath=Path.Combine(fixtureRoot,"DuToan.xlsx");
        H2AgentEvidence FileEvidence(string path)=>new("fixture:"+Path.GetFileName(path),"linked-file",null,"Dữ liệu minh họa kiểm tra UI · chưa xác minh thay đổi",LocalPath:path,Provenance:"Bộ kiểm tra cục bộ");
        if(File.Exists(tablePath))ShowWorkspaceDocument(FileEvidence(tablePath));
        await Capture(this,"01-agent-wide",1440,860);
        await Capture(this,"02-agent-medium",1040,760);CloseWorkspaceDocument();
        await Capture(this,"03-agent-narrow",560,820);
        await Capture(this,"04-agent-minimum",560,600);
        _drawerOpen=true;ApplyResponsive();await Capture(this,"05-project-picker",560,820);_drawerOpen=false;ApplyResponsive();
        if(File.Exists(tablePath))ShowWorkspaceDocument(FileEvidence(tablePath));
        await Capture(this,"06-document-narrow",560,820);CloseWorkspaceDocument();
        ShowCommandCenter();await Capture(this,"07-command-center",1440,860);
        OpenProjectWorkspace(_board.Projects[0].Id);
        ShowProjectDetail(ProjectWorkspaceTasksMode);await Capture(this,"08-project-tasks",1440,860);
        ShowProjectDetail(ProjectWorkspaceNotesMode);await Capture(this,"09-project-notes",1040,760);
        ShowProjectDetail(ProjectWorkspaceResourcesMode);await Capture(this,"10-project-files",1440,860);
        ShowProjectDetail(ProjectWorkspaceHistoryMode);await Capture(this,"11-history-evidence",1440,860);
        var ai=new AiSettingsWindow(_app);await Capture(ai,"12-ai-settings",1040,760);ai.Close();
        var review=new WorkspaceNoteConflictReview("fixture",Guid.NewGuid(),"Ghi chú minh họa",RichDocument.Plain("Cập nhật hồ sơ ngày 21/09.\nKiểm tra khối lượng trước khi gửi."),RichDocument.Plain("Cập nhật hồ sơ ngày 21/09.\nĐính kèm phụ lục nghiệm thu."),"");
        var conflict=new Window { Title="Đối chiếu ghi chú · dữ liệu minh họa" };
        conflict.Content=SettingsFrame.Build(conflict,new Border { Padding=new Thickness(24),Child=new NoteConflictPanel(review,_=>false) },"Dữ liệu và đồng bộ",null);
        await Capture(conflict,"13-storage-conflict",1040,760);conflict.Close();
        var bubble=new WorkAssistantBubbleWindow(new WorkAssistantSettings(),()=>{}) { ShowInTaskbar=true };
        foreach(var state in Enum.GetValues<WorkAssistantBubbleState>())
        { bubble.SetState(state);await Capture(bubble,"14-bubble-"+state.ToString().ToLowerInvariant(),(int)bubble.Width,54); }
        bubble.Close();
        var assistant=new WorkAssistantCompactWindow(new WorkAssistantSettings()) { ShowInTaskbar=true };
        assistant.ConfigureModels(_app.LocalSettings.Ai.Profiles,_app.LocalSettings.Ai.SelectedId);
        assistant.ShowHistory([new H2AgentTaskSummary(Guid.NewGuid(),null,"Rà soát tài liệu minh họa",H2AgentTaskStatus.Completed,null,[],"Đây là nội dung minh họa để đối chiếu bố cục.\n\nBạn có thể tiếp tục trao đổi bên dưới.",null,DateTime.UtcNow.AddMinutes(-2),DateTime.UtcNow.AddMinutes(-1))],null);
        await Capture(assistant,"15-assistant-640",640,610);await Capture(assistant,"16-assistant-380",380,610);
        assistant.SetFullMode(true);await Capture(assistant,"17-assistant-full",1040,760);assistant.Close();
        ShowAgentWorkspace();await Capture(this,"18-composer",1040,760);
        await _chat.CaptureComposerEvidence(directory);
        var transfer=WorkspaceTransferDialog.Create(@"D:\H2Data — dữ liệu minh họa",@"E:\H2Archive — dữ liệu minh họa",new WorkspaceTransfer(_app.State,new SheetState()));
        await Capture(transfer,"19-storage-transfer",1040,760);transfer.Close();
        ShowAgentWorkspace();var pdf=Path.Combine(fixtureRoot,"gioi-thieu.pdf");
        if(File.Exists(pdf)) { ShowWorkspaceDocument(FileEvidence(pdf));await Task.Delay(2500); }
        await Capture(this,"20-pdf-evidence",1440,860);
        var word=Path.Combine(fixtureRoot,"gioi-thieu.docx");
        if(File.Exists(word)) { ShowWorkspaceDocument(FileEvidence(word));await Capture(this,"10-word-preview",1440,860); }
        File.WriteAllText(Path.Combine(directory,"complete.txt"),"Running Avalonia app; isolated synthetic data; no AI request or storage transfer executed. RenderTargetBitmap captures; native mouse checks recorded separately.");
    }
}
