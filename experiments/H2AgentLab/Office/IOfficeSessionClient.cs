using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Office;

/// <summary>The existing OfficeHost operations at their transport boundary. Production uses
/// OfficeHostClient; a host-injected fixture is never selected from a model/tool argument.</summary>
public interface IOfficeSessionClient : IDisposable
{
    // Identity of the helper connection lifetime, NOT the native Excel/Word process or a durable ID.
    string InstanceIdentity { get; }
    Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken cancellationToken = default);
    Task<ExcelLiveSnapshot> SnapshotExcelAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest request, CancellationToken cancellationToken = default);
    Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest request, CancellationToken cancellationToken = default);
    Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default);
    Task<WordDiscovery> DiscoverWordAsync(CancellationToken cancellationToken = default);
    Task<WordLiveSnapshot> SnapshotWordAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WordPatchResult> PatchWordAsync(WordPatchRequest request, CancellationToken cancellationToken = default);
    Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest request, CancellationToken cancellationToken = default);
    Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Optional additive targeted capture, so fixture and legacy session clients remain compatible.</summary>
public interface IOfficeCaptureClient
{
    Task<OfficeCaptureResult> CaptureAsync(OfficeCaptureRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Additive AR-021 client contract for bounded, versioned Excel range pages.</summary>
public interface IExcelRangeReadClient
{
    Task<ExcelRangeReadPage> ReadExcelRangeAsync(
        ExcelReadRangeRequest request,
        CancellationToken cancellationToken = default);
}


/// <summary>Additive AR-023 contract for bounded, versioned Word paragraph/range/table reads.</summary>
public interface IWordPagedReadClient
{
    Task<WordParagraphReadPage> ReadWordParagraphsAsync(WordParagraphReadRequest request, CancellationToken cancellationToken = default);
    Task<WordRangeReadPage> ReadWordRangeAsync(WordRangeReadRequest request, CancellationToken cancellationToken = default);
    Task<WordTableReadPage> ReadWordTablesAsync(WordTableReadRequest request, CancellationToken cancellationToken = default);
}
