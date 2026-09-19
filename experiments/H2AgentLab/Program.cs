using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using H2AgentLab.Context;
using H2AgentLab.Documents;
using H2AgentLab.Desktop;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.Phase10;
using H2AgentLab.Phase11;
using H2AgentLab.Runtime;
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
        if (args.Contains("--v2-closed-file-test")) return V2ClosedFileTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-phase10-test")) return V2Phase10Tests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-phase11-test")) return V2Phase11Tests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--v2-extensibility-refinement-test")) return V2ExtensibilityRefinementTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-runtime-test")) return MbAgentRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-orchestrator-runtime-test")) return MbOrchestratorRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-provider-runtime-test")) return MbProviderRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-context-runtime-test")) return MbContextRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-compaction-runtime-test")) return MbCompactionRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-prompt-cache-runtime-test")) return MbPromptCacheRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-initial-tool-exposure-test")) return MbInitialToolExposureTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-tool-registry-execution-test")) return MbToolRegistryExecutionTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-deferred-tool-loading-test")) return MbDeferredToolLoadingTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-scheduler-runtime-test")) return MbSchedulerRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-permission-runtime-test")) return MbPermissionRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-evidence-runtime-test")) return MbEvidenceRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-verification-runtime-test")) return MbVerificationRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-repair-runtime-test")) return MbRepairRuntimeTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-completion-gate-test")) return MbCompletionGateTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-skill-catalog-test")) return H2AgentLab.Skills.MbSkillCatalogTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-skill-discovery-test")) return H2AgentLab.Skills.MbSkillDiscoveryTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-progressive-skill-loading-test")) return MbProgressiveSkillLoadingTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-extension-registration-test")) return H2AgentLab.Extensions.MbExtensionRegistrationTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-generic-tool-preference-test")) return H2AgentLab.Tools.MbGenericToolPreferenceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-computer-extension-registration-test")) return H2AgentLab.Computer.MbComputerExtensionRegistrationTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-first-party-extension-cards-test")) return H2AgentLab.Extensions.MbFirstPartyExtensionCardsTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-domain-neutral-capability-search-test")) return H2AgentLab.Capabilities.MbDomainNeutralCapabilitySearchTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-installed-capability-projection-test")) return H2AgentLab.Capabilities.MbInstalledCapabilityProjectionTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-available-capability-search-test")) return H2AgentLab.Capabilities.MbAvailableCapabilitySearchTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-thin-capability-resolver-test")) return H2AgentLab.Capabilities.MbThinCapabilityResolverTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-runtime-capability-install-test")) return H2AgentLab.Capabilities.MbRuntimeCapabilityInstallTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-task-capability-pinning-test")) return H2AgentLab.Capabilities.MbTaskCapabilityPinningTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-minimal-catalog-source-test")) return H2AgentLab.Catalog.MbMinimalCatalogSourceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-package-retriever-boundary-test")) return H2AgentLab.Catalog.MbPackageRetrieverBoundaryTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-end-to-end-install-continue-test")) return H2AgentLab.Capabilities.MbEndToEndInstallContinueTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-retire-agent-runner-test")) return H2AgentLab.Runtime.MbRetireAgentRunnerTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-retire-agent-tools-switch-test")) return H2AgentLab.Runtime.MbRetireAgentToolsSwitchTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-retire-v1-tool-registry-adapter-test")) return H2AgentLab.Runtime.MbRetireV1ToolRegistryAdapterTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-retire-duplicate-skill-catalog-test")) return H2AgentLab.Skills.MbSkillCatalogRetirementTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-trace-journal-boundary-test")) return H2AgentLab.Tasking.MbTraceJournalBoundaryTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-minimum-bootable-agent-acceptance-test")) return H2AgentLab.Acceptance.MbMinimumBootableAgentAcceptanceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-normal-ui-v2-path-test")) return H2AgentLab.Acceptance.MbNormalUiV2PathTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-context-boundedness-gate-test")) return H2AgentLab.Acceptance.MbContextBoundednessGateTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-extension-bus-gate-test")) return H2AgentLab.Acceptance.MbExtensionBusGateTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-core-correctness-gate-test")) return H2AgentLab.Acceptance.MbCoreCorrectnessGateTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-web-freshness-legal-acceptance-test")) return H2AgentLab.Web.MbWebFreshnessLegalAcceptanceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-autocad-acceptance-test")) return H2AgentLab.Acceptance.MbAutoCadAcceptanceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-mcp-acceptance-test")) return H2AgentLab.Acceptance.MbMcpAcceptanceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-plugin-package-lifecycle-acceptance-test")) return H2AgentLab.Acceptance.MbPluginPackageLifecycleAcceptanceTests.Run(args[^1]).GetAwaiter().GetResult();
        if (args.Contains("--mb-final-architecture-report-test")) return H2AgentLab.Acceptance.MbFinalArchitectureReportTests.Run(args[^1]).GetAwaiter().GetResult();
        var mb104 = Array.IndexOf(args, "--mb-permission-scope-gate-test");
        if (mb104 >= 0)
        {
            if (mb104 + 3 >= args.Length) throw new ArgumentException("--mb-permission-scope-gate-test requires <output-directory> <desktop-host-exe> <office-host-exe>.");
            return H2AgentLab.Acceptance.MbPermissionScopeGateTests.Run(args[mb104 + 1], args[mb104 + 2], args[mb104 + 3]).GetAwaiter().GetResult();
        }
        var mb94 = Array.IndexOf(args, "--mb-retire-computer-tools-test");
        if (mb94 >= 0)
        {
            if (mb94 + 2 >= args.Length) throw new ArgumentException("--mb-retire-computer-tools-test requires <output-directory> <desktop-host-exe>.");
            return H2AgentLab.Desktop.MbRetireComputerToolsTests.Run(args[mb94 + 1], args[mb94 + 2]).GetAwaiter().GetResult();
        }
        var desktopTest = Array.IndexOf(args, "--v2-desktop-host-test");
        if (desktopTest >= 0)
        {
            if (desktopTest + 2 >= args.Length) throw new ArgumentException("--v2-desktop-host-test requires <output-directory> <desktop-host-exe>.");
            return V2DesktopHostTests.Run(args[desktopTest + 1], args[desktopTest + 2]).GetAwaiter().GetResult();
        }
        var mb112 = Array.IndexOf(args, "--mb-desktop-computer-use-acceptance-test");
        if (mb112 >= 0)
        {
            if (mb112 + 2 >= args.Length) throw new ArgumentException("--mb-desktop-computer-use-acceptance-test requires <output-directory> <desktop-host-exe>.");
            return H2AgentLab.Acceptance.MbDesktopComputerUseAcceptanceTests.Run(args[mb112 + 1], args[mb112 + 2]).GetAwaiter().GetResult();
        }
        var mb110 = Array.IndexOf(args, "--mb-office-acceptance-test");
        if (mb110 >= 0)
        {
            if (mb110 + 2 >= args.Length) throw new ArgumentException("--mb-office-acceptance-test requires <output-directory> <office-host-exe>.");
            return H2AgentLab.Acceptance.MbOfficeAcceptanceTests.Run(args[mb110 + 1], args[mb110 + 2]).GetAwaiter().GetResult();
        }
        var officeTest = Array.IndexOf(args, "--v2-office-host-test");
        if (officeTest >= 0)
        {
            if (officeTest + 2 >= args.Length) throw new ArgumentException("--v2-office-host-test requires <output-directory> <office-host-exe>.");
            return V2OfficeHostTests.Run(args[officeTest + 1], args[officeTest + 2]).GetAwaiter().GetResult();
        }
        var officeLive = Array.IndexOf(args, "--v2-office-live-acceptance");
        if (officeLive >= 0)
        {
            if (officeLive + 2 >= args.Length) throw new ArgumentException("--v2-office-live-acceptance requires <output-directory> <office-host-exe>.");
            return V2OfficeLiveAcceptance.Run(args[officeLive + 1], args[officeLive + 2]).GetAwaiter().GetResult();
        }
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
