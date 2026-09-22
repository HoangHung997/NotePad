using System.Collections.Concurrent;
using System.Text.Json;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Verification;
using H2Notes.Core;

namespace H2AgentLab.Integration;

/// <summary>Executes structured operations through the isolated STA OfficeHost, with real
/// post-action snapshots. No fixture backend is constructed by production composition.</summary>
internal sealed class H2OfficeRuntimeTools(Func<bool> authorized, string outputRoot, H2AgentPermissionScope? scope,
    IReadOnlyList<H2AgentTargetPath>? projectTargets = null) : IAgentRuntimeDomainVerifier, IDisposable
{
    private OfficeHostClient? _client;
    private readonly ConcurrentDictionary<string, AgentRuntimeDomainVerification> _reports = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OfficeRejectedMutationRecovery _wordRecovery = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private OfficeHostClient Client => _client ??= new(H2HelperLocator.Resolve("H2AgentLab.OfficeHost"), defaultTimeout: TimeSpan.FromSeconds(60));
    public string DomainId => "h2-office-live";

    public void Register(ToolRegistry registry)
    {
        foreach (var capability in StructuredOfficeCapabilityCatalog.All.Where(item => item.Name != "word.export"))
        {
            var name = capability.Name;
            var discovery = name.Contains("list_", StringComparison.Ordinal) || name is "excel.get_active_workbook" or "word.get_active_document";
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            if (!discovery) { properties["session_id"] = new { type = "string", description = "Exact SessionId from discovery." }; required.Add("session_id"); }
            if (capability.Access == AgentToolAccess.Mutating || name is "word.get_spelling_errors" or "word.get_grammar_candidates" or "word.extract_legal_citations")
            { properties["state_token"] = new { type = "string", description = "Latest observed StateToken. Stale values are rejected." }; required.Add("state_token"); }
            if (name.StartsWith("excel.") && name is "excel.write_range" or "excel.set_formula" or "excel.apply_format")
            {
                properties["sheet_name"] = new { type = "string" };
                properties["cells"] = new { type = "array", minItems = ExcelPatchLimits.MinCells, maxItems = ExcelPatchLimits.MaxCells, items = new { type = "object", properties = new {
                    address = new { type = "string" }, value = new { type = "string" }, clearValue = new { type = "boolean" },
                    formula = new { type = "string" }, bold = new { type = "boolean" }, italic = new { type = "boolean" },
                    fillColor = new { type = "integer" }, numberFormat = new { type = "string" } }, required = new[] { "address" }, additionalProperties = false } };
                required.AddRange(["sheet_name", "cells"]);
            }
            if (name is "word.replace_range" or "word.apply_format")
            {
                properties["paragraphs"] = new { type = "array", minItems = 1, maxItems = 100, items = new { type = "object", properties = new {
                    paragraphIndex = new { type = "integer", minimum = 0 }, text = new { type = "string" }, bold = new { type = "boolean" },
                    italic = new { type = "boolean" }, underline = new { type = "boolean" } }, required = new[] { "paragraphIndex" }, additionalProperties = false } };
                required.Add("paragraphs");
            }
            if (name == "word.insert_text")
            {
                properties["paragraph_index"] = new { type = "integer", minimum = 0 };
                properties["offset"] = new { type = "integer", minimum = 0 };
                properties["text"] = new { type = "string" };
                required.AddRange(["paragraph_index", "offset", "text"]);
            }
            if (name.EndsWith("save_copy", StringComparison.Ordinal))
            { properties["destination"] = new { type = "string", description = "New file path under the approved output workspace. Existing files are never overwritten." }; required.Add("destination"); }
            if (name == "word.find_text") { properties["query"] = new { type = "string" }; required.Add("query"); }
            var description = capability.Description + " Discovery lists metadata only, with no usable state token. Get the selected/active document or an explicit session snapshot before any mutation. Word replacement/format uses paragraph indexes; Excel patches use observed cell addresses.";
            if (name is "word.replace_range" or "word.insert_text")
                description += " Newlines in text create real Word paragraphs and inherit the original paragraph style/format. All paragraph indexes refer to the BEFORE snapshot, even when earlier replacements add paragraphs. To rewrite a CV, use newline-separated text on existing paragraphs, never invent new paragraph indexes. Mixed character formatting requires a separate explicit formatting operation. Read back the new snapshot before further edits.";
            registry.Register(new ToolDescriptor(name, new(capability.Namespace, "Structured live OfficeHost operations."), description,
                capability.Risk, capability.Access, false, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description,
                    parameters = new { type = "object", properties, required, additionalProperties = false } } }),
                new DelegatingToolExecutor("h2-office-host", ExecuteAsync), resourceScope: new(capability.ResourceScope, "observed-session"),
                serializationKey: "office-host", canProvideVerificationEvidence: true,
                preference: new("active-content", ToolInteractionFidelity.Structured),
                readiness: new(!OperatingSystem.IsWindows() ? ToolReadinessState.Unsupported
                    : H2HelperLocator.IsPackaged("H2AgentLab.OfficeHost") ? ToolReadinessState.Degraded : ToolReadinessState.Unavailable),
                limits: new(MaxBatchItems: name is "excel.write_range" or "excel.set_formula" or "excel.apply_format" ? ExcelPatchLimits.MaxCells : null),
                dependencies: ["office-host"], resultFormat: ToolResultFormat.Json, preflight: CountPreflight));
        }
    }

    private static ToolExecutionOutput? CountPreflight(ToolCall call)
    {
        if (call.Name is not ("excel.write_range" or "excel.set_formula" or "excel.apply_format")) return null;
        var count = call.Arguments.TryGetProperty("cells", out var batch) && batch.ValueKind == JsonValueKind.Array ? batch.GetArrayLength() : -1;
        return ExcelPatchLimits.ValidationError(count) is { } problem
            ? ToolOutcomeBridge.Failure(call, null, ExcelPatchLimits.ErrorCode, ToolErrorPhase.Preflight, ToolMutationEffect.None,
                JsonSerializer.Serialize(new { ok = false, error = ExcelPatchLimits.ErrorCode, message = problem, mutationApplied = false }))
            : null;
    }

    private async ValueTask<string> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Count preflight precedes discovery/snapshot/IPC and any native write.
        if (call.Name is "excel.write_range" or "excel.set_formula" or "excel.apply_format")
        {
            var count = call.Arguments.TryGetProperty("cells", out var batch)
                && batch.ValueKind == JsonValueKind.Array ? batch.GetArrayLength() : -1;
            if (ExcelPatchLimits.ValidationError(count) is { } problem)
                return JsonSerializer.Serialize(new { ok = false, error = ExcelPatchLimits.ErrorCode,
                    message = problem, mutationApplied = false });
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            object result;
            var name = call.Name;
            var session = H2ProductionToolSession.Arg(call, "session_id") ?? "";
            var token = H2ProductionToolSession.Arg(call, "state_token") ?? "";
            if (projectTargets is not null && session.Length > 0)
            {
                var targetPath = name.StartsWith("excel.", StringComparison.Ordinal)
                    ? (await Client.DiscoverExcelAsync(ct).ConfigureAwait(false)).Workbooks.SingleOrDefault(w => w.SessionId == session)?.FullName
                    : (await Client.DiscoverWordAsync(ct).ConfigureAwait(false)).Documents.SingleOrDefault(d => d.SessionId == session)?.FullName;
                if (!H2AgentTargetScope.Contains(projectTargets, targetPath))
                    throw new UnauthorizedAccessException("Tài liệu này không thuộc dự án hoặc đích được chỉ rõ. Chọn liên kết/tệp cụ thể trước khi tiếp tục.");
            }
            if (session.Length > 0 && scope?.ScopeKind is H2AgentResourceScopeKind.Document or H2AgentResourceScopeKind.Session)
            {
                if (session != scope.DocumentSessionId) throw new UnauthorizedAccessException("Office session is outside the selected resource.");
                var path = name.StartsWith("excel.", StringComparison.Ordinal)
                    ? (await Client.DiscoverExcelAsync(ct).ConfigureAwait(false)).Workbooks.SingleOrDefault(w => w.SessionId == session)?.FullName
                    : (await Client.DiscoverWordAsync(ct).ConfigureAwait(false)).Documents.SingleOrDefault(d => d.SessionId == session)?.FullName;
                if (path is null || (!string.IsNullOrWhiteSpace(scope.DocumentPath)
                    && !string.Equals(path, scope.DocumentPath, StringComparison.OrdinalIgnoreCase)))
                    throw new UnauthorizedAccessException("Selected Office document was closed or replaced; select it again.");
            }
            if (name is "excel.list_workbooks" or "excel.get_active_workbook")
            {
                var discovery = await Client.DiscoverExcelAsync(ct).ConfigureAwait(false);
                var workbooks = projectTargets is null ? discovery.Workbooks : discovery.Workbooks.Where(w => H2AgentTargetScope.Contains(projectTargets, w.FullName)).ToArray();
                var selected = scope?.DocumentSessionId ?? discovery.ActiveSessionId;
                result = name == "excel.get_active_workbook" && workbooks.Any(w => w.SessionId == selected)
                    ? await Client.SnapshotExcelAsync(selected!, ct).ConfigureAwait(false)
                    : new ExcelDiscovery(workbooks, workbooks.Any(w => w.SessionId == discovery.ActiveSessionId) ? discovery.ActiveSessionId : null);
            }
            else if (name is "word.list_documents" or "word.get_active_document")
            {
                var discovery = await Client.DiscoverWordAsync(ct).ConfigureAwait(false);
                var documents = projectTargets is null ? discovery.Documents : discovery.Documents.Where(d => H2AgentTargetScope.Contains(projectTargets, d.FullName)).ToArray();
                var selected = scope?.DocumentSessionId ?? discovery.ActiveSessionId;
                result = name == "word.get_active_document" && documents.Any(d => d.SessionId == selected)
                    ? await Client.SnapshotWordAsync(selected!, ct).ConfigureAwait(false)
                    : new WordDiscovery(documents, documents.Any(d => d.SessionId == discovery.ActiveSessionId) ? discovery.ActiveSessionId : null);
            }
            else if (name.EndsWith("save_copy", StringComparison.Ordinal))
            {
                RequireAuthorization();
                var destination = new SafeWorkspace(outputRoot, scope?.Mode == H2AgentPermissionMode.FullAccess
                    ? () => scope.HasFullAccessAt(DateTime.UtcNow) : null).Resolve(H2ProductionToolSession.Arg(call, "destination") ?? "");
                if (File.Exists(destination)) throw new IOException("Choose a new output file; overwriting is not permitted.");
                var request = new OfficeSaveCopyRequest(session, token, true, destination);
                var saved = name.StartsWith("excel.") ? await Client.SaveExcelCopyAsync(request, ct).ConfigureAwait(false)
                    : await Client.SaveWordCopyAsync(request, ct).ConfigureAwait(false);
                var actual = SafeWorkspace.Hash(await File.ReadAllBytesAsync(destination, ct).ConfigureAwait(false));
                Record(call, string.Equals(actual, saved.SavedCopySha256, StringComparison.OrdinalIgnoreCase), "saved-copy:" + actual);
                result = saved;
            }
            else if (name.StartsWith("excel.", StringComparison.Ordinal))
            {
                if (name is "excel.write_range" or "excel.set_formula" or "excel.apply_format")
                {
                    RequireAuthorization();
                    var cells = call.Arguments.GetProperty("cells").Deserialize<ExcelCellPatch[]>(Json) ?? [];
                    if (ExcelPatchLimits.ValidationError(cells.Length) is { } problem)
                        throw new ArgumentException(problem);
                    var sheetName = H2ProductionToolSession.Arg(call, "sheet_name") ?? "";
                    var patch = await Client.PatchExcelAsync(new(session, token, true, sheetName, cells), ct).ConfigureAwait(false);
                    var after = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    var sheet = after.Sheets.Single(item => item.Name == sheetName);
                    var targets = cells.Select(cell => new LiveExcelExpectedCell(sheetName, cell.Address,
                        OfficeMutationReadback.ExpectedExcelCell(
                            patch.Before.Sheets.Single(s => s.Name == sheetName).Cells.Single(c => c.Address == cell.Address),
                            cell, sheet.Cells.Single(item => item.Address == cell.Address)))).ToArray();
                    var expectedMatch = cells.All(cell => Matches(cell, sheet.Cells.Single(item => item.Address == cell.Address)));
                    var preserved = LiveExcelVerifier.Verify(patch.Before, after, new(targets));
                    Record(call, expectedMatch && preserved.Passed, "office-state:" + after.StateToken);
                    result = patch with { After = after };
                }
                else if (name == "excel.recalculate")
                {
                    RequireAuthorization();
                    var before = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    var calculated = await Client.RecalculateExcelAsync(new(session, token, true), ct).ConfigureAwait(false);
                    var after = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    var preserve = before.Sheets.SelectMany(s => s.Cells.Select(c => (s.Name, c.Address, c.Formula)))
                        .SequenceEqual(after.Sheets.SelectMany(s => s.Cells.Select(c => (s.Name, c.Address, c.Formula))));
                    Record(call, preserve && calculated.StateToken == after.StateToken, "office-state:" + after.StateToken);
                    result = after;
                }
                else result = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
            }
            else if (name is "word.get_spelling_errors" or "word.get_grammar_candidates" or "word.extract_legal_citations")
                result = await Client.InspectWordLanguageAsync(new(session, token), ct).ConfigureAwait(false);
            else if (name is "word.replace_range" or "word.apply_format" or "word.insert_text")
            {
                RequireAuthorization();
                var beforeWord = await Client.SnapshotWordAsync(session, ct).ConfigureAwait(false);
                if (token != beforeWord.StateToken)
                    return JsonSerializer.Serialize(new { ok = false, error = "stale_state", message = "Read word.read_paragraphs for the current state token before editing." });
                WordParagraphPatch[] paragraphs;
                if (name == "word.insert_text")
                {
                    var index = call.Arguments.GetProperty("paragraph_index").GetInt32();
                    var offset = call.Arguments.GetProperty("offset").GetInt32();
                    var text = H2ProductionToolSession.Arg(call, "text") ?? "";
                    if (index < 0 || index >= beforeWord.Paragraphs.Count || offset < 0 || offset > beforeWord.Paragraphs[index].Text.Length)
                        return JsonSerializer.Serialize(new { ok = false, error = "word_patch_rejected", mutationApplied = false,
                            message = "Use an existing zero-based paragraph_index and an offset from 0 through that paragraph's text length. Newlines in text add paragraphs.",
                            failureId = _wordRecovery.Reject(name, beforeWord, [new(index, text)]) });
                    paragraphs = [new(index, beforeWord.Paragraphs[index].Text.Insert(offset, text))];
                }
                else paragraphs = call.Arguments.GetProperty("paragraphs").Deserialize<WordParagraphPatch[]>(Json) ?? [];
                if (WordPatchRules.ValidationError(beforeWord, paragraphs) is { } problem)
                    return JsonSerializer.Serialize(new { ok = false, error = "word_patch_rejected", message = problem,
                        failureId = _wordRecovery.Reject(name, beforeWord, paragraphs), mutationApplied = false,
                        next = "No document content was changed. Correct the patch for the same original paragraphs and retry. A verified correction resolves this rejected attempt." });
                WordPatchResult patch;
                try { patch = await Client.PatchWordAsync(new(session, token, true, paragraphs), ct).ConfigureAwait(false); }
                catch (OfficeHostClientException ex) when (ex.Code == "word_patch_rejected")
                {
                    return JsonSerializer.Serialize(new { ok = false, error = ex.Code, message = ex.Message,
                        failureId = _wordRecovery.Reject(name, beforeWord, paragraphs), mutationApplied = false });
                }
                var after = await Client.SnapshotWordAsync(session, ct).ConfigureAwait(false);
                var verified = OfficeMutationReadback.VerifyWordPatch(patch.Before, after, paragraphs);
                Record(call, verified, "office-state:" + after.StateToken);
                var resolvedFailureIds = _wordRecovery.Resolve(name, patch.Before, paragraphs, verified);
                _wordRecovery.ObserveFormatting(patch.Before, after, paragraphs, verified);
                result = new { patch.Before, After = after, patch.ChangedParagraphs,
                    resolvedFailureIds };
            }
            else
            {
                var snapshot = await Client.SnapshotWordAsync(session, ct).ConfigureAwait(false);
                result = name == "word.find_text" ? (object)snapshot.Paragraphs.Where(p => p.Text.Contains(H2ProductionToolSession.Arg(call, "query") ?? "", StringComparison.OrdinalIgnoreCase)).ToArray() : snapshot;
            }
            return JsonSerializer.Serialize(result);
        }
        finally { _gate.Release(); }
    }

    private void RequireAuthorization() { if (!authorized()) throw new UnauthorizedAccessException("Office mutation is not authorized."); }
    private void Record(ToolCall call, bool passed, string evidence)
        => _reports[call.Id] = new(DomainId, passed, [evidence], passed ? null : "Native Office readback failed target/preservation checks.");
    private static bool Matches(ExcelCellPatch expected, ExcelCellState actual)
        => (expected.Value is null || expected.Value == actual.Value) && (!expected.ClearValue || actual.Value.Length == 0)
            && (expected.Formula is null || expected.Formula == actual.Formula)
            && (expected.Bold is null || expected.Bold == actual.Bold) && (expected.Italic is null || expected.Italic == actual.Italic)
            && (expected.FillColor is null || expected.FillColor == actual.FillColor)
            && (expected.NumberFormat is null || expected.NumberFormat == actual.NumberFormat);
    public bool CanVerify(ToolCall call, string rawToolOutput) => StructuredOfficeCapabilityCatalog.All.Any(item => item.Name == call.Name && item.Access == AgentToolAccess.Mutating);
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context, ToolCall call, string rawToolOutput, CancellationToken cancellationToken)
        => Task.FromResult(_reports.TryGetValue(call.Id, out var report) ? report : new(DomainId, false, [], "No authorized Office mutation/readback was completed."));
    public void Dispose() { _client?.Dispose(); _gate.Dispose(); }
}
