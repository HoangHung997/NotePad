using System.Text.Json;
using H2AgentLab.Capabilities;
using H2AgentLab.Plugins;
using H2AgentLab.Tools;

namespace H2AgentLab.Catalog;

/// <summary>
/// Ordinary AgentRuntime tools for external extension discovery and installation.
/// The model may search/select package metadata, but host-owned policy and approval are injected
/// into this executor and are never model-controlled tool arguments.
/// </summary>
public sealed class CatalogRuntimeToolExecutor : IAgentToolExecutor
{
    public const string SearchToolName = "catalog_search";
    public const string InstallToolName = "plugin_install";

    private readonly CapabilityResolver _resolver;
    private readonly IPackageRetriever _retriever;
    private readonly PluginManager _plugins;
    private readonly PluginInstallPolicy _installPolicy;
    private readonly bool _userApproved;
    private readonly long _maxPackageBytes;
    private readonly TimeSpan _retrievalTimeout;

    public CatalogRuntimeToolExecutor(
        CapabilityResolver resolver,
        IPackageRetriever retriever,
        PluginManager plugins,
        PluginInstallPolicy installPolicy,
        bool userApproved,
        long maxPackageBytes = 64L * 1024 * 1024,
        TimeSpan? retrievalTimeout = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _installPolicy = installPolicy ?? throw new ArgumentNullException(nameof(installPolicy));
        _userApproved = userApproved;
        if (maxPackageBytes is < 1 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxPackageBytes));
        _maxPackageBytes = maxPackageBytes;
        _retrievalTimeout = retrievalTimeout ?? TimeSpan.FromSeconds(30);
        if (_retrievalTimeout <= TimeSpan.Zero || _retrievalTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(retrievalTimeout));
    }

    public string ExecutorId => "extension-catalog-runtime";

    public async ValueTask<string> ExecuteAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Name switch
        {
            SearchToolName => await SearchAsync(call, cancellationToken).ConfigureAwait(false),
            InstallToolName => await InstallAsync(call, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Catalog runtime executor cannot execute '{call.Name}'.")
        };
    }

    private async Task<string> SearchAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var query = RequiredString(call.Arguments, "query");
        var resolution = await _resolver.SearchCatalogAsync(
            query,
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            query,
            status = resolution.Status.ToString(),
            catalogMetadataRefreshed = resolution.CatalogMetadataRefreshed,
            candidates = resolution.Candidates
                .Where(x => x.AvailablePackage is not null)
                .Select(x =>
                {
                    var package = x.AvailablePackage!;
                    return new
                    {
                        status = x.Status.ToString(),
                        pluginId = package.PluginId,
                        pluginVersion = package.PluginVersion,
                        package.Publisher,
                        trust = package.TrustState.ToString(),
                        package.MinAgentVersion,
                        package.SourceId,
                        package.MetadataStale,
                        toolSummaries = package.ToolSummaries,
                        skills = package.Skills.Select(skill => new
                        {
                            skill.SkillId,
                            skill.Name,
                            skill.Description
                        }).ToArray(),
                        providers = package.Providers,
                        permissions = package.Permissions
                    };
                })
                .ToArray(),
            note = "Package metadata only. The model may propose a candidate; install permission and approval remain host-owned."
        });
    }

    private async Task<string> InstallAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var pluginId = RequiredString(call.Arguments, "plugin_id");
        var pluginVersion = RequiredString(call.Arguments, "version");

        var resolution = await _resolver.SearchCatalogAsync(
            pluginId,
            cancellationToken).ConfigureAwait(false);
        var candidate = resolution.Candidates.SingleOrDefault(x =>
            x.AvailablePackage is not null
            && string.Equals(x.PluginId, pluginId, StringComparison.Ordinal)
            && string.Equals(x.PluginVersion, pluginVersion, StringComparison.Ordinal));

        if (candidate?.AvailablePackage is null)
            throw new global::H2AgentLab.AgentFaultException(
                "package_not_found",
                $"Catalog package '{pluginId}@{pluginVersion}' is unavailable.");

        if (candidate.Status != CapabilityResolutionStatus.AVAILABLE)
            throw new global::H2AgentLab.AgentFaultException(
                "package_not_installable",
                $"Host policy reports '{pluginId}@{pluginVersion}' as {candidate.Status}.");

        var package = candidate.AvailablePackage;
        var retrieval = await _retriever.RetrieveAsync(
            new PackageRetrievalRequest(
                package.PluginId,
                package.PluginVersion,
                package.PackageLocation,
                package.ArchiveSha256,
                _maxPackageBytes,
                _retrievalTimeout),
            cancellationToken).ConfigureAwait(false);

        var entry = new PluginCatalogEntry(
            package.PluginId,
            package.PluginId,
            package.PluginVersion,
            CompactDescription(package),
            package.Publisher,
            package.ToolSummaries,
            package.Skills.Select(x => x.Name).ToArray(),
            package.MinAgentVersion,
            package.TrustState,
            package.ArchiveSha256,
            retrieval.StagedPath);

        var installed = _plugins.InstallFromArchive(
            retrieval.StagedPath,
            entry,
            _installPolicy,
            _userApproved);

        var active = _plugins.GetActive(installed.PluginId)
            ?? throw new InvalidOperationException(
                "Plugin activation completed without an active manifest.");
        if (!string.Equals(
                active.Manifest.Version,
                installed.Version,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Plugin activation version differs from the installed package.");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            pluginId = installed.PluginId,
            version = installed.Version,
            registeredTools = installed.RegisteredTools,
            skills = active.Manifest.Skills,
            providers = active.Manifest.Providers,
            permissions = active.Manifest.Permissions,
            archiveSha256 = installed.ArchiveSha256,
            payloadSha256 = installed.PayloadSha256,
            note = "Extension activated at a completed tool-call boundary. Use tool_search/list_skills again to discover the refreshed surface; the original task continues."
        });
    }

    private static string CompactDescription(AvailableCapabilityRecord package)
    {
        var parts = package.ToolSummaries
            .Concat(package.Skills.Select(x => x.Description))
            .Concat(package.Providers)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(12)
            .ToArray();
        return parts.Length == 0
            ? $"Extension package {package.PluginId}."
            : string.Join(" ", parts);
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException(
                $"Catalog runtime tool requires non-empty string '{name}'.");
        return value.GetString()!.Trim();
    }
}

