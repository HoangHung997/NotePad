using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

// E2 only: concrete host/runtime/disk executor/archive; a scripted model, not native Office.
internal static class H2AgentDiskOfficeSourceTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
        foreach (var live in new[] { false, true })
        foreach (var tool in new[] { "word_paragraphs", "check_word" })
            test($"AR-066 disk Office source {tool} live={live} project={project}",
                () => Task.Run(() => Execute(project, live, tool)).GetAwaiter().GetResult());
    }

    private static async Task Execute(bool project, bool live, string tool)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar066-office-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var source = Path.Combine(workspace, "source.docx"); var control = Path.Combine(workspace, "control.txt");
        var marker = Path.Combine(workspace, "unexpected.txt");
        using (var doc = WordprocessingDocument.Create(source, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("DISK-ONLY-SENTINEL")))));
            main.Document.Save();
        }
        await File.WriteAllTextAsync(control, "UNCHANGED");
        var before = Hash(source); var controlBefore = Hash(control);
        Check(AiDocuments.Read(source).Text.Contains("DISK-ONLY-SENTINEL", StringComparison.Ordinal), "Invalid fixture document.");
        var wire = new Wire(tool); var factory = new Factory(wire);
        var profile = new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "scripted-ar066", BaseUrl = "https://example.test/v1" };
        var state = Path.Combine(root, "state");
        Guid task; H2AgentTaskSummary original;
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
            {
                var grant = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope;
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null,
                    live ? "Đọc tài liệu Word đang mở; không thay bằng bản trên đĩa." : "Inspect the saved disk file source.docx.",
                    new(workspace, "AR066 disposable DOCX", PermissionScope: grant), readOnly: true);
                var until = Environment.TickCount64 + 20000;
                original = adapter.GetTaskSummary(task);
                while (!H2AgentActivity.IsTerminal(original.Status) && Environment.TickCount64 < until)
                { await Task.Delay(10); original = adapter.GetTaskSummary(task); }
                Check(H2AgentActivity.IsTerminal(original.Status), "Bounded fixture timed out.");
                var result = wire.Results.Single(r => r.ToolCallId == "disk-office").Content;
                if (live)
                {
                    Check(result.Contains("live_resource_required", StringComparison.Ordinal), "Disk Office tool bypassed live source admission.");
                    Check(!result.Contains("DISK-ONLY-SENTINEL", StringComparison.Ordinal) && original.Status != H2AgentTaskStatus.Completed,
                        "Disk observation escaped as completed live work.");
                }
                else
                {
                    Check(!result.Contains("live_resource_required", StringComparison.Ordinal), "Explicit saved-file work was blocked.");
                    Check(tool == "word_paragraphs" ? result.Contains("DISK-ONLY-SENTINEL", StringComparison.Ordinal)
                        : LeadingJson(result).GetProperty("structureValid").GetBoolean(), "Real disk executor result missing.");
                    Check(original.Status == H2AgentTaskStatus.Completed, "Explicit disk task did not complete.");
                }
                Check(Hash(source) == before && Hash(control) == controlBefore && !File.Exists(marker), "Read-only case changed fixture bytes.");
            }
            var rounds = wire.Rounds;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
            {
                Check(reopened.ObserveTask(task).Summary.Status == original.Status && factory.Created == 1 && wire.Rounds == rounds,
                    "Reopening allocated a provider or replayed the disk operation.");
                Check(Hash(source) == before && Hash(control) == controlBefore && !File.Exists(marker), "Reopen changed fixture bytes.");
                var receiptRoot = Environment.GetEnvironmentVariable("H2_AR066_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(receiptRoot))
                {
                    Directory.CreateDirectory(receiptRoot);
                    File.WriteAllText(Path.Combine(receiptRoot, "disk-office-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
                    {
                        level = "E2", injected = "scripted model; actual disk Word executor, not native Office", task, project,
                        mode = tool + (live ? "-live" : "-disk"), factory.Created, wire.Rounds, status = original.Status.ToString(),
                        sourceHash = before, wordHash = before, controlHash = controlBefore,
                        forbiddenMarkerAbsent = !File.Exists(marker), reopenedWithoutReplay = true,
                        E3 = "AWAITING_ENVIRONMENT", E4 = "AWAITING_ENVIRONMENT", E5 = "DEFERRED_BY_USER"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static JsonElement LeadingJson(string text)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text));
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Factory(Wire wire) : IAgentTransportFactory
    {
        public int Created;
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) { Created++; return wire; }
    }
    private sealed class Wire(string tool) : IAgentTransport
    {
        public int Rounds; public List<AgentToolResult> Results = [];
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken ct = default) => Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken ct = default)
        { Results.AddRange(request.ToolResults); return Round(ct); }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await Task.CompletedTask; Rounds++;
            if (Rounds == 1) yield return AgentTransportEvent.Tool(new("search", "tool_search", JsonSerializer.Serialize(new { query = tool })));
            else if (Rounds == 2) yield return AgentTransportEvent.Tool(new("disk-office", tool, JsonSerializer.Serialize(new { path = "source.docx" })));
            else yield return AgentTransportEvent.TextDeltaEvent("Scripted final; host outcome is authoritative.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
