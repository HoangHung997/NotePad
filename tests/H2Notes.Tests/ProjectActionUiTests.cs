using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ProjectActionUiTests
{
    internal static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string why) { if (!value) throw new Exception(why); }
        static void PumpUntil(Func<bool> done, string why)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
            Check(done(), why);
        }

        foreach (var permission in Enum.GetValues<AiPermissionMode>())
            test("Composer project edits enforce " + permission + " with one-step permission UX", () =>
            {
                var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "fixture" };
                app.LocalSettings.Ai = new() { Profiles = [profile], SelectedId = profile.Id };
                var conversation = new AiConversation { PermissionMode = permission };
                if (permission == AiPermissionMode.ProjectAccess) app.LocalSettings.Ai.ProjectAccessConversationIds.Add(conversation.Id);
                var project = new ProjectRecord { Conversations = [conversation] };
                var panel = new AiChatPanel(app, () => new AiClient(new Handler())); panel.SetProject(project);
                var window = new Window { Content = panel, Width = 420, Height = 700 }; window.Show(); Dispatcher.UIThread.RunJobs();
                var refreshCount = 0;
                panel.ProjectActionsRequested += (target, actions) =>
                {
                    Check(target == project, "Wrong target project");
                    Check(actions.Count == 0, "Host must only refresh; core already applied the mutation");
                    refreshCount++;
                };
                try
                {
                    panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ChatComposer").Text = "Thêm công việc kiểm tra hồ sơ";
                    var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
                    PumpUntil(() => send.IsCompleted, "Send timed out"); send.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
                    var answer = conversation.Messages.Last(); Check(answer.Status == "complete", answer.ErrorText);

                    if (permission == AiPermissionMode.ConfirmChanges)
                    {
                        PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "AI muốn thay đổi dự án"), "Confirmation did not open automatically");
                        var confirm = window.OwnedWindows.Single(w => w.Title == "AI muốn thay đổi dự án");
                        var body = confirm.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.Contains("Thêm công việc") == true);
                        Check(body?.Text?.Contains("Kiểm tra hồ sơ") == true, "Confirmation does not show the exact proposed change");
                        var apply = confirm.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Đồng ý và áp dụng"));
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(() => answer.ProjectActionsApplied, "Confirmed project action was not applied");
                    }
                    else if (permission == AiPermissionMode.ProjectAccess)
                    {
                        PumpUntil(() => answer.ProjectActionsApplied, "Full project access did not auto-apply");
                        Check(!window.OwnedWindows.Any(w => w.Title == "AI muốn thay đổi dự án"), "Full project access unexpectedly asked for confirmation");
                    }
                    else
                    {
                        Dispatcher.UIThread.RunJobs();
                        Check(!answer.ProjectActionsApplied && project.ChecklistItems.Count == 0, "Read-only mutated the project");
                    }

                    var expected = permission == AiPermissionMode.ReadOnly ? 0 : 1;
                    Check(project.ChecklistItems.Count == expected, "Project mutation count is wrong");
                    Check(refreshCount == expected, "Host refresh count is wrong");
                    if (expected == 1) Check(project.ChecklistItems.Single().DisplayText == "Kiểm tra hồ sơ", "Core action content lost");

                    typeof(AiChatPanel).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
                    Check(refreshCount == expected, "Rendering applied actions again");
                    Check(panel.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ChatProjectActions"), "Action preview missing");
                    Check(!AiHistory.RequestTurns(conversation).Any(t => t.Content.Contains("Đã áp dụng") && expected == 0), "Unapplied action marked as done in context");
                }
                finally { panel.Cancel(); foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); }
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
