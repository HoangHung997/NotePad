using System.Diagnostics;
using System.Text.Json;
using H2AgentLab.Providers;
using H2AgentLab.Tools;

namespace H2AgentLab.Acceptance;

public static class MbMcpAcceptanceTests
{
    private sealed record AcceptanceCase(
        string Id,
        string Requirement,
        string Evidence,
        bool Passed,
        string? Failure);

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-114 MCP acceptance directory.");
        Directory.CreateDirectory(root);

        var cases = new List<AcceptanceCase>();

        async Task Case(
            string id,
            string requirement,
            string evidence,
            Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                cases.Add(new AcceptanceCase(id, requirement, evidence, true, null));
            }
            catch (Exception ex)
            {
                cases.Add(new AcceptanceCase(
                    id,
                    requirement,
                    evidence,
                    false,
                    ex.GetType().Name + ": " + ex.Message));
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Case(
            "MCP-LIFECYCLE",
            "connect/disconnect/reconnect and transient RPC recovery preserve negotiated provider health",
            "McpServerConnection lifecycle + bounded reconnect fixture",
            async () =>
            {
                var transport = new FixtureMcpTransport();
                await using var provider = Provider(
                    transport,
                    Policy(),
                    reconnectAttempts: 1);

                await provider.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                Check(provider.Health.Status == ProviderHealthStatus.Ready
                    && transport.IsRunning
                    && transport.StartCount == 1
                    && transport.InitializeCount == 1
                    && transport.InitializedNotificationCount == 1,
                    "Initial MCP connect/handshake did not reach Ready.");

                await provider.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                Check(provider.Health.Status == ProviderHealthStatus.Disconnected
                    && !transport.IsRunning
                    && transport.StopCount == 1,
                    "Explicit MCP disconnect did not stop provider state.");

                await provider.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                Check(provider.Health.Status == ProviderHealthStatus.Ready
                    && transport.StartCount == 2,
                    "Explicit MCP reconnect did not restart provider.");

                transport.ThrowNextToolsList = true;
                var summaries = await provider.ListToolSummariesAsync(
                    CancellationToken.None).ConfigureAwait(false);
                Check(summaries.Count == 2
                    && provider.Health.Status == ProviderHealthStatus.Ready
                    && transport.StartCount == 3
                    && transport.StopCount >= 2
                    && transport.ToolsListCount == 2,
                    "Transient MCP RPC failure did not reconnect and retry exactly through the connection boundary.");
            }).ConfigureAwait(false);

        await Case(
            "MCP-DISCOVERY",
            "tool and resource discovery/read use normalized bounded provider metadata",
            "McpToolProvider tools/list + resources/list/read",
            async () =>
            {
                var transport = new FixtureMcpTransport();
                await using var provider = Provider(transport, Policy());

                var tools = await provider.ListToolSummariesAsync(
                    CancellationToken.None).ConfigureAwait(false);
                var read = tools.Single(x => x.Name == "mcp.read");
                var write = tools.Single(x => x.Name == "mcp.write");
                Check(read.Namespace == "fixture"
                    && read.Access == AgentToolAccess.ReadOnly
                    && read.Risk == AgentToolRisk.Low
                    && read.SupportsParallel
                    && read.SchemaVersion == "fixture-v2"
                    && read.ResourceScope == "scope:read"
                    && read.ToolVersion == "tool-read-3",
                    "Read-only MCP tool metadata was not normalized correctly.");
                Check(write.Access == AgentToolAccess.Mutating
                    && write.Risk == AgentToolRisk.High
                    && write.ResourceScope == "scope:write"
                    && write.ToolVersion == "tool-write-7",
                    "Mutating MCP tool metadata/risk was not normalized correctly.");

                var resources = await provider.ListResourcesAsync(
                    CancellationToken.None).ConfigureAwait(false);
                Check(resources.Count == 2
                    && resources.Any(x => x.ResourceId == "resource://public"
                        && x.Scope == "scope:resource-public")
                    && resources.Any(x => x.ResourceId == "resource://private"
                        && x.Scope == "scope:resource-private"),
                    "MCP resource discovery lost resource IDs/scopes.");

                var text = await provider.ReadResourceAsync(
                    "resource://public",
                    CancellationToken.None).ConfigureAwait(false);
                Check(text == "public-resource-body"
                    && transport.ResourceReadCount == 1,
                    "Allowed MCP resource read did not return observed content.");
            }).ConfigureAwait(false);

        await Case(
            "MCP-DEFERRED-SCHEMAS",
            "provider discovery does not inject detailed schemas until selected",
            "CapabilityProviderManager + provider-neutral selected-schema adapter",
            async () =>
            {
                var registry = new ToolRegistry();
                var transport = new FixtureMcpTransport();
                var provider = Provider(transport, Policy());
                await using var manager = new CapabilityProviderManager(registry);
                manager.Register(provider);

                var namespaces = await manager.DiscoverNamespacesAsync(
                    CancellationToken.None).ConfigureAwait(false);
                Check(namespaces.Any(x => x.Name == "fixture")
                    && !registry.TryGet("mcp.read", out _)
                    && !registry.TryGet("mcp.write", out _),
                    "MCP discovery eagerly injected detailed schemas into ToolRegistry.");

                var loaded = await manager.LoadMcpToolsAsync(
                    "mcp-acceptance",
                    ["mcp.read"],
                    CancellationToken.None).ConfigureAwait(false);
                Check(loaded.Count == 1
                    && registry.TryGet("mcp.read", out var descriptor)
                    && !registry.TryGet("mcp.write", out _),
                    "MCP selected-schema loading did not remain deferred/selective.");

                var function = descriptor.CallableSchema.GetProperty("function");
                var parameters = function.GetProperty("parameters");
                Check(function.GetProperty("name").GetString() == "mcp.read"
                    && parameters.GetProperty("required")
                        .EnumerateArray()
                        .Any(x => x.GetString() == "query")
                    && parameters.GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
                    "Loaded MCP callable schema did not preserve advertised inputSchema.");

                var result = await descriptor.Executor.ExecuteAsync(
                    Call("mcp.read", new { query = "fixture" }),
                    CancellationToken.None).ConfigureAwait(false);
                Check(result.Contains("mcp-read-ok", StringComparison.Ordinal)
                    && transport.ToolCallCount == 1,
                    "Deferred MCP descriptor did not execute through provider RPC.");
            }).ConfigureAwait(false);

        await Case(
            "MCP-SCOPE-PERMISSION",
            "host policy blocks denied mutation/resource scopes before provider side effects",
            "CapabilityProviderPolicy enforcement for tools and resources",
            async () =>
            {
                var transport = new FixtureMcpTransport();
                await using var provider = Provider(transport, Policy());

                _ = await provider.ListToolSummariesAsync(
                    CancellationToken.None).ConfigureAwait(false);
                _ = await provider.ListResourcesAsync(
                    CancellationToken.None).ConfigureAwait(false);

                var allowed = await provider.ExecuteToolAsync(
                    "mcp.read",
                    JsonSerializer.SerializeToElement(new { query = "ok" }),
                    CancellationToken.None).ConfigureAwait(false);
                Check(allowed.Contains("mcp-read-ok", StringComparison.Ordinal)
                    && transport.ToolCallCount == 1,
                    "Allowed MCP read tool did not execute.");

                try
                {
                    _ = await provider.ExecuteToolAsync(
                        "mcp.write",
                        JsonSerializer.SerializeToElement(new { value = "blocked" }),
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("Denied MCP mutation executed.");
                }
                catch (UnauthorizedAccessException)
                {
                }
                Check(transport.ToolCallCount == 1,
                    "Denied MCP mutation reached tools/call.");

                var publicText = await provider.ReadResourceAsync(
                    "resource://public",
                    CancellationToken.None).ConfigureAwait(false);
                Check(publicText == "public-resource-body",
                    "Allowed MCP resource read failed.");
                var resourceCalls = transport.ResourceReadCount;

                try
                {
                    _ = await provider.ReadResourceAsync(
                        "resource://private",
                        CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException("Denied MCP resource read executed.");
                }
                catch (UnauthorizedAccessException)
                {
                }
                Check(transport.ResourceReadCount == resourceCalls,
                    "Denied MCP resource scope reached resources/read.");
            }).ConfigureAwait(false);

        await Case(
            "MCP-PROVENANCE",
            "loaded descriptors and execution evidence preserve provider/server/tool versions without secret leakage",
            "ToolDescriptor provenance + ProviderExecutionEvidence + safe definition metadata",
            async () =>
            {
                var registry = new ToolRegistry();
                var transport = new FixtureMcpTransport();
                var provider = Provider(transport, Policy());
                await using var manager = new CapabilityProviderManager(registry);
                manager.Register(provider);

                var loaded = await manager.LoadMcpToolsAsync(
                    "mcp-acceptance",
                    ["mcp.read"],
                    CancellationToken.None).ConfigureAwait(false);
                var descriptor = loaded.Single();
                var provenance = descriptor.Provenance
                    ?? throw new InvalidOperationException("MCP descriptor lost provenance.");
                Check(provenance.ProviderId == "mcp-acceptance"
                    && provenance.ProviderVersion == "7.4.2"
                    && provenance.ServerId == "fixture-mcp-server"
                    && provenance.ToolVersion == "tool-read-3"
                    && descriptor.ResourceScope?.ScopeId == "scope:read",
                    "MCP descriptor lost provider/server/tool version or scope identity.");

                var evidence = manager.BuildExecutionEvidence(
                    descriptor,
                    new string('A', 64));
                Check(evidence.ProviderId == "mcp-acceptance"
                    && evidence.ServerId == "fixture-mcp-server"
                    && evidence.ToolName == "mcp.read"
                    && evidence.ToolVersion == "tool-read-3"
                    && evidence.ScopeId == "scope:read"
                    && evidence.ResultHash == new string('a', 64),
                    "MCP execution evidence lost normalized provenance/version/scope.");

                var metadataJson = JsonSerializer.Serialize(
                    provider.Provenance);
                var definitionJson = JsonSerializer.Serialize(
                    Definition().SafeMetadata());
                Check(metadataJson.Contains("7.4.2", StringComparison.Ordinal)
                    && definitionJson.Contains("SECRET_TOKEN", StringComparison.Ordinal)
                    && !definitionJson.Contains("super-secret-value", StringComparison.Ordinal),
                    "MCP safe metadata lost version identity or leaked environment values.");
            }).ConfigureAwait(false);

        await Case(
            "MCP-CANCEL-TIMEOUT",
            "cancellation propagates without retry and configured timeout fails closed with terminal health",
            "McpServerConnection cancellation + timeout state transitions",
            async () =>
            {
                var cancelTransport = new FixtureMcpTransport
                {
                    BlockToolsList = true
                };
                await using (var provider = Provider(
                    cancelTransport,
                    Policy(),
                    timeout: TimeSpan.FromSeconds(2),
                    reconnectAttempts: 2))
                {
                    using var cancel = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(60));
                    try
                    {
                        _ = await provider.ListToolSummariesAsync(
                            cancel.Token).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            "Cancelled MCP discovery unexpectedly completed.");
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    Check(cancelTransport.StartCount == 1
                        && cancelTransport.ToolsListCount == 1
                        && provider.Health.Status == ProviderHealthStatus.Ready,
                        "Cancellation was retried or incorrectly converted into provider failure.");
                }

                var timeoutTransport = new FixtureMcpTransport
                {
                    TimeoutToolsList = true
                };
                var configuredTimeout = TimeSpan.FromMilliseconds(50);
                await using (var provider = Provider(
                    timeoutTransport,
                    Policy(),
                    timeout: configuredTimeout,
                    reconnectAttempts: 0))
                {
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        _ = await provider.ListToolSummariesAsync(
                            CancellationToken.None).ConfigureAwait(false);
                        throw new InvalidOperationException(
                            "Timed-out MCP discovery unexpectedly completed.");
                    }
                    catch (TimeoutException)
                    {
                    }

                    Check(timeoutTransport.LastTimeout == configuredTimeout
                        && clock.Elapsed < TimeSpan.FromSeconds(2)
                        && provider.Health.Status == ProviderHealthStatus.Failed
                        && !timeoutTransport.IsRunning,
                        "MCP timeout did not honor configured deadline or terminal failure state.");
                }
            }).ConfigureAwait(false);

        var passed = cases.Count(x => x.Passed);
        var failed = cases.Count - passed;
        var gatePassed = failed == 0;

        var lines = cases.Select(x =>
                (x.Passed ? "PASS " : "FAIL ")
                + x.Id + " " + x.Requirement
                + " | evidence=" + x.Evidence
                + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        lines.Add($"RESULT: {passed} passed, {failed} failed.");
        lines.Add($"GATE: {(gatePassed ? "PASS" : "FAIL")} MB-114 MCP acceptance.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-mcp-acceptance-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-mcp-acceptance-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-114",
                    passed = gatePassed,
                    passedCases = passed,
                    failedCases = failed,
                    cases
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return gatePassed ? 0 : 1;
    }

    private static McpToolProvider Provider(
        FixtureMcpTransport transport,
        CapabilityProviderPolicy policy,
        TimeSpan? timeout = null,
        int reconnectAttempts = 2)
        => new(
            new McpServerConnection(
                Definition(timeout),
                transport,
                reconnectAttempts),
            policy);

    private static McpServerDefinition Definition(TimeSpan? timeout = null)
        => new(
            "mcp-acceptance",
            "7.4.2",
            "fixture-mcp-server",
            "fixture-command",
            ["--stdio"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SECRET_TOKEN"] = "super-secret-value"
            },
            timeout ?? TimeSpan.FromSeconds(1));

    private static CapabilityProviderPolicy Policy()
        => new(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "scope:read",
                "scope:resource-public"
            },
            new HashSet<string>(StringComparer.Ordinal),
            AllowParallelReadOnly: true);

    private static global::H2AgentLab.ToolCall Call(
        string name,
        object arguments)
        => new(
            "mb114-" + name,
            name,
            JsonSerializer.SerializeToElement(arguments));

    private sealed class FixtureMcpTransport : IMcpRpcTransport
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int InitializeCount { get; private set; }
        public int InitializedNotificationCount { get; private set; }
        public int ToolsListCount { get; private set; }
        public int ToolCallCount { get; private set; }
        public int ResourcesListCount { get; private set; }
        public int ResourceReadCount { get; private set; }
        public bool ThrowNextToolsList { get; set; }
        public bool BlockToolsList { get; set; }
        public bool TimeoutToolsList { get; set; }
        public TimeSpan LastTimeout { get; private set; }

        public Task StartAsync(
            McpServerDefinition definition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            StopCount++;
            return Task.CompletedTask;
        }

        public async Task<JsonElement> CallAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastTimeout = timeout;

            if (method == "initialize")
            {
                InitializeCount++;
                return Element("""
                    {
                      "protocolVersion": "2025-06-18"
                    }
                    """);
            }

            if (method == "tools/list")
            {
                ToolsListCount++;
                if (ThrowNextToolsList)
                {
                    ThrowNextToolsList = false;
                    throw new IOException("synthetic transient MCP transport failure");
                }

                if (TimeoutToolsList)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    deadline.CancelAfter(timeout);
                    try
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("synthetic MCP timeout");
                    }
                }

