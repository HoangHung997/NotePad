using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public sealed record AgentRequestBudgetReceipt(
    Guid BudgetId, Guid TaskId, Guid TurnId, long Sequence, string Kind, string Protocol,
    int SerializedBytes, string PayloadSha256, long SerializedInputEstimate,
    long RetainedContextEstimate, long UsageCorrectionTokens, long EstimatedInputTokens,
    int ReservedOutputTokens, int SafetyMarginTokens, int ContextLimitTokens,
    bool ProviderLimitConfigured, string LimitSource, string MediaEstimateSource,
    int Images, int Files, bool Allowed, string? ErrorCode)
{
    public string TokenAccounting => "EstimatedSerializedUtf8PlusMediaAndRetainedContext";
    public string ByteAccounting => "ExactUtf8SerializedBody";
    public string DispatchState => Kind == "CompactionCandidate" ? "CandidateOnlyNotSent" : "PreSendDecision";
}

public interface IAgentRequestBudgetSource
{
    AgentRequestBudgetReceipt? LastRequestBudget { get; }
    event Action<AgentRequestBudgetReceipt>? RequestBudgetEvaluated;
}

public sealed class AgentRequestBudgetException : InvalidOperationException
{
    public string Code { get; }
    public AgentRequestBudgetReceipt? Receipt { get; }
    internal AgentRequestBudgetException(string code, AgentRequestBudgetReceipt? receipt = null)
        : base($"{code}: Request not sent. Preserve task state and tool results; rebase context or configure a sourced budget before continuing. No automatic retry or provider switch.")
    { Code = code; Receipt = receipt; }
}

/// <summary>One guard per concrete transport turn, directly at the final serializer/send boundary.
/// This is an estimate, not a tokenizer or bill. Every byte of the actual JSON (including
/// escaped text, reasoning items, tool schemas and base64 media) is charged as one estimated
/// input token, plus explicit media charges. Cached tokens are never deducted. For native
/// continuation the server-held lineage is additional input even when the new wire body is tiny.
/// Only counters/fingerprints are retained; there is no second transcript or durable state store.</summary>
public sealed class AgentRequestBudgetGuard
{
    private readonly AiRequestBudgetSettings _settings;
    private readonly AiProtocol _protocol;
    private readonly string _chatLimitField;
    private readonly Guid _id = Guid.NewGuid();
    private long _sequence;
    private long _generationCount;
    private string? _retainedResponseId;
    private long _retainedContext;
    private long _usageCorrection;
    private bool _accountingInvalid;
    public AgentRequestBudgetReceipt? LastReceipt { get; private set; }
    public event Action<AgentRequestBudgetReceipt>? Evaluated;

    public AgentRequestBudgetGuard(AiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _settings = profile.RequestBudget ?? new(); // Immutable record: snapshot cannot alias mutable settings.
        _protocol = profile.Protocol;
        var official = Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == "https" && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            && endpoint.IsDefaultPort && endpoint.AbsolutePath.TrimEnd('/') == "/v1";
        _chatLimitField = string.IsNullOrEmpty(_settings.ChatOutputLimitParameter)
            ? official ? "max_completion_tokens" : "max_tokens" : _settings.ChatOutputLimitParameter;
        var limit = _settings.ContextLimitTokens ?? AiRequestBudgetSettings.LocalFallbackContextTokens;
        if (limit is < 256 or > 16_777_216
            || _settings.ReservedOutputTokens < 1 || _settings.ReservedOutputTokens >= limit
            || _settings.SafetyMarginTokens < 0 || (long)_settings.SafetyMarginTokens + _settings.ReservedOutputTokens >= limit
            || _settings.MaxSerializedBytes is < 256 or > 67_108_864
            || _settings.NativeImageTokenEstimate is < 1 or > 16_777_216
            || _settings.NativeFileTokenEstimate is < 1 or > 16_777_216
            || !SafeSource(_settings.MediaEstimateSource)
            || _settings.ContextLimitTokens.HasValue && !SafeSource(_settings.ContextLimitSource)
            || _chatLimitField is not ("max_tokens" or "max_completion_tokens"))
            throw new AgentRequestBudgetException("request_budget_invalid_config");
    }

