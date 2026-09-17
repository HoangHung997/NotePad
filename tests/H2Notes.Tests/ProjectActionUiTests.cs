using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ProjectActionUiTests
{
    internal static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string why) { if (!value) throw new Exception(why); }
        foreach (var permission in Enum.GetValues<AiPermissionMode>())
            test("Composer project edits enforce " + permission + " without duplicate application", () =>
            {
                var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "fixture" };
                app.LocalSettings.Ai = new() { Profiles = [profile], SelectedId = profile.Id };
                var conversation = new AiConversation { PermissionMode = permission };
                if (permission == AiPermissionMode.ProjectAccess) app.LocalSettings.Ai.ProjectAccessConversationIds.Add(conversation.Id);
                var project = new ProjectRecord { Conversations = [conversation] };
                var panel = new AiChatPanel(app, () => new AiClient(new Handler())); panel.SetProject(project);
                var window = new Window { Content = panel, Width = 420, Height = 700 }; window.Show(); Dispatcher.UIThread.RunJobs();
                var count = 0;
                panel.ProjectActionsRequested += (target, actions) =>
                { Check(target == project, "Wrong target project"); count++; target.ChecklistItems.Add(new() { Text = actions[0].Text }); };
                try
                {
                    panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ChatComposer").Text = "Thêm công việc kiểm tra hồ sơ";
                    var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
                    var end = DateTime.UtcNow.AddSeconds(10);
                    while (!send.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
                    Check(send.IsCompleted, "Send timed out"); send.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
                    var answer = conversation.Messages.Last(); Check(answer.Status == "complete", answer.ErrorText);
                    Check(count == (permission == AiPermissionMode.ProjectAccess ? 1 : 0), "Permission not enforced");
                    Check(answer.ProjectActionsApplied == (count == 1), "Audit does not match mutation");
                    typeof(AiChatPanel).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
                    Check(count == (permission == AiPermissionMode.ProjectAccess ? 1 : 0), "Rendering applied actions again");
                    Check(panel.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ChatProjectActions"), "Action preview missing");
                    Check(!AiHistory.RequestTurns(conversation).Any(t => t.Content.Contains("Đã áp dụng") && count == 0), "Unapplied action marked as done in context");
                }
                finally { panel.Cancel(); window.Close(); }
            });
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            const string answer = "Đề xuất thao tác\n```h2-actions\n[{\"kind\":\"add_task\",\"text\":\"Kiểm tra hồ sơ\"}]\n```";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { message = new { content = answer }, done = false }) + "\n{\"done\":true}\n", Encoding.UTF8, "application/x-ndjson") });
        }
    }
}
