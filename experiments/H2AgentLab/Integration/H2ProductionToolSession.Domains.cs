using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Web;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    private partial void ConfigureDomains(AgentTools tools, ToolRegistry registry, List<IAgentRuntimeDomainVerifier> verifiers)
    {
        var office = new H2OfficeRuntimeTools(() => IsExecutingAuthorizedCall, tools.Workspace.Root, _scope,
            _projectId.HasValue ? (_context?.TargetPaths ?? []).Append(new H2Notes.Core.H2AgentTargetPath(tools.Workspace.Root, true, "project-workspace")).ToArray() : null,
            _targetPolicy, _context?.TargetIntent ?? H2Notes.Core.H2AgentTargetIntent.OpenDocument,
            _targetObserved, _officeClientFactory, _captureValidator);
        _owned.Add(office);
        if (_scope?.ScopeKind != H2Notes.Core.H2AgentResourceScopeKind.Workspace) office.Register(registry);
        verifiers.RemoveAll(item => item is StructuredOfficeRuntimeDomainVerifier);
        verifiers.Add(office);
        if (tools.Desktop is not null) verifiers.Add(new H2DesktopRuntimeVerifier());
        if (_scope?.Mode == H2Notes.Core.H2AgentPermissionMode.FullAccess && H2AutoCadFileTools.FindExecutable() is { } cadExecutable)
        {
            var cad = new H2AutoCadFileTools(cadExecutable, tools.Workspace, tools.StateRoot); cad.Register(registry); verifiers.Add(cad);
        }

        registry.RegisterCapabilityNotice(new("autocad.live_drawing", "No live AutoCAD drawing/SelectionSet provider is composed here. Core Console operates on disk files, not the current unsaved drawing.", new(ToolReadinessState.Unsupported, "live_resource_required")));
        registry.RegisterCapabilityNotice(new("browser.live_tab", "No existing live browser tab/session is bound. HTTP fetch is a separate public disk/network snapshot, not the logged-in current tab.", new(ToolReadinessState.NeedsConfiguration, "live_resource_required")));
        registry.RegisterCapabilityNotice(new("web.search", "Web search requires a configured search backend; fetch accepts an explicit URL.", new(ToolReadinessState.NeedsConfiguration)));
        registry.RegisterCapabilityNotice(new("web.open_browser", "Interactive browser automation requires a configured browser backend; fetch is not browser control.", new(ToolReadinessState.NeedsConfiguration)));
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _owned.Add(http);
        var digest = new H2NewsDigestTools(tools.Workspace, tools.StateRoot); digest.Register(registry); verifiers.Add(digest);
        var web = new WebResearchHost(new HttpWebResearchBackend(http)) { FeedObserved = digest.Observe };
        web.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        // Fetch/extract are available without a search subscription. Do not advertise an
        // unconfigured search backend or a browser action that merely echoes a URL.
        new CapabilityProviderToolRegistryAdapter(registry).LoadSelectedAsync(web,
            ["web.fetch", "web.download", "web.extract", "web.get_metadata", "web.read_feed"], CancellationToken.None).GetAwaiter().GetResult();
    }
}
