using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using H2Notes.Core;

namespace H2AgentLab.Transport;

/// <summary>Provider wire projection only. The registry, journal, arguments and call IDs keep their identity.</summary>
internal static class OpenAiResponsesWireContract
{
    private const string Prefix = "h2w_";
    private static readonly Regex SafeName = new("\\A[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant);

    internal static string WireName(string internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName) || internalName.Length > 4096)
            throw new ArgumentException("Responses tool name is empty or exceeds the host limit.");
        if (SafeName.IsMatch(internalName) && !internalName.StartsWith(Prefix, StringComparison.Ordinal))
            return internalName;
        // Reserved prefix prevents a legal literal name from impersonating a generated alias.
        // No dependence on registration order, loading batch, process or randomized GetHashCode.
        var slug = new string(internalName.Take(20).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(internalName))).ToLowerInvariant()[..32];
        return Prefix + slug + "_" + hash;
    }

    private static Dictionary<string, string> Reverse(IEnumerable<AgentToolDefinition> tools)
    {
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!names.Add(tool.Name) || !reverse.TryAdd(WireName(tool.Name), tool.Name))
                throw new ArgumentException("Duplicate or colliding Responses wire tool identity; request refused.");
            if (tool.Parameters.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Responses function parameters must be a JSON object.");
        }
        return reverse;
    }

    internal static JsonArray Tools(IEnumerable<AgentToolDefinition> tools)
    {
        var snapshot = tools.ToArray();
        _ = Reverse(snapshot); // Validate the entire projection before serialization or any network write.
        var result = new JsonArray();
        foreach (var tool in snapshot)
            result.Add(new JsonObject
            {
                ["type"] = "function", ["name"] = WireName(tool.Name), ["description"] = tool.Description,
                // Preserve optional fields and the host's schema. Do not silently normalize it into strict mode.
                ["strict"] = false, ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
            });
        return result;
    }

    internal static string InternalName(string wireName, IEnumerable<AgentToolDefinition> tools)
    {
        if (!Reverse(tools).TryGetValue(wireName, out var name))
            throw new InvalidDataException("Responses returned an unadvertised wire tool name; no tool was dispatched.");
        return name;
    }

    internal static void Admit(Dictionary<string, AgentToolDefinition> current, IEnumerable<AgentToolDefinition> additions)
    {
        // Validate all replacements/collisions before changing the live table. Reloading the same
        // schema is idempotent, but changing a callable contract mid-turn requires a context rebase.
        var next = new Dictionary<string, AgentToolDefinition>(current, StringComparer.Ordinal);
        foreach (var tool in additions)
        {
            if (next.TryGetValue(tool.Name, out var prior)
                && (prior.Description != tool.Description || prior.SupportsParallelExecution != tool.SupportsParallelExecution
                    || !JsonElement.DeepEquals(prior.Parameters, tool.Parameters)))
                throw new InvalidOperationException("Responses tool contract changed during an active turn; rebase required.");
            next[tool.Name] = tool.Snapshot();
        }
        _ = Reverse(next.Values);
        current.Clear();
        foreach (var pair in next) current.Add(pair.Key, pair.Value);
    }
}

/// <summary>Bounded allow-list diagnostics; never retain an error body or provider-echoed message.</summary>
internal static class OpenAiResponsesDiagnostics
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "invalid_request_error", "invalid_value", "invalid_function_name", "invalid_function_parameters",
        "invalid_tool_schema", "invalid_tool_call", "unsupported_parameter", "unsupported_value",
        "unknown_parameter", "missing_required_parameter", "context_length_exceeded", "model_not_found",
        "rate_limit_exceeded", "insufficient_quota", "server_error", "invalid_api_key", "permission_denied"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "invalid_request_error", "authentication_error", "permission_error", "rate_limit_error", "server_error" };
    private static readonly Regex Parameter = new(
        "\\A(?:model|tools|tool_choice|parallel_tool_calls|previous_response_id|store|include|stream|max_output_tokens|reasoning(?:\\.(?:effort|summary))?|tools\\[[0-9]{1,4}\\]\\.(?:name|parameters|strict)|input(?:\\[[0-9]{1,4}\\](?:\\.(?:type|role|call_id|name|arguments|output|content|encrypted_content))?)?)\\z",
        RegexOptions.CultureInvariant);

    internal static async Task<AiServiceException> HttpError(HttpResponseMessage response, string phase, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var bytes = new byte[16_384]; var used = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            while (used < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(used), limit.Token).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            using var json = JsonDocument.Parse(bytes.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 24 });
            return Error(json.RootElement, phase, response.StatusCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException) { }
        token.ThrowIfCancellationRequested();
        return Error(default, phase, response.StatusCode);
    }

    internal static AiServiceException Error(JsonElement root, string phase, HttpStatusCode? status = null)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var response)) root = response;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)) root = error;
        static string Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        var code = Field(root, "code"); code = Codes.Contains(code) ? code : "not_exposed";
        var type = Field(root, "type"); type = Types.Contains(type) ? type : "not_exposed";
        var param = Field(root, "param"); param = param.Length <= 100 && Parameter.IsMatch(param) ? param : "not_exposed";
        phase = phase is "initial" or "tool_continuation" or "prewarm" ? phase : "response";
        var hint = code is "invalid_function_parameters" or "invalid_tool_schema" || param.EndsWith(".parameters", StringComparison.Ordinal)
            ? "Kiểm tra JSON schema của tool."
            : code == "invalid_function_name" || param.EndsWith(".name", StringComparison.Ordinal)
                ? "Kiểm tra tên tool trên wire."
                : "Kiểm tra hợp đồng request tại tham số đã chỉ ra; nội dung phản chiếu từ máy chủ không được lưu.";
        var prefix = status is { } s ? $"HTTP {(int)s}" : "Responses stream failed";
        // Only host literals plus exact allow-listed protocol tokens reach UI/journal/logs.
        return new AiServiceException(status is { } http ? AiFailure.FromStatus(http, "").Code : "response_failed",
            $"{prefix} [phase={phase}; code={code}; type={type}; param={param}]: {hint} Không tự gửi lại; kết quả tool trước đó phải được đối chiếu.", status);
    }
}
