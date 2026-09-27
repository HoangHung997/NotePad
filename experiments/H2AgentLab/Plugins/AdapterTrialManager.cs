using System.Text;
using System.Text.Json;

namespace H2AgentLab.Plugins;

public sealed record AdapterTrialHostPin(
    string ApplicationId,
    string ApplicationVersion,
    string AdapterApiVersion);

public sealed record AdapterTrialDependencyPin(
    string Id,
    string Version,
    string Sha256);

public sealed record AdapterTrialRequest(
    Guid TaskId,
    string Name,
    string ArchivePath,
    PluginCatalogEntry CatalogEntry,
    PluginInstallPolicy Policy,
    bool UserApproved,
    string ApprovedWorkspaceRoot,
    IReadOnlyList<string> InputPaths,
    AdapterTrialHostPin Host,
    IReadOnlyList<AdapterTrialDependencyPin> Dependencies);

public sealed record AdapterTrialFileState(
    string Path,
    long Bytes,
    string Sha256);

public sealed record AdapterTrialFileDelta(
    string Path,
    string? BeforeSha256,
    string? AfterSha256);

public sealed record AdapterTrialEnvironmentManifest(
    int SchemaVersion,
    Guid TaskId,
    string TrialId,
    string Name,
    string CandidatePluginId,
    string CandidatePluginVersion,
    string CandidateArchiveSha256,
    string CandidatePayloadSha256,
    string CandidateManifestSha256,
    string? StablePluginVersion,
    AdapterTrialHostPin Host,
    IReadOnlyList<AdapterTrialDependencyPin> Dependencies,
    string PermissionFingerprint,
    IReadOnlyList<AdapterTrialFileState> Inputs,
    DateTime CreatedUtc);

public sealed record AdapterTrialCapabilityProbe(
    bool Compatible,
    string Fingerprint,
    string Summary);

public sealed record AdapterTrialExecutionResult(
    bool Success,
    string Summary,
    IReadOnlyList<string> ExpectedReadbackPaths);

public sealed record AdapterTrialExecutionContext(
    AdapterTrialEnvironmentManifest Manifest,
    global::H2AgentLab.SafeWorkspace Workspace,
    string CandidateRoot,
    bool ReusingPreparedAdapter);

public interface IAdapterTrialRunner
{
    Task<AdapterTrialCapabilityProbe> ProbeAsync(
        AdapterTrialExecutionContext context,
        CancellationToken cancellationToken);

