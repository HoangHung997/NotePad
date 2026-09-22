using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2AgentChatSurfaceTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Agent chat retains distinct thread and turn identities, drafts and replay after restart", () => WithRoot(root =>
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var firstTurn = Guid.NewGuid(); Guid first;
            using (var adapter = Adapter(root, new Script()))
            {
                first = adapter.StartTaskAsync(null, "Hello A", new(root, null, ThreadId: a, TurnId: firstTurn)).Result;
                Wait(adapter, first);
                Wait(adapter, adapter.StartTaskAsync(null, "Continue A", new(root, null, ThreadId: a, TurnId: Guid.NewGuid())).Result);
                Wait(adapter, adapter.StartTaskAsync(null, "Hello B", new(root, null, ThreadId: b, TurnId: Guid.NewGuid())).Result);
                adapter.SaveThread(adapter.GetThread(a)! with { Draft = "Bản nháp giữ lại" });
            }
            using var restarted = Adapter(root, new Script());
            Check(restarted.GetThreads().Count == 2, "Global conversations were merged");
            Check(restarted.GetThread(a)!.TaskIds!.Count == 2 && restarted.GetThread(b)!.TaskIds!.Count == 1, "Task ownership changed");
            Check(restarted.GetThread(a)!.Draft == "Bản nháp giữ lại", "Draft was lost");
            var replay = restarted.ObserveTask(first);
            Check(replay.Summary.ThreadId == a && replay.Summary.TurnId == firstTurn && replay.Progress.Count > 0, "Restart lost identity or activity");
            Check(restarted.ObserveTask(first, replay.Progress.Last().Sequence).Progress.Count == 0, "Replay cursor repeats activity");
        }));

        test("Agent chat applies busy follow-up exactly once inside the original runtime task", () => WithRoot(root =>
        {
            var script = new Script { Hold = true };
            using var adapter = Adapter(root, script);
            var task = adapter.StartTaskAsync(null, "Hello", new(root, null, ThreadId: Guid.NewGuid())).Result;
            var input = Guid.NewGuid();
            Check(adapter.SupplementTask(task, input, "Include the word follow-up"), "Active task refused input");
            Check(adapter.SupplementTask(task, input, "Include the word follow-up"), "Duplicate acknowledgement is not idempotent");
            script.Release.TrySetResult();
            var done = Wait(adapter, task);
            Check(done.Status == H2AgentTaskStatus.Completed && done.FinalText == "follow-up received", done.Error ?? "Supplement not applied");
            Check(script.Inputs.SequenceEqual(new[] { "Include the word follow-up" }), "Supplement was duplicated or replaced by original prompt");
            Check(script.Starts == 1 && script.Task == task, "Steering resubmitted original task");
            Check(!adapter.SupplementTask(task, Guid.NewGuid(), "too late"), "Completed task silently accepted input");
        }));

        test("Agent chat replays each activity once and keeps final answer outside collapsed activity", () =>
        {
            var now = DateTime.UtcNow; var view = new AgentTurnView(true);
            var task = new H2AgentTaskSummary(Guid.NewGuid(), null, "Request", H2AgentTaskStatus.Completed, null, [], "**Answer**", null, now, now);
            var observation = new H2AgentTaskObservation(task, [new(0, now, "tool", "tool-ok", "read_file")]);
            view.Present(null, observation); view.Present(null, observation);
            Check(view.EventCount == 1, "Duplicate replay created more rows");
            var activity = view.Children.OfType<Expander>().Single();
            Check(!activity.IsExpanded && view.Children.OfType<MarkdownMessageView>().Single().Markdown == "**Answer**", "Final answer collapsed into activity");
        });

        test("Agent chat queues in order and cancelling a queued turn does not cancel the running task", () => WithRoot(root =>
        {
            var script = new Script { Hold = true }; using var adapter = Adapter(root, script);
            var thread = Guid.NewGuid();
            var first = adapter.StartTaskAsync(null, "First", new(root, null, ThreadId: thread)).Result;
            var second = adapter.StartTaskAsync(null, "Second", new(root, null, ThreadId: thread, AfterTaskId: first)).Result;
            var cancelled = adapter.StartTaskAsync(null, "Cancel me", new(root, null, ThreadId: thread, AfterTaskId: second)).Result;
            Check(adapter.GetTaskSummary(second).Status == H2AgentTaskStatus.Queued && script.Starts == 1, "Queued turn executed concurrently");
            adapter.CancelTask(cancelled); script.Release.TrySetResult();
            Check(Wait(adapter, first).Status == H2AgentTaskStatus.Completed, "Cancelling queue cancelled running task");
            Check(Wait(adapter, second).Status == H2AgentTaskStatus.Completed, "Queue did not continue");
            Check(Wait(adapter, cancelled).Status == H2AgentTaskStatus.Cancelled && script.Starts == 2, "Cancelled queue executed");
        }));

        test("Agent chat explicit file scope does not grant siblings, parent folders or quoted path prefixes", () => WithRoot(root =>
        {
            var target = Path.Combine(root, "target space.txt"); File.WriteAllText(target, "fixture");
            var targets = H2AgentTargetScope.FromUserRequest("Read \"" + target + "\" now");
            Check(targets.Count == 1 && H2AgentTargetScope.Contains(targets, target), "Quoted target was broadened or lost");
            Check(!H2AgentTargetScope.Contains(targets, root) && !H2AgentTargetScope.Contains(targets, Path.Combine(root, "sibling.txt")), "File granted parent/sibling");
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            var workspace = new H2AgentLab.SafeWorkspace(work, additionalTarget: p => H2AgentTargetScope.Contains(targets, p));
            Check(workspace.Resolve(target) == target, "Explicit file not readable");
            try { workspace.Resolve(Path.Combine(root, "sibling.txt")); throw new Exception("Sibling escaped scope"); }
            catch (H2AgentLab.AgentFaultException) { }
        }));

        test("Agent chat CSV preview retains multiline quoted cells and bounds each page", () => WithRoot(root =>
        {
            var path = Path.Combine(root, "sample.csv");
            File.WriteAllText(path, "Name,Value\n\"two\nlines\",\"a,b\"\n" + string.Join("\n", Enumerable.Range(0, 100).Select(i => i + ",=1+1")));
            var first = AgentSpreadsheetPreview.Read(path, "", 0); var second = AgentSpreadsheetPreview.Read(path, "", 1);
            Check(first.Rows.Count == 40 && first.HasMore && second.Rows[0].Number == 41, "Paging lost or repeated rows");
            Check(first.Rows[1].Cells[0].Value == "two\nlines" && first.Rows[1].Cells[1].Value == "a,b", "Quoted CSV was split incorrectly");
            Check(first.Rows[2].Cells[1].Value == "=1+1" && first.Rows[2].Cells[1].Formula is null, "CSV value interpreted as executable formula");
        }));

        test("Agent chat Markdown keeps unchanged blocks, literal code and bounds large table controls", () =>
        {
            var view = new MarkdownMessageView();
            view.SetMarkdown("First paragraph.\n\nGrowing"); var first = view.Children[0];
            view.SetMarkdown("First paragraph.\n\nGrowing answer");
            Check(ReferenceEquals(first, view.Children[0]), "Streaming recreated stable blocks");
            view.SetMarkdown("**Data:**\n| A | B |\n| :--- | :--- |\n| one | two |\n\n```text\n**Data:**\n| A | B |\n| --- | --- |\n```");
            var renderedTable = view.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<ScrollViewer>()).Select(s => s.Content).OfType<Grid>().Count(c => c.Name == "MarkdownTable");
            Check(renderedTable == 1, "Provider table beside prose did not become a table");
            view.SetMarkdown("| A | B |\n|---|---|\n" + string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"|{i}|value|")) + "\n\n```text\n|literal|table|\n```\n\n<script>alert(1)</script>");
            var window = new Window { Content = view, Width = 500, Height = 600 }; window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                Check(view.GetVisualDescendants().Count(c => c.Name == "MarkdownTableCell") <= 62, "Large table eagerly created all rows");
                Check(view.GetVisualDescendants().Any(c => c.Name == "MarkdownCodeText"), "Code was reinterpreted");
                Check(view.GetVisualDescendants().Any(c => c.Name == "MarkdownHtmlLiteral"), "Raw HTML was not literal");
            }
            finally { window.Close(); }
        });

        test("Agent chat XLSX preview pages sparse rows without dropping formulas or changing the file", () => WithRoot(root =>
        {
            var path = Path.Combine(root, "sample.xlsx");
            using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
            {
                var book = document.AddWorkbookPart(); book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
                var sheet = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                var data = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
                for (uint i = 1; i <= 101; i++) data.Append(new DocumentFormat.OpenXml.Spreadsheet.Row(
                    new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "B" + (i * 2), CellFormula = new("1+1"), CellValue = new("2") }) { RowIndex = i * 2 });
                sheet.Worksheet = new(data);
                book.Workbook.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheets(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    { Id = book.GetIdOfPart(sheet), SheetId = 1, Name = "Sparse" }));
            }
            var before = File.ReadAllBytes(path);
            var first = AgentSpreadsheetPreview.Read(path, "Sparse", 0); var second = AgentSpreadsheetPreview.Read(path, "Sparse", 1);
            var third = AgentSpreadsheetPreview.Read(path, "Sparse", 2);
            Check(first.Rows.Count == 40 && second.Rows.Count == 40 && third.Rows.Count == 21 && !third.HasMore, "XLSX pagination dropped rows");
            Check(second.Rows[0].Number == 82 && second.Rows[0].Cells.Single() == new AgentPreviewCell("B82", "2", "1+1"), "Sparse coordinate/formula changed");
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "Preview modified workbook");
        }));

        test("Agent chat bounds conversation controls and reveals earlier turns in chronological order", () =>
        {
            var now = DateTime.UtcNow;
            var tasks = Enumerable.Range(0, 65).Select(i => new H2AgentTaskSummary(Guid.NewGuid(), null, "Turn " + i,
                H2AgentTaskStatus.Completed, null, [], "Answer", null, now.AddSeconds(i), now.AddSeconds(i))).ToArray();
            var view = new AgentChatSurface(); view.PresentTasks(null, tasks, "one");
            Check(view.Timeline.Children.OfType<AgentTurnView>().Count() == 30, "All history rendered eagerly");
            view.Timeline.Children.OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Check(view.Timeline.Children.OfType<AgentTurnView>().Count() == 60, "Earlier turns not loaded");
            view.PresentTasks(null, [], "two");
            Check(!view.Timeline.Children.OfType<AgentTurnView>().Any(), "Old conversation leaked into new thread");
        });

        test("Agent chat artifact inspector applies native tab theme and exposes spreadsheet preview", () => WithRoot(root =>
        {
            var path = Path.Combine(root, "preview.csv"); File.WriteAllText(path, "Name,Value\nAlpha,42");
            var view = new AgentArtifactView(new("preview", "artifact", null, "CSV fixture", LocalPath: path));
            var window = new Window { Content = view, Width = 600, Height = 520 }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            try
            {
                Check(view.GetVisualDescendants().OfType<TabItem>().Any(t => t.IsVisible), "Inspector tabs have no visual theme");
                Check(view.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Sao chép trang"), "Spreadsheet preview is blank");
            }
            finally { window.Close(); }
        }));
    }

    private static H2ProductionAgentAdapter Adapter(string root, Script script) => new(Path.Combine(root, "state"),
        () => new(new AiProfile { Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1", Model = "ci" }, ""), script);

    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        { var task = adapter.GetTaskSummary(id); if (H2AgentActivity.IsTerminal(task.Status)) return task; Thread.Sleep(10); }
        throw new TimeoutException("Agent task did not finish");
    }
    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-chat-surface-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Script : IAgentTransportFactory, IAgentTransport
    {
        public bool Hold; public int Starts; public Guid Task;
        public readonly List<string> Inputs = [];
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAgentTransport Create(AiProfile profile, string apiKey, AgentRunTelemetry telemetry) => this;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Starts++; Task = request.TaskId;
            if (Hold) await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return AgentTransportEvent.TextDeltaEvent("Hello"); yield return AgentTransportEvent.Complete();
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(request.SupplementalUserMessages ?? []);
            yield return AgentTransportEvent.TextDeltaEvent("follow-up received"); yield return AgentTransportEvent.Complete();
            await System.Threading.Tasks.Task.CompletedTask;
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
