using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using H2Notes.Core;
using H2Notes.Avalonia.Controls;

internal static class AiDocumentTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Xml.XmlException) { return; } throw new Exception("Expected rejection"); }
    internal static void Run(Action<string, Action> test, string folder)
    {
        test("Full project context includes all ordered tasks, notes, links and related conversations but no local markers/drafts", () =>
        {
            var current = new AiConversation { Draft = "current draft" };
            var project = new ProjectRecord { Name = "Project A", Notes = "All project notes", ChecklistItems = [new() { Text = "Done task", IsCompleted = true, Comment = "Task detail" }, new() { Text = "Next task" }],
                Links = [new(Guid.NewGuid(), "CAD", "C:/example/not-opened.dwg")], Conversations = [current, new() { Draft = "other private draft", Messages = [new() { Content = "Earlier decision" }, new() { Content = "private milestone", IsTimelineMarker = true }] }] };
            var context = AiLegacyRequestContext.Build(project, current.Id);
            using var json = JsonDocument.Parse(context); var data = json.RootElement;
            Check(data.GetProperty("name").GetString() == "Project A" && data.GetProperty("tasks").GetArrayLength() == 2, "Missing identity/tasks");
            Check(context.Contains("All project notes") && context.Contains("Task detail") && context.Contains("Earlier decision") && context.Contains("not-opened.dwg"), "Incomplete context");
            Check(!context.Contains("private milestone") && !context.Contains("other private draft"), "Local-only data leaked");
            Check(!AiLegacyRequestContext.Build(project, current.Id, false).Contains("Earlier decision"), "History opt-out ignored");
        });
        test("Fresh context is sent once; old snapshots never override current project data", () =>
        {
            var conversation = new AiConversation { Messages = [new() { Content = "Previous question", Context = "STALE PROJECT SNAPSHOT" }, new() { Role = "assistant", Content = "Previous reply" }] };
            var turns = AiLegacyRequestContext.Prepare(conversation, new() { Content = "Summarize" }, "LATEST CONTEXT");
            Check(turns.Count == 4 && turns[0].Role == "system" && turns[^1].Content.Contains("LATEST CONTEXT"), "Missing trusted instructions/current context");
            Check(turns.All(t => !t.Content.Contains("STALE PROJECT SNAPSHOT")), "Duplicated stale snapshots");
            Check(conversation.Messages.Count == 2, "Preview changed saved history");
            Reject(() => AiLegacyRequestContext.Prepare(new(), new() { Content = "summary" }, new string('x', AiLegacyRequestContext.MaxRequestCharacters)));
        });
        foreach (var extension in new[] { ".docx", ".xlsx", ".csv", ".txt", ".md" })
            test("AI file " + extension + " generates valid actual bytes and reads Vietnamese content back", () =>
            {
                var file = new AiArtifact { FileName = "Bao-cao" + extension, Text = extension is ".xlsx" or ".csv" ? "" : "Báo cáo dự án\nĐã hoàn thành khối lượng.",
                    Sheets = extension is ".xlsx" or ".docx" or ".csv" ? [new() { Name = "Cong viec", Rows = [["Tên", "Trạng thái"], ["Thi công", "Chưa xong"], ["=HYPERLINK(\"https://invalid.test\")", "Giữ nguyên chữ"]] }] : [] };
                var bytes = AiArtifacts.Create(file); File.WriteAllBytes(Path.Combine(folder, file.FileName), bytes);
                if (extension is ".docx" or ".xlsx")
                {
                    using OpenXmlPackage package = extension == ".docx" ? WordprocessingDocument.Open(new MemoryStream(bytes), false) : SpreadsheetDocument.Open(new MemoryStream(bytes), false);
                    var errors = new OpenXmlValidator().Validate(package).ToArray(); Check(errors.Length == 0, string.Join("; ", errors.Select(e => e.Description)));
                }
                var read = AiDocuments.Read(file.FileName, bytes);
                Check(read.Text.Contains(extension is ".xlsx" or ".csv" ? "Thi công" : "Đã hoàn thành"), "Extracted text lost Unicode content");
                if (extension == ".xlsx") { using var zip = new ZipArchive(new MemoryStream(bytes)); using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()); Check(!reader.ReadToEnd().Contains("<f>"), "Model text became executable formula"); }
                if (extension == ".csv") Check(read.Text.Contains("'=HYPERLINK"), "CSV formula injection not neutralized");
            });
        test("Excel extraction includes every sheet, cached formulas, shared strings and cell addresses", () =>
        {
            var file = new AiArtifact { FileName = "multi.xlsx", Sheets = [new() { Name = "First", Rows = [["One"]] }, new() { Name = "Second", Rows = [["Two"]] }] };
            using var output = new MemoryStream(); output.Write(AiArtifacts.Create(file));
            using (var doc = SpreadsheetDocument.Open(output, true))
            {
                var main = doc.WorkbookPart!; var shared = main.AddNewPart<SharedStringTablePart>();
                shared.SharedStringTable = new(new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("Chuỗi dùng chung")));
                var sheet = main.WorksheetParts.First().Worksheet!;
                sheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().First().Append(new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "B1", DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString, CellValue = new("0") },
                    new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "C1", CellFormula = new("1+2"), CellValue = new("3") });
            }
            var text = AiDocuments.Read("multi.xlsx", output.ToArray()).Text;
            Check(text.Contains("First") && text.Contains("Second") && text.Contains("B1: Chuỗi dùng chung") && text.Contains("C1: 3") && text.Contains("1+2"), "Workbook incompletely extracted");
        });
        test("Attachment readers reject oversized files, old Office, macros, DTD and empty input", () =>
        {
            Reject(() => AiDocuments.Read("a.doc", [1, 2])); Reject(() => AiDocuments.Read("a.xls", [1, 2]));
            Reject(() => AiDocuments.Read("a.png", [1, 2])); Reject(() => AiDocuments.Read("a.txt", []));
            Reject(() => AiDocuments.Read("a.txt", new byte[AiDocuments.MaxFileBytes + 1]));
            Reject(() => AiDocuments.Read("a.txt", Encoding.UTF8.GetBytes(new string('x', AiDocuments.MaxTextCharacters + 1))));
            byte[] Package(string entry, string xml)
            { using var stream = new MemoryStream(); using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write(xml); } return stream.ToArray(); }
            Reject(() => AiDocuments.Read("a.docx", Package("word/document.xml", "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///secret'>]><a>&x;</a>")));
            Reject(() => AiDocuments.Read("a.docx", Package("word/vbaProject.bin", "macro")));
            Reject(() => AiDocuments.Read("a.docx", Package("huge.txt", new string('x', 41 * 1024 * 1024))));
        });
        test("Artifact validation rejects paths, executables, reserved names, data loss and excess sheets", () =>
        {
            foreach (var name in new[] { "../secret.docx", "C:\\secret.txt", "\\\\server\\secret.txt", "file.exe", "CON.txt", "a.docx:stream", "file.txt " })
                Reject(() => AiArtifacts.Create(new() { FileName = name, Text = "x" }));
            Reject(() => AiArtifacts.Create(new() { FileName = "a.xlsx", Text = "would disappear", Sheets = [new()] }));
            Reject(() => AiArtifacts.Create(new() { FileName = "a.xlsx", Sheets = [new() { Name = "same" }, new() { Name = "same" }] }));
            Check(AiArtifacts.Parse("No file requested").Count == 0, "Invented artifact");
            var parsed = AiArtifacts.Parse("Report\n```h2-file\n{\"fileName\":\"report.txt\",\"text\":\"Hello\"}\n```");
            Check(parsed.Count == 1 && Encoding.UTF8.GetString(AiArtifacts.Create(parsed[0])).Contains("Hello"), "Artifact contract not recognized");
        });
        foreach (var protocol in Enum.GetValues<AiProtocol>())
            test("Multimodal " + protocol + " sends native image format and trusted system instruction without credentials in body", () =>
            {
                var handler = new Stub(protocol); using var client = new AiClient(handler);
                var profile = new AiProfile { Model = "vision-test", Protocol = protocol, BaseUrl = protocol == AiProtocol.Ollama ? "http://localhost:11434" : "https://example.test/v1" };
                async Task Send() { await foreach (var _ in client.Stream(profile, "fake-secret", [new("system", "Trusted policy"), new("user", "Analyze image", [new("image/png", [1, 2, 3])])])) { } }
                Send().GetAwaiter().GetResult(); using var doc = JsonDocument.Parse(handler.Body!); var root = doc.RootElement;
                Check(!handler.Body!.Contains("fake-secret") && handler.Body.Contains("AQID"), "Key leak or missing image");
                if (protocol == AiProtocol.Ollama) Check(root.GetProperty("messages")[1].GetProperty("images")[0].GetString() == "AQID", "Wrong Ollama images");
                else if (protocol == AiProtocol.Gemini) { Check(root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString() == "Trusted policy", "System became user message"); Check(root.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inline_data").GetProperty("mime_type").GetString() == "image/png", "Wrong Gemini image"); }
                else if (protocol == AiProtocol.OpenAiResponses) Check(root.GetProperty("input")[1].GetProperty("content")[1].GetProperty("type").GetString() == "input_image", "Wrong Responses image");
                else Check(root.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString() == "data:image/png;base64,AQID", "Wrong Chat image");
            });
        test("Sent attachments and export audit round-trip in one project file while draft attachments stay device-local", () =>
        {
            var draftAttachment = AiDocuments.Read("draft-notes.txt", Encoding.UTF8.GetBytes("Bản nháp cục bộ"));
            var sentAttachment = AiDocuments.Read("notes.txt", Encoding.UTF8.GetBytes("Tài liệu đính kèm"));
            var conversation = new AiConversation
            {
                DraftAttachments = [draftAttachment],
                Messages = [new() { Content = "Artifact", Attachments = [sentAttachment], SavedFiles = [new("report.txt", "C:/user-selected/report.txt", "sha", DateTime.UtcNow)] }]
            };
            var project = new ProjectRecord { Conversations = [conversation] }; var state = new SheetState { Notes = [new() { Projects = [project] }] };
            var store = new ProjectWorkspaceStore(Path.Combine(folder, "attachments")); _ = store.LoadOrImport(); store.Save(state);
            var restored = store.Read(); var c = restored.Notes[0].Projects[0].Conversations.Single();
            Check(c.DraftAttachments.Count == 0, "Draft attachment leaked to shared NAS storage");
            Check(c.Messages[0].Attachments[0].Data.SequenceEqual(sentAttachment.Data) && c.Messages[0].Attachments[0].Text == sentAttachment.Text && c.Messages[0].SavedFiles.Count == 1, "Lost sent attachment/audit");
            Check(!File.ReadAllText(store.FilePath).Contains(sentAttachment.Sha256), "Attachment duplicated in index");
            var original = c.Messages[0].Attachments[0].Id; AiHistory.RenewIds([c], c.Id); Check(c.Messages[0].Attachments[0].Id != original, "Attachment ID was not remapped");
            ProjectWorkspaceStore.ValidateState(restored);
        });
        test("Workspace v3 upgrades every project to current schema with a backup before attachments are saved", () =>
        {
            var root = Path.Combine(folder, "attachments-v3-upgrade"); var store = new ProjectWorkspaceStore(root); _ = store.LoadOrImport(); store.Save(SheetStorage.Demo());
            var index = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(store.FilePath))!; index["SchemaVersion"] = 3;
            foreach (var entry in index["Files"]!.AsArray())
            {
                var path = Path.Combine(root, entry!["File"]!.GetValue<string>()); var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!;
                doc["SchemaVersion"] = 3; var bytes = Encoding.UTF8.GetBytes(doc.ToJsonString()); File.WriteAllBytes(path, bytes);
                entry["Hash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            }
            File.WriteAllText(store.FilePath, index.ToJsonString()); var before = File.ReadAllBytes(store.FilePath);
            var upgrade = new ProjectWorkspaceStore(root); var state = upgrade.Read(); upgrade.SaveIncremental(state, new HashSet<Guid>());
            var read = new ProjectWorkspaceStore(root).Read(); Check(read.Notes[0].Projects.Count == state.Notes[0].Projects.Count, "Migration lost projects");
            Check(JsonDocument.Parse(File.ReadAllBytes(store.FilePath)).RootElement.GetProperty("SchemaVersion").GetInt32() == ProjectWorkspaceStore.SchemaVersion, "Schema was not upgraded");
            Check(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.bak", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(before)), "No original index backup");
        });
        test("Chat draft files stay with project; marker attachment stays local; artifact buttons render beside history", () =>
        {
            var file = AiDocuments.Read("local.txt", Encoding.UTF8.GetBytes("private attachment"));
            var conversation = new AiConversation { DraftAttachments = [file], MarkerOnlyMode = true, Draft = "Local milestone", Messages = [new() { Role = "assistant", Content = "```h2-file\n{\"fileName\":\"Report.docx\",\"text\":\"Report text\"}\n```" }] };
            var project = new ProjectRecord { Conversations = [conversation] }; var panel = new AiChatPanel(new H2Notes.Avalonia.App(), () => throw new Exception("No network for marker")); panel.SetProject(project);
            var window = new Window { Content = panel, Width = 360, Height = 660 }; window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                Check(panel.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ChatSaveArtifact"), "Missing file save action");
                var files = panel.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "ChatDraftFiles"); Check(files.Children.Count == 1, "Draft file missing");
                panel.SetProject(new ProjectRecord()); Dispatcher.UIThread.RunJobs(); Check(files.Children.Count == 0, "Attachment leaked to another project");
                panel.SetProject(project); Dispatcher.UIThread.RunJobs();
                panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Check(conversation.DraftAttachments.Count == 0 && conversation.Messages.Last().Attachments.Single().Id == file.Id, "Marker failed to move draft attachments");
                Check(AiHistory.RequestTurns(conversation).All(t => !t.Content.Contains("private attachment")), "Marker attachment entered request");
            }
            finally { window.Close(); }
        });
        test("Project Agent send flushes editor and grounds exact snapshot without a confirmation dialog", () =>
        {
            var app = new H2Notes.Avalonia.App();
            var project = new ProjectRecord { Name = "Requested project", Notes = "stale" };
            var agent = new SnapshotAgentAdapter(project.Id);
            app.AgentAdapter = agent;
            var clientCalls = 0;
            var panel = new AiChatPanel(app, () => { clientCalls++; return new AiClient(); }); panel.SetProject(project);
            panel.PrepareProjectContext = () => project.NotesRich = RichDocument.Plain("Unsaved editor text");
            panel.ReadContext = () => project.NotesText;
            var window = new Window { Content = panel, Width = 560, Height = 660 }; window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                panel.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ChatComposer").Text = "Summarize this project";
                var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, null)!;
                var end = DateTime.UtcNow.AddSeconds(3); while (!send.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
                Check(send.IsCompleted && agent.Calls == 1 && clientCalls == 0 && !window.OwnedWindows.Any(), "Project Agent send used the wrong execution path or opened a dialog");
                send.GetAwaiter().GetResult();
                Check(agent.Context?.Summary?.Contains("Unsaved editor text", StringComparison.Ordinal) == true, "Agent received stale editor context");
                Check(project.Conversations[0].Messages.Count == 2 && project.Conversations[0].Messages.Last().Status == "complete", "Missing user/final Agent presentation");
                var other = new ProjectRecord { Name = "Other project" }; panel.SetProject(other);
                Check(other.Conversations.Count == 0 && agent.Calls == 1, "Project switch resent the Agent request or mixed history");
            }
            finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
        });
    }
    private sealed class SnapshotAgentAdapter : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private H2AgentTaskSummary? _summary;
        public SnapshotAgentAdapter(Guid projectId) { _projectId = projectId; TaskId = Guid.NewGuid(); }
        public Guid TaskId { get; }
        public int Calls { get; private set; }
        public H2AgentTaskContext? Context { get; private set; }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
        {
            Check(projectId == _projectId, "Wrong project ID reached snapshot Agent.");
            Calls++; Context = context; var now = DateTime.UtcNow;
            _summary = new H2AgentTaskSummary(TaskId, projectId, goal, H2AgentTaskStatus.Completed, null,
                Array.Empty<H2AgentEvidence>(), "OK", null, now, now);
            return Task.FromResult(TaskId);
        }
        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => taskId == TaskId && _summary is not null
                ? new(_summary, [new H2AgentProgress(0, DateTime.UtcNow, "final", "completed", "Completed")])
                : throw new KeyNotFoundException();
        public void CancelTask(Guid taskId) { }
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => taskId == TaskId && _summary is not null ? _summary : throw new KeyNotFoundException();
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is not null && (projectId is null || projectId == _projectId) ? [_summary] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }

    private sealed class Stub(AiProtocol protocol) : HttpMessageHandler
    {
        public string? Body; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = protocol switch
            {
                AiProtocol.Ollama => "{\"message\":{\"content\":\"OK\"},\"done\":true}\n",
                AiProtocol.Gemini => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                AiProtocol.OpenAiResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\ndata: [DONE]\n\n"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
