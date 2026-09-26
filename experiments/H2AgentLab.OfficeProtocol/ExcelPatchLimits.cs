namespace H2AgentLab.OfficeProtocol;

/// <summary>One cardinality contract for callable schemas, adapters, IPC and Office backends.
/// This rejects an oversized batch; it never truncates or automatically splits a mutation.</summary>
public static class ExcelPatchLimits
{
    public const int MinCells = 1;
    public const int MaxCells = 128;
    public const string ErrorCode = "invalid_request";

    public static string? ValidationError(int count)
        => count is < MinCells or > MaxCells
            ? $"Excel patch must contain {MinCells}..{MaxCells} single-cell operations; no cells were written."
            : null;
}
