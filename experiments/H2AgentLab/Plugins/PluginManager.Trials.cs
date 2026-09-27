using System.IO.Compression;
using System.Text.Json;

namespace H2AgentLab.Plugins;

public sealed record PluginTrialStageResult(
    Guid TaskId,
    string TrialId,
    H2PluginManifest Manifest,
    string TrialRoot,
    string ArchiveSha256,
    string PayloadSha256,
    string ManifestSha256,
    string? StableActiveVersion,
    IReadOnlyList<string> ToolNames);

public sealed partial class PluginManager
{
    /// <summary>
    /// Validate and extract one plugin candidate for a task-local compatibility trial.
    /// This reuses the canonical PluginManager admission checks but deliberately does not
    /// register tools, write active.json, change generations, or promote anything globally.
    /// </summary>
    public PluginTrialStageResult StageForTrial(
        Guid taskId,
        string archivePath,
        PluginCatalogEntry catalogEntry,
        PluginInstallPolicy policy,
        bool userApproved)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId cannot be empty.", nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(policy);

        lock (_sync)
        {
            H2PluginManifest.ValidateHash(catalogEntry.ArchiveSha256, "Catalog archive hash");
            var file = new FileInfo(archivePath);
            if (!file.Exists) throw new FileNotFoundException("Plugin trial archive is missing.", archivePath);
            if (file.Length > 32L * 1024 * 1024)
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

            var archiveSha = global::H2AgentLab.SafeWorkspace.Hash(archiveBytes).ToLowerInvariant();
            if (!string.Equals(archiveSha, catalogEntry.ArchiveSha256[7..], StringComparison.OrdinalIgnoreCase))
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
            if (!string.Equals(manifest.PackageHash[7..], payloadSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin payload hash does not match manifest.");
            if (Version.Parse(manifest.MinAgentVersion) > _agentVersion)
                throw new InvalidOperationException(
                    $"Plugin requires Agent {manifest.MinAgentVersion}, current {_agentVersion}.");

            ValidatePolicy(manifest, catalogEntry, policy, userApproved);
            var stable = GetActiveCore(manifest.Id)?.Manifest.Version;
            if (stable is { } stableVersion)
            {
                var active = GetActiveCore(manifest.Id)!.Value.Manifest;
                var addedPermissions = manifest.Permissions.Except(active.Permissions, StringComparer.Ordinal).ToArray();
                if (addedPermissions.Length > 0 && !userApproved)
                    throw new UnauthorizedAccessException(
                        "Plugin trial requests broader permissions and requires new approval: "
                        + string.Join(", ", addedPermissions));
            }

            var trialId = Guid.NewGuid().ToString("N");
            var stateRoot = Directory.GetParent(_root)?.FullName
                ?? throw new InvalidOperationException("Plugin state root is unavailable.");
            var taskRoot = Path.Combine(stateRoot, "adapter-trials", taskId.ToString("N"));
            RejectLinks(taskRoot);
            Directory.CreateDirectory(taskRoot);
            var finalRoot = Path.Combine(taskRoot,
                manifest.Id + "-" + manifest.Version + "-" + trialId);
            var staging = finalRoot + ".staging";
            Directory.CreateDirectory(staging);
            try
            {
                Extract(zip, staging);
                RunDeclarativeSelfTest(manifest, staging);
                ValidateToolDefinitions(manifest, staging);
                var toolNames = ReadToolDefinitions(staging).Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
                var receipt = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    taskId,
                    trialId,
                    pluginId = manifest.Id,
                    pluginVersion = manifest.Version,
                    archiveSha256 = archiveSha,
                    payloadSha256 = payloadSha,
                    manifestSha256 = global::H2AgentLab.SafeWorkspace.Hash(
                        System.Text.Encoding.UTF8.GetBytes(manifestText)).ToLowerInvariant(),
                    activated = false,
                    createdUtc = DateTime.UtcNow
                });
                WriteAtomic(Path.Combine(staging, "trial-admission.json"), receipt);
                Directory.Move(staging, finalRoot);
                return new(taskId, trialId, manifest, finalRoot, archiveSha, payloadSha,
                    global::H2AgentLab.SafeWorkspace.Hash(
                        System.Text.Encoding.UTF8.GetBytes(manifestText)).ToLowerInvariant(),
                    stable, toolNames);
            }
            catch
            {
                TryDeleteDirectory(staging);
                TryDeleteDirectory(finalRoot);
                throw;
            }
        }
    }

    public void DiscardTrial(PluginTrialStageResult trial)
    {
        ArgumentNullException.ThrowIfNull(trial);
        lock (_sync)
        {
            var stateRoot = Directory.GetParent(_root)?.FullName
                ?? throw new InvalidOperationException("Plugin state root is unavailable.");
            var allowedRoot = Path.GetFullPath(Path.Combine(
                stateRoot, "adapter-trials", trial.TaskId.ToString("N")));
            var target = Path.GetFullPath(trial.TrialRoot);
            if (!target.StartsWith(
                    Path.TrimEndingDirectorySeparator(allowedRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Trial root is outside the task-local adapter trial area.");
            RejectLinks(target);
            TryDeleteDirectory(target);
        }
    }
}
