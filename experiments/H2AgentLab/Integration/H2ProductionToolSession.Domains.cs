using H2AgentLab.Cad;
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
        var coreConsole = H2AutoCadFileTools.FindExecutable();
        if (_scope?.Mode == H2Notes.Core.H2AgentPermissionMode.FullAccess && coreConsole is not null)
        {
            var cad = new H2AutoCadFileTools(coreConsole, tools.Workspace, tools.StateRoot);
            cad.Register(registry);
            verifiers.Add(cad);
        }
        else if (coreConsole is null)
        {
            registry.RegisterCapabilityNotice(new("autocad.closed_file",
                "Closed DWG create/inspect/update/export requires an installed Autodesk AutoCAD Core Console. This does not affect the separate live AutoCAD capability.",
                new(ToolReadinessState.NeedsConfiguration, "autocad_core_console_not_found")));
        }

        IAutoCadNativeBridge? liveCadBridge = null;
        var liveCadReason = "live_autocad_not_running";
        try
        {
            if (_autoCadLiveBridgeFactory is not null)
            {
                liveCadBridge = _autoCadLiveBridgeFactory();
                liveCadReason = liveCadBridge is null ? "live_autocad_bridge_unavailable" : "ready";
            }
            else
            {
                _ = AutoCadComLiveBridge.TryCreate(out liveCadBridge, out liveCadReason);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            liveCadBridge = null;
            liveCadReason = "live_autocad_bridge_unavailable";
        }

        if (liveCadBridge is not null)
        {
            var liveCad = new H2AutoCadLiveTools(liveCadBridge, () => IsExecutingAuthorizedCall);
            liveCad.Register(registry);
            verifiers.Add(liveCad);
            _owned.Add(liveCad);
            registry.RegisterCapabilityNotice(new("autocad.live_drawing",
                "Live AutoCAD external COM bridge is ready. Scope is current PickFirst selection for entity reads and bounded block-attribute edit/readback; general dynamic-block/update_entity/plot is not advertised.",
                new(ToolReadinessState.Ready, "external_com_live")));
        }
        else
        {
            registry.RegisterCapabilityNotice(new("autocad.live_drawing",
                "Live AutoCAD is unavailable. Start AutoCAD in the same Windows user session and select target entities. Closed-file Core Console is a separate capability and never substitutes for an unsaved live drawing.",
                new(ToolReadinessState.Unavailable, liveCadReason)));
        }

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
