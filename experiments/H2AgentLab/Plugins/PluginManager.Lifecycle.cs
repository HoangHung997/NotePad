using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Plugins;

public sealed partial class PluginManager
{
    private sealed record AdmissionReceipt(int SchemaVersion, string ManifestHash, string PayloadHash);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versionCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _versionPins = new(StringComparer.Ordinal);

    public PluginInstallResult InstallFromArchive(string archivePath, PluginCatalogEntry entry,
        PluginInstallPolicy policy, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            EnsurePluginBoundary(entry.Id);
            _ = VersionRoot(entry.Id, entry.Version);
            return InstallFromArchiveCore(archivePath, entry, policy, userApproved);
        }
    }

    public H2PluginManifest Rollback(string pluginId)
    { lock (_sync) { EnsurePluginBoundary(pluginId); return RollbackCore(pluginId); } }

    public void Quarantine(string pluginId, string version, string reason)
    { lock (_sync) { EnsurePluginBoundary(pluginId, ignorePins: true); QuarantineCore(pluginId, version, reason); } }

    public IReadOnlyList<(H2PluginManifest Manifest, string VersionRoot)> ActivePlugins()
    { lock (_sync) return ActivePluginsCore(); }

    public (H2PluginManifest Manifest, string VersionRoot)? GetActive(string pluginId)
    { lock (_sync) return GetActiveCore(pluginId); }

    public void Disable(string pluginId)
    { lock (_sync) { EnsurePluginBoundary(pluginId, ignorePins: true); DisableCore(pluginId); } }

    private void DisableCore(string pluginId)
    {
        var root = PluginRoot(pluginId);
        var active = ReadActivation(root);
        if (active is null) return;
        _registry.ReplaceWhere(x => x.Provenance?.ProviderId == "plugin." + pluginId,
            Array.Empty<ToolDescriptor>(), () =>
            {
                WriteActivation(root, active with { Enabled = false });
                _generations[pluginId] = _generations.GetValueOrDefault(pluginId) + 1;
            });
    }

    /// <summary>Re-enable only an already admitted, intact version. No trust is inferred from disk files.</summary>
    public H2PluginManifest Enable(string pluginId, string version)
    {
        lock (_sync)
        {
            EnsurePluginBoundary(pluginId);
            var manifest = VerifyVersion(pluginId, version);
            RunDeclarativeSelfTest(manifest, VersionRoot(pluginId, version));
            var old = ReadActivation(PluginRoot(pluginId));
            var previous = old?.ActiveVersion == version ? old.PreviousVersion : old?.ActiveVersion;
            ActivateIntoRegistry(manifest, VersionRoot(pluginId, version), () => WriteActivation(
                PluginRoot(pluginId), new(version, previous, DateTime.UtcNow)));
            return manifest;
        }
    }

    public H2PluginManifest SelfTest(string pluginId, string version)
    {
        lock (_sync)
        {
            var manifest = VerifyVersion(pluginId, version);
            RunDeclarativeSelfTest(manifest, VersionRoot(pluginId, version));
            ValidateToolDefinitions(manifest, VersionRoot(pluginId, version));
            return manifest;
        }
    }

    public void Uninstall(string pluginId, string version)
    {
        lock (_sync)
        {
            EnsurePluginBoundary(pluginId);
            var root = VersionRoot(pluginId, version);
            if (!Directory.Exists(root)) return;
            if (ReadActivation(PluginRoot(pluginId))?.ActiveVersion == version) DisableCore(pluginId);
            _ = EnumeratePayload(root);
            // Package bytes only. The Agent's historical artifacts/journal are not under this root.
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Host task lifetime pin. An update waits at the affected plugin boundary only.
    /// Revocation may still disable/quarantine a pinned version; it never permits later calls.</summary>
    public IDisposable PinVersion(string pluginId, string version)
    {
        lock (_sync)
        {
            var active = GetActiveCore(pluginId);
            if (active?.Manifest.Version != version) throw new InvalidOperationException("Plugin version is not active.");
            _versionPins[pluginId] = _versionPins.GetValueOrDefault(pluginId) + 1;
            return new VersionScope(this, pluginId, pin: true);
        }
    }

    private void EnsurePluginBoundary(string pluginId, bool ignorePins = false)
    {
        _ = PluginRoot(pluginId);
        EnsureActivationBoundary();
        if (_versionCalls.GetValueOrDefault(pluginId) != 0 || (!ignorePins && _versionPins.GetValueOrDefault(pluginId) != 0))
            throw new InvalidOperationException("Plugin version is pinned or a tool call is in flight.");
    }

    private string VersionRoot(string pluginId, string version)
    {
        if (!Version.TryParse(version, out var parsed) || parsed.ToString() != version)
            throw new ArgumentException("Plugin version path is invalid.");
        var path = Path.Combine(PluginRoot(pluginId), version);
        RejectLinks(path);
        return path;
    }

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Plugin storage cannot traverse symbolic links or reparse points.");
    }

    private static string HashFile(string path)
    {
        RejectLinks(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private H2PluginManifest VerifyVersion(string pluginId, string version)
    {
        var root = VersionRoot(pluginId, version);
        if (File.Exists(Path.Combine(root, "quarantine.json")))
            throw new InvalidOperationException("Plugin version is quarantined.");
        var admissionPath = Path.Combine(root, "admission.json");
        RejectLinks(admissionPath);
        if (!File.Exists(admissionPath) || new FileInfo(admissionPath).Length > 4096)
            throw new InvalidDataException("Plugin has no bounded admission receipt; reinstall with approval.");
        var admission = JsonSerializer.Deserialize<AdmissionReceipt>(File.ReadAllBytes(admissionPath))
            ?? throw new InvalidDataException("Plugin admission receipt is invalid.");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (admission.SchemaVersion != 1 || new FileInfo(manifestPath).Length > 128000
            || HashFile(manifestPath) != admission.ManifestHash)
            throw new InvalidDataException("Plugin manifest differs from its admitted version.");
        var manifest = ReadManifest(root);
        if (manifest.Id != pluginId || manifest.Version != version || !string.Equals(manifest.PackageHash[7..], admission.PayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin admitted identity differs from its directory.");
        var files = EnumeratePayload(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        foreach (var file in files.OrderBy(x => x.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative)); hash.AppendData([0]);
            using var input = File.OpenRead(file.Path); var buffer = new byte[16384]; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                totalBytes += count;
                if (totalBytes > 64L * 1024 * 1024) throw new InvalidDataException("Plugin expanded bytes exceed limit.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            hash.AppendData([0]);
        }
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != admission.PayloadHash)
            throw new InvalidDataException("Plugin payload differs from its admitted version.");
        return manifest;
    }

    private static List<(string Path, string Relative)> EnumeratePayload(string root)
    {
        var files = new List<(string Path, string Relative)>();
        var pending = new Stack<string>(); pending.Push(root);
        int entries = 0;
        long bytes = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop(); RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096) throw new InvalidDataException("Plugin entry count exceeds limit.");
                RejectLinks(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative is "manifest.json" or "admission.json" or "quarantine.json") continue;
                bytes += new FileInfo(path).Length;
                if (bytes > 64L * 1024 * 1024) throw new InvalidDataException("Plugin expanded bytes exceed limit.");
                files.Add((path, relative));
            }
        }
        return files;
    }

    private ToolReadiness VersionReadiness(string id, string version, long generation)
    {
        lock (_sync)
        {
            // Readiness is cheap host state. Integrity is rechecked at the execution boundary.
            return new(_generations.GetValueOrDefault(id) == generation
                ? ToolReadinessState.Ready : ToolReadinessState.Unavailable);
        }
    }

    private sealed class VersionExecutor(PluginManager owner, H2PluginManifest manifest,
        long generation, IAgentToolExecutor inner) : IAgentToolExecutor
    {
        public string ExecutorId => inner.ExecutorId;
        public async ValueTask<string> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (owner._sync)
            {
                if (owner._generations.GetValueOrDefault(manifest.Id) != generation
                    || owner.GetActiveCore(manifest.Id)?.Manifest.Version != manifest.Version)
                    throw new InvalidOperationException("Plugin executor has been revoked or requires rebinding.");
                owner._versionCalls[manifest.Id] = owner._versionCalls.GetValueOrDefault(manifest.Id) + 1;
            }
            using var lease = new VersionScope(owner, manifest.Id, pin: false);
            cancellationToken.ThrowIfCancellationRequested();
            return await inner.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class VersionScope(PluginManager owner, string id, bool pin) : IDisposable
    {
        private PluginManager? _owner = owner;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            lock (value._sync)
            {
                var counts = pin ? value._versionPins : value._versionCalls;
                var count = counts[id] - 1;
                if (count == 0) counts.Remove(id); else counts[id] = count;
            }
        }
    }
}
