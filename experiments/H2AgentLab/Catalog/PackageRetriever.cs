using System.Security.Cryptography;

namespace H2AgentLab.Catalog;

public sealed record PackageRetrievalRequest(
    string PluginId,
    string PluginVersion,
    string Location,
    string ExpectedArchiveSha256,
    long MaxBytes,
    TimeSpan Timeout);

public sealed record PackageRetrievalResult(
    string PluginId,
    string PluginVersion,
    string StagedPath,
    string Sha256,
    long Bytes,
    DateTime RetrievedUtc);

public interface IPackageRetriever
{
    Task<PackageRetrievalResult> RetrieveAsync(
        PackageRetrievalRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic acceptance implementation. It copies immutable package bytes from an approved
/// local/catalog location into a private staging root, verifies size/hash, and never executes them.
/// </summary>
public sealed class LocalPackageRetriever : IPackageRetriever
{
    private readonly string _stagingRoot;
    private readonly IReadOnlyList<string> _allowedSourceRoots;

    public LocalPackageRetriever(
        string stagingRoot,
        IEnumerable<string> allowedSourceRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(allowedSourceRoots);
        _stagingRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(_stagingRoot);
        _allowedSourceRoots = allowedSourceRoots
            .Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_allowedSourceRoots.Count == 0)
            throw new ArgumentException("At least one package source root is required.", nameof(allowedSourceRoots));
    }

    public async Task<PackageRetrievalResult> RetrieveAsync(
        PackageRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxBytes is < 1 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request.MaxBytes));
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(request.Timeout));
        ValidateHash(request.ExpectedArchiveSha256);

        var source = Path.GetFullPath(request.Location);
        if (!_allowedSourceRoots.Any(root =>
                source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, root, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Package location is outside approved retrieval roots.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Package source does not exist.", source);

        var info = new FileInfo(source);
        if (info.Length > request.MaxBytes)
            throw new IOException("Package exceeds configured retrieval size limit.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);

        var target = Path.Combine(
            _stagingRoot,
            request.PluginId + "-" + request.PluginVersion + "-" + Guid.NewGuid().ToString("N") + ".h2pkg");
        try
        {
            await using (var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > request.MaxBytes)
                        throw new IOException("Package exceeded configured retrieval size limit during copy.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                }
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }

            var bytes = await File.ReadAllBytesAsync(target, deadline.Token).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var expected = request.ExpectedArchiveSha256[7..].ToLowerInvariant();
            if (!string.Equals(hash, expected, StringComparison.Ordinal))
                throw new InvalidDataException("Retrieved package hash does not match catalog metadata.");

            return new PackageRetrievalResult(
                request.PluginId,
                request.PluginVersion,
                target,
                hash,
                bytes.LongLength,
                DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(target);
            throw new TimeoutException("Package retrieval timed out.");
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    private static void ValidateHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || value.Length != 71
            || value[7..].Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected archive hash must be sha256:<64 hex>.", nameof(value));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
