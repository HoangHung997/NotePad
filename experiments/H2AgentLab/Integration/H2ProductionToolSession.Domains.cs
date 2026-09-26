using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Web;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    private partial void ConfigureDomains(AgentTools tools, ToolRegistry registry, List<IAgentRuntimeDomainVerifier> verifiers)
    {
        var workspaceLaunchHandoff = _scope?.ScopeKind == H2Notes.Core.H2AgentResourceScopeKind.Workspace
            && _scope.Mode == H2Notes.Core.H2AgentPermissionMode.AskBeforeChanges;
        var office = new H2OfficeRuntimeTools(() => IsExecutingAuthorizedCall, tools.Workspace.Root, _scope,
            _projectId.HasValue ? (_context?.TargetPaths ?? []).Append(new H2Notes.Core.H2AgentTargetPath(tools.Workspace.Root, true, "project-workspace")).ToArray() : null,
            _targetPolicy, _context?.TargetIntent ?? H2Notes.Core.H2AgentTargetIntent.OpenDocument,
            _targetObserved, _officeClientFactory, _captureValidator,
            IsTaskLaunchedOfficeCandidate, PinTaskLaunchedOfficeBinding,
            HasTaskLaunchedOfficeAuthority, workspaceLaunchHandoff);
        _owned.Add(office);
        _liveOffice = office;
        if (_scope?.ScopeKind != H2Notes.Core.H2AgentResourceScopeKind.Workspace || workspaceLaunchHandoff)
            office.Register(registry);
        verifiers.RemoveAll(item => item is StructuredOfficeRuntimeDomainVerifier);
        verifiers.Add(office);
        // App lifecycle is independent of a preselected window; click/type remain unavailable
        // when no selected Desktop controller exists, but launch/activate can still be verified.
        verifiers.Add(new H2DesktopRuntimeVerifier());
        if (_scope?.Mode == H2Notes.Core.H2AgentPermissionMode.FullAccess && H2AutoCadFileTools.FindExecutable() is { } cadExecutable)
        {
            var cad = new H2AutoCadFileTools(cadExecutable, tools.Workspace, tools.StateRoot); cad.Register(registry); verifiers.Add(cad);
        }

        registry.RegisterCapabilityNotice(new("autocad.live_drawing", "No live AutoCAD drawing/SelectionSet provider is composed here. Core Console operates on disk files, not the current unsaved drawing.", new(ToolReadinessState.Unsupported, "live_resource_required")));

        var webConfig = WebResearchProductionOptions.FromEnvironment();
        if (!webConfig.SearchConfigured)
            registry.RegisterCapabilityNotice(new("web.search",
                "Web search is not configured. Set H2_BRAVE_SEARCH_API_KEY to enable the Brave Search API. Explicit-URL fetch remains available.",
                new(ToolReadinessState.NeedsConfiguration, "search_backend_not_configured")));
        if (!webConfig.BrowserConfigured)
            registry.RegisterCapabilityNotice(new("browser.live_tab",
                "Interactive browser automation is not configured. Start a dedicated Chrome/Edge debugging session and set H2_BROWSER_CDP_ENDPOINT to its loopback endpoint.",
                new(ToolReadinessState.NeedsConfiguration, "browser_backend_not_configured")));
        registry.RegisterCapabilityNotice(new("web.open_browser",
            "Legacy URL-only browser fallback is not a browser-control capability. Use browser.* only when a configured CDP session is ready.",
            new(ToolReadinessState.Unsupported, "use_browser_namespace")));

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        _owned.Add(http);

        Func<string,int,CancellationToken,Task<IReadOnlyList<WebSearchHit>>>? search = null;
        if (webConfig.BraveApiKey is { } braveKey)
        {
            var brave = new BraveWebSearchClient(http, braveKey);
            search = brave.SearchAsync;
        }

        var digest = new H2NewsDigestTools(tools.Workspace, tools.StateRoot); digest.Register(registry); verifiers.Add(digest);
        var web = new WebResearchHost(new PolicyHttpWebResearchBackend(http, search, enforcePublicNetwork: true))
        { FeedObserved = digest.Observe };
        web.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        var adapter = new CapabilityProviderToolRegistryAdapter(registry);
        var webTools = new List<string> { "web.fetch", "web.download", "web.extract", "web.get_metadata", "web.read_feed" };
        if (webConfig.SearchConfigured) webTools.Insert(0, "web.search");
        adapter.LoadSelectedAsync(web, webTools, CancellationToken.None).GetAwaiter().GetResult();

        if (webConfig.BrowserCdpEndpoint is { } cdpEndpoint)
        {
            try
            {
                var browser = new CdpBrowserCapabilityProvider(
                    new CdpBrowserResearchBackend(http, cdpEndpoint, webConfig.BrowserAllowLoopbackNavigation));
                browser.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                adapter.LoadSelectedAsync(browser,
                    ["browser.list_tabs", "browser.inspect", "browser.query",
                     "browser.navigate", "browser.click", "browser.type"],
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                registry.RegisterCapabilityNotice(new("browser.live_tab",
                    "Configured browser endpoint is invalid. H2_BROWSER_CDP_ENDPOINT must be an explicit loopback HTTP/HTTPS DevTools endpoint.",
                    new(ToolReadinessState.NeedsConfiguration, "browser_endpoint_invalid")));
            }
        }
    }
}
