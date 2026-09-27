using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace H2AgentLab.Session;

public enum AgentCompactionSourceKind
{
    JournalEvent,
    Artifact,
    ToolResult,
    Snapshot,
    Checkpoint
}

public sealed record AgentCompactionSourceReference(
    AgentCompactionSourceKind Kind,
    string SourceId,
    string? Sha256 = null,
    long? Sequence = null);

public sealed record AgentCompactionCheckpoint(
    int Schema,
    string Id,
    DateTime CreatedUtc,
    long CoveredThroughSequence,
    string Summary,
    string SummarySha256,
    List<AgentCompactionSourceReference> Sources,
    string? PreviousCheckpointId = null);

/// <summary>
/// Persists compact historical summaries without deleting or rewriting raw evidence. A checkpoint
/// is only a bounded index: its summary must carry durable source references back to journal events,
/// artifacts, tool results, snapshots or an earlier checkpoint.
/// </summary>
public sealed class CompactionManager
{
    public const int SchemaVersion = 1;
    public const int MaxSummaryCharacters = 1_800;
    public const int MaxSources = 8;
    public const int MaxContextCharacters = 4_000;
    private static readonly Regex CheckpointPattern = new("^h2cp1_[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly string _root;

    public CompactionManager(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.Combine(Path.GetFullPath(stateRoot), "checkpoints");
        Directory.CreateDirectory(_root);
    }

    public AgentCompactionCheckpoint CreateCheckpoint(
        string summary,
        IReadOnlyList<AgentCompactionSourceReference> sources,
        long coveredThroughSequence,
        string? previousCheckpointId = null)
    {
        var normalizedSummary = NormalizeSummary(summary);
        if (normalizedSummary.Length == 0)
            throw new ArgumentException("Compaction summary cannot be empty.", nameof(summary));
        if (coveredThroughSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(coveredThroughSequence));
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > MaxSources)
            throw new ArgumentOutOfRangeException(nameof(sources), $"Compaction checkpoint requires 1..{MaxSources} durable sources.");

        var normalizedSources = sources.Select(ValidateSource).ToList();
        var distinct = normalizedSources
            .Select(x => (x.Kind, x.SourceId))
            .Distinct()
            .Count();
        if (distinct != normalizedSources.Count)
            throw new ArgumentException("Compaction source references must be unique.", nameof(sources));

        if (!string.IsNullOrWhiteSpace(previousCheckpointId))
        {
            ValidateCheckpointId(previousCheckpointId);
            _ = Load(previousCheckpointId);
        }
        else previousCheckpointId = null;

        var checkpoint = new AgentCompactionCheckpoint(
            SchemaVersion,
            "h2cp1_" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            coveredThroughSequence,
            normalizedSummary,
            HashText(normalizedSummary),
            normalizedSources,
            previousCheckpointId);

        _ = RenderContext(checkpoint);
        WriteAtomic(PathFor(checkpoint.Id), JsonSerializer.SerializeToUtf8Bytes(checkpoint));
        return checkpoint;
    }

