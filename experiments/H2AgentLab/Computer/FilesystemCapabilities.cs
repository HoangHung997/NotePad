namespace H2AgentLab.Computer;

public sealed record FilesystemCapabilityPolicy(
    bool AllowWrite,
    bool AllowMove,
    bool AllowDelete,
    int MaxReadBytes = 8 * 1024 * 1024,
    int MaxScanFiles = 500);

public sealed record FilesystemStat(
    string Path,
    bool Exists,
    bool IsDirectory,
    long Bytes,
    DateTime? LastWriteUtc,
    string? Sha256);

public sealed record FilesystemWatchEntry(
    string Path,
    long Bytes,
    DateTime LastWriteUtc,
    string Sha256);

public sealed record FilesystemWatchSnapshot(
    string Token,
    IReadOnlyList<FilesystemWatchEntry> Entries);

public sealed record FilesystemWatchDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed,
    FilesystemWatchSnapshot Current);

public sealed class FilesystemCapabilities
{
    private readonly global::H2AgentLab.SafeWorkspace _workspace;
    private readonly string _stateRoot;
    private readonly FilesystemCapabilityPolicy _policy;

    public FilesystemCapabilities(
        global::H2AgentLab.SafeWorkspace workspace,
        string stateRoot,
        FilesystemCapabilityPolicy policy)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(_stateRoot);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public IReadOnlyList<string> List(
        string path = ".",
        CancellationToken cancellationToken = default)
        => _workspace.Scan(
            path,
            Math.Clamp(_policy.MaxScanFiles, 1, 2_000),
            cancellationToken).Files;

    public FilesystemStat Stat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = _workspace.Resolve(path, directory: path is "." or "./");
        if (Directory.Exists(full))
            return new FilesystemStat(
                path,
                true,
                true,
                0,
                Directory.GetLastWriteTimeUtc(full),
                null);
        if (!File.Exists(full))
            return new FilesystemStat(path, false, false, 0, null, null);

        var info = new FileInfo(full);
        string? hash = null;
        if (info.Length <= _policy.MaxReadBytes)
            hash = global::H2AgentLab.SafeWorkspace.Hash(_workspace.Read(path));
        return new FilesystemStat(
            path,
            true,
            false,
            info.Length,
            info.LastWriteTimeUtc,
            hash);
    }

    public IReadOnlyList<string> Search(
        string query,
        string path = ".",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalized = query.Trim();
        return List(path, cancellationToken)
            .Where(x => x.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToArray();
    }

    public byte[] Read(string path)
        => _workspace.Read(path);

    public string Hash(string path)
        => global::H2AgentLab.SafeWorkspace.Hash(_workspace.Read(path));

    public string Write(
        string path,
        byte[] bytes,
        string expectedHash)
    {
        if (!_policy.AllowWrite)
            throw new UnauthorizedAccessException("filesystem.write is not allowed by current policy.");
        ArgumentNullException.ThrowIfNull(bytes);
        return _workspace.Write(path, bytes, expectedHash, _stateRoot);
    }

    public string Copy(
        string source,
        string destination,
        string expectedDestinationHash = "")
    {
        if (!_policy.AllowWrite)
            throw new UnauthorizedAccessException("filesystem.copy is not allowed by current policy.");
        var data = _workspace.Read(source);
        return _workspace.Write(destination, data, expectedDestinationHash, _stateRoot);
    }

    public string Move(
        string source,
        string destination,
        string expectedSourceHash,
        string expectedDestinationHash = "")
    {
        if (!_policy.AllowMove || !_policy.AllowWrite)
            throw new UnauthorizedAccessException("filesystem.move is not allowed by current policy.");

        var sourceData = _workspace.Read(source);
        var actualSourceHash = global::H2AgentLab.SafeWorkspace.Hash(sourceData);
        if (string.IsNullOrWhiteSpace(expectedSourceHash)
            || !string.Equals(actualSourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source changed or was not hash-validated before move.");

        var destinationHash = _workspace.Write(
            destination,
            sourceData,
            expectedDestinationHash,
            _stateRoot);

        Delete(source, expectedSourceHash);
        return destinationHash;
    }

    public void Delete(
        string path,
        string expectedHash)
    {
        if (!_policy.AllowDelete)
            throw new UnauthorizedAccessException("filesystem.delete is not allowed by current policy.");

        var full = _workspace.Resolve(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("File was not found.", path);

        var bytes = _workspace.Read(path);
        var current = global::H2AgentLab.SafeWorkspace.Hash(bytes);
        if (string.IsNullOrWhiteSpace(expectedHash)
            || !string.Equals(current, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("File changed or was not hash-validated before delete.");

        var backupRoot = Path.Combine(_stateRoot, "deleted-backups");
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(
            backupRoot,
            Guid.NewGuid().ToString("N") + Path.GetExtension(full));
        File.WriteAllBytes(backup, bytes);
        File.Delete(full);
    }

    public FilesystemWatchSnapshot Watch(
        string path = ".",
        CancellationToken cancellationToken = default)
    {
        var entries = new List<FilesystemWatchEntry>();
        foreach (var relative in List(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = _workspace.Resolve(relative);
            var info = new FileInfo(full);
            if (info.Length > _policy.MaxReadBytes)
                continue;
            var bytes = _workspace.Read(relative);
            entries.Add(new FilesystemWatchEntry(
                relative.Replace('\', '/'),
                info.Length,
                info.LastWriteTimeUtc,
                global::H2AgentLab.SafeWorkspace.Hash(bytes)));
        }

        var ordered = entries.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        var material = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            ordered.Select(x => new { x.Path, x.Bytes, x.LastWriteUtc, x.Sha256 }));
        return new FilesystemWatchSnapshot(
            global::H2AgentLab.SafeWorkspace.Hash(material).ToLowerInvariant(),
            ordered);
    }

    public FilesystemWatchDiff WatchDiff(
        FilesystemWatchSnapshot previous,
        string path = ".",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var current = Watch(path, cancellationToken);
        var old = previous.Entries.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var now = current.Entries.ToDictionary(x => x.Path, StringComparer.Ordinal);

        return new FilesystemWatchDiff(
            now.Keys.Except(old.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            old.Keys.Except(now.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            old.Keys.Intersect(now.Keys, StringComparer.Ordinal)
                .Where(x => !string.Equals(old[x].Sha256, now[x].Sha256, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray(),
            current);
    }
}
