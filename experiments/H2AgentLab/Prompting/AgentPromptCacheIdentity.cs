using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Prompting;

/// <summary>A stable skill document hash included only when that skill is part of the cacheable prefix.</summary>
public sealed record AgentStableSkillHash
{
    public AgentStableSkillHash(string skillId, string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        SkillId = skillId.Trim();
        Sha256Hex = NormalizeSha256(sha256Hex);
    }

    public string SkillId { get; }
    public string Sha256Hex { get; }

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Stable skill hash must be a 64-character SHA-256 hex value.", nameof(value));
        return value;
    }
}

/// <summary>
/// Deterministic identity for a cacheable prompt prefix. Key is safe for provider prompt-cache-key
/// fields and contains no prompt text, credentials or runtime context.
/// </summary>
public sealed record AgentPromptCacheIdentity(
    string Key,
    string Sha256Hex,
    string ProviderIdentity,
    string Model,
    AgentVersionIdentifiers Versions,
    IReadOnlyList<AgentStableSkillHash> StableSkillHashes);

/// <summary>
/// Builds one cache identity from stable prompt messages + provider/model + host policy/tool versions.
/// Dynamic task state, current time, session journals, workspace state and user input are absent from
/// this API by construction. The canonical field framing is versioned so future format changes can
/// invalidate old identities intentionally rather than accidentally colliding with them.
/// </summary>
public static class AgentPromptCacheIdentityBuilder
{
    public const string Scheme = "h2pc1";
    public const int MaxProviderCacheKeyCharacters = 64;

    public static AgentPromptCacheIdentity Build(
        AgentPromptLayout layout,
        AiProfile profile,
        IEnumerable<AgentStableSkillHash>? stableSkillHashes = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Prompt cache identity requires a model.", nameof(profile));

        var endpoint = AiClient.Endpoint(profile, "");
        var model = CanonicalText(profile.Model.Trim());
        var providerIdentity = ProviderIdentity(profile.Protocol, endpoint);
        var skills = SnapshotSkills(stableSkillHashes);
        using var writer = new CanonicalHashWriter();

        writer.Field("scheme", Scheme);
        writer.Field("protocol", profile.Protocol.ToString());
        writer.Field("endpoint.scheme", endpoint.Scheme.ToLowerInvariant());
        writer.Field("endpoint.host", endpoint.IdnHost.ToLowerInvariant());
        writer.Field("endpoint.port", endpoint.Port.ToString(CultureInfo.InvariantCulture));
        writer.Field("endpoint.path", CanonicalText(endpoint.AbsolutePath));
        writer.Field("model", model);
        writer.Field("version.agent-policy", layout.Versions.AgentPolicyVersion);
        writer.Field("version.safety-policy", layout.Versions.SafetyPolicyVersion);
        writer.Field("version.toolset", layout.Versions.ToolsetVersion);

        writer.Field("stable-message-count", layout.StablePrefix.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < layout.StablePrefix.Count; i++)
        {
            var message = layout.StablePrefix[i];
            writer.Field($"stable[{i}].role", message.Role.ToString());
            writer.Field($"stable[{i}].content", CanonicalText(message.Content));
        }

        writer.Field("stable-skill-count", skills.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < skills.Count; i++)
        {
            writer.Field($"skill[{i}].id", CanonicalText(skills[i].SkillId));
            writer.Field($"skill[{i}].sha256", skills[i].Sha256Hex);
        }

        var digest = writer.Finish();
        var key = Scheme + "_" + Base64Url(digest);
        if (key.Length > MaxProviderCacheKeyCharacters)
            throw new InvalidOperationException("Prompt cache key exceeded the provider-safe length bound.");
        return new AgentPromptCacheIdentity(
            key,
            Convert.ToHexString(digest).ToLowerInvariant(),
            providerIdentity,
            model,
            layout.Versions,
            skills);
    }

    private static IReadOnlyList<AgentStableSkillHash> SnapshotSkills(IEnumerable<AgentStableSkillHash>? source)
    {
        if (source is null) return [];
        var result = source
            .Select(x => x ?? throw new ArgumentException("Stable skill hash entries cannot be null.", nameof(source)))
            .OrderBy(x => x.SkillId, StringComparer.Ordinal)
            .ThenBy(x => x.Sha256Hex, StringComparer.Ordinal)
            .ToArray();
        for (var i = 1; i < result.Length; i++)
        {
            if (string.Equals(result[i - 1].SkillId, result[i].SkillId, StringComparison.Ordinal))
                throw new ArgumentException("Stable skill IDs must be unique: " + result[i].SkillId, nameof(source));
        }
        return result;
    }

    private static string ProviderIdentity(AiProtocol protocol, Uri endpoint)
    {
        var authority = endpoint.IsDefaultPort
            ? endpoint.IdnHost.ToLowerInvariant()
            : endpoint.IdnHost.ToLowerInvariant() + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);
        return protocol + ":" + endpoint.Scheme.ToLowerInvariant() + "://" + authority + CanonicalText(endpoint.AbsolutePath);
    }

    private static string CanonicalText(string value)
        => value.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Base64Url(byte[] digest)
        => Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class CanonicalHashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finished;

        public void Field(string name, string value)
        {
            if (_finished) throw new InvalidOperationException("Prompt cache hash has already been finalized.");
            AppendUtf8(CanonicalText(name));
            Span<byte> separator = stackalloc byte[1] { 0 };
            _hash.AppendData(separator);
            var bytes = Encoding.UTF8.GetBytes(CanonicalText(value));
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            _hash.AppendData(length);
            _hash.AppendData(bytes);
        }

        public byte[] Finish()
        {
            if (_finished) throw new InvalidOperationException("Prompt cache hash has already been finalized.");
            _finished = true;
            return _hash.GetHashAndReset();
        }

        private void AppendUtf8(string value) => _hash.AppendData(Encoding.UTF8.GetBytes(value));

        public void Dispose() => _hash.Dispose();
    }
}