    public AgentCompactionCheckpoint Load(string id)
    {
        ValidateCheckpointId(id);
        var path = PathFor(id);
        if (!File.Exists(path)) throw new FileNotFoundException("Compaction checkpoint was not found.", id);
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length > 64 * 1024) throw new IOException("Compaction checkpoint exceeds its safety limit.");
            bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
        }

        AgentCompactionCheckpoint checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<AgentCompactionCheckpoint>(bytes)
                ?? throw new IOException("Compaction checkpoint is empty.");
        }
        catch (JsonException ex)
        {
            throw new IOException("Compaction checkpoint JSON is invalid.", ex);
        }

        ValidateCheckpoint(checkpoint, id);
        _ = RenderContext(checkpoint);
        return checkpoint;
    }

    public string RenderContext(string id) => RenderContext(Load(id));

    public string RenderContext(AgentCompactionCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateCheckpoint(checkpoint, checkpoint.Id);

        var builder = new StringBuilder();
        builder.Append("## Compacted historical checkpoint ").Append(checkpoint.Id).Append('\n');
        builder.Append(checkpoint.Summary).Append('\n');
        builder.Append("Durable source references:");
        foreach (var source in checkpoint.Sources)
        {
            builder.Append("\n- ")
                .Append(source.Kind)
                .Append(':')
                .Append(source.SourceId);
            if (source.Sequence is long sequence) builder.Append("; seq=").Append(sequence);
            if (!string.IsNullOrEmpty(source.Sha256)) builder.Append("; sha256=").Append(source.Sha256);
        }
        if (!string.IsNullOrEmpty(checkpoint.PreviousCheckpointId))
            builder.Append("\nPrevious checkpoint: ").Append(checkpoint.PreviousCheckpointId);

        var rendered = builder.ToString();
        if (rendered.Length > MaxContextCharacters)
            throw new IOException("Compaction checkpoint context exceeds its bounded active-context representation.");
        return rendered;
    }

    private static AgentCompactionSourceReference ValidateSource(AgentCompactionSourceReference source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(source.Kind))
            throw new ArgumentOutOfRangeException(nameof(source), "Unknown compaction source kind.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId);
        var id = source.SourceId.Trim();
        if (id.Length > 128 || id.Any(char.IsControl))
            throw new ArgumentException("Compaction source ID must be at most 128 characters and contain no control characters.", nameof(source));
        string? sha = null;
        if (!string.IsNullOrWhiteSpace(source.Sha256))
        {
            sha = source.Sha256.Trim().ToLowerInvariant();
            if (!ShaPattern.IsMatch(sha))
                throw new ArgumentException("Compaction source SHA-256 is invalid.", nameof(source));
        }
        if (source.Sequence is < 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Compaction source sequence cannot be negative.");
        return new AgentCompactionSourceReference(source.Kind, id, sha, source.Sequence);
    }

    private static void ValidateCheckpoint(AgentCompactionCheckpoint checkpoint, string expectedId)
    {
        if (checkpoint.Schema != SchemaVersion || checkpoint.Id != expectedId)
            throw new IOException("Compaction checkpoint identity/version mismatch.");
        ValidateCheckpointId(checkpoint.Id);
        if (checkpoint.CreatedUtc.Kind != DateTimeKind.Utc)
            throw new IOException("Compaction checkpoint timestamp must be UTC.");
        if (checkpoint.CoveredThroughSequence < 0)
            throw new IOException("Compaction checkpoint sequence is invalid.");
        var summary = NormalizeSummary(checkpoint.Summary);
        if (!string.Equals(summary, checkpoint.Summary, StringComparison.Ordinal)
            || !string.Equals(HashText(summary), checkpoint.SummarySha256, StringComparison.Ordinal))
            throw new IOException("Compaction checkpoint summary changed after it was stored.");
        if (checkpoint.Sources is null || checkpoint.Sources.Count is < 1 or > MaxSources)
            throw new IOException("Compaction checkpoint source references are invalid.");

        var normalized = checkpoint.Sources.Select(ValidateSource).ToArray();
        for (var i = 0; i < normalized.Length; i++)
            if (normalized[i] != checkpoint.Sources[i])
                throw new IOException("Compaction checkpoint source reference is not canonical.");
        if (normalized.Select(x => (x.Kind, x.SourceId)).Distinct().Count() != normalized.Length)
            throw new IOException("Compaction checkpoint contains duplicate source references.");

        if (!string.IsNullOrEmpty(checkpoint.PreviousCheckpointId))
            ValidateCheckpointId(checkpoint.PreviousCheckpointId);
    }

    public string Fingerprint(string id)
    {
        // Load validates bounded schema and summary. The full fingerprint is pinned by the
        // Agent journal so even a self-rehashed checkpoint cannot replace an admitted source.
        var checkpoint = Load(id);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint))).ToLowerInvariant();
    }

    private static string NormalizeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var value = summary.Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (value.Length > MaxSummaryCharacters)
            throw new ArgumentOutOfRangeException(nameof(summary), $"Compaction summary exceeds {MaxSummaryCharacters} characters.");
        if (value.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t')))
            throw new ArgumentException("Compaction summary contains unsupported control characters.", nameof(summary));
        return value;
    }

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private string PathFor(string id) => Path.Combine(_root, id + ".json");

    private static void ValidateCheckpointId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!CheckpointPattern.IsMatch(id))
            throw new IOException("Invalid compaction checkpoint ID.");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temp, path, false);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
