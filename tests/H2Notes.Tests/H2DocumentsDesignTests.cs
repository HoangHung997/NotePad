using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2DocumentsDesignTests
{
    public static void Run(Action<string,Action> test)
    {
        test("Documents conflict review preserves rich versions, rejects stale edits and cannot replay",()=>
        {
            var local=RichDocument.Plain("Bản trên máy");local.Format(0,3,s=>s with { Bold=true });
            var remote=RichDocument.Plain("Bản từ máy khác");remote.Format(0,3,s=>s with { Italic=true });
            var project=new ProjectRecord { NotesRich=local.Clone() };
            var conflict=new WorkspaceMergeConflict(DateTime.UtcNow,$"project/{project.Id}/notes-rich","local","null",JsonSerializer.Serialize(local),JsonSerializer.Serialize(remote));
            var review=WorkspaceNoteConflictReview.Create(conflict,project)!;
            Check(review.Apply(project,ConflictResolution.KeepBoth),"Could not resolve");
            Check(project.NotesText.Contains(local.Text) && project.NotesText.Contains(remote.Text),"Lost version");
            Check(project.NotesRich!.Runs.Any(r=>r.Style.Bold) && project.NotesRich.Runs.Any(r=>r.Style.Italic),"Lost formatting");
            Check(WorkspaceNoteConflictReview.Create(conflict,project) is null && !review.Apply(project,ConflictResolution.KeepCurrent),"Replayed conflict");
            var fresh=new ProjectRecord { NotesRich=local.Clone() };
            var other=conflict with { Path=$"project/{fresh.Id}/notes-rich" };
            var stale=WorkspaceNoteConflictReview.Create(other,fresh)!;fresh.NotesRich.Replace(0,0,"Changed later. ");
            Check(!stale.Apply(fresh,ConflictResolution.KeepDestination) && fresh.NotesText.StartsWith("Changed later."),"Overwrote newer edit");
            Check(!stale.Apply(project,ConflictResolution.KeepCurrent),"Wrong project accepted");
            Check(WorkspaceNoteConflictReview.Create(conflict with { Path=$"project/{project.Id}/name" },project) is null,"Non-notes conflict exposed");
        });
        test("Documents narrow inspector hides composer and restores the same draft on back",()=>
        {
            var app=new App();var board=SheetStorage.Demo().Notes[0];app.State.Notes.Add(board);
            var path=Path.Combine(Path.GetTempPath(),"h2-doc-review-"+Guid.NewGuid()+".txt");File.WriteAllText(path,"Preview only");
            board.Projects[0].Links.Add(new(Guid.NewGuid(),"Preview",path));
            var window=new MainWindow(app,board) { Width=560,Height=820 };window.Show();window.OpenProjectWorkspace(board.Projects[0].Id);Pump(window);
            try
            {
                var input=window.GetVisualDescendants().OfType<TextBox>().Single(c=>c.Name=="ChatComposer");input.Text="Keep my unsent draft";
                window.FindControl<Button>("ResourcesTabButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                var list=window.FindControl<ListBox>("ProjectResourcesList")!;list.SelectedIndex=0;Pump(window);
                Check(!window.FindControl<Grid>("WorkContent")!.IsVisible,"Document did not replace narrow workspace");
                var back=window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"← Quay lại Agent"));back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                window.FindControl<Button>("AgentTabButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                Check(input.Text=="Keep my unsent draft" && input.IsEffectivelyVisible,"Returning lost composer draft");
            }
            finally { typeof(App).GetProperty(nameof(App.IsExiting))!.SetValue(app,true);window.Close();File.Delete(path); }
        });
        test("Documents empty API tab clears Ollama credentials and disables save until a connection is chosen",()=>
        {
            var app=new App();app.LocalSettings.Ai.Profiles=[new AiProfile { Model="gemma4:cloud" }];
            var window=new AiSettingsWindow(app);window.Show();Pump(window);
            try
            {
                window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"API online")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                Check(window.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="AiServerUrl").Text=="","Stale Ollama address in API tab");
                Check(!window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Lưu kết nối")).IsEnabled,"Save accepts missing profile");
            }
            finally { window.Close(); }
        });
        test("Documents Assistant attachment drafts stay with their thread and survive reopening without changing permissions",()=>
        {
            var first=Guid.NewGuid();var second=Guid.NewGuid();var window=new WorkAssistantCompactWindow(new());
            window.ConversationId=first;
            var method=typeof(WorkAssistantCompactWindow).GetMethod("ImportAttachments",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var attachment=AiDocuments.Read("sample.txt",System.Text.Encoding.UTF8.GetBytes("Synthetic attachment"));
            var task=(Task)method.Invoke(window,[new Func<Task<IReadOnlyList<AiAttachment>>>(()=>Task.FromResult<IReadOnlyList<AiAttachment>>([attachment]))])!;
            task.GetAwaiter().GetResult();
            Check(window.DraftAttachments.Count==1 && window.SelectedPermissionMode==H2AgentPermissionMode.ObserveOnly,"Attachment changed permissions");
            window.ConversationId=second;Check(window.DraftAttachments.Count==0,"Attachment leaked to another thread");
            window.ConversationId=first;Check(window.DraftAttachments.Single().Text=="Synthetic attachment","Attachment draft was lost");
            var reopened=new WorkAssistantCompactWindow(new()) { ConversationId=first };
            Check(reopened.DraftAttachments.Single().Sha256==attachment.Sha256,"Restart lost attachment");
            reopened.ClearAttachments();window.ConversationId=second;window.ConversationId=first;
            Check(window.DraftAttachments.Count==0,"Sent attachment returned after reopen");
        });
    }
    private static void Pump(Window window) { for(var i=0;i<4;i++) { Dispatcher.UIThread.RunJobs();window.UpdateLayout(); } }
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
}