/// <summary>
/// Registration helper keeps catalog/install capabilities in ordinary ToolRegistry discovery.
/// No special continuation object or skill-required result type participates in execution.
/// </summary>
public static class CatalogRuntimeTools
{
    public static void Register(
        ToolRegistry registry,
        CatalogRuntimeToolExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executor);

        var toolNamespace = new ToolNamespace(
            "extensions",
            "Search external extension catalog metadata and install a host-approved selected plugin package.");

        registry.Register(new ToolDescriptor(
            CatalogRuntimeToolExecutor.SearchToolName,
            toolNamespace,
            "Search external extension catalog metadata for missing tools, skills or providers. Returns compact candidates only.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "v1",
            callableSchema: SearchSchema(),
            executor: executor,
            provenance: new ToolProvenance(
                "extension-catalog",
                "1.0.0",
                "host",
                "1.0.0")));

        registry.Register(new ToolDescriptor(
            CatalogRuntimeToolExecutor.InstallToolName,
            toolNamespace,
            "Install one exact catalog plugin package through host policy, immutable retrieval and PluginManager verification.",
            AgentToolRisk.High,
            AgentToolAccess.Mutating,
            supportsParallel: false,
            schemaVersion: "v1",
            callableSchema: InstallSchema(),
            executor: executor,
            provenance: new ToolProvenance(
                "extension-catalog",
                "1.0.0",
                "host",
                "1.0.0"),
            resourceScope: new ToolResourceScope(
                "plugins",
                "host-managed plugin installation set"),
            serializationKey: "plugin-install"));
    }

    private static JsonElement SearchSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = CatalogRuntimeToolExecutor.SearchToolName,
                description = "Search external extension catalog metadata when installed tools/skills/providers are insufficient.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "Concise missing capability description."
                        }
                    },
                    required = new[] { "query" },
                    additionalProperties = false
                }
            }
        });

    private static JsonElement InstallSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = CatalogRuntimeToolExecutor.InstallToolName,
                description = "Request installation of one exact catalog candidate. Host policy and user approval are not model arguments.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        plugin_id = new
                        {
                            type = "string",
                            description = "Exact plugin ID returned by catalog_search."
                        },
                        version = new
                        {
                            type = "string",
                            description = "Exact plugin version returned by catalog_search."
                        }
                    },
                    required = new[] { "plugin_id", "version" },
                    additionalProperties = false
                }
            }
        });
}
