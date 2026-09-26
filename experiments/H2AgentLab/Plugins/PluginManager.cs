using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Plugins;

public enum PluginInstallMode
{
    Disabled = 0,
    TrustedOfficialOnly = 1,
    AskForNewPublisherPackage = 2,
    OrganizationApproved = 3,
    DeveloperLocal = 4
}

public sealed record PluginInstallPolicy(
    PluginInstallMode Mode,
    IReadOnlySet<string> ApprovedPublishers,
    bool AllowNativeHelpers,
    bool AllowLifecycleHooks);

public sealed record PluginActivationRecord(
    string ActiveVersion,
    string? PreviousVersion,
    DateTime ActivatedUtc)
{
    public bool Enabled { get; init; } = true;
}

public sealed record PluginToolDefinition(
    string Name,
    string Namespace,
    string Description,
    AgentToolAccess Access,
    AgentToolRisk Risk,
    bool SupportsParallel,
    string SchemaVersion,
    string ToolVersion,
    string ResourceScope,
    string SerializationKey,
    JsonElement CallableSchema);

public sealed record PluginInstallResult(
    string PluginId,
    string Version,
    string VersionRoot,
    bool Activated,
    IReadOnlyList<string> RegisteredTools,
    string ArchiveSha256,
    string PayloadSha256);

public sealed record PluginVersionEvidence(
    string PluginId,
    string PluginVersion,
    string Publisher,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<PluginCapabilityIndexEntry> Skills,
    IReadOnlyList<string> ToolVersions);

public interface IPluginToolExecutorResolver
{
    IAgentToolExecutor Resolve(
        H2PluginManifest manifest,
        PluginToolDefinition tool);
}

public sealed partial class PluginManager
{
    private readonly string _root;
    private readonly ToolRegistry _registry;
    private readonly IPluginToolExecutorResolver _resolver;
    private readonly Version _agentVersion;
    private readonly object _sync = new();
    private int _inFlightToolCalls;

    public PluginManager(
        string stateRoot,
        ToolRegistry registry,
        IPluginToolExecutorResolver resolver,
        string agentVersion = "2.0.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.Combine(Path.GetFullPath(stateRoot), "plugins");
        Directory.CreateDirectory(_root);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _agentVersion = Version.Parse(agentVersion);
    }

    public string Root => _root;

    public IDisposable EnterToolCall()
    {
        lock (_sync)
            _inFlightToolCalls++;
        return new Scope(this);
    }

