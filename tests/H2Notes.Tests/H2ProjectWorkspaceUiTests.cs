using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2ProjectWorkspaceUiTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Opening a project is Agent-first with compact status and one-click details", () =>
        {
            var board = SheetStorage.Demo().Notes.First(note => note.IsBoard);
            var project = board.Projects[0];
            var app = new App();
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board)
            {
                Width = 1040,
                Height = 760
            };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();

                Check(!window.FindControl<Grid>("CommandCenter")!.IsVisible,
                    "Project did not leave Command Center.");
                Check(window.FindControl<Grid>("WorkContent")!.IsVisible,
                    "Compact project status surface is hidden.");
                Check(window.FindControl<Border>("AiHostBorder")!.IsVisible,
                    "Agent is not the primary project work surface.");
                Check(!window.FindControl<Grid>("EditorSplit")!.IsVisible,
                    "Legacy Task+Notes split still dominates the default project view.");
                Check(window.FindControl<StackPanel>("CompactTabs")!.IsVisible,
                    "Project detail navigation is not one click away.");
                Check(window.FindControl<Button>("AgentTabButton") is not null,
                    "Agent workspace tab is missing.");
                Check(window.FindControl<TextBlock>("ProjectProgress")!.Text!.Contains("công việc", StringComparison.Ordinal),
                    "Compact project progress is missing.");
                Check(window.FindControl<TextBlock>("ProjectNextSummary")!.Text!.StartsWith("Tiếp theo:", StringComparison.Ordinal),
                    "Compact next-task summary is missing.");
                Check(!string.IsNullOrWhiteSpace(window.FindControl<TextBlock>("ProjectAttentionSummary")!.Text),
                    "Compact attention summary is missing.");
                Check(window.FindControl<ContentControl>("AiHost")!.Content is AiChatPanel,
                    "H2M-060 replaced the existing Agent/chat presentation surface instead of reframing it.");

                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Grid>("EditorSplit")!.IsVisible,
                    "Tasks detail did not open in one click.");
                Check(window.FindControl<Border>("TasksPane")!.IsVisible,
                    "ProjectGrid tasks detail is hidden.");

                window.FindControl<Button>("NotesTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Grid>("EditorSplit")!.IsVisible,
                    "Notes detail did not stay in the detail surface.");
                Check(window.FindControl<Border>("NotesPane")!.IsVisible,
                    "Rich notes detail is hidden.");

                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Border>("AiHostBorder")!.IsVisible
                    && !window.FindControl<Grid>("EditorSplit")!.IsVisible,
                    "Agent tab did not restore the primary Agent work surface.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Agent-first workspace mode is transient and does not add project persistence state", () =>
        {
            foreach (var type in new[] { typeof(ProjectRecord), typeof(ProjectLayout) })
                foreach (var name in new[]
                {
                    "WorkspaceMode", "ProjectWorkspaceMode", "PrimarySurface",
                    "AgentFirstMode", "DetailMode"
                })
                    Check(type.GetProperty(name) is null && type.GetField(name) is null,
                        $"Transient workspace mode leaked into {type.Name}: {name}");

            var board = SheetStorage.Demo().Notes.First(note => note.IsBoard);
            var project = board.Projects[0];
            var app = new App();
            app.State.Notes.Add(board);
            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();
                var beforeAgentRoundTrip = JsonSerializer.Serialize(project);

                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                // Entering Agent mode itself must not add/write a persisted workspace-mode field.
                var afterAgentRoundTrip = JsonSerializer.Serialize(project);
                Check(!afterAgentRoundTrip.Contains("WorkspaceMode", StringComparison.Ordinal)
                    && !afterAgentRoundTrip.Contains("PrimarySurface", StringComparison.Ordinal),
                    "Agent-first UI mode was persisted into project JSON.");

                // Existing detail layout settings may retain their historical task/note tab state;
                // the new primary-mode decision remains runtime-only.
                Check(beforeAgentRoundTrip.Length > 0 && afterAgentRoundTrip.Length > 0,
                    "Project serialization unexpectedly disappeared.");
            }
            finally
            {
                window.Close();
            }
        });

        test("H2M-060 changes presentation only and does not replace project AI execution early", () =>
        {
            var repo = FindRepoRoot();
            var responsive = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.Responsive.cs"));
            var xaml = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.axaml"));

            Check(responsive.Contains("ProjectWorkspaceAgentMode", StringComparison.Ordinal)
                && responsive.Contains("ShowAgentWorkspace", StringComparison.Ordinal),
                "Agent-first project presentation state is missing.");
            Check(xaml.Contains("x:Name=\"AgentTabButton\"", StringComparison.Ordinal)
                && xaml.Contains("x:Name=\"ProjectNextSummary\"", StringComparison.Ordinal)
                && xaml.Contains("x:Name=\"ProjectAttentionSummary\"", StringComparison.Ordinal),
                "AI-first project navigation/status controls are missing.");

            foreach (var marker in new[]
            {
                "StartTaskAsync(", "AgentRuntime", "ToolRegistry",
                "IAgentTransport", "AgentOrchestrator"
            })
                Check(!responsive.Contains(marker, StringComparison.Ordinal),
                    "H2M-060 crossed into H2M-070 execution replacement: " + marker);
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
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
}
