using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantHotkeyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant hotkey parser normalizes configurable chords and rejects invalid input", () =>
        {
            Check(WorkAssistantHotkeyParser.TryParse(
                "control + shift + space",
                out var chord,
                out var error),
                "Valid hotkey did not parse: " + error);
            Check(chord.Normalized == "Ctrl+Shift+Space",
                "Hotkey normalization is wrong: " + chord.Normalized);
            Check((chord.Modifiers & 0x4000) != 0,
                "MOD_NOREPEAT was not applied.");

            Check(WorkAssistantHotkeyParser.TryParse(
                "Alt+F8",
                out var functionKey,
                out _)
                && functionKey.Normalized == "Alt+F8",
                "Configurable function-key hotkey failed.");

            Check(!WorkAssistantHotkeyParser.TryParse("A", out _, out var noModifier)
                && !string.IsNullOrWhiteSpace(noModifier),
                "Hotkey without modifier should fail.");
            Check(!WorkAssistantHotkeyParser.TryParse("Ctrl+Control+A", out _, out var duplicate)
                && !string.IsNullOrWhiteSpace(duplicate),
                "Duplicate modifier should fail.");
            Check(!WorkAssistantHotkeyParser.TryParse("Ctrl+Shift+Enter", out _, out var unsupported)
                && !string.IsNullOrWhiteSpace(unsupported),
                "Unsupported main key should fail closed.");
        });

        test("Work Assistant hotkey conflict fails gracefully and binding stays configurable", () =>
        {
            var registration = new FakeRegistration { FailRegistration = true };
            var presses = 0;
            using var controller = new WorkAssistantHotkeyController(
                registration,
                () => presses++);

            Check(!controller.Apply("Ctrl+Shift+Space"),
                "Conflict registration unexpectedly succeeded.");
            Check(!controller.IsRegistered
                && !string.IsNullOrWhiteSpace(controller.Error)
                && presses == 0,
                "Conflict did not fail softly.");
            Check(registration.RegisterAttempts == 1,
                "Conflict was not surfaced by registration boundary.");

            registration.FailRegistration = false;
            Check(controller.Apply("Ctrl+Alt+K"),
                "Valid replacement hotkey did not register.");
            Check(controller.IsRegistered && controller.Binding == "Ctrl+Alt+K",
                "Registered binding was not normalized/exposed.");
            registration.Trigger();
            Check(presses == 1, "Registered hotkey callback was not invoked.");

            Check(controller.Apply("Alt+F8"),
                "Changing hotkey binding failed.");
            Check(controller.Binding == "Alt+F8"
                && registration.UnregisterCalls >= 2,
                "Changing binding did not unregister the previous shortcut.");
        });

        test("Work Assistant hotkey callback only opens compact assistant and does not start or mutate work", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.LocalSettings.WorkAssistant.Hotkey = "Ctrl+Shift+Space";
            var agent = new CountingAgentAdapter();
            app.AgentAdapter = agent;

            var before = JsonSerializer.Serialize(app.State);
            var registration = new FakeRegistration();
            using var controller = new WorkAssistantHotkeyController(
                registration,
                app.ShowWorkAssistantCompact);

            Check(controller.Apply(app.LocalSettings.WorkAssistant.Hotkey),
                "Hotkey could not be registered in test boundary.");
            registration.Trigger();
            Pump();

            Check(app.IsWorkAssistantCompactVisible,
                "Hotkey callback did not open compact assistant.");
            Check(agent.StartCalls == 0,
                "Opening compact assistant started an Agent task.");
            Check(before == JsonSerializer.Serialize(app.State),
                "Opening compact assistant mutated shared SheetState/project truth.");

            var compact = Compact(app);
            Check(compact.PromptText.Length == 0,
                "Hotkey opening fabricated a prompt.");
            var statusField = typeof(WorkAssistantCompactWindow).GetField(
                "_status",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("Compact assistant status field missing.");
            var status = (Avalonia.Controls.TextBlock)(statusField.GetValue(compact)
                ?? throw new Exception("Compact assistant status control missing."));
            Check((status.Text ?? "").Contains("chưa gửi", StringComparison.OrdinalIgnoreCase),
                "Compact assistant does not communicate non-mutating hotkey behavior.");

            compact.Close();
        });

        test("Work Assistant hotkey stays machine-local and is not shared project truth", () =>
        {
            var local = new LocalConfiguration();
            local.WorkAssistant.Enabled = true;
            local.WorkAssistant.Hotkey = "Alt+F8";
            var localJson = JsonSerializer.Serialize(local);
            var restored = JsonSerializer.Deserialize<LocalConfiguration>(localJson)
                ?? throw new Exception("Could not deserialize local configuration.");
            Check(restored.WorkAssistant.Hotkey == "Alt+F8",
                "Local Work Assistant hotkey did not round-trip.");

            var shared = JsonSerializer.Serialize(new SheetState());
            foreach (var marker in new[]
            {
                "WorkAssistant", "Hotkey", "GlobalHotkey", "CompactAssistant"
            })
                Check(!shared.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "Work Assistant hotkey leaked into shared SheetState: " + marker);
        });
    }

    private static WorkAssistantCompactWindow Compact(App app)
    {
        var field = typeof(App).GetField(
            "_workAssistantCompact",
            BindingFlags.Instance | BindingFlags.NonPublic)
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

    private sealed class FakeRegistration : IWorkAssistantHotkeyRegistration
    {
        private Action? _callback;
        public bool IsSupported { get; set; } = true;
        public bool FailRegistration { get; set; }
        public int RegisterAttempts { get; private set; }
        public int UnregisterCalls { get; private set; }

        public bool TryRegister(
            WorkAssistantHotkeyChord chord,
            Action onPressed,
            out string? error)
        {
            RegisterAttempts++;
            if (FailRegistration)
            {
                error = "Hotkey đang được ứng dụng khác sử dụng.";
                _callback = null;
                return false;
            }

            error = null;
            _callback = onPressed;
            return true;
        }

        public void Trigger() => _callback?.Invoke();

        public void Unregister()
        {
            UnregisterCalls++;
            _callback = null;
        }

        public void Dispose() => Unregister();
    }

    private sealed class CountingAgentAdapter : IH2AgentAdapter
    {
        public int StartCalls { get; private set; }

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(Guid.NewGuid());
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => throw new NotSupportedException();
        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => throw new KeyNotFoundException();
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}