    private PluginInstallResult InstallFromArchiveCore(
        string archivePath,
        PluginCatalogEntry catalogEntry,
        PluginInstallPolicy policy,
        bool userApproved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(policy);
        EnsureActivationBoundary();

        H2PluginManifest.ValidateHash(catalogEntry.ArchiveSha256, "Catalog archive hash");
        if (new FileInfo(archivePath).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Plugin archive exceeds 32 MiB.");
        byte[] archiveBytes;
        using (var input = File.OpenRead(archivePath))
        using (var memory = new MemoryStream())
        {
            var buffer = new byte[16384]; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (memory.Length + count > 32L * 1024 * 1024)
                    throw new InvalidDataException("Plugin archive exceeds 32 MiB.");
                memory.Write(buffer, 0, count);
            }
            archiveBytes = memory.ToArray();
        }
        if (archiveBytes.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Plugin archive exceeds 32 MiB.");
        var archiveSha = global::H2AgentLab.SafeWorkspace.Hash(archiveBytes).ToLowerInvariant();
        var expectedArchive = catalogEntry.ArchiveSha256[7..].ToLowerInvariant();
        if (!string.Equals(archiveSha, expectedArchive, StringComparison.Ordinal))
            throw new InvalidDataException("Plugin archive hash does not match catalog metadata.");

        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = zip.Entries.SingleOrDefault(x =>
            string.Equals(NormalizeEntry(x.FullName), "manifest.json", StringComparison.Ordinal))
            ?? throw new InvalidDataException("Plugin package is missing manifest.json.");

        var manifestText = ReadTextBounded(manifestEntry, 128_000);
        var manifest = H2PluginManifest.Parse(manifestText);
        if (manifest.Id != catalogEntry.Id
            || manifest.Version != catalogEntry.Version
            || manifest.Publisher != catalogEntry.Publisher)
            throw new InvalidDataException("Plugin manifest identity does not match catalog entry.");

        ValidateEntries(zip, manifest, policy);
        H2PluginManifest.ValidateHash(manifest.PackageHash, "Plugin payload hash");
        var payloadSha = ComputePayloadHash(zip);
        if (!string.Equals(
                manifest.PackageHash[7..],
                payloadSha,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin payload hash does not match manifest.");

        if (Version.Parse(manifest.MinAgentVersion) > _agentVersion)
            throw new InvalidOperationException(
                $"Plugin requires Agent {manifest.MinAgentVersion}, current {_agentVersion}.");

        ValidatePolicy(manifest, catalogEntry, policy, userApproved);

        var previousActive = GetActive(manifest.Id);
        if (previousActive is not null)
        {
            var addedPermissions = manifest.Permissions
                .Except(
                    previousActive.Value.Manifest.Permissions,
                    StringComparer.Ordinal)
                .ToArray();
            if (addedPermissions.Length > 0 && !userApproved)
                throw new UnauthorizedAccessException(
                    "Plugin update requests broader permissions and requires new approval: "
                    + string.Join(", ", addedPermissions));
        }

        ValidateEntries(zip, manifest, policy);

        var pluginRoot = Path.Combine(_root, manifest.Id);
        Directory.CreateDirectory(pluginRoot);
        var finalRoot = Path.Combine(pluginRoot, manifest.Version);
        if (Directory.Exists(finalRoot))
            throw new InvalidOperationException(
                $"Plugin {manifest.Id}@{manifest.Version} is already installed.");

        var staging = finalRoot + ".staging." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            Extract(zip, staging);
            RunDeclarativeSelfTest(manifest, staging);
            ValidateToolDefinitions(manifest, staging);
            WriteAtomic(Path.Combine(staging, "admission.json"), JsonSerializer.SerializeToUtf8Bytes(
                new AdmissionReceipt(1, HashFile(Path.Combine(staging, "manifest.json")), payloadSha)));
            Directory.Move(staging, finalRoot);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        var previous = previousActive?.Manifest.Version;
        var registered = ActivateIntoRegistry(manifest, finalRoot, () => WriteActivation(
            pluginRoot, new PluginActivationRecord(manifest.Version, previous, DateTime.UtcNow)));

        return new PluginInstallResult(
            manifest.Id,
            manifest.Version,
            finalRoot,
            true,
            registered,
            archiveSha,
            payloadSha);
    }

    public IReadOnlyList<PluginCatalogEntry> DiscoverUpdates(
        PluginCatalog catalog,
        string pluginId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var active = GetActive(pluginId);
        if (active is null)
            return Array.Empty<PluginCatalogEntry>();

        var activeVersion = Version.Parse(active.Value.Manifest.Version);
        return catalog.All
            .Where(x => x.Id == pluginId
                && Version.Parse(x.Version) > activeVersion
                && Version.Parse(x.MinAgentVersion) <= _agentVersion)
            .OrderByDescending(x => Version.Parse(x.Version))
            .ToArray();
    }

    private H2PluginManifest RollbackCore(string pluginId)
    {
        EnsureActivationBoundary();
        var pluginRoot = PluginRoot(pluginId);
        var activation = ReadActivation(pluginRoot)
            ?? throw new InvalidOperationException("Plugin has no active version.");
        if (string.IsNullOrWhiteSpace(activation.PreviousVersion))
            throw new InvalidOperationException("Plugin has no previous version to roll back to.");

        var previousRoot = VersionRoot(pluginId, activation.PreviousVersion);
        if (!Directory.Exists(previousRoot))
            throw new InvalidOperationException("Previous plugin version is unavailable.");

        var previousManifest = VerifyVersion(pluginId, activation.PreviousVersion);
        var current = activation.ActiveVersion;
        ActivateIntoRegistry(previousManifest, previousRoot, () => WriteActivation(
            pluginRoot, new PluginActivationRecord(previousManifest.Version, current, DateTime.UtcNow)));
        return previousManifest;
    }

    private void QuarantineCore(
        string pluginId,
        string version,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureActivationBoundary();
        var pluginRoot = PluginRoot(pluginId);
        var versionRoot = VersionRoot(pluginId, version);
        if (!Directory.Exists(versionRoot))
            throw new DirectoryNotFoundException("Plugin version is not installed.");

        WriteAtomic(
            Path.Combine(versionRoot, "quarantine.json"),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                quarantinedUtc = DateTime.UtcNow,
                reason = Bound(reason, 2_000)
            }));

        var activation = ReadActivation(pluginRoot);
        if (activation?.ActiveVersion == version)
        {
            DisableCore(pluginId);
            // A first/only version has no fallback. It must remain disabled, not callable.
            if (!string.IsNullOrWhiteSpace(activation.PreviousVersion))
            {
                try { _ = VerifyVersion(pluginId, activation.PreviousVersion); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                    or InvalidOperationException or ArgumentException)
                { return; }
                _ = RollbackCore(pluginId);
            }
        }
    }

    private IReadOnlyList<(H2PluginManifest Manifest, string VersionRoot)> ActivePluginsCore()
    {
        var result = new List<(H2PluginManifest, string)>();
        foreach (var pluginRoot in Directory.EnumerateDirectories(_root))
        {
            var active = GetActiveCore(Path.GetFileName(pluginRoot));
            if (active is not null) result.Add(active.Value);
        }
        return result
            .OrderBy(x => x.Item1.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public PluginVersionEvidence BuildEvidence(string pluginId)
    {
        var active = GetActive(pluginId)
            ?? throw new InvalidOperationException("Plugin is not active.");
        var manifest = active.Manifest;
        var index = new PluginCapabilityIndex();
        index.Rebuild(new[] { (manifest, active.VersionRoot) });
        var tools = ReadToolDefinitions(active.VersionRoot);
        return new PluginVersionEvidence(
            manifest.Id,
            manifest.Version,
            manifest.Publisher,
            manifest.Capabilities.ToArray(),
            index.Entries.Where(x => x.Kind == "skill").ToArray(),
            tools.Select(x => x.Name + "@" + x.ToolVersion).ToArray());
    }

    private (H2PluginManifest Manifest, string VersionRoot)? GetActiveCore(string pluginId)
    {
        var pluginRoot = PluginRoot(pluginId);
        if (!Directory.Exists(pluginRoot)) return null;
        var activation = ReadActivation(pluginRoot);
        if (activation is null || !activation.Enabled) return null;
        var versionRoot = VersionRoot(pluginId, activation.ActiveVersion);
        if (File.Exists(Path.Combine(versionRoot, "quarantine.json"))) return null;
        return (VerifyVersion(pluginId, activation.ActiveVersion), versionRoot);
    }

    private IReadOnlyList<string> ActivateIntoRegistry(
        H2PluginManifest manifest,
        string versionRoot, Action commit)
    {
        EnsureActivationBoundary();
        _ = VerifyVersion(manifest.Id, manifest.Version);
        var generation = _generations.GetValueOrDefault(manifest.Id) + 1;
        var providerId = "plugin." + manifest.Id;
        var descriptors = new List<ToolDescriptor>();

        foreach (var tool in ReadToolDefinitions(versionRoot))
        {
            if (!manifest.Capabilities.Contains(tool.Name, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"Plugin tool '{tool.Name}' is not declared in manifest capabilities.");

            if (_registry.TryGet(tool.Name, out var existing)
                && !string.Equals(
                    existing.Provenance?.ProviderId,
                    providerId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Plugin tool '{tool.Name}' conflicts with an existing registered tool.");

            var executor = _resolver.Resolve(manifest, tool);
            descriptors.Add(new ToolDescriptor(
                tool.Name,
                new ToolNamespace(
                    tool.Namespace,
                    $"Plugin capability namespace '{tool.Namespace}'."),
                tool.Description,
                tool.Risk,
                tool.Access,
                tool.SupportsParallel,
                tool.SchemaVersion,
                tool.CallableSchema,
                WrapVersionExecutor(manifest, tool.Name, generation, executor),
                provenance: new ToolProvenance(
                    providerId,
                    manifest.Version,
                    manifest.Publisher,
                    tool.ToolVersion),
                resourceScope: new ToolResourceScope(
                    tool.ResourceScope,
                    tool.ResourceScope),
                serializationKey: tool.SerializationKey,
                canProvideVerificationEvidence: false,
                readinessSnapshot: () => VersionReadiness(manifest.Id, manifest.Version, generation)));
        }

        _registry.ReplaceWhere(x => string.Equals(x.Provenance?.ProviderId,
            providerId, StringComparison.Ordinal), descriptors, () =>
        {
            commit();
            _generations[manifest.Id] = generation;
        });

        return descriptors.Select(x => x.Name).ToArray();
    }

    private void ValidateToolDefinitions(
        H2PluginManifest manifest,
        string versionRoot)
    {
        var definitions = ReadToolDefinitions(versionRoot);
        var names = definitions.Select(x => x.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Plugin tools.json contains duplicate names.");

        var undeclared = names.Except(
            manifest.Capabilities,
            StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
            throw new InvalidDataException(
                "Plugin tools are missing from manifest capabilities: "
                + string.Join(", ", undeclared));
    }

    private static IReadOnlyList<PluginToolDefinition> ReadToolDefinitions(
        string versionRoot)
    {
        var path = Path.Combine(versionRoot, "tools.json");
        if (!File.Exists(path))
            return Array.Empty<PluginToolDefinition>();
        if (new FileInfo(path).Length > 512_000)
            throw new InvalidDataException("Plugin tools.json exceeds 512 KB.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Plugin tools.json must be an array.");

        var result = new List<PluginToolDefinition>();
        foreach (var node in document.RootElement.EnumerateArray())
        {
            var name = Required(node, "name", 128);
            var ns = Required(node, "namespace", 64);
            var description = Required(node, "description", 2_000);
            var access = Enum.Parse<AgentToolAccess>(
                Required(node, "access", 32),
                ignoreCase: true);
            var risk = Enum.Parse<AgentToolRisk>(
                Required(node, "risk", 32),
                ignoreCase: true);
            var parallel = node.TryGetProperty("supportsParallel", out var p)
                && p.ValueKind == JsonValueKind.True;
            var schemaVersion = Required(node, "schemaVersion", 64);
            var toolVersion = Required(node, "toolVersion", 64);
            var scope = Required(node, "resourceScope", 256);
            var serialization = Required(node, "serializationKey", 64);
            if (!node.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Plugin tool '{name}' is missing schema object.");

            result.Add(new PluginToolDefinition(
                name,
                ns,
                description,
                access,
                risk,
                parallel,
                schemaVersion,
                toolVersion,
                scope,
                serialization,
                schema.Clone()));
        }
        return result;
    }

    private static void ValidateEntries(
        ZipArchive zip,
        H2PluginManifest manifest,
        PluginInstallPolicy policy)
    {
        if (zip.Entries.Count > 2048 || zip.Entries.Sum(x => x.Length) > 64L * 1024 * 1024)
            throw new InvalidDataException("Plugin expanded package exceeds bounded limits.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var normalized = NormalizeEntry(entry.FullName);
            H2PluginManifest.ValidateRelativePath(normalized, "package entry");
            if (!paths.Add(normalized.TrimEnd('/'))
                || normalized.TrimEnd('/').Split('/').Any(x => x.Length == 0 || x == "." || x.EndsWith('.') || x.EndsWith(' '))
                || new[] { "admission.json", "quarantine.json", "active.json" }.Contains(normalized, StringComparer.OrdinalIgnoreCase)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Plugin package contains an ambiguous or reserved path.");
            if (normalized.StartsWith(".", StringComparison.Ordinal)
                || normalized.Contains("/.", StringComparison.Ordinal))
                throw new InvalidDataException("Plugin package contains hidden/dot paths.");
        }

        if ((manifest.NativeHelpers?.Count ?? 0) > 0
            && !policy.AllowNativeHelpers)
            throw new UnauthorizedAccessException("Plugin native helpers are not allowed by policy.");
        if ((manifest.LifecycleHooks?.Count ?? 0) > 0
            && !policy.AllowLifecycleHooks)
            throw new UnauthorizedAccessException("Plugin lifecycle hooks are not allowed by policy.");
    }

    private static void ValidatePolicy(
        H2PluginManifest manifest,
        PluginCatalogEntry entry,
        PluginInstallPolicy policy,
        bool userApproved)
    {
        var publisherApproved = policy.ApprovedPublishers.Contains(manifest.Publisher);
        var allowed = policy.Mode switch
        {
            PluginInstallMode.Disabled => false,
            PluginInstallMode.TrustedOfficialOnly =>
                entry.TrustState == PluginTrustState.TrustedOfficial,
            PluginInstallMode.OrganizationApproved =>
                entry.TrustState == PluginTrustState.OrganizationApproved
                || entry.TrustState == PluginTrustState.TrustedOfficial,
            PluginInstallMode.AskForNewPublisherPackage =>
                (publisherApproved
                    || entry.TrustState == PluginTrustState.TrustedOfficial)
                && userApproved,
            PluginInstallMode.DeveloperLocal =>
                entry.TrustState == PluginTrustState.LocalDeveloper
                && userApproved,
            _ => false
        };

        if (!allowed)
            throw new UnauthorizedAccessException(
                "Plugin install is not allowed by current trust/approval policy.");
    }

    private static void RunDeclarativeSelfTest(
        H2PluginManifest manifest,
        string versionRoot)
    {
        if (string.IsNullOrWhiteSpace(manifest.SelfTestFile))
            return;

        var path = Path.Combine(
            versionRoot,
            manifest.SelfTestFile!.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidDataException("Plugin declarative self-test file is missing.");
        if (new FileInfo(path).Length > 64_000)
            throw new InvalidDataException("Plugin self-test exceeds 64 KB.");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Plugin declarative self-test did not pass.");

        if (root.TryGetProperty("requiredFiles", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var relative = item.GetString()
                    ?? throw new InvalidDataException("Plugin self-test requiredFiles entry is invalid.");
                H2PluginManifest.ValidateRelativePath(relative, "self-test required file");
                if (!File.Exists(Path.Combine(
                        versionRoot,
                        relative.Replace('/', Path.DirectorySeparatorChar))))
                    throw new InvalidDataException(
                        "Plugin self-test required file is missing: " + relative);
            }
        }
    }

    private static void Extract(
        ZipArchive zip,
        string staging)
    {
        foreach (var entry in zip.Entries)
        {
            var normalized = NormalizeEntry(entry.FullName);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
                continue;

            var destination = Path.GetFullPath(Path.Combine(
                staging,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            var stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));
            if (!destination.StartsWith(
                    stagingRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin archive entry escapes staging root.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = entry.Open();
            using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            source.CopyTo(target);
        }
    }

    public static string ComputePayloadHash(ZipArchive zip)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in zip.Entries
            .Where(x => !x.FullName.EndsWith("/", StringComparison.Ordinal))
            .Where(x => !string.Equals(
                NormalizeEntry(x.FullName),
                "manifest.json",
                StringComparison.Ordinal))
            .OrderBy(x => NormalizeEntry(x.FullName), StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(NormalizeEntry(entry.FullName));
            hash.AppendData(name);
            hash.AppendData([0]);
            using var stream = entry.Open();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, count));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeEntry(string value)
        => value.Replace((char)92, '/');

    private string PluginRoot(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 128
            || !char.IsAsciiLetterOrDigit(pluginId[0])
            || pluginId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Plugin ID is invalid.", nameof(pluginId));
        var path = Path.Combine(_root, pluginId);
        RejectLinks(path);
        return path;
    }

    private static H2PluginManifest ReadManifest(string versionRoot)
        => H2PluginManifest.Parse(
            File.ReadAllText(Path.Combine(versionRoot, "manifest.json")));

    private static PluginActivationRecord? ReadActivation(string pluginRoot)
    {
        var path = Path.Combine(pluginRoot, "active.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PluginActivationRecord>(
            File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Plugin active.json is invalid.");
    }

    private static void WriteActivation(
        string pluginRoot,
        PluginActivationRecord activation)
        => WriteAtomic(
            Path.Combine(pluginRoot, "active.json"),
            JsonSerializer.SerializeToUtf8Bytes(activation));

    private static string Required(
        JsonElement node,
        string property,
        int max)
    {
        if (!node.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(
                $"Plugin tool definition is missing '{property}'.");
        var text = value.GetString()!.Trim();
        return text.Length <= max
            ? text
            : throw new InvalidDataException(
                $"Plugin tool '{property}' exceeds {max} characters.");
    }

    private void EnsureActivationBoundary()
    {
        lock (_sync)
        {
            if (_inFlightToolCalls != 0)
                throw new InvalidOperationException(
                    "Plugin activation/update/rollback is blocked while a tool call is in flight.");
        }
    }

    private static void WriteAtomic(
        string path,
        byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string Bound(string value, int max)
    {
        value = value.Replace((char)13, ' ').Replace((char)10, ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string ReadTextBounded(
        ZipArchiveEntry entry,
        int maxBytes)
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException("Plugin text entry exceeds safety limit.");
        using var stream = entry.Open();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return text.Length <= maxBytes
            ? text
            : throw new InvalidDataException("Plugin text entry exceeds safety limit.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class Scope : IDisposable
    {
        private PluginManager? _owner;

        public Scope(PluginManager owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._sync)
                owner._inFlightToolCalls--;
        }
    }
}
