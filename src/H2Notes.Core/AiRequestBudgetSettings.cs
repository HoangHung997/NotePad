namespace H2Notes.Core;

/// <summary>Local, request-snapshotted policy; never a claim about a model's advertised limit.
/// ContextLimitTokens must come from the selected deployment/user configuration. A missing
/// limit uses a labelled local conservative policy, not a guessed model-name lookup.
/// Native media charges are estimates, in addition to the complete serialized byte charge.
/// Compressed file size cannot establish its expanded token cost; calibrate these estimates
/// for the selected provider/document corpus before claiming native context acceptance.</summary>
public sealed record AiRequestBudgetSettings
{
    public const int LocalFallbackContextTokens = 131_072;
    public int? ContextLimitTokens { get; init; }
    public string ContextLimitSource { get; init; } = "";
    public int ReservedOutputTokens { get; init; } = 4096;
    public int SafetyMarginTokens { get; init; } = 2048;
    public int MaxSerializedBytes { get; init; } = 16 * 1024 * 1024;
    public int NativeImageTokenEstimate { get; init; } = 8192;
    public int NativeFileTokenEstimate { get; init; } = 32768;
    public string MediaEstimateSource { get; init; } = "h2-local-media-estimate-v1";
    // Empty chooses transport dialect (official Chat max_completion_tokens; compatible max_tokens).
    public string ChatOutputLimitParameter { get; init; } = "";
}