    public string Prepare(JsonObject payload, Guid taskId, Guid turnId, string protocol,
        CancellationToken cancellationToken = default, bool prewarm = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (taskId == Guid.Empty || turnId == Guid.Empty) throw new ArgumentException("Budget requires task and turn identity.");
        ApplyOutputLimit(payload);
        // Serialize exactly once. The returned string, not a second reconstruction, is sent.
        var serialized = payload.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(serialized);
        var images = 0; var files = 0;
        CountMedia(payload, ref images, ref files);
        var wireEstimate = (long)bytes.Length + (long)images * _settings.NativeImageTokenEstimate
            + (long)files * _settings.NativeFileTokenEstimate;
        var previous = payload["previous_response_id"]?.GetValue<string>();
        var retained = previous is null ? 0 : _retainedContext;
        var correction = previous is null ? _usageCorrection : 0;
        var estimate = SaturatingAdd(SaturatingAdd(wireEstimate, retained), correction);
        var reserve = prewarm ? 0 : _settings.ReservedOutputTokens;
        var limit = _settings.ContextLimitTokens ?? AiRequestBudgetSettings.LocalFallbackContextTokens;
        var total = SaturatingAdd(estimate, (long)reserve + _settings.SafetyMarginTokens);
        var error = _accountingInvalid ? "request_budget_accounting_invalid"
            : previous is not null && (!string.Equals(previous, _retainedResponseId, StringComparison.Ordinal) || retained <= 0)
                ? "request_budget_context_unknown"
            : bytes.Length > _settings.MaxSerializedBytes ? "request_wire_limit"
            : total > limit ? "request_budget_exceeded" : null;
        var receipt = new AgentRequestBudgetReceipt(_id, taskId, turnId, ++_sequence,
            prewarm ? "Prewarm" : _generationCount == 0 ? "Start" : "Continue", protocol,
            bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), wireEstimate,
            retained, correction, estimate, reserve, _settings.SafetyMarginTokens, limit,
            _settings.ContextLimitTokens.HasValue,
            _settings.ContextLimitTokens.HasValue ? _settings.ContextLimitSource : "h2-local-conservative-policy-v1-not-provider-limit",
            _settings.MediaEstimateSource, images, files, error is null, error);
        LastReceipt = receipt;
        Evaluated?.Invoke(receipt); // A failed mandatory observer prevents dispatch, not a silent metrics loss.
        cancellationToken.ThrowIfCancellationRequested();
        if (error is not null) throw new AgentRequestBudgetException(error, receipt);
        if (!prewarm) _generationCount++;
        return serialized;
    }

    internal AgentRequestBudgetReceipt Preview(JsonObject payload, Guid taskId, Guid turnId,
        string protocol, CancellationToken cancellationToken)
    {
        var copy = new AgentRequestBudgetGuard(new AiProfile
        {
            Protocol = _protocol,
            RequestBudget = _settings with { ChatOutputLimitParameter = _chatLimitField }
        })
        {
            _usageCorrection = _usageCorrection,
            _accountingInvalid = _accountingInvalid,
            _retainedContext = _retainedContext,
            _retainedResponseId = _retainedResponseId
        };
        copy.Prepare((JsonObject)payload.DeepClone(), taskId, turnId, protocol, cancellationToken);
        return copy.LastReceipt! with { Kind = "CompactionCandidate" };
    }

    public void ObserveCompleted(AgentTransportUsage? usage, string? responseId = null, bool prewarm = false)
    {
        if (LastReceipt is not { Allowed: true } last) throw new InvalidOperationException("Completion without an admitted request.");
        if (usage is { } u && (u.InputTokens < 0 || u.OutputTokens < 0 || u.TotalTokens < 0 || u.CachedInputTokens < 0))
        { _accountingInvalid = true; return; }
        // Reserve also covers omitted/encrypted provider output; usage may increase, never erase it.
        var input = Math.Max(last.EstimatedInputTokens, usage?.InputTokens ?? 0);
        var output = prewarm ? 0 : Math.Max(_settings.ReservedOutputTokens, usage?.OutputTokens ?? 0);
        _retainedContext = Math.Max(SaturatingAdd(input, output), usage?.TotalTokens ?? 0);
        _retainedResponseId = responseId;
        // Replay serializers carry the transcript. Preserve only any observed under-estimate,
        // never subtract cache hits or use tiny continuation usage to shrink the earlier lineage.
        if (last.RetainedContextEstimate == 0 && usage?.InputTokens is long actual && actual > last.SerializedInputEstimate)
            _usageCorrection = Math.Max(_usageCorrection, actual - last.SerializedInputEstimate);
        if (_retainedContext == long.MaxValue) _accountingInvalid = true;
    }

    private void ApplyOutputLimit(JsonObject payload)
    {
        switch (_protocol)
        {
            case AiProtocol.Ollama:
                var options = payload["options"] as JsonObject;
                if (options is null) { options = new JsonObject(); payload["options"] = options; }
                options["num_predict"] = _settings.ReservedOutputTokens;
                // Do not silently increase RAM/VRAM context allocation from a local fallback.
                if (_settings.ContextLimitTokens is int context) options["num_ctx"] = context;
                break;
            case AiProtocol.OpenAiChat: payload[_chatLimitField] = _settings.ReservedOutputTokens; break;
            case AiProtocol.OpenAiResponses:
                payload["max_output_tokens"] = _settings.ReservedOutputTokens;
                payload["truncation"] = "disabled";
                break;
            default: throw new AgentRequestBudgetException("request_budget_protocol_unsupported");
        }
    }

    private static void CountMedia(JsonNode? node, ref int images, ref int files)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var kind))
            {
                if (kind is "input_image" or "image_url") images++;
                if (kind is "input_file" or "file") files++;
            }
            if (obj["images"] is JsonArray ollamaImages) images += ollamaImages.Count;
            foreach (var item in obj) CountMedia(item.Value, ref images, ref files);
        }
        else if (node is JsonArray array)
            foreach (var item in array) CountMedia(item, ref images, ref files);
    }
    private static bool SafeSource(string? value) => value is { Length: > 0 and <= 160 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');
    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