                if (BlockToolsList)
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken).ConfigureAwait(false);
                }

                return Element("""
                    {
                      "tools": [
                        {
                          "name": "mcp.read",
                          "description": "Read fixture data.",
                          "annotations": {
                            "readOnlyHint": true
                          },
                          "_meta": {
                            "h2.namespace": "fixture",
                            "h2.scope": "scope:read",
                            "h2.parallelSafe": true,
                            "h2.schemaVersion": "fixture-v2",
                            "h2.toolVersion": "tool-read-3"
                          },
                          "inputSchema": {
                            "type": "object",
                            "properties": {
                              "query": { "type": "string" }
                            },
                            "required": [ "query" ],
                            "additionalProperties": false
                          }
                        },
                        {
                          "name": "mcp.write",
                          "description": "Mutate fixture data.",
                          "annotations": {
                            "readOnlyHint": false,
                            "destructiveHint": true
                          },
                          "_meta": {
                            "h2.namespace": "fixture",
                            "h2.scope": "scope:write",
                            "h2.serializationKey": "fixture-write",
                            "h2.schemaVersion": "fixture-v2",
                            "h2.toolVersion": "tool-write-7"
                          },
                          "inputSchema": {
                            "type": "object",
                            "properties": {
                              "value": { "type": "string" }
                            },
                            "required": [ "value" ],
                            "additionalProperties": false
                          }
                        }
                      ]
                    }
                    """);
            }

            if (method == "resources/list")
            {
                ResourcesListCount++;
                return Element("""
                    {
                      "resources": [
                        {
                          "uri": "resource://public",
                          "name": "Public fixture resource",
                          "description": "Readable fixture resource.",
                          "_meta": {
                            "h2.namespace": "fixture.resources",
                            "h2.scope": "scope:resource-public"
                          }
                        },
                        {
                          "uri": "resource://private",
                          "name": "Private fixture resource",
                          "description": "Denied fixture resource.",
                          "_meta": {
                            "h2.namespace": "fixture.resources",
                            "h2.scope": "scope:resource-private"
                          }
                        }
                      ]
                    }
                    """);
            }

            if (method == "resources/read")
            {
                ResourceReadCount++;
                var uri = JsonSerializer.SerializeToElement(parameters)
                    .GetProperty("uri")
                    .GetString();
                return uri switch
                {
                    "resource://public" => Element("""
                        {
                          "contents": [
                            { "text": "public-resource-body" }
                          ]
                        }
                        """),
                    "resource://private" => Element("""
                        {
                          "contents": [
                            { "text": "private-resource-body" }
                          ]
                        }
                        """),
                    _ => throw new IOException("unknown fixture resource")
                };
            }

            if (method == "tools/call")
            {
                ToolCallCount++;
                var payload = JsonSerializer.SerializeToElement(parameters);
                var name = payload.GetProperty("name").GetString();
                return name switch
                {
                    "mcp.read" => Element("""
                        {
                          "content": [
                            { "type": "text", "text": "mcp-read-ok" }
                          ]
                        }
                        """),
                    "mcp.write" => Element("""
                        {
                          "content": [
                            { "type": "text", "text": "mcp-write-ok" }
                          ]
                        }
                        """),
                    _ => throw new IOException("unknown fixture tool")
                };
            }

            throw new InvalidOperationException(
                "Unexpected MCP fixture method: " + method);
        }

        public Task NotifyAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "notifications/initialized")
                InitializedNotificationCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        private static JsonElement Element(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
