using System.Text.Json;
using H2AgentLab.Capabilities;
using H2AgentLab.Plugins;
using H2AgentLab.Providers;
using H2AgentLab.Runtime;
using H2AgentLab.Skills;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed class H2AgentExtensionLifecycleHost : IAsyncDisposable
{
    private readonly ToolRegistry _extensions = new();
    private readonly CapabilityProviderManager _providers;
    private readonly PluginManager _plugins;
    private readonly object _gate = new();
    private IReadOnlyList<PluginManager.RestoreResult> _restore = [];

    public H2AgentExtensionLifecycleHost(
        string stateRoot,
        IEnumerable<ICapabilityProvider>? providers = null,
        IPluginToolExecutorResolver? trustedFallbackResolver = null)
    {
        _providers = new CapabilityProviderManager(_extensions);
        foreach (var provider in providers ?? [])
            _providers.Register(provider);
        _plugins = new PluginManager(
            Path.Combine(Path.GetFullPath(stateRoot), "extensions"),
            _extensions,
            new ProductionPluginResolver(_providers, trustedFallbackResolver));
        _restore = _plugins.RestoreActivePlugins();
    }

    public IReadOnlyList<H2AgentPluginState> GetPlugins()
    {
        lock (_gate)
        {
            var restoreByKey = _restore.ToDictionary(
                x => x.PluginId + "@" + x.Version,
                x => x,
                StringComparer.Ordinal);
            return _plugins.InstalledVersions()
                .Select(state =>
                {
                    restoreByKey.TryGetValue(state.PluginId + "@" + state.Version, out var restore);
                    var ready = state.IntegrityValid && (restore?.Restored != false);
                    return new H2AgentPluginState(
                        state.PluginId,
                        state.Version,
                        state.Publisher,
                        state.Enabled,
                        state.Quarantined,
                        ready,
                        state.Tools,
                        state.Skills,
                        state.Providers,
                        state.ProblemCode ?? restore?.ProblemCode);
                })
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .ThenByDescending(x => Version.TryParse(x.Version, out var version) ? version : new Version())
                .ToArray();
        }
    }

    public H2AgentPluginState Install(H2AgentPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var path = Path.GetFullPath(package.LocalArchivePath);
        var trust = package.Trust switch
        {
            H2AgentPluginTrust.TrustedOfficial => PluginTrustState.TrustedOfficial,
            H2AgentPluginTrust.OrganizationApproved => PluginTrustState.OrganizationApproved,
            H2AgentPluginTrust.LocalDeveloper => PluginTrustState.LocalDeveloper,
            H2AgentPluginTrust.Untrusted => PluginTrustState.Untrusted,
            _ => PluginTrustState.Unknown
        };
        var mode = package.Trust switch
        {
            H2AgentPluginTrust.TrustedOfficial => PluginInstallMode.TrustedOfficialOnly,
            H2AgentPluginTrust.OrganizationApproved => PluginInstallMode.OrganizationApproved,
            H2AgentPluginTrust.LocalDeveloper => PluginInstallMode.DeveloperLocal,
            _ => PluginInstallMode.Disabled
        };
        var policy = new PluginInstallPolicy(
            mode,
            package.UserApproved
                ? new HashSet<string>([package.Publisher], StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            AllowNativeHelpers: false,
            AllowLifecycleHooks: false);
        var entry = new PluginCatalogEntry(
            package.Id,
            package.Name,
            package.Version,
            package.Summary,
            package.Publisher,
            [package.Id, package.Name],
            [],
            package.MinAgentVersion,
            trust,
            package.ArchiveSha256,
            path);

        lock (_gate)
        {
            _ = _plugins.InstallFromArchive(path, entry, policy, package.UserApproved);
            _restore = [];
            return State(package.Id, package.Version);
        }
    }

    public H2AgentPluginState Enable(string pluginId, string version)
    {
        lock (_gate)
        {
            _ = _plugins.Enable(pluginId, version);
            _restore = [];
            return State(pluginId, version);
        }
    }

    public void Disable(string pluginId)
    {
        lock (_gate)
        {
            _plugins.Disable(pluginId);
            _restore = [];
        }
    }

    public H2AgentPluginState SelfTest(string pluginId, string version)
    {
        lock (_gate)
        {
            _ = _plugins.SelfTest(pluginId, version);
            return State(pluginId, version);
        }
    }

    public H2AgentPluginState Rollback(string pluginId)
    {
        lock (_gate)
        {
            var manifest = _plugins.Rollback(pluginId);
            _restore = [];
            return State(pluginId, manifest.Version);
        }
    }

    public void Quarantine(string pluginId, string version, string reason)
    {
        lock (_gate)
        {
            _plugins.Quarantine(pluginId, version, reason);
            _restore = [];
        }
    }

    public void Uninstall(string pluginId, string version)
    {
        lock (_gate)
        {
            _plugins.Uninstall(pluginId, version);
            _restore = [];
        }
    }

    public IAgentRuntimeExtensionSession CreateTaskSession(
        Guid taskId,
        global::H2AgentLab.AgentTools tools,
        Action<TaskCapabilitySnapshot> snapshotObserved)
        => new TaskSession(this, taskId, tools, snapshotObserved);

    private H2AgentPluginState State(string pluginId, string version)
        => GetPlugins().Single(x => x.Id == pluginId && x.Version == version);

    private sealed class ProductionPluginResolver(
        CapabilityProviderManager providers,
        IPluginToolExecutorResolver? fallback) : IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(H2PluginManifest manifest, PluginToolDefinition tool)
        {
            if (manifest.Providers.Count == 1)
                return new ProviderBoundExecutor(providers, manifest.Providers[0], tool);
            if (manifest.Providers.Count == 0 && fallback is not null)
                return fallback.Resolve(manifest, tool);
            throw new NotSupportedException(
                manifest.Providers.Count == 0
                    ? "Plugin tool has no configured trusted product executor."
                    : "Plugin tool must bind to exactly one declared provider.");
        }
    }

    private sealed class ProviderBoundExecutor(
        CapabilityProviderManager providers,
        string providerId,
        PluginToolDefinition expected) : IAgentToolExecutor
    {
        public string ExecutorId => "plugin-provider:" + providerId + ":" + expected.Name;

        public async ValueTask<string> ExecuteAsync(
            global::H2AgentLab.ToolCall call,
            CancellationToken cancellationToken)
        {
            var provider = providers.Provider(providerId);
            if (provider.Health.Status != ProviderHealthStatus.Ready)
                await provider.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var definitions = await provider.LoadToolDefinitionsAsync(
                [expected.Name],
                cancellationToken).ConfigureAwait(false);
            var definition = definitions.SingleOrDefault()
                ?? throw new InvalidDataException("Declared plugin provider tool is unavailable.");
            var actual = definition.Summary;
            if (actual.Name != expected.Name
                || actual.Namespace != expected.Namespace
                || actual.Access != expected.Access
                || actual.Risk != expected.Risk
                || actual.SupportsParallel != expected.SupportsParallel
                || actual.SchemaVersion != expected.SchemaVersion
                || actual.ResourceScope != expected.ResourceScope
                || actual.SerializationKey != expected.SerializationKey
                || actual.ToolVersion != expected.ToolVersion
                || !JsonElement.DeepEquals(definition.CallableSchema, expected.CallableSchema))
                throw new InvalidDataException("Plugin/provider tool contract differs from admitted package metadata.");

            return provider is IInvocationAwareCapabilityProvider aware && call.Invocation is { } invocation
                ? await aware.ExecuteToolAsync(expected.Name, call.Arguments, invocation, cancellationToken).ConfigureAwait(false)
                : await provider.ExecuteToolAsync(expected.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TaskSession : IAgentRuntimeExtensionSession
    {
        private readonly H2AgentExtensionLifecycleHost _owner;
        private readonly Guid _taskId;
        private readonly Action<TaskCapabilitySnapshot> _snapshotObserved;
        private readonly object _gate = new();
        private readonly Dictionary<string, IDisposable> _pins = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedTools = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedPlugins = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SkillSummary> _selectedSkills = new(StringComparer.Ordinal);
        private ToolRegistry? _registry;
        private long _revision;
        private bool _disposed;

        public TaskSession(
            H2AgentExtensionLifecycleHost owner,
            Guid taskId,
            global::H2AgentLab.AgentTools tools,
            Action<TaskCapabilitySnapshot> snapshotObserved)
        {
            _owner = owner;
            _taskId = taskId;
            _snapshotObserved = snapshotObserved;
            tools.Skills.Register(new TaskPinnedPluginSkillSource(
                new PluginSkillSource(owner._plugins),
                PinSkill));
        }

        public void Populate(ToolRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _registry = registry;
                foreach (var descriptor in _owner._extensions.Tools.Where(x =>
                    x.Provenance?.ProviderId.StartsWith("plugin.", StringComparison.Ordinal) == true))
                {
                    if (registry.TryGet(descriptor.Name, out _))
                        throw new InvalidOperationException(
                            "Installed plugin tool conflicts with an existing production tool: " + descriptor.Name);
                    var pluginId = descriptor.Provenance!.ProviderId["plugin.".Length..];
                    var version = descriptor.Provenance.ProviderVersion;
                    var executor = new TaskPinnedExecutor(this, pluginId, version, descriptor.Name, descriptor.Executor);
                    registry.Register(new ToolDescriptor(
                        descriptor.Name,
                        descriptor.Namespace,
                        descriptor.Description,
                        descriptor.Risk,
                        descriptor.Access,
                        descriptor.SupportsParallel,
                        descriptor.SchemaVersion,
                        descriptor.CallableSchema,
                        executor,
                        descriptor.Provenance,
                        descriptor.ResourceScope,
                        descriptor.SerializationKey,
                        descriptor.CanProvideVerificationEvidence,
                        descriptor.Preference,
                        descriptor.Readiness,
                        descriptor.Limits,
                        descriptor.Dependencies,
                        descriptor.SupportedOperations,
                        descriptor.ResultFormat,
                        descriptor.Preflight,
                        descriptor.ReadinessSnapshot));
                }
            }
        }

        private void PinTool(string pluginId, string version, string toolName)
        {
            lock (_gate)
            {
                EnsurePin(pluginId, version);
                if (_usedTools.Add(toolName))
                    PublishSnapshot("plugin tool used: " + toolName);
            }
        }

        private void PinSkill(SkillSummary skill)
        {
            var pluginId = skill.Identity.PluginId
                ?? throw new InvalidOperationException("Plugin skill has no plugin identity.");
            var version = skill.Identity.PluginVersion
                ?? throw new InvalidOperationException("Plugin skill has no plugin version.");
            lock (_gate)
            {
                EnsurePin(pluginId, version);
                var key = skill.Identity.SourceId + "|" + pluginId + "|" + version + "|" + skill.Identity.SkillId;
                if (_selectedSkills.TryAdd(key, skill))
                    PublishSnapshot("plugin skill selected: " + skill.Identity.SkillId);
            }
        }

        private void EnsurePin(string pluginId, string version)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pins.ContainsKey(pluginId))
                return;
            _pins.Add(pluginId, _owner._plugins.PinVersion(pluginId, version));
            _usedPlugins.Add(pluginId);
        }

        private void PublishSnapshot(string reason)
        {
            if (_registry is null)
                throw new InvalidOperationException("Runtime registry is not ready for capability pinning.");
            var activePlugins = _owner._plugins.ActivePlugins();
            var activeById = activePlugins.ToDictionary(x => x.Manifest.Id, x => x.Manifest, StringComparer.Ordinal);
            var knownProviders = _owner._providers.Providers.Select(x => x.ProviderId).ToHashSet(StringComparer.Ordinal);
            var providerIds = _usedPlugins
                .Where(activeById.ContainsKey)
                .SelectMany(id => activeById[id].Providers)
                .Where(knownProviders.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var snapshot = TaskCapabilitySnapshotBuilder.CaptureUsed(
                _taskId,
                ++_revision,
                _registry,
                _owner._providers.Providers,
                activePlugins,
                new TaskCapabilitySelection(
                    Model: null,
                    UsedToolNames: _usedTools.ToArray(),
                    UsedProviderIds: providerIds,
                    UsedPluginIds: _usedPlugins.ToArray(),
                    SelectedSkills: _selectedSkills.Values.ToArray(),
                    Policies: Array.Empty<TaskPolicyPin>()),
                reason);
            _snapshotObserved(snapshot);
        }

        public void Dispose()
        {
            IDisposable[] leases;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                leases = _pins.Values.ToArray();
                _pins.Clear();
            }
            foreach (var lease in leases.Reverse())
                lease.Dispose();
        }

        private sealed class TaskPinnedExecutor(
            TaskSession owner,
            string pluginId,
            string version,
            string toolName,
            IAgentToolExecutor inner) : IAgentToolExecutor
        {
            public string ExecutorId => inner.ExecutorId;
            public async ValueTask<string> ExecuteAsync(
                global::H2AgentLab.ToolCall call,
                CancellationToken cancellationToken)
            {
                owner.PinTool(pluginId, version, toolName);
                return await inner.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class TaskPinnedPluginSkillSource(
            PluginSkillSource inner,
            Action<SkillSummary> selected) : ISkillSource, IInstalledSkillMetadataSource
        {
            public string SourceId => inner.SourceId;
            public SkillSourceKind SourceKind => inner.SourceKind;
            public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
                => inner.Search(query, maxResults);
            public IReadOnlyList<SkillSummary> SnapshotMetadata()
                => inner.SnapshotMetadata();
            public SkillContent Read(SkillIdentity identity)
            {
                var content = inner.Read(identity);
                selected(content.Summary);
                return content;
            }
            public SkillResourceContent ReadResource(SkillIdentity identity, string relativePath)
            {
                var summary = inner.SnapshotMetadata().Single(x =>
                    x.Identity.PluginId == identity.PluginId
                    && x.Identity.PluginVersion == identity.PluginVersion
                    && x.Identity.SkillId == identity.SkillId);
                selected(summary);
                return inner.ReadResource(identity, relativePath);
            }
        }
    }

    public ValueTask DisposeAsync() => _providers.DisposeAsync();
}
