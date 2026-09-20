using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantContextChipTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant context chips show current interpretation and support remove reset scope", () =>
        {
            var context = Context();
            var compact = new WorkAssistantCompactWindow(new WorkAssistantSettings());
            compact.SetActiveContext(context);
            compact.Show();
            Pump();
            try
            {
                var chips = Chips(compact);
                Check(chips.Length == 4, "Expected application/document/session/selection chips.");
                Check(chips.Any(button => Text(button).Contains("Excel", StringComparison.Ordinal)),
                    "Application context chip missing.");
                Check(chips.Any(button => Text(button).Contains("DuToan.xlsx", StringComparison.Ordinal)),
                    "Document context chip missing.");
                Check(chips.Any(button => Text(button).Contains("BAOCAOGS", StringComparison.Ordinal)),
                    "Session context chip missing.");
                Check(chips.Any(button => Text(button).Contains("D51:F80", StringComparison.Ordinal)),
                    "Selection context chip missing.");

                Check(compact.SelectedContextScope == WorkAssistantContextScope.All,
                    "Default context scope did not include all available chips.");
                Check(compact.SelectedContextSummary.Contains("Document=C:\\Projects\\DuToan.xlsx", StringComparison.Ordinal)
                    && compact.SelectedContextSummary.Contains("Session=BAOCAOGS", StringComparison.Ordinal)
                    && compact.SelectedContextSummary.Contains("Selection=D51:F80", StringComparison.Ordinal),
                    "Selected context summary did not preserve full scoped grounding.");

                Chip(compact, "WorkAssistantContextSelectionChip")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check((compact.SelectedContextScope & WorkAssistantContextScope.Selection) == 0,
                    "Removing selection chip did not narrow selected scope.");
                Check(!compact.SelectedContextSummary.Contains("D51:F80", StringComparison.Ordinal),
                    "Removed selection still leaked into send summary.");
                Check(compact.SelectedContextSummary.Contains("Document=C:\\Projects\\DuToan.xlsx", StringComparison.Ordinal)
                    && compact.SelectedContextSummary.Contains("Session=BAOCAOGS", StringComparison.Ordinal),
                    "Removing selection accidentally removed unrelated document/session scope.");

                var reset = (Button)(typeof(WorkAssistantCompactWindow)
                    .GetField("_contextReset", Private)!
                    .GetValue(compact)
                    ?? throw new Exception("Context reset button missing."));
                Check(reset.IsVisible, "Reset scope action was not exposed after narrowing context.");
                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(compact.SelectedContextScope == WorkAssistantContextScope.All
                    && compact.SelectedContextSummary.Contains("D51:F80", StringComparison.Ordinal),
                    "Reset scope did not restore captured interpretation.");

                compact.RemoveContextScope(WorkAssistantContextScope.Document | WorkAssistantContextScope.Selection);
                Check(!compact.SelectedContextSummary.Contains("DuToan.xlsx", StringComparison.Ordinal)
                    && !compact.SelectedContextSummary.Contains("D51:F80", StringComparison.Ordinal)
                    && compact.SelectedContextSummary.Contains("Session=BAOCAOGS", StringComparison.Ordinal),
                    "Programmatic scope change did not stay selective.");

                compact.RemoveContextScope(WorkAssistantContextScope.All);
                Check(compact.SelectedContextScope == WorkAssistantContextScope.None
                    && compact.SelectedContextSummary.Length == 0,
                    "Removing all chips did not produce empty context for the next send.");
            }
            finally
            {
                compact.Close();
            }
        });

        test("App projects captured context into chips without weakening stale-target revalidation", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            var capture = new FakeCapture(Context());
            app.WorkAssistantContextCapture = capture;

            var sharedBefore = JsonSerializer.Serialize(app.State);
            app.ShowWorkAssistantCompact();
            Pump();
            try
            {
                var compact = Compact(app);
                Check(capture.CaptureCalls == 1,
                    "ShowWorkAssistantCompact did not capture foreground context exactly once.");
                Check(compact.CapturedContext?.DocumentSessionId == "BAOCAOGS"
                    && compact.SelectedContextSummary.Contains("D51:F80", StringComparison.Ordinal),
                    "Captured ActiveWorkContext was not projected into compact chips.");

                compact.RemoveContextScope(WorkAssistantContextScope.Selection);
                Check(!compact.SelectedContextSummary.Contains("D51:F80", StringComparison.Ordinal),
                    "UI scope removal did not affect next-send interpretation.");

                Check(app.TryGetValidatedWorkAssistantContext(out var validated)
                    && validated?.Selection == "D51:F80",
                    "UI chip removal weakened/rewrote the full context used for target revalidation.");
                Check(capture.RevalidateCalls == 1,
                    "App stale-target guard did not revalidate the original capture.");

                Check(sharedBefore == JsonSerializer.Serialize(app.State),
                    "Opening/changing context chips mutated shared SheetState/project truth.");
            }
            finally
            {
                Compact(app).Close();
            }
        });

        test("Context chip scope stays RAM-only and starts no work by itself", () =>
        {
            var localJson = JsonSerializer.Serialize(new LocalConfiguration());
            var sharedJson = JsonSerializer.Serialize(new SheetState());
            foreach (var marker in new[]
            {
                "WorkAssistantContextScope",
                "SelectedContextScope",
                "ContextChip",
                "ActiveWorkContext"
            })
            {
                Check(!localJson.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "Context chip scope leaked into local persistent config: " + marker);
                Check(!sharedJson.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "Context chip scope leaked into shared workspace JSON: " + marker);
            }

            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "WorkAssistantCompactWindow.cs"));
            foreach (var forbidden in new[]
            {
                "StartTaskAsync(",
                "ProjectRecord",
                "ProjectWorkspaceStore",
                "ScheduleSave(",
                "SaveNow("
            })
                Check(!source.Contains(forbidden, StringComparison.Ordinal),
                    "Context-chip UI performs work/persistence by itself: " + forbidden);
        });
    }

    private static H2ActiveWorkContext Context()
        => new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 987654321,
            ProcessName: "excel",
            ApplicationKind: H2ApplicationKind.Excel,
            NativeWindowHandle: 0x1234,
            WindowIdentity: "win32:1234:4242:987654321",
            WindowTitle: "DuToan.xlsx - Excel",
            DocumentSessionId: "BAOCAOGS",
            DocumentPath: @"C:\Projects\DuToan.xlsx",
            Selection: "D51:F80",
            Provider: "OfficeHost",
            CapturedUtc: DateTime.UtcNow);

    private static Button[] Chips(WorkAssistantCompactWindow compact)
    {
        var panel = (WrapPanel)(typeof(WorkAssistantCompactWindow)
            .GetField("_contextChips", Private)!
            .GetValue(compact)
            ?? throw new Exception("Context chip panel missing."));
        return panel.Children.OfType<Button>().ToArray();
    }

    private static Button Chip(WorkAssistantCompactWindow compact, string name)
        => Chips(compact).Single(button => button.Name == name);

    private static string Text(Button button)
        => button.Content?.ToString() ?? "";

    private static WorkAssistantCompactWindow Compact(App app)
    {
        var field = typeof(App).GetField("_workAssistantCompact", Private)
            ?? throw new Exception("App._workAssistantCompact field missing.");
        return (WorkAssistantCompactWindow)(field.GetValue(app)
            ?? throw new Exception("Compact Work Assistant was not created."));
    }

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FakeCapture : IWorkAssistantActiveContextCapture
    {
        private readonly H2ActiveWorkContext _context;

        public FakeCapture(H2ActiveWorkContext context)
            => _context = context;

        public int CaptureCalls { get; private set; }
        public int RevalidateCalls { get; private set; }

        public H2ActiveWorkContext? Capture()
        {
            CaptureCalls++;
            return _context;
        }

        public bool Revalidate(H2ActiveWorkContext context)
        {
            RevalidateCalls++;
            return context.WindowIdentity == _context.WindowIdentity;
        }
    }
}