    Task<AdapterTrialExecutionResult> ExecuteAsync(
        AdapterTrialExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record AdapterTrialReport(
    Guid TaskId,
    string TrialId,
    string Status,
    bool Compatible,
    bool CacheReused,
    bool Promoted,
    string? StablePluginVersionBefore,
    string? StablePluginVersionAfter,
    string CandidatePluginVersion,
    string CapabilityFingerprint,
    IReadOnlyList<AdapterTrialFileDelta> Deltas,
    IReadOnlyList<AdapterTrialFileState> Readback,
    string ReceiptPath,
    string Summary)
{
    public bool TrialBytesRetained { get; init; }
}

/// <summary>
/// Conditional AR-071 compatibility trial host. It stages through PluginManager, copies only
/// approved workspace inputs, runs a host-supplied adapter probe/execution against the copy,
/// records hash-only evidence, and never exposes a promotion operation.
/// </summary>
public sealed class AdapterTrialManager
{
    private const int MaxFiles = 256;
    private const long MaxBytes = 64L * 1024 * 1024;
    private readonly PluginManager _plugins;
    private readonly string _stateRoot;

    public AdapterTrialManager(PluginManager plugins)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _stateRoot = Directory.GetParent(_plugins.Root)?.FullName
            ?? throw new InvalidOperationException("Plugin state root is unavailable.");
    }

    public async Task<AdapterTrialReport> RunAsync(
        AdapterTrialRequest request,
        IAdapterTrialRunner runner,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(runner);
        cancellationToken.ThrowIfCancellationRequested();

        var stableBefore = _plugins.GetActive(request.CatalogEntry.Id)?.Manifest.Version;
        PluginTrialStageResult? staged = null;
        string? receiptPath = null;
        try
        {
            staged = _plugins.StageForTrial(
                request.TaskId,
                request.ArchivePath,
                request.CatalogEntry,
                request.Policy,
                request.UserApproved);

            var workspaceRoot = Path.Combine(staged.TrialRoot, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var source = new global::H2AgentLab.SafeWorkspace(request.ApprovedWorkspaceRoot);
            var trial = new global::H2AgentLab.SafeWorkspace(workspaceRoot);
            var inputs = CopyInputs(source, trial, request.InputPaths, cancellationToken);
            var permissionFingerprint = HashText(string.Join("\n",
                staged.Manifest.Permissions.Order(StringComparer.Ordinal)));
            var manifest = new AdapterTrialEnvironmentManifest(
                1,
                request.TaskId,
                staged.TrialId,
                request.Name.Trim(),
                staged.Manifest.Id,
                staged.Manifest.Version,
                staged.ArchiveSha256,
                staged.PayloadSha256,
                staged.ManifestSha256,
                stableBefore,
                NormalizeHost(request.Host),
                NormalizeDependencies(request.Dependencies),
                permissionFingerprint,
                inputs,
                DateTime.UtcNow);
            File.WriteAllBytes(
                Path.Combine(staged.TrialRoot, "environment.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

            var cacheKey = CacheKey(manifest);
            var cached = ReadCache(request.TaskId, cacheKey);
            var initialContext = new AdapterTrialExecutionContext(
                manifest, trial, staged.TrialRoot, ReusingPreparedAdapter: false);
            var probe = await runner.ProbeAsync(initialContext, cancellationToken).ConfigureAwait(false);
            ValidateProbe(probe);

            var cacheReusable = cached is not null
                && cached.CapabilityFingerprint == probe.Fingerprint
                && cached.CandidateArchiveSha256 == staged.ArchiveSha256
                && cached.Host == manifest.Host
                && DependenciesEqual(cached.Dependencies, manifest.Dependencies);

            if (!probe.Compatible)
            {
                var stableAfterIncompatible = _plugins.GetActive(staged.Manifest.Id)?.Manifest.Version;
                EnsureStable(stableBefore, stableAfterIncompatible);
                var report = new AdapterTrialReport(
                    request.TaskId, staged.TrialId, "Incompatible", false, false, false,
                    stableBefore, stableAfterIncompatible, staged.Manifest.Version,
                    probe.Fingerprint, [], inputs,
                    "", Bound(probe.Summary, 1000))
                { TrialBytesRetained = false };
                receiptPath = WriteReceipt(report, manifest);
                return report with { ReceiptPath = receiptPath };
            }

            var before = Snapshot(trial, cancellationToken);
            var context = initialContext with { ReusingPreparedAdapter = cacheReusable };
            AdapterTrialExecutionResult execution;
            try
            {
                execution = await runner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                var stableAfterFailure = _plugins.GetActive(staged.Manifest.Id)?.Manifest.Version;
                EnsureStable(stableBefore, stableAfterFailure);
                var failed = new AdapterTrialReport(
                    request.TaskId, staged.TrialId, "Failed", true, cacheReusable, false,
                    stableBefore, stableAfterFailure, staged.Manifest.Version,
                    probe.Fingerprint, Diff(before, Snapshot(trial, cancellationToken)), [],
                    "", "trial_execution_" + ex.GetType().Name)
                { TrialBytesRetained = false };
                receiptPath = WriteReceipt(failed, manifest);
                return failed with { ReceiptPath = receiptPath };
            }

            ArgumentNullException.ThrowIfNull(execution);
            var after = Snapshot(trial, cancellationToken);
            var readback = Readback(trial, execution.ExpectedReadbackPaths, cancellationToken);
            var stableAfter = _plugins.GetActive(staged.Manifest.Id)?.Manifest.Version;
            EnsureStable(stableBefore, stableAfter);

            var status = execution.Success ? "Passed" : "Failed";
            var reportFinal = new AdapterTrialReport(
                request.TaskId, staged.TrialId, status, true, cacheReusable, false,
                stableBefore, stableAfter, staged.Manifest.Version, probe.Fingerprint,
                Diff(before, after), readback, "", Bound(execution.Summary, 1000))
            { TrialBytesRetained = false };
            receiptPath = WriteReceipt(reportFinal, manifest);
            if (execution.Success)
                WriteCache(request.TaskId, cacheKey, new(
                    staged.ArchiveSha256,
                    manifest.Host,
                    manifest.Dependencies,
                    probe.Fingerprint,
                    DateTime.UtcNow));
            return reportFinal with { ReceiptPath = receiptPath };
        }
        finally
        {
            if (staged is not null)
                _plugins.DiscardTrial(staged);
        }
    }

    private static IReadOnlyList<AdapterTrialFileState> CopyInputs(
        global::H2AgentLab.SafeWorkspace source,
        global::H2AgentLab.SafeWorkspace target,
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken)
    {
        var result = new List<AdapterTrialFileState>();
        long bytes = 0;
        foreach (var relative in (inputPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= MaxFiles) throw new IOException("Adapter trial input file count exceeds bound.");
            var sourcePath = source.Resolve(relative);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Adapter trial input is missing.", relative);
            var info = new FileInfo(sourcePath);
            bytes += info.Length;
            if (bytes > MaxBytes) throw new IOException("Adapter trial input bytes exceed bound.");
            var destination = target.Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: false);
            var data = File.ReadAllBytes(destination);
            result.Add(new(relative.Replace('\\','/'), data.LongLength,
                global::H2AgentLab.SafeWorkspace.Hash(data).ToLowerInvariant()));
        }
        return result.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<AdapterTrialFileState> Snapshot(
        global::H2AgentLab.SafeWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var files = workspace.Scan(".", MaxFiles, cancellationToken);
        if (files.Truncated) throw new IOException("Adapter trial workspace scan exceeded bound.");
        var result = new List<AdapterTrialFileState>();
        long total = 0;
        foreach (var relative in files.Files.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = workspace.Resolve(relative);
            var data = File.ReadAllBytes(path);
            total += data.LongLength;
            if (total > MaxBytes) throw new IOException("Adapter trial workspace bytes exceed bound.");
            result.Add(new(relative.Replace('\\','/'), data.LongLength,
                global::H2AgentLab.SafeWorkspace.Hash(data).ToLowerInvariant()));
        }
        return result;
    }

    private static IReadOnlyList<AdapterTrialFileState> Readback(
        global::H2AgentLab.SafeWorkspace workspace,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new List<AdapterTrialFileState>();
        foreach (var relative in (paths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = workspace.Resolve(relative);
            if (!File.Exists(path)) throw new InvalidDataException(
                "Adapter trial expected readback file is missing: " + relative);
            var data = File.ReadAllBytes(path);
            if (data.LongLength > MaxBytes) throw new IOException("Adapter trial readback file exceeds bound.");
            result.Add(new(relative.Replace('\\','/'), data.LongLength,
                global::H2AgentLab.SafeWorkspace.Hash(data).ToLowerInvariant()));
        }
        return result.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<AdapterTrialFileDelta> Diff(
        IReadOnlyList<AdapterTrialFileState> before,
        IReadOnlyList<AdapterTrialFileState> after)
    {
        var left = before.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var right = after.ToDictionary(x => x.Path, StringComparer.Ordinal);
        return left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(path => !left.TryGetValue(path, out var a)
                || !right.TryGetValue(path, out var b)
                || a.Sha256 != b.Sha256)
            .Select(path => new AdapterTrialFileDelta(
                path,
                left.GetValueOrDefault(path)?.Sha256,
                right.GetValueOrDefault(path)?.Sha256))
            .ToArray();
    }

    private string WriteReceipt(
        AdapterTrialReport report,
        AdapterTrialEnvironmentManifest manifest)
    {
        var root = Path.Combine(_stateRoot, "adapter-trial-receipts", report.TaskId.ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, report.TrialId + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            report,
            environment = new
            {
                manifest.TaskId,
                manifest.TrialId,
                manifest.Name,
                manifest.CandidatePluginId,
                manifest.CandidatePluginVersion,
                manifest.CandidateArchiveSha256,
                manifest.CandidatePayloadSha256,
                manifest.CandidateManifestSha256,
                manifest.StablePluginVersion,
                manifest.Host,
                manifest.Dependencies,
                manifest.PermissionFingerprint,
                inputs = manifest.Inputs.Select(x => new { x.Path, x.Bytes, x.Sha256 }).ToArray(),
                manifest.CreatedUtc
            }
        }, JsonOptions));
        return path;
    }

    private CacheReceipt? ReadCache(Guid taskId, string key)
    {
        var path = CachePath(taskId, key);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64_000) return null;
        try { return JsonSerializer.Deserialize<CacheReceipt>(File.ReadAllBytes(path)); }
        catch (JsonException) { return null; }
    }

    private void WriteCache(Guid taskId, string key, CacheReceipt receipt)
    {
        var path = CachePath(taskId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
    }

    private string CachePath(Guid taskId, string key)
        => Path.Combine(_stateRoot, "adapter-trial-cache", taskId.ToString("N"), key + ".json");

    private static string CacheKey(AdapterTrialEnvironmentManifest manifest)
        => HashText(JsonSerializer.Serialize(new
        {
            manifest.CandidatePluginId,
            manifest.CandidatePluginVersion,
            manifest.CandidateArchiveSha256,
            manifest.Host,
            manifest.Dependencies,
            manifest.PermissionFingerprint
        }, JsonOptions));

    private static bool DependenciesEqual(
        IReadOnlyList<AdapterTrialDependencyPin> left,
        IReadOnlyList<AdapterTrialDependencyPin> right)
        => left.OrderBy(x => x.Id, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(x => x.Id, StringComparer.Ordinal));

    private static AdapterTrialHostPin NormalizeHost(AdapterTrialHostPin host)
    {
        ArgumentNullException.ThrowIfNull(host);
        static string Part(string value, string label, int max)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0 || value.Length > max || value.Any(char.IsControl))
                throw new ArgumentException("Adapter trial " + label + " is invalid.");
            return value;
        }
        return new(Part(host.ApplicationId, "application id", 128),
            Part(host.ApplicationVersion, "application version", 128),
            Part(host.AdapterApiVersion, "adapter API version", 128));
    }

    private static IReadOnlyList<AdapterTrialDependencyPin> NormalizeDependencies(
        IReadOnlyList<AdapterTrialDependencyPin> dependencies)
    {
        var result = (dependencies ?? []).Select(x =>
        {
            var id = (x.Id ?? "").Trim();
            var version = (x.Version ?? "").Trim();
            if (id.Length is < 1 or > 128 || version.Length is < 1 or > 128
                || id.Any(char.IsControl) || version.Any(char.IsControl))
                throw new ArgumentException("Adapter trial dependency pin is invalid.");
            H2PluginManifest.ValidateHash(x.Sha256, "Adapter trial dependency hash");
            return new AdapterTrialDependencyPin(id, version, x.Sha256.ToLowerInvariant());
        }).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Adapter trial dependency IDs must be unique.");
        return result;
    }

    private static void Validate(AdapterTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TaskId == Guid.Empty) throw new ArgumentException("TaskId cannot be empty.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160)
            throw new ArgumentException("Adapter trial name is invalid.");
        if (string.IsNullOrWhiteSpace(request.ApprovedWorkspaceRoot))
            throw new ArgumentException("Adapter trial workspace is required.");
        _ = NormalizeHost(request.Host);
        _ = NormalizeDependencies(request.Dependencies);
    }

    private static void ValidateProbe(AdapterTrialCapabilityProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (string.IsNullOrWhiteSpace(probe.Fingerprint)
            || probe.Fingerprint.Length > 512
            || probe.Fingerprint.Any(char.IsControl))
            throw new InvalidDataException("Adapter capability probe fingerprint is invalid.");
        _ = Bound(probe.Summary, 1000);
    }

    private static void EnsureStable(string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Adapter trial changed the stable active plugin; task-local trials must never auto-promote.");
    }

    private static string Bound(string? value, int max)
    {
        value = (value ?? "").Replace('\r',' ').Replace('\n',' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string HashText(string text)
        => global::H2AgentLab.SafeWorkspace.Hash(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record CacheReceipt(
        string CandidateArchiveSha256,
        AdapterTrialHostPin Host,
        IReadOnlyList<AdapterTrialDependencyPin> Dependencies,
        string CapabilityFingerprint,
        DateTime CreatedUtc);
}
