using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2AgentLab.Tools;
using H2AgentLab.Verification;

namespace H2AgentLab;

public static class Program
{
    public static string[] Arguments = [];
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--computer-worker")) return ComputerTools.Worker();
        if (args.Contains("--self-test")) return LabTests.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--skills-test")) return SkillTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-guard-test")) return V2ArchitectureTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-metrics-test")) return V2MetricsTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-baseline-test")) return V2BaselineTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-session-context-test")) return V2SessionContextTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-artifact-store-test")) return V2ArtifactStoreTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-compaction-test")) return V2CompactionTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-context-cache-test")) return V2ContextCacheTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-orchestrator-test")) return V2OrchestratorTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-tool-registry-test")) return V2ToolRegistryTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-verification-test")) return V2VerificationTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-transport-contract-test")) return V2TransportContractTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-ollama-transport-test")) return V2OllamaTransportTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-chat-transport-test")) return V2ChatCompletionsTransportTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-responses-transport-test")) return V2OpenAiResponsesTransportTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-responses-websocket-test")) return V2ResponsesWebSocketTransportTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-provider-resilience-test")) return V2ProviderTransportResilienceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--portability-test")) return PortabilityTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--verify-install")) return LabEnvironment.Verify(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--recovery-test")) return RecoveryTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--recovery-live")) return RecoveryLiveEvaluation.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--word-edit-live")) return WordEditLiveEvaluation.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--sandbox-probe")) return SkillTests.ConcurrencyProbe(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--skills-live")) return SkillLiveEvaluation.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--live-eval")) return LabTests.Live(args).GetAwaiter().GetResult();
        var evidence = Array.IndexOf(args, "--evidence"); var data = Array.IndexOf(args, "--data");
        var stateRoot = evidence >= 0 ? Path.Combine(args[evidence + 1], "private-demo") : data >= 0 ? args[data + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2AgentLab");
        var identity = SafeWorkspace.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(stateRoot).ToUpperInvariant()));
        using var mutex = new Mutex(false, "Local\\H2AgentLab-" + identity);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) { System.Windows.MessageBox.Show("H2 Agent Lab đã mở kho này. Không chạy hai bản cùng ghi lịch sử.", "H2 Agent Lab"); return 2; }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); return 0;
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<LabApp>().UsePlatformDetect().LogToTrace();
}
public sealed class LabApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new LabWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
