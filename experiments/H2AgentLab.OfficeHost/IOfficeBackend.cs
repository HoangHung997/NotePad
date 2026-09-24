using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public interface IOfficeBackend
{
    ExcelDiscovery DiscoverExcel();
    ExcelLiveSnapshot SnapshotExcel(string sessionId);
    ExcelPatchResult PatchExcel(ExcelPatchRequest request);
    ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest request);
    OfficeSaveCopyResult SaveExcelCopy(OfficeSaveCopyRequest request);

    WordDiscovery DiscoverWord();
    WordLiveSnapshot SnapshotWord(string sessionId);
    WordPatchResult PatchWord(WordPatchRequest request);
    WordLanguageEvidenceResult InspectWordLanguage(WordLanguageEvidenceRequest request);
    OfficeSaveCopyResult SaveWordCopy(OfficeSaveCopyRequest request);
}

public interface IOfficeCaptureBackend
{
    OfficeCaptureResult Capture(OfficeCaptureRequest request);
}

/// <summary>Additive bounded Excel range reader. Keeping this separate from IOfficeBackend
/// preserves compatibility with older injected fixtures while production COM/fixture backends
/// opt into the AR-021 paged read contract.</summary>
public interface IExcelRangeReadBackend
{
    ExcelRangeReadPage ReadExcelRange(ExcelReadRangeRequest request);
}

public sealed class OfficeHostFaultException : Exception
{
    public OfficeHostFaultException(string code, string message, bool noEffect = false) : base(message)
    {
        Code = code; NoEffect = noEffect;
    }

    public string Code { get; }
    public bool NoEffect { get; }
}
