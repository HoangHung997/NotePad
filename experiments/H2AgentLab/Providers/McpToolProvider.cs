using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Providers;

public sealed class McpToolProvider : ICapabilityProvider
{
    private readonly McpServerConnection _connection;
    private readonly CapabilityProviderPolicy _policy;
    private readonly Dictionary<string, ProviderToolDefinition> _definitions = new(StringComparer.Ordinal);

    public McpToolProvider(
        McpServerConnection connection,
        CapabilityProviderPolicy policy)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Provenance = new ProviderProvenance(
            connection.Definition.ProviderId,
            connection.Definition.ProviderVersion,
            connection.Definition.ServerId,
            "mcp");
    }

    public ProviderProvenance Provenance { get; }
    public ProviderHealthState Health => _connection.Health;

    public Task ConnectAsync(CancellationToken cancellationToken)
        => _connection.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => _connection.DisconnectAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(
        CancellationToken cancellationToken)
    {
        var tools = await ListToolSummariesAsync(cancellationToken).ConfigureAwait(false);
        return tools
            .GroupBy(x => x.Namespace, StringComparer.Ordinal)
            .Select(group => new ProviderNamespaceSummary(
                group.Key,
                $"MCP capabilities from {Provenance.ProviderId}/{Provenance.ServerId}.",
                group.Select(x => x.Name)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Take(32)
                    .ToArray()))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(
        CancellationToken cancellationToken)
    {
        var result = await _connection.CallAsync(
            "tools/list",
            new { },
            cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("tools", out var toolsNode)
            || toolsNode.ValueKind != JsonValueKind.Array)
            throw new IOException("MCP tools/list response is missing tools array.");

        var summaries = new List<ProviderToolSummary>();
        _definitions.Clear();

        foreach (var tool in toolsNode.EnumerateArray())
        {
            var name = RequiredString(tool, "name", 128);
            var description = OptionalString(tool, "description", 2_000) ?? name;
            var namespaceName = NamespaceFor(name, tool);
            var access = AccessFor(tool);
            var risk = RiskFor(tool, access);
            var scope = MetaString(tool, "h2.scope", 256)
                ?? $"provider:{Provenance.ProviderId}";
            var parallel = MetaBool(tool, "h2.parallelSafe")
                ?? (access == AgentToolAccess.ReadOnly && _policy.AllowParallelReadOnly);
            var serializationKey = MetaString(tool, "h2.serializationKey", 64)
                ?? Provenance.ProviderId;
            var schemaVersion = MetaString(tool, "h2.schemaVersion", 64) ?? "v1";
            var toolVersion = MetaString(tool, "h2.toolVersion", 64)
                ?? Provenance.ProviderVersion;

            var summary = new ProviderToolSummary(
                name,
                namespaceName,
                description,
                access,
                risk,
                parallel,
                schemaVersion,
                scope,
                serializationKey,
                toolVersion);
            summaries.Add(summary);

            var schema = tool.TryGetProperty("inputSchema", out var inputSchema)
                && inputSchema.ValueKind == JsonValueKind.Object
                ? JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name,
                        description,
                        parameters = inputSchema.Clone()
                    }
                })
                : JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name,
                        description,
                        parameters = new
                        {
                            type = "object",
                            properties = new { },
                            additionalProperties = false
                        }
                    }
                });

            _definitions[name] = new ProviderToolDefinition(summary, schema);
        }

        return summaries
            .OrderBy(x => x.Namespace, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(
        IReadOnlyList<string> toolNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        if (_definitions.Count == 0)
            _ = await ListToolSummariesAsync(cancellationToken).ConfigureAwait(false);

        var selected = new List<ProviderToolDefinition>();
        foreach (var name in toolNames.Distinct(StringComparer.Ordinal))
        {
            if (!_definitions.TryGetValue(name, out var definition))
                throw new KeyNotFoundException($"MCP tool '{name}' is not advertised by provider '{Provenance.ProviderId}'.");
            selected.Add(definition);
        }
        return selected;
    }

    public async Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            result = await _connection.CallAsync(
                "resources/list",
                new { },
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Array.Empty<ProviderResourceSummary>();
        }

        if (!result.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Array)
            return Array.Empty<ProviderResourceSummary>();

        return resources.EnumerateArray()
            .Select(resource =>
            {
                var id = resource.TryGetProperty("uri", out var uri)
                    && uri.ValueKind == JsonValueKind.String
                    ? Bound(uri.GetString() ?? "", 512)
                    : throw new IOException("MCP resource is missing uri.");
                var description = OptionalString(resource, "description", 2_000)
                    ?? OptionalString(resource, "name", 256)
                    ?? id;
                var ns = MetaString(resource, "h2.namespace", 64) ?? "resources";
                var scope = MetaString(resource, "h2.scope", 256)
                    ?? $"provider:{Provenance.ProviderId}";
                return new ProviderResourceSummary(id, ns, description, scope);
            })
            .OrderBy(x => x.ResourceId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> ReadResourceAsync(
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        var result = await _connection.CallAsync(
            "resources/read",
            new { uri = resourceId },
            cancellationToken).ConfigureAwait(false);

        if (!result.TryGetProperty("contents", out var contents)
            || contents.ValueKind != JsonValueKind.Array)
            throw new IOException("MCP resources/read response is missing contents.");

        var text = string.Join(
            ((char)10).ToString(),
            contents.EnumerateArray()
                .Select(item => item.TryGetProperty("text", out var node)
                    && node.ValueKind == JsonValueKind.String
                        ? node.GetString() ?? ""
                        : "")
                .Where(x => x.Length > 0));

        const int maxChars = 64_000;
        return text.Length <= maxChars
            ? text
            : text[..maxChars] + ((char)10) + "[truncated]";
    }

    public async ValueTask<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (_definitions.Count == 0)
            _ = await ListToolSummariesAsync(cancellationToken).ConfigureAwait(false);
        if (!_definitions.TryGetValue(toolName, out var definition))
            throw new KeyNotFoundException($"MCP tool '{toolName}' is not available.");

        if (!_policy.Allows(
                definition.Summary.ResourceScope,
                definition.Summary.Access))
            throw new UnauthorizedAccessException(
                $"MCP provider scope '{definition.Summary.ResourceScope}' is not allowed for {definition.Summary.Access}.");

        var result = await _connection.CallAsync(
            "tools/call",
            new
            {
                name = toolName,
                arguments = arguments.Clone()
            },
            cancellationToken).ConfigureAwait(false);

        var serialized = JsonSerializer.Serialize(result);
        return serialized.Length <= 64_000
            ? serialized
            : serialized[..64_000] + "...[truncated]";
    }

    public ValueTask DisposeAsync()
        => _connection.DisposeAsync();

    private static AgentToolAccess AccessFor(JsonElement tool)
    {
        if (tool.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("readOnlyHint", out var readOnly)
            && readOnly.ValueKind is JsonValueKind.True or JsonValueKind.False
            && readOnly.GetBoolean())
            return AgentToolAccess.ReadOnly;

        return AgentToolAccess.Mutating;
    }

    private static AgentToolRisk RiskFor(
        JsonElement tool,
        AgentToolAccess access)
    {
        if (tool.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("destructiveHint", out var destructive)
            && destructive.ValueKind is JsonValueKind.True or JsonValueKind.False
            && destructive.GetBoolean())
            return AgentToolRisk.High;

        return access == AgentToolAccess.ReadOnly
            ? AgentToolRisk.Low
            : AgentToolRisk.Medium;
    }

    private static string NamespaceFor(
        string name,
        JsonElement tool)
    {
        var explicitNamespace = MetaString(tool, "h2.namespace", 64);
        if (!string.IsNullOrWhiteSpace(explicitNamespace))
            return explicitNamespace;

        var dot = name.IndexOf('.');
        if (dot > 0)
            return name[..dot].ToLowerInvariant();

        return "mcp." + name
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.ToLowerInvariant() ?? "mcp";
    }

    private static string RequiredString(
        JsonElement element,
        string property,
        int max)
    {
        if (!element.TryGetProperty(property, out var node)
            || node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
            throw new IOException($"MCP metadata is missing required string '{property}'.");
        return Bound(node.GetString()!, max);
    }

    private static string? OptionalString(
        JsonElement element,
        string property,
        int max)
        => element.TryGetProperty(property, out var node)
            && node.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(node.GetString())
                ? Bound(node.GetString()!, max)
                : null;

    private static string? MetaString(
        JsonElement element,
        string property,
        int max)
    {
        if (!element.TryGetProperty("_meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty(property, out var node)
            || node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
            return null;
        return Bound(node.GetString()!, max);
    }

    private static bool? MetaBool(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty("_meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty(property, out var node)
            || node.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        return node.GetBoolean();
    }

    private static string Bound(string value, int max)
    {
        value = value.Trim().Replace((char)13, ' ').Replace((char)10, ' ');
        return value.Length <= max ? value : value[..max];
    }
}

public sealed class McpToolRegistryAdapter
{
    private readonly ToolRegistry _registry;

    public McpToolRegistryAdapter(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<IReadOnlyList<ToolDescriptor>> LoadSelectedAsync(
        McpToolProvider provider,
        IReadOnlyList<string> selectedNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var definitions = await provider.LoadToolDefinitionsAsync(
            selectedNames,
            cancellationToken).ConfigureAwait(false);

        _registry.UnregisterWhere(x =>
            x.Provenance?.ProviderId == provider.Provenance.ProviderId
            && definitions.Any(d => d.Summary.Name == x.Name));

        var descriptors = new List<ToolDescriptor>();
        foreach (var definition in definitions)
        {
            var summary = definition.Summary;
            var executor = new McpRegistryExecutor(provider, summary.Name);
            var descriptor = new ToolDescriptor(
                summary.Name,
                new ToolNamespace(
                    summary.Namespace,
                    $"Capabilities provided by {provider.Provenance.ProviderId}."),
                summary.Description,
                summary.Risk,
                summary.Access,
                summary.SupportsParallel,
                summary.SchemaVersion,
                definition.CallableSchema,
                executor,
                provenance: new ToolProvenance(
                    provider.Provenance.ProviderId,
                    provider.Provenance.ProviderVersion,
                    provider.Provenance.ServerId,
                    summary.ToolVersion),
                resourceScope: new ToolResourceScope(
                    summary.ResourceScope,
                    summary.ResourceScope),
                serializationKey: summary.SerializationKey,
                canProvideVerificationEvidence: false);
            _registry.Register(descriptor);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private sealed class McpRegistryExecutor : IAgentToolExecutor
    {
        private readonly McpToolProvider _provider;
        private readonly string _toolName;

        public McpRegistryExecutor(
            McpToolProvider provider,
            string toolName)
        {
            _provider = provider;
            _toolName = toolName;
        }

        public string ExecutorId
            => "mcp." + _provider.Provenance.ProviderId;

        public ValueTask<string> ExecuteAsync(
            global::H2AgentLab.ToolCall call,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(call.Name, _toolName, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"MCP executor expected '{_toolName}', received '{call.Name}'.");
            return _provider.ExecuteToolAsync(
                _toolName,
                call.Arguments,
                cancellationToken);
        }
    }
}
