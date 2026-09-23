using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>Additional E2: real production file write and journal, intercepted Ollama wire;
/// actual headless Settings window save in the runner's isolated local configuration. No API,
/// native GUI/model acceptance, personal data or user credential is involved.</summary>
internal static class H2AgentRequestBudgetPersistenceTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-050 settings save preserves explicitly sourced request limits", () =>
        {
            var isolated = Environment.GetEnvironmentVariable(LocalConfiguration.SettingsDirectoryEnvironmentVariable);
            Check(isolated is not null && isolated.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "Settings test requires isolated runner configuration.");
            var settings = new AiRequestBudgetSettings { ContextLimitTokens = 8192, ContextLimitSource = "ar050-settings-fixture-v1",
                ReservedOutputTokens = 512, SafetyMarginTokens = 256, MaxSerializedBytes = 500000,
                NativeImageTokenEstimate = 1234, NativeFileTokenEstimate = 4321, MediaEstimateSource = "ar050-media-fixture-v1" };
            var profile = new AiProfile { Name = "AR050-SETTINGS-ORIGINAL", Model = "fixture-only", RequestBudget = settings };
            var app = new App(); app.LocalSettings.Ai.Profiles = [profile]; app.LocalSettings.Ai.SelectedId = profile.Id;
            var window = new AiSettingsWindow(app); window.Show(); Pump(window);
            try
            {
                window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Text == profile.Name).Text = "AR050-SETTINGS-RENAMED";
                window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Lưu kết nối")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(5) && app.LocalSettings.Ai.Profiles.Single().Name != "AR050-SETTINGS-RENAMED")
                { Pump(window); Thread.Sleep(5); }
                Check(app.LocalSettings.Ai.Profiles.Single().Name == "AR050-SETTINGS-RENAMED", "Settings save did not complete.");
                Check(app.LocalSettings.Ai.Profiles.Single().RequestBudget == settings, "Settings draft dropped the configured budget.");
                Check(LocalConfiguration.Read().Ai.Profiles.Single(x => x.Id == profile.Id).RequestBudget == settings, "Persisted settings dropped the configured budget.");
                Check(profile.RequestBudget == settings, "Editing mutated the old immutable settings.");
            }
            finally { window.Close(); }
        });
        foreach (var project in new[] { false, true })
            test("AR-050 overflow after a real write preserves evidence and never repeats the effect " + (project ? "Project" : "Global"), () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "h2-ar050-effects-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                try
                {
                    const int context = 1_000_000;
                    var profile = new AiProfile { Model = "fixture-no-network", RequestBudget = new() { ContextLimitTokens = context,
                        ContextLimitSource = "ar050-post-effect-fixture-v1", ReservedOutputTokens = 64, SafetyMarginTokens = 32 } };
                    var factory = new PostEffectFactory(context); var state = Path.Combine(root, "state"); Guid id;
                    var file = Path.Combine(root, "written-once.txt"); File.WriteAllText(Path.Combine(root, "control.txt"), "UNTOUCHED");
                    using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
                    {
                        var scope = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope;
                        id = adapter.StartTaskAsync(project ? Guid.NewGuid() : null, "Write written-once.txt; preserve control.txt",
                            new(root, "Disposable AR-050 fixture", PermissionScope: scope), false).GetAwaiter().GetResult();
                        var summary = Wait(adapter, id);
                        Check(summary.Status == H2AgentTaskStatus.Blocked && summary.Error?.Contains("request_budget_exceeded") == true,
                            "Post-effect overflow was not a budget blocker: " + summary.Status + " " + summary.Error);
                        Check(factory.Sends == 2 && File.ReadAllText(file) == "WRITTEN-ONCE-ĐÚNG", "Write was lost or duplicate request was sent.");
                        Check(File.ReadAllText(Path.Combine(root, "control.txt")) == "UNTOUCHED", "Unrelated fixture changed.");
                        Check(summary.GoalState is { MutationRevisions.Count: 1 } && summary.Evidence.Count > 0, "Actual effect/evidence was lost at budget exception.");
                        Check(factory.Decisions.Count == 3 && factory.Decisions.Take(2).All(x => x.Allowed) && !factory.Decisions[^1].Allowed,
                            "Actual start, tool continuation and blocked post-write continuation were not measured.");
                        adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    }
                    using var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory);
                    var restored = reopened.GetTaskSummary(id);
                    Check(restored.Status == H2AgentTaskStatus.Blocked && restored.GoalState is { MutationRevisions.Count: 1 }
                        && restored.Evidence.Count > 0 && factory.Sends == 2 && File.ReadAllText(file) == "WRITTEN-ONCE-ĐÚNG",
                        "Reopen erased effect history or auto-replayed a mutation.");
                    Save(project ? "post-effect-project" : "post-effect-global", new { id, sends = factory.Sends,
                        actualFile = File.ReadAllText(file), status = restored.Status.ToString(), restored.GoalState, restored.Evidence,
                        budgets = factory.Decisions });
                }
                finally { try { Directory.Delete(root, true); } catch (IOException) { } }
            });
    }
    private sealed class PostEffectFactory(int contextLimit) : IAgentTransportFactory
    {
        public int Sends;
        public List<AgentRequestBudgetReceipt> Decisions { get; } = [];
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry)
        {
            var transport = new OllamaTransport(profile, new Handler(this, contextLimit));
            transport.RequestBudgetEvaluated += Decisions.Add;
            return transport;
        }
        private sealed class Handler(PostEffectFactory owner, int limit) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var raw = await request.Content!.ReadAsStringAsync(token);
                var receipt = owner.Decisions.LastOrDefault();
                Check(receipt is { Allowed: true } && receipt.Sequence == owner.Sends + 1
                    && receipt.SerializedBytes == Encoding.UTF8.GetByteCount(raw), "Provider send preceded the matching budget decision.");
                owner.Sends++;
                Check(owner.Sends <= 2, "Unexpected network boundary after the real write.");
                var search = owner.Sends == 1;
                var call = new { function = new { name = search ? "tool_search" : "write_text", arguments = search
                    ? JsonSerializer.SerializeToElement(new { query = "write_text" })
                    : JsonSerializer.SerializeToElement(new { path = "written-once.txt", text = "WRITTEN-ONCE-ĐÚNG", expectedHash = "" }) } };
                // Synthetic provider usage intentionally reveals an underestimate only after its
                // accepted response. The following local mutation happens once; the next request
                // must account for the observed correction and stay off this handler.
                var response = JsonSerializer.Serialize(new { message = new { role = "assistant", content = "", tool_calls = new[] { call } },
                    done = true, prompt_eval_count = search ? 20 : limit, eval_count = 1 });
                return new(HttpStatusCode.OK) { Content = new StringContent(response + "\n", Encoding.UTF8, "application/x-ndjson") };
            }
        }
    }
    private static H2AgentTaskSummary Wait(H2ProductionAgentAdapter adapter, Guid id)
    {
        var clock = Stopwatch.StartNew(); while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var value = adapter.GetTaskSummary(id);
            if (value.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled) return value;
            Thread.Sleep(10);
        }
        adapter.CancelTask(id); throw new TimeoutException("Post-effect fixture did not drain.");
    }
    private static void Save(string name, object value)
    {
        var folder = Environment.GetEnvironmentVariable("H2_AR050_EVIDENCE_DIR"); if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
