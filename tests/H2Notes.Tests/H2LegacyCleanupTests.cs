using System.Reflection;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2LegacyCleanupTests
{
    public static void Run(Action<string, Action> test)
    {
        test("H2M-120 legacy AiProjectContext is absent from new project Agent execution", () =>
        {
            var repo = FindRepoRoot();
            Check(!File.Exists(Path.Combine(repo, "src", "H2Notes.Core", "AiProjectContext.cs")),
                "Retired AiProjectContext source returned.");
            Check(typeof(ProjectRecord).Assembly.GetType("H2Notes.Core.AiProjectContext") is null,
                "Retired AiProjectContext runtime type returned.");

            var agent = ReadSource("src/H2Notes.Avalonia/Controls/AiChatPanel.Agent.cs");
            foreach (var forbidden in new[]
            {
                "AiProjectContext",
                "AiLegacyRequestContext",
                "AiClient",
                "SecretVault",
                "AiProjectActions"
            })
                Check(!agent.Contains(forbidden, StringComparison.Ordinal),
                    "New project Agent path depends on legacy request/action runtime: " + forbidden);

            Check(agent.Contains("new H2AgentTaskContext(", StringComparison.Ordinal)
                  && agent.Contains("BoundAgentContext(summary.ToString(), 15_000)", StringComparison.Ordinal),
                "Project Agent grounding is no longer explicitly bounded by H2AgentTaskContext.");
        });

        test("H2M-121 h2-actions remain historical preview only with no execution dependency", () =>
        {
            var actions = ReadSource("src/H2Notes.Avalonia/Controls/AiChatPanel.Actions.cs");
            var responsive = ReadSource("src/H2Notes.Avalonia/MainWindow.Responsive.cs");
            var legacy = ReadSource("src/H2Notes.Core/AiLegacyRequestContext.cs");

            Check(actions.Contains("historical user data only", StringComparison.OrdinalIgnoreCase)
                  && actions.Contains("typed IH2ProjectToolHost", StringComparison.Ordinal),
                "Legacy action renderer does not document the typed-tool ownership boundary.");

            foreach (var forbidden in new[]
            {
                "AiProjectActions.Validate",
                "AiProjectActions.Apply",
                "Dialogs.Confirm",
                "ProjectActionsRequested",
                "ApplyProjectActions(",
                "ApplyAutomaticProjectActions"
            })
                Check(!actions.Contains(forbidden, StringComparison.Ordinal),
                    "Legacy project action renderer still has an execution hook: " + forbidden);

            Check(!responsive.Contains("ProjectActionsRequested", StringComparison.Ordinal),
                "MainWindow still subscribes to legacy project action execution.");
            Check(!legacy.Contains("AiProjectActions.Instructions", StringComparison.Ordinal),
                "Standalone direct-AI prompt still asks the model to emit h2-actions.");

            var typed = typeof(IH2ProjectToolHost).GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Check(typed.SequenceEqual(
                    new[] { "AddTask", "AppendNote", "ReadProject", "ReplaceNote" },
                    StringComparer.Ordinal),
                "Typed H2 project mutation surface drifted while retiring h2-actions.");
        });

        test("H2M-122 normal project send cannot execute through direct legacy AiClient", () =>
        {
            var panel = ReadSource("src/H2Notes.Avalonia/Controls/AiChatPanel.cs");
            var agent = ReadSource("src/H2Notes.Avalonia/Controls/AiChatPanel.Agent.cs");

            var sendAt = panel.IndexOf("private async Task Send()", StringComparison.Ordinal);
            var projectRouteAt = panel.IndexOf("await SendProjectAgent();", sendAt, StringComparison.Ordinal);
            var legacyAt = panel.IndexOf("private async Task SendLegacy()", projectRouteAt, StringComparison.Ordinal);
            var failClosedAt = panel.IndexOf(
                "Project AI requests must use H2AgentAdapter",
                legacyAt,
                StringComparison.Ordinal);
            var clientAt = panel.IndexOf("_createClient()", legacyAt, StringComparison.Ordinal);

            Check(sendAt >= 0 && projectRouteAt > sendAt && legacyAt > projectRouteAt,
                "Project-first Send router is missing.");
            Check(failClosedAt > legacyAt && clientAt > failClosedAt,
                "Legacy direct client can be created before project-scope fail-closed guard.");
            Check(agent.Contains("_app.AgentAdapter.StartTaskAsync(", StringComparison.Ordinal)
                  && agent.Contains("_app.AgentAdapter.ObserveTask(", StringComparison.Ordinal),
                "Project chat no longer uses IH2AgentAdapter lifecycle.");
            Check(!agent.Contains("_createClient", StringComparison.Ordinal)
                  && !agent.Contains("new AiClient", StringComparison.Ordinal),
                "Project Agent source leaked direct AiClient execution.");
        });

        test("H2M-123 ProjectAiWindow is a detached view of the same Agent workspace", () =>
        {
            var window = ReadSource("src/H2Notes.Avalonia/ProjectAiWindow.cs");
            var host = ReadSource("src/H2Notes.Avalonia/MainWindow.ProjectAi.cs");
            var responsive = ReadSource("src/H2Notes.Avalonia/MainWindow.Responsive.cs");

            Check(host.Contains("DetachedAiWindow.AttachChat(_chat)", StringComparison.Ordinal)
                  && host.Contains("DetachChatHost(AiHost)", StringComparison.Ordinal)
                  && host.Contains("AiHost.Content = _chat", StringComparison.Ordinal),
                "Detached project window no longer moves the same Agent/chat presentation instance.");
            Check(!host.Contains("new AiChatPanel", StringComparison.Ordinal),
                "Detached project window creates a redundant chat/Agent presentation session.");

            foreach (var forbidden in new[]
            {
                "H2ProductionAgentAdapter",
                "AgentRuntime",
                "AgentOrchestrator",
                "ToolRegistry",
                "new H2UnavailableAgentAdapter"
            })
            {
                Check(!window.Contains(forbidden, StringComparison.Ordinal),
                    "ProjectAiWindow owns an Agent/runtime subsystem: " + forbidden);
                Check(!host.Contains(forbidden, StringComparison.Ordinal),
                    "MainWindow detached-Agent host owns a second runtime: " + forbidden);
            }

            Check(window.Contains("Agent dự án", StringComparison.Ordinal)
                  && responsive.Contains("Tách Agent dự án ra màn hình", StringComparison.Ordinal),
                "Detached window still presents itself as a separate legacy project-AI runtime.");
        });

        test("H2M-124 superseded product docs are clearly historical and master files stay canonical", () =>
        {
            var redesign = ReadSource("docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md");
            var approved = ReadSource("docs/APPROVED_PRODUCT_SPEC.md");
            var baseline = ReadSource("docs/H2_PRODUCT_CURRENT_BASELINE.md");
            var master = ReadSource("docs/H2_PRODUCT_MASTER_SPEC.md");
            var tasks = ReadSource("docs/H2_PRODUCT_MASTER_TASKS.md");

            Check(redesign.Contains("SUPERSEDED FOR FUTURE H2 PRODUCT ARCHITECTURE", StringComparison.Ordinal)
                  && redesign.Contains("HISTORICAL / SUPERSEDED FUTURE-DESIGN DRAFT", StringComparison.Ordinal),
                "Old Command Center redesign doc is not clearly superseded/historical.");
            Check(approved.Contains("HISTORICAL / CURRENT-IMPLEMENTATION BASELINE", StringComparison.Ordinal),
                "Old approved product spec is not marked as historical current-implementation evidence.");
            Check(baseline.Contains("HISTORICAL / CURRENT-IMPLEMENTATION EVIDENCE", StringComparison.Ordinal)
                  && baseline.Contains("NOT FUTURE ARCHITECTURE", StringComparison.Ordinal),
                "H2M-000 baseline is not clearly marked historical evidence.");
            Check(master.Contains("CANONICAL FUTURE H2 PRODUCT SPECIFICATION", StringComparison.Ordinal)
                  && master.Contains("H2_PRODUCT_MASTER_TASKS.md", StringComparison.Ordinal),
                "Product Master Spec is not clearly canonical.");
            Check(tasks.Contains("CANONICAL FUTURE H2 NOTES TRACKER", StringComparison.Ordinal)
                  && tasks.Contains("Architecture source of truth: `docs/H2_PRODUCT_MASTER_SPEC.md`", StringComparison.Ordinal),
                "Product Master Tasks is not identifiable as the active canonical tracker.");
            Check(master.Contains("H2_NOTES_NON_AI_BUG_LEDGER.md", StringComparison.Ordinal),
                "Canonical spec no longer references the independent bug/data-integrity ledger.");
        });
    }

    private static string ReadSource(string relative)
        => File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            relative.Replace('/', Path.DirectorySeparatorChar)));

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
}
