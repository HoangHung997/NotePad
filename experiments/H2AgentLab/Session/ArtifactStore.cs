using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using H2AgentLab.Context;

namespace H2AgentLab.Session;

public enum AgentArtifactKind
{
    ToolOutput,
    Stdout,
    Stderr,
    DocumentExtract
}

public sealed record AgentArtifactHandle(
    string Id,
    AgentArtifactKind Kind,
    string SourceId,
    long Bytes,
    string Sha256,
    DateTime CreatedUtc);

public sealed record AgentArtifactProjection(
    AgentArtifactHandle Handle,
    AgentContextToolSummary ContextSummary);

internal sealed record AgentArtifactManifest(
    int Schema,
    string Id,
    AgentArtifactKind Kind,
    string SourceId,
    long Bytes,
    string Sha256,
    DateTime CreatedUtc);

/// <summary>
/// Durable v2 store for large text outputs that must not be replayed into every model request.
/// The model receives only a bounded summary + opaque handle; full bytes stay in Lab state and
/// can be fetched explicitly through the host in a later tool layer.
/// </summary>
public sealed class ArtifactStore
{
    public const int SchemaVersion = 1;
    public const int MaxArtifactBytes = 16 * 1024 * 1024;
    public const int MaxSummaryCharacters = 600;
    public const int MaxContextHandleCharacters = 900;
    private static readonly Regex HandlePattern = new("^h2a1_[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _root;

    public ArtifactStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var fullStateRoot = Path.GetFullPath(stateRoot);
        _root = Path.Combine(fullStateRoot, "artifacts", "context");
        Directory.CreateDirectory(_root);
    }

    public AgentArtifactProjection StoreText(
        AgentArtifactKind kind,
        string sourceId,
        string toolName,
        string content,
        string summary,
        long sequence,
        double relevance = 1)
    {
        ArgumentNullException.ThrowIfNull(content);
        var validatedSourceId = AgentContextTurn.ValidateSourceId(sourceId);
        var validatedRelevance = AgentContextTurn.ValidateRelevance(relevance);
        var bytes = StrictUtf8.GetBytes(content);
        if (bytes.Length > MaxArtifactBytes)
            throw new IOException($"Artifact exceeds {MaxArtifactBytes:N0} bytes.");

        var id = "h2a1_" + Guid.NewGuid().ToString("N");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var created = DateTime.UtcNow;
        var handle = new AgentArtifactHandle(id, kind, validatedSourceId, bytes.LongLength, sha, created);
        var manifest = new AgentArtifactManifest(
            SchemaVersion, id, kind, validatedSourceId, bytes.LongLength, sha, created);

        WriteAtomic(ContentPath(id), bytes);
        try
        {
            WriteAtomic(ManifestPath(id), JsonSerializer.SerializeToUtf8Bytes(manifest));
        }
        catch
        {
            TryDelete(ContentPath(id));
            throw;
        }

        var boundedSummary = NormalizeSummary(summary);
        if (boundedSummary.Length == 0)
            boundedSummary = $"{kind} output stored outside active context.";
        var contextText = boundedSummary + "\n" +
            $"[artifact:{id}] kind={kind}; bytes={bytes.LongLength}; sha256={sha}; read remaining content using read_tool_output(artifact_id, offset). This is not a Python run ID.";
        if (contextText.Length > MaxContextHandleCharacters)
            throw new InvalidOperationException("Artifact context projection exceeded its bounded handle size.");

        return new AgentArtifactProjection(
            handle,
            new AgentContextToolSummary(
                validatedSourceId,
                toolName,
                contextText,
                sequence,
                validatedRelevance));
    }

    public AgentArtifactHandle LoadHandle(string id)
    {
        ValidateHandle(id);
        var manifest = LoadManifest(id);
        ValidateManifest(id, manifest);
        ValidateContent(manifest);
        return new AgentArtifactHandle(
            manifest.Id,
            manifest.Kind,
            manifest.SourceId,
            manifest.Bytes,
            manifest.Sha256,
            manifest.CreatedUtc);
    }

    public string ReadText(string id)
    {
        ValidateHandle(id);
        var manifest = LoadManifest(id);
        ValidateManifest(id, manifest);
        var bytes = ReadAndValidateContent(manifest);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException("Stored artifact is not valid UTF-8 text.", ex);
        }
    }

    private AgentArtifactManifest LoadManifest(string id)
    {
        var path = ManifestPath(id);
        if (!File.Exists(path)) throw new FileNotFoundException("Artifact handle was not found.", id);
        var bytes = ReadBounded(path, 64 * 1024);
        try
        {
            return JsonSerializer.Deserialize<AgentArtifactManifest>(bytes)
                ?? throw new IOException("Artifact manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new IOException("Artifact manifest is invalid.", ex);
        }
    }

    private void ValidateManifest(string id, AgentArtifactManifest manifest)
    {
        if (manifest.Schema != SchemaVersion || manifest.Id != id)
            throw new IOException("Artifact manifest identity/version mismatch.");
        AgentContextTurn.ValidateSourceId(manifest.SourceId);
        if (manifest.Bytes < 0 || manifest.Bytes > MaxArtifactBytes)
            throw new IOException("Artifact manifest byte count is invalid.");
        if (!Regex.IsMatch(manifest.Sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new IOException("Artifact manifest hash is invalid.");
        if (manifest.CreatedUtc.Kind != DateTimeKind.Utc)
            throw new IOException("Artifact manifest timestamp must be UTC.");
    }

    private void ValidateContent(AgentArtifactManifest manifest)
        => _ = ReadAndValidateContent(manifest);

    private byte[] ReadAndValidateContent(AgentArtifactManifest manifest)
    {
        var path = ContentPath(manifest.Id);
        if (!File.Exists(path)) throw new FileNotFoundException("Artifact content was not found.", manifest.Id);
        var bytes = ReadBounded(path, MaxArtifactBytes);
        if (bytes.LongLength != manifest.Bytes)
            throw new IOException("Artifact size changed after it was stored.");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(sha, manifest.Sha256, StringComparison.Ordinal))
            throw new IOException("Artifact content hash changed after it was stored.");
        return bytes;
    }

    private string ContentPath(string id) => Path.Combine(_root, id + ".txt");
    private string ManifestPath(string id) => Path.Combine(_root, id + ".json");

    private static void ValidateHandle(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!HandlePattern.IsMatch(id))
            throw new IOException("Invalid artifact handle.");
    }

    private static byte[] ReadBounded(string path, int maxBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maxBytes) throw new IOException("Stored artifact file exceeds its safety limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string NormalizeSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var value = summary.Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (value.Length <= MaxSummaryCharacters) return value;
        const string marker = "…[summary truncated]";
        return value[..(MaxSummaryCharacters - marker.Length)] + marker;
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
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only; callers still receive the original failure.
        }
    }
}
