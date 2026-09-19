using System.Text.Json;
using H2AgentLab.Cad;
using H2AgentLab.Extensions;
using H2AgentLab.FirstPartyExtensions.Cad;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;
using H2AgentLab.Verification;

namespace H2AgentLab.Acceptance;

public static class MbAutoCadAcceptanceTests
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
            throw new IOException("Use a new MB-113 AutoCAD acceptance directory.");
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
            "AUTOCAD-STRUCTURED-SURFACE",
            "AutoCAD extension exposes only structured typed discovery/read/mutate/verify tools",
            "AutoCadFirstPartyExtension + AutoCadProviderPolicy typed v2 schemas",
            async () =>
            {
                var bridge = new FixtureBridge();
                var registry = new ToolRegistry();
                var skills = new SkillCatalog();
                var verifiers = new ArtifactVerifierRegistry();
                await using var providers = new CapabilityProviderManager(registry);
                var extensions = new AgentExtensionRegistry(
                    registry,
                    skills,
                    verifiers,
                    providers);
                extensions.Register(new AutoCadFirstPartyExtension(
                    bridge,
                    (_, _) => ValueTask.FromResult(true)));

                var expected = new[]
                {
                    "autocad.get_active_document",
                    "autocad.list_documents",
                    "autocad.plot",
                    "autocad.query_entities",
                    "autocad.read_attributes",
                    "autocad.read_layers",
                    "autocad.update_attribute",
                    "autocad.update_entity",
                    "autocad.verify_entity",
                    "autocad.verify_plot"
                }.OrderBy(x => x, StringComparer.Ordinal).ToArray();

                var actual = registry.GetNamespace("autocad")
                    .Select(x => x.Name)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();
                Check(actual.SequenceEqual(expected),
                    "Structured AutoCAD tool surface changed.");

                foreach (var descriptor in registry.GetNamespace("autocad"))
                {
                    var function = descriptor.CallableSchema.GetProperty("function");
                    var parameters = function.GetProperty("parameters");
                    Check(descriptor.SchemaVersion == "v2"
                        && descriptor.Preference?.InteractionFidelity == ToolInteractionFidelity.Structured
                        && descriptor.Provenance?.ProviderId == "autocad-native-plugin"
                        && parameters.GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
                        "AutoCAD descriptor lost typed/fail-closed structured metadata: " + descriptor.Name);
                }

                Check(!actual.Any(x =>
                        x.Contains("command", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("script", StringComparison.OrdinalIgnoreCase)
                        || x.Contains("lisp", StringComparison.OrdinalIgnoreCase)),
                    "Arbitrary command/script tool leaked into the default AutoCAD surface.");
            }).ConfigureAwait(false);

        await Case(
            "AUTOCAD-DISCOVERY-READ",
            "structured document/entity discovery and attribute/layer reads preserve state tokens",
            "typed native bridge list/query/read fixture",
            async () =>
            {
                var bridge = new FixtureBridge();
                var registry = Registry(
                    bridge,
                    (_, _) => ValueTask.FromResult(true));

                var documents = await Execute(
                    registry,
                    "autocad.list_documents",
                    new { }).ConfigureAwait(false);
                var document = documents.EnumerateArray().Single();
                var session = document.GetProperty("SessionId").GetString()!;
                var documentState = document.GetProperty("StateToken").GetString()!;
                Check(session == FixtureBridge.DocumentSession
                    && documentState == bridge.DocumentStateToken,
                    "Document discovery lost session/state identity.");

                var entities = await Execute(
                    registry,
                    "autocad.query_entities",
                    new
                    {
                        document_session_id = session,
                        document_state_token = documentState,
                        query = new
                        {
                            object_type = "BlockReference",
                            layer = "A-WALL",
                            max_results = 10
                        }
                    }).ConfigureAwait(false);
                var entity = entities.EnumerateArray().Single();
                var handle = entity.GetProperty("Handle").GetString()!;
                var entityState = entity.GetProperty("StateToken").GetString()!;
                Check(handle == FixtureBridge.EntityHandle
                    && entity.GetProperty("DocumentStateToken").GetString() == documentState
                    && entityState == bridge.EntityStateToken,
                    "Entity discovery lost document/entity state tokens.");

                var attributes = await Execute(
                    registry,
                    "autocad.read_attributes",
                    new
                    {
                        document_session_id = session,
                        document_state_token = documentState,
                        entity_handle = handle,
                        object_type = "BlockReference",
                        layer = "A-WALL",
                        entity_state_token = entityState
                    }).ConfigureAwait(false);
                Check(attributes.GetProperty("attributes")
                        .GetProperty("NAME")
                        .GetString() == "Original",
                    "Structured attribute read failed.");

                var layers = await Execute(
                    registry,
                    "autocad.read_layers",
                    new
                    {
                        document_session_id = session,
                        document_state_token = documentState
                    }).ConfigureAwait(false);
                Check(layers.GetProperty("layers")
                        .EnumerateArray()
                        .Any(x => x.GetProperty("name").GetString() == "A-WALL"),
                    "Structured layer read failed.");
            }).ConfigureAwait(false);

        await Case(
            "AUTOCAD-BOUNDED-MUTATION",
            "typed mutation requires host approval and rejects oversized/arbitrary execution payloads before the bridge",
            "AutoCadNativeToolExecutor host authorizer + AutoCadProviderPolicy bounds",
            async () =>
            {
                var deniedBridge = new FixtureBridge();
                var denied = Registry(
                    deniedBridge,
                    (_, _) => ValueTask.FromResult(false));
                var beforeDenied = deniedBridge.MutationCount;

                try
                {
                    _ = await UpdateAttribute(
                        denied,
                        deniedBridge,
                        "Denied").ConfigureAwait(false);
                    throw new InvalidOperationException("Denied mutation executed.");
                }
                catch (UnauthorizedAccessException)
                {
                }

                Check(deniedBridge.MutationCount == beforeDenied,
                    "Host-denied mutation reached the native bridge.");

                var boundedBridge = new FixtureBridge();
                var bounded = Registry(
                    boundedBridge,
                    (_, _) => ValueTask.FromResult(true));
                var beforeBounded = boundedBridge.MutationCount;

                try
                {
                    _ = await Execute(
                        bounded,
                        "autocad.update_entity",
                        new
                        {
                            document_session_id = FixtureBridge.DocumentSession,
                            document_state_token = boundedBridge.DocumentStateToken,
                            entity_handle = FixtureBridge.EntityHandle,
                            object_type = "BlockReference",
                            layer = "A-WALL",
                            entity_state_token = boundedBridge.EntityStateToken,
                            changes = new
                            {
                                command = "_.ERASE ALL"
                            }
                        }).ConfigureAwait(false);
                    throw new InvalidOperationException("Arbitrary command field was accepted.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("arbitrary command", StringComparison.OrdinalIgnoreCase))
                {
                }

                var oversized = new string(
                    'x',
                    AutoCadProviderPolicy.MaxStructuredStringCharacters + 1);
                try
                {
                    _ = await Execute(
                        bounded,
                        "autocad.update_entity",
                        new
                        {
                            document_session_id = FixtureBridge.DocumentSession,
                            document_state_token = boundedBridge.DocumentStateToken,
                            entity_handle = FixtureBridge.EntityHandle,
                            object_type = "BlockReference",
                            layer = "A-WALL",
                            entity_state_token = boundedBridge.EntityStateToken,
                            changes = new
                            {
                                note = oversized
                            }
                        }).ConfigureAwait(false);
                    throw new InvalidOperationException("Oversized mutation payload was accepted.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("exceeds the limit", StringComparison.OrdinalIgnoreCase))
                {
                }

                Check(boundedBridge.MutationCount == beforeBounded,
                    "Rejected mutation reached the native bridge.");
            }).ConfigureAwait(false);

        await Case(
            "AUTOCAD-STATE-TOKEN",
            "successful mutation advances document/entity state tokens and invalidates stale reads",
            "fixture bridge before/after state identity",
            async () =>
            {
                var bridge = new FixtureBridge();
                var registry = Registry(
                    bridge,
                    (_, _) => ValueTask.FromResult(true));
                var oldDocumentState = bridge.DocumentStateToken;
                var oldEntityState = bridge.EntityStateToken;

                var mutation = await UpdateAttribute(
                    registry,
                    bridge,
                    "Updated").ConfigureAwait(false);
                var after = mutation.GetProperty("After");
                Check(mutation.GetProperty("MutationId").GetString() == "mutation-1"
                    && after.GetProperty("DocumentStateToken").GetString() != oldDocumentState
                    && after.GetProperty("StateToken").GetString() != oldEntityState
                    && mutation.GetProperty("EvidenceId").GetString() == "evidence:autocad:mutation-1",
                    "Mutation did not return new state/evidence identity.");

                try
                {
                    _ = await Execute(
                        registry,
                        "autocad.read_attributes",
                        new
                        {
                            document_session_id = FixtureBridge.DocumentSession,
                            document_state_token = oldDocumentState,
                            entity_handle = FixtureBridge.EntityHandle,
                            object_type = "BlockReference",
                            layer = "A-WALL",
                            entity_state_token = oldEntityState
                        }).ConfigureAwait(false);
                    throw new InvalidOperationException("Stale AutoCAD read was accepted.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("stale", StringComparison.OrdinalIgnoreCase))
                {
                }
            }).ConfigureAwait(false);

        await Case(
            "AUTOCAD-VERIFY",
            "mutation completion is independently verified against current document/entity state",
            "autocad.verify_entity typed verification path",
            async () =>
            {
                var bridge = new FixtureBridge();
                var registry = Registry(
                    bridge,
                    (_, _) => ValueTask.FromResult(true));

                var mutation = await UpdateAttribute(
                    registry,
                    bridge,
                    "Verified").ConfigureAwait(false);
                var after = mutation.GetProperty("After");

                var verified = await Execute(
                    registry,
                    "autocad.verify_entity",
                    new
                    {
                        document_session_id = FixtureBridge.DocumentSession,
                        document_state_token = after.GetProperty("DocumentStateToken").GetString(),
                        entity_handle = FixtureBridge.EntityHandle,
                        entity_state_token = after.GetProperty("StateToken").GetString(),
                        expectation = new
                        {
                            attribute_tag = "NAME",
                            equals = "Verified"
                        }
                    }).ConfigureAwait(false);
                Check(verified.ValueKind == JsonValueKind.True
                    && verified.GetBoolean(),
                    "Current AutoCAD mutation did not verify.");

                var failed = await Execute(
                    registry,
                    "autocad.verify_entity",
                    new
                    {
                        document_session_id = FixtureBridge.DocumentSession,
                        document_state_token = after.GetProperty("DocumentStateToken").GetString(),
                        entity_handle = FixtureBridge.EntityHandle,
                        entity_state_token = after.GetProperty("StateToken").GetString(),
                        expectation = new
                        {
                            attribute_tag = "NAME",
                            equals = "Wrong"
                        }
                    }).ConfigureAwait(false);
                Check(failed.ValueKind == JsonValueKind.False
                    && !failed.GetBoolean(),
                    "Incorrect AutoCAD expectation was accepted as verified.");
            }).ConfigureAwait(false);

        await Case(
            "AUTOCAD-NO-ARBITRARY-COMMAND",
            "source and public schemas expose typed provider operations rather than arbitrary AutoCAD command execution",
            "provider contract source guard + descriptor schema inspection",
            () =>
            {
                var repo = FindRepoRoot();
                var contract = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Cad",
                    "AutoCadProviderContract.cs"));
                var extension = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "FirstPartyExtensions",
                    "Cad",
                    "AutoCadFirstPartyExtension.cs"));
                var runtime = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab",
                    "Runtime",
                    "AgentRuntime.cs"));

                Check(contract.Contains(
                        "Agent -> provider/IPC -> AutoCAD plugin -> DocumentLock -> Transaction -> Database",
                        StringComparison.Ordinal)
                    && contract.Contains(
                        "AutoCadNativeToolExecutor",
                        StringComparison.Ordinal)
                    && contract.Contains(
                        "additionalProperties = false",
                        StringComparison.Ordinal),
                    "AutoCAD typed bridge/schema boundary is missing.");
                Check(!extension.Contains("SendCommand", StringComparison.OrdinalIgnoreCase)
                    && !extension.Contains("ExecuteCommand", StringComparison.OrdinalIgnoreCase)
                    && !runtime.Contains("autocad.", StringComparison.OrdinalIgnoreCase),
                    "AutoCAD arbitrary command/domain execution leaked into extension/core runtime.");
                return Task.CompletedTask;
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
        lines.Add($"GATE: {(gatePassed ? "PASS" : "FAIL")} MB-113 AutoCAD extension acceptance.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-autocad-acceptance-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-autocad-acceptance-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-113",
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

    private static ToolRegistry Registry(
        FixtureBridge bridge,
        Func<string, CancellationToken, ValueTask<bool>> authorizeMutation)
    {
        var registry = new ToolRegistry();
        var descriptors = AutoCadProviderPolicy.BuildDescriptors(
            new AutoCadNativeToolExecutor(bridge, authorizeMutation));
        foreach (var descriptor in descriptors)
            registry.Register(descriptor);
        return registry;
    }

    private static async Task<JsonElement> UpdateAttribute(
        ToolRegistry registry,
        FixtureBridge bridge,
        string value)
        => await Execute(
            registry,
            "autocad.update_attribute",
            new
            {
                document_session_id = FixtureBridge.DocumentSession,
                document_state_token = bridge.DocumentStateToken,
                entity_handle = FixtureBridge.EntityHandle,
                object_type = "BlockReference",
                layer = "A-WALL",
                entity_state_token = bridge.EntityStateToken,
                attribute_tag = "NAME",
                value
            }).ConfigureAwait(false);

    private static async Task<JsonElement> Execute(
        ToolRegistry registry,
        string name,
        object arguments)
    {
        if (!registry.TryGet(name, out var descriptor))
            throw new InvalidOperationException("Missing AutoCAD tool: " + name);
        var result = await descriptor.Executor.ExecuteAsync(
            new global::H2AgentLab.ToolCall(
                "mb113-" + name,
                name,
                JsonSerializer.SerializeToElement(arguments)),
            CancellationToken.None).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result);
        return document.RootElement.Clone();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FixtureBridge : IAutoCadNativeBridge
    {
        public const string DocumentSession = "cad-doc-1";
        public const string EntityHandle = "1A2B";

        private readonly Dictionary<string, string> _attributes =
            new(StringComparer.Ordinal)
            {
                ["NAME"] = "Original",
                ["CODE"] = "A1"
            };
        private string _layer = "A-WALL";
        private int _documentVersion = 1;
        private int _entityVersion = 1;
        private int _mutationVersion;
        private JsonElement? _lastPlot;

        public string DocumentStateToken => "doc-state-" + _documentVersion;
        public string EntityStateToken => "entity-state-" + _entityVersion;
        public int MutationCount => _mutationVersion;

        public Task<IReadOnlyList<AutoCadDocumentRef>> ListDocumentsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AutoCadDocumentRef>>(
            [
                Document()
            ]);
        }

        public Task<AutoCadDocumentRef?> GetActiveDocumentAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AutoCadDocumentRef?>(Document());
        }

        public Task<IReadOnlyList<AutoCadEntityRef>> QueryEntitiesAsync(
            string documentSessionId,
            string documentStateToken,
            JsonElement query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDocument(documentSessionId, documentStateToken);
            if (query.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Fixture query must be an object.");
            return Task.FromResult<IReadOnlyList<AutoCadEntityRef>>(
            [
                Entity()
            ]);
        }

        public Task<JsonElement> ReadAttributesAsync(
            AutoCadEntityRef entity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireEntity(entity);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                entity = entity.Handle,
                attributes = _attributes
            }));
        }

        public Task<JsonElement> ReadLayersAsync(
            string documentSessionId,
            string documentStateToken,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDocument(documentSessionId, documentStateToken);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                layers = new[]
                {
                    new { name = _layer, locked = false },
                    new { name = "0", locked = false }
                }
            }));
        }

        public Task<AutoCadMutationResult> MutateAsync(
            AutoCadMutationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutoCadProviderPolicy.ValidateMutation(request);
            RequireDocument(request.DocumentSessionId, request.DocumentStateToken);
            var before = Entity();
            if (request.EntityHandle != before.Handle
                || request.EntityStateToken != before.StateToken)
                throw new InvalidOperationException("stale entity state token");

            if (request.Operation == AutoCadOperationKind.UpdateAttribute)
            {
                var tag = request.Parameters.GetProperty("attributeTag").GetString()!;
                var value = request.Parameters.GetProperty("value").GetString()!;
                _attributes[tag] = value;
            }
            else if (request.Operation == AutoCadOperationKind.UpdateEntity)
            {
                if (request.Parameters.TryGetProperty("layer", out var layer)
                    && layer.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(layer.GetString()))
                    _layer = layer.GetString()!;
            }

            _mutationVersion++;
            _documentVersion++;
            _entityVersion++;
            var after = Entity();
            return Task.FromResult(new AutoCadMutationResult(
                "mutation-" + _mutationVersion,
                before,
                after,
                "evidence:autocad:mutation-" + _mutationVersion));
        }

        public Task<JsonElement> PlotAsync(
            string documentSessionId,
            string documentStateToken,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDocument(documentSessionId, documentStateToken);
            AutoCadProviderPolicy.ValidateBoundedPayload(request, "plot request");
            _lastPlot = request.Clone();
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                plotted = true,
                evidenceId = "evidence:autocad:plot"
            }));
        }

        public Task<bool> VerifyAsync(
            string documentSessionId,
            string currentDocumentStateToken,
            JsonElement verification,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDocument(documentSessionId, currentDocumentStateToken);

            var kind = verification.GetProperty("kind").GetString();
            if (kind == "entity")
            {
                if (verification.GetProperty("entityHandle").GetString() != EntityHandle
                    || verification.GetProperty("entityStateToken").GetString() != EntityStateToken)
                    return Task.FromResult(false);

                var expectation = verification.GetProperty("expectation");
                if (expectation.TryGetProperty("attribute_tag", out var tag)
                    && expectation.TryGetProperty("equals", out var expected))
                {
                    return Task.FromResult(
                        _attributes.TryGetValue(tag.GetString() ?? "", out var actual)
                        && actual == expected.GetString());
                }

                if (expectation.TryGetProperty("layer", out var expectedLayer))
                    return Task.FromResult(_layer == expectedLayer.GetString());

                return Task.FromResult(false);
            }

            if (kind == "plot")
                return Task.FromResult(_lastPlot is not null);

            return Task.FromResult(false);
        }

        private AutoCadDocumentRef Document()
            => new(
                DocumentSession,
                "fixture.dwg",
                @"C:\fixture\fixture.dwg",
                DocumentStateToken);

        private AutoCadEntityRef Entity()
            => new(
                DocumentSession,
                DocumentStateToken,
                EntityHandle,
                "BlockReference",
                _layer,
                EntityStateToken);

        private void RequireDocument(
            string documentSessionId,
            string documentStateToken)
        {
            if (documentSessionId != DocumentSession
                || documentStateToken != DocumentStateToken)
                throw new InvalidOperationException(
                    "stale document state token");
        }

        private void RequireEntity(AutoCadEntityRef entity)
        {
            RequireDocument(
                entity.DocumentSessionId,
                entity.DocumentStateToken);
            if (entity.Handle != EntityHandle
                || entity.StateToken != EntityStateToken)
                throw new InvalidOperationException(
                    "stale entity state token");
        }
    }
}
