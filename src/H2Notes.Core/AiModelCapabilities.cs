namespace H2Notes.Core;

public static class AiModelCapabilities
{
    // Exact models only: a compatible API or a similar model name is not proof of support.
    // Verified 2026-09-15 against individual OpenAI model pages and these protocol guides:
    // https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5
    // https://ai.google.dev/gemini-api/docs/generate-content/thinking
    // https://docs.ollama.com/capabilities/thinking
    public static IReadOnlyList<string> GetReasoningOptions(AiProfile profile)
    {
        var model = profile.Model.Trim();
        if (profile.Protocol is AiProtocol.OpenAiResponses or AiProtocol.OpenAiChat && IsOfficialOpenAi(profile))
            return model switch
            {
                "gpt-5" or "gpt-5-2025-08-07" or "gpt-5-mini" or "gpt-5-nano"
                    or "gpt-5-mini-2025-08-07" or "gpt-5-nano-2025-08-07" => ["minimal", "low", "medium", "high"],
                "gpt-5.1" or "gpt-5.1-2025-11-13" => ["none", "low", "medium", "high"],
                "gpt-5.2" or "gpt-5.2-2025-12-11" or "gpt-5.4" => ["none", "low", "medium", "high", "xhigh"],
                "gpt-6-astra" => ["low", "medium", "high", "xhigh", "max"],
                _ => []
            };
        if (profile.Protocol == AiProtocol.Gemini && IsOfficialGemini(profile))
            return model switch
            {
                // The retired alias now targets 3.1 Pro; retain the documented low/high intersection.
                // https://ai.google.dev/gemini-api/docs/changelog (2026-03-09)
                "gemini-3-pro-preview" => ["low", "high"],
                "gemini-3.1-pro-preview" or "gemini-3.7-flash" or "gemini-3.8-flash" => ["low", "medium", "high"],
                "gemini-3-flash-preview" or "gemini-3.1-flash-lite" or "gemini-3.5-flash" or "gemini-3.6-flash" => ["minimal", "low", "medium", "high"],
                _ => []
            };
        if (profile.Protocol == AiProtocol.Ollama && model is "gpt-oss" or "gpt-oss:latest" or "gpt-oss:20b" or "gpt-oss:120b")
            return ["low", "medium", "high"];
        return [];
    }

    // Null inherits the profile. "max" is the UI's maximum choice, never an invented wire value.
    public static AiProfile WithReasoning(AiProfile profile, string? effort)
    {
        var copy = profile.Copy();
        if (effort is not null) copy.ReasoningEffort = effort;
        return copy;
    }

    public static string? ResolveReasoningEffort(AiProfile profile)
    {
        var effort = profile.ReasoningEffort?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(effort)) return null;
        var options = GetReasoningOptions(profile);
        if (effort == "max") return options.LastOrDefault();
        if (!options.Contains(effort))
            throw new InvalidOperationException("Mức suy luận chưa được xác nhận cho model/endpoint này. Chọn Tối đa hoặc đặt lại mức suy luận trong Thiết lập AI.");
        return effort;
    }

    public static bool SupportsNativePdf(AiProfile profile)
    {
        return GetNativePdfModels(profile).Contains(profile.Model.Trim());
    }

    // Documented input formats, not a claim that a given account can call every model.
    // https://developers.openai.com/api/docs/guides/file-inputs
    // https://ai.google.dev/gemini-api/docs/generate-content/document-processing
    public static IReadOnlyList<string> GetNativePdfModels(AiProfile profile)
    {
        if (profile.Protocol is AiProtocol.OpenAiResponses or AiProtocol.OpenAiChat && IsOfficialOpenAi(profile))
            return ["gpt-4o", "gpt-4o-2024-08-06", "gpt-4o-2024-11-20",
                "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4.1-2025-04-14", "gpt-4.1-mini-2025-04-14", "gpt-4.1-nano-2025-04-14",
                "gpt-5", "gpt-5-2025-08-07", "gpt-5-mini", "gpt-5-nano", "gpt-5-mini-2025-08-07", "gpt-5-nano-2025-08-07",
                "gpt-5.1", "gpt-5.1-2025-11-13", "gpt-5.2", "gpt-5.2-2025-12-11", "gpt-5.4", "gpt-6-astra"];
        if (profile.Protocol == AiProtocol.Gemini && IsOfficialGemini(profile))
            return ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite",
                "gemini-3-pro-preview", "gemini-3-flash-preview", "gemini-3.1-pro-preview", "gemini-3.1-flash-lite",
                "gemini-3.5-flash", "gemini-3.6-flash", "gemini-3.7-flash", "gemini-3.8-flash"];
        return [];
    }

    public static bool IsOfficialOpenAi(AiProfile profile) => OfficialEndpoint(profile, "api.openai.com", "/v1/");
    public static bool IsOfficialGemini(AiProfile profile) => OfficialEndpoint(profile, "generativelanguage.googleapis.com", "/v1/", "/v1beta/");

    private static bool OfficialEndpoint(AiProfile profile, string host, params string[] paths)
    {
        try
        {
            var endpoint = AiClient.Endpoint(profile, "");
            return endpoint.Scheme == "https" && endpoint.IsDefaultPort && endpoint.IdnHost == host && paths.Contains(endpoint.AbsolutePath);
        }
        catch (InvalidOperationException) { return false; }
    }
}
