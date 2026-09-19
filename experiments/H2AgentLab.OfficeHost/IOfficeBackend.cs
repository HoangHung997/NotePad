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

public sealed class OfficeHostFaultException : Exception
{
    public OfficeHostFaultException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
