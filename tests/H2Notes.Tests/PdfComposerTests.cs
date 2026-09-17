using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class PdfComposerTests
{
    internal static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var (engine, image) in new[] { (AiPdfEngine.Direct, false), (AiPdfEngine.Docling, false), (AiPdfEngine.MinerU, true) })
            test("Document " + engine + " image=" + image + " failure preserves draft and never sends a text-only substitute", () =>
            {
                var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "fixture" };
                app.LocalSettings.Ai = new() { Profiles = [profile], SelectedId = profile.Id, Pdf = new()
                { Engine = engine, OcrImages = image, RuntimeRoot = Path.Combine(Path.GetTempPath(), "h2-missing-runtime-" + Guid.NewGuid().ToString("N")) } };
                var original = image ? AiDocuments.Read("fixture.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="))
                    : AiDocuments.Read("fixture.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfixture\n%%EOF"));
                var conversation = new AiConversation { Draft = "Đọc tài liệu", DraftAttachments = [original] };
                var project = new ProjectRecord { Conversations = [conversation] };
                var calls = 0; var panel = new AiChatPanel(app, () => { calls++; throw new Exception("Must not call network"); }); panel.SetProject(project);
                var window = new Window { Width = 420, Height = 700, Content = panel }; window.Show(); Dispatcher.UIThread.RunJobs();
                try
                {
                    var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
                    var end = DateTime.UtcNow.AddSeconds(6);
                    while (!send.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
                    Check(send.IsCompleted, "PDF operation hung"); send.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
                    Check(calls == 0 && conversation.Messages.Count == 0, "Incomplete PDF was sent");
                    Check(conversation.Draft == "Đọc tài liệu" && conversation.DraftAttachments.Single() == original, "PDF failure discarded draft");
                    var input = panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ChatComposer");
                    Check(!input.IsReadOnly, "PDF failure left composer locked");
                    Check(panel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChatConnectionStatus").Text!.StartsWith("Chưa gửi PDF/ảnh:"), "Missing document failure explanation");
                }
                finally { panel.Cancel(); window.Close(); }
            });
        test("AI settings exposes all four PDF paths without running an OCR engine", () =>
        {
            var app = new H2Notes.Avalonia.App(); var before = System.Text.Json.JsonSerializer.Serialize(app.LocalSettings.Ai.Pdf);
            var window = new H2Notes.Avalonia.AiSettingsWindow(app); window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AiPdfEngine");
                Check(selector.ItemsSource!.Cast<object>().Count() == 4, "PDF engine choice missing");
                selector.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
                var images = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AiOcrImages");
                Check(images.IsVisible && images.IsChecked != true, "Image OCR should be explicit, not enabled silently");
                images.IsChecked = true;
                Check(window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "AiOcrRuntimeRoot"), "Runtime path missing");
                Check(before == System.Text.Json.JsonSerializer.Serialize(app.LocalSettings.Ai.Pdf), "Preview selection saved settings without Save");
            }
            finally { window.Close(); }
        });
    }
}
