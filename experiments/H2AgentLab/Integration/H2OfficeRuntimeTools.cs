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
internal sealed class H2OfficeRuntimeTools : IAgentRuntimeDomainVerifier, IDisposable
{
    private readonly Func<bool> authorized;
    private readonly string outputRoot;
    private readonly H2AgentPermissionScope? scope;
    private readonly H2AgentTargetBindingPolicy _binding;
    private readonly H2AgentTargetIntent _intent;
    private readonly Action<H2AgentTargetResolution>? _targetObserved;
    private readonly Func<IOfficeSessionClient>? _clientFactory;
    private readonly Func<H2ActiveWorkContext, bool> _captureValidator;
    private IOfficeSessionClient? _client;
    private readonly Dictionary<H2ApplicationKind, H2AgentTargetResolution> _selected = new();
    private readonly Dictionary<string, H2AgentTargetResolution> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentRuntimeDomainVerification> _reports = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OfficeRejectedMutationRecovery _wordRecovery = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private IOfficeSessionClient Client => _client ??= _clientFactory is not null
        ? _clientFactory() ?? throw new ToolPreflightException("needs_configuration")
        : new OfficeHostClient(H2HelperLocator.Resolve("H2AgentLab.OfficeHost"), defaultTimeout: TimeSpan.FromSeconds(60));

    // Preserve the old constructor used by cardinality-only callers. It does NOT authorize
    // arbitrary global sessions; a selected workspace or explicit target is still required.
    public H2OfficeRuntimeTools(Func<bool> authorized, string outputRoot, H2AgentPermissionScope? scope,
        IReadOnlyList<H2AgentTargetPath>? projectTargets = null)
        : this(authorized, outputRoot, scope, projectTargets, null, H2AgentTargetIntent.OpenDocument, null, null, null) { }

    public H2OfficeRuntimeTools(Func<bool> authorized, string outputRoot, H2AgentPermissionScope? scope,
        IReadOnlyList<H2AgentTargetPath>? projectTargets, H2AgentTargetBindingPolicy? binding,
        H2AgentTargetIntent intent, Action<H2AgentTargetResolution>? targetObserved,
        Func<IOfficeSessionClient>? clientFactory, Func<H2ActiveWorkContext, bool>? captureValidator)
    {
        this.authorized = authorized; this.outputRoot = outputRoot; this.scope = scope;
        _binding = binding ?? new H2AgentTargetBindingPolicy(null, outputRoot, projectTargets);
        _intent = intent; _targetObserved = targetObserved; _clientFactory = clientFactory;
        _captureValidator = captureValidator ?? H2CapturedWindowIdentity.IsCurrent;
    }
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
            var description = capability.Description + " Discovery lists metadata only, with no usable state token. Get the host-bound document or an explicit session snapshot before any mutation. A model-supplied session cannot resolve ambiguous targets or override captured active/selection intent. Word replacement/format uses paragraph indexes; Excel patches use observed cell addresses.";
            if (name is "word.replace_range" or "word.insert_text")
                description += " Newlines in text create real Word paragraphs and inherit the original paragraph style/format. All paragraph indexes refer to the BEFORE snapshot, even when earlier replacements add paragraphs. To rewrite a CV, use newline-separated text on existing paragraphs, never invent new paragraph indexes. Mixed character formatting requires a separate explicit formatting operation. Read back the new snapshot before further edits.";
            registry.Register(new ToolDescriptor(name, new(capability.Namespace, "Structured live OfficeHost operations."), description,
                capability.Risk, capability.Access, false, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description,
                    parameters = new { type = "object", properties, required, additionalProperties = false } } }),
                new DelegatingToolExecutor("h2-office-host", ExecuteAsync), resourceScope: new(capability.ResourceScope, "observed-session"),
                serializationKey: "office-host", canProvideVerificationEvidence: true,
                preference: new("active-content", ToolInteractionFidelity.Structured),
                readiness: new(_clientFactory is not null ? ToolReadinessState.Ready : !OperatingSystem.IsWindows() ? ToolReadinessState.Unsupported
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
            var application = name.StartsWith("excel.", StringComparison.Ordinal) ? H2ApplicationKind.Excel : H2ApplicationKind.Word;
            var listing = name is "excel.list_workbooks" or "word.list_documents";
            var observed = await DiscoverAsync(application, ct).ConfigureAwait(false);
            H2AgentTargetResolution? target = null;
            if (listing)
            {
                // Show only in-scope metadata. Merely discovering a session does not bind it.
                var ids = observed.Resources.Where(item => _binding.IsCandidate(item, DateTime.UtcNow)
                    && ScopeContains(item.DocumentSessionId!, item.CanonicalPath))
                    .Select(item => item.DocumentSessionId).ToHashSet(StringComparer.Ordinal);
                var decision = Resolve(application, observed.Resources, null);
                result = observed.Discovery is ExcelDiscovery excel
                    ? new { Workbooks = excel.Workbooks.Where(item => ids.Contains(item.SessionId)).ToArray(),
                        ActiveSessionId = ids.Contains(excel.ActiveSessionId) ? excel.ActiveSessionId : null,
                        SelectedSessionId = decision.Resolved && ids.Contains(decision.Binding!.DocumentSessionId) ? decision.Binding.DocumentSessionId : null, TargetSelection = decision.Code }
                    : new { Documents = ((WordDiscovery)observed.Discovery).Documents.Where(item => ids.Contains(item.SessionId)).ToArray(),
                        ActiveSessionId = ids.Contains(((WordDiscovery)observed.Discovery).ActiveSessionId) ? ((WordDiscovery)observed.Discovery).ActiveSessionId : null,
                        SelectedSessionId = decision.Resolved && ids.Contains(decision.Binding!.DocumentSessionId) ? decision.Binding.DocumentSessionId : null, TargetSelection = decision.Code };
            }
            else
            {
                target = Resolve(application, observed.Resources, session.Length == 0 ? null : session);
                if (!target.Resolved) throw new ToolPreflightException(target.Code == "outside_resource_scope" ? "target_not_grounded" : target.Code);
                session = target.Binding!.DocumentSessionId!;
                ValidateScope(session, target.Binding.CanonicalPath); // execution permission remains separate
                _selected[application] = target;
                _pins[application + ":" + session] = target;
                _targetObserved?.Invoke(target);
                if (_intent == H2AgentTargetIntent.CapturedSelection)
                {
                    // Metadata discovery deliberately has no selection/content. Read ONLY the bound
                    // session, then compare the original UI precondition; never query another foreground.
                    if (application == H2ApplicationKind.Excel)
                    {
                        var view = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                        ValidateSnapshot(target, view.SessionId, view.FullName, view.ActiveSheet + "!" + view.SelectionAddress, false, true);
                    }
                    else
                    {
                        var view = await Client.SnapshotWordAsync(session, ct).ConfigureAwait(false);
                        ValidateSnapshot(target, view.SessionId, view.FullName, view.SelectionText, false, true);
                    }
                }
                if (name == "excel.get_active_workbook") result = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                else if (name == "word.get_active_document") result = await Client.SnapshotWordAsync(session, ct).ConfigureAwait(false);
            else if (name.EndsWith("save_copy", StringComparison.Ordinal))
            {
                RequireAuthorization();
                var destination = new SafeWorkspace(outputRoot, scope?.Mode == H2AgentPermissionMode.FullAccess
                    ? () => scope.HasFullAccessAt(DateTime.UtcNow) : null, groundedTarget: _binding.AllowsPath).Resolve(H2ProductionToolSession.Arg(call, "destination") ?? "");
                if (File.Exists(destination)) throw new IOException("Choose a new output file; overwriting is not permitted.");
                var request = new OfficeSaveCopyRequest(session, token, true, destination);
                var saved = name.StartsWith("excel.") ? await Client.SaveExcelCopyAsync(request, ct).ConfigureAwait(false)
                    : await Client.SaveWordCopyAsync(request, ct).ConfigureAwait(false);
                var actual = SafeWorkspace.Hash(await File.ReadAllBytesAsync(destination, ct).ConfigureAwait(false));
                if (saved.SessionId != session || !H2AgentTargetScope.PathComparer.Equals(saved.DestinationPath, destination)) throw new OfficeHostClientException("stale_resource", "Save-copy response changed source identity.");
                await RevalidateAsync(application, target, ct, true).ConfigureAwait(false);
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
                    var beforeWrite = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    ValidateSnapshot(target, beforeWrite.SessionId, beforeWrite.FullName, beforeWrite.ActiveSheet + "!" + beforeWrite.SelectionAddress, false,
                        _intent == H2AgentTargetIntent.CapturedSelection);
                    if (beforeWrite.StateToken != token) throw new ToolPreflightException("stale_resource");
                    var patch = await Client.PatchExcelAsync(new(session, token, true, sheetName, cells), ct).ConfigureAwait(false);
                    var after = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    ValidateSnapshot(target, patch.Before.SessionId, patch.Before.FullName, null, true);
                    ValidateSnapshot(target, after.SessionId, after.FullName, null, true);
                    await RevalidateAsync(application, target, ct, true).ConfigureAwait(false);
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
                    ValidateSnapshot(target, before.SessionId, before.FullName, before.ActiveSheet + "!" + before.SelectionAddress, false);
                    var calculated = await Client.RecalculateExcelAsync(new(session, token, true), ct).ConfigureAwait(false);
                    var after = await Client.SnapshotExcelAsync(session, ct).ConfigureAwait(false);
                    ValidateSnapshot(target, after.SessionId, after.FullName, null, true);
                    await RevalidateAsync(application, target, ct, true).ConfigureAwait(false);
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
                ValidateSnapshot(target, beforeWord.SessionId, beforeWord.FullName, beforeWord.SelectionText, false);
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
                ValidateSnapshot(target, patch.Before.SessionId, patch.Before.FullName, null, true);
                ValidateSnapshot(target, after.SessionId, after.FullName, null, true);
                await RevalidateAsync(application, target, ct, true).ConfigureAwait(false);
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
                ValidateSnapshot(target, snapshot.SessionId, snapshot.FullName, snapshot.SelectionText, false);
                result = name == "word.find_text" ? (object)snapshot.Paragraphs.Where(p => p.Text.Contains(H2ProductionToolSession.Arg(call, "query") ?? "", StringComparison.OrdinalIgnoreCase)).ToArray() : snapshot;
            }
                // A mismatched read response is not forwarded to the model. After any possible
                // mutation a mismatch is an execution error with Unknown effect, never a preflight reject.
                var mutating = StructuredOfficeCapabilityCatalog.All.Any(item => item.Name == name && item.Access == AgentToolAccess.Mutating);
                if (result is ExcelLiveSnapshot x) ValidateSnapshot(target, x.SessionId, x.FullName, x.ActiveSheet + "!" + x.SelectionAddress, mutating);
                else if (result is WordLiveSnapshot w) ValidateSnapshot(target, w.SessionId, w.FullName, w.SelectionText, mutating);
                else if (result is WordLanguageEvidenceResult language && language.SessionId != session)
                    throw new ToolPreflightException("stale_resource");
                await RevalidateAsync(application, target, ct, mutating).ConfigureAwait(false);
            }
            return JsonSerializer.Serialize(result);
        }
        catch { _reports.TryRemove(call.Id, out _); throw; }
        finally { _gate.Release(); }
    }

    private sealed record DiscoveryObservation(object Discovery, IReadOnlyList<H2AgentResourceBinding> Resources);

    private async Task<DiscoveryObservation> DiscoverAsync(H2ApplicationKind application, CancellationToken ct)
    {
        if (application == H2ApplicationKind.Excel)
        {
            var discovery = await Client.DiscoverExcelAsync(ct).ConfigureAwait(false);
            return new(discovery, discovery.Workbooks.Select(item => Observe(application, item.SessionId, item.FullName, !item.Saved)).ToArray());
        }
        var word = await Client.DiscoverWordAsync(ct).ConfigureAwait(false);
        return new(word, word.Documents.Select(item => Observe(application, item.SessionId, item.FullName, !item.Saved)).ToArray());
    }

    private H2AgentResourceBinding Observe(H2ApplicationKind app, string session, string path, bool dirty)
        => H2AgentResourceBinding.FromLiveObservation(app, "office-host", session, path, DateTime.UtcNow,
            providerInstanceId: Client.InstanceIdentity, dirty: dirty);

    private H2AgentTargetResolution Resolve(H2ApplicationKind application, IReadOnlyList<H2AgentResourceBinding> observed, string? session)
    {
        // Captured selection is checked on the one selected native snapshot, not on a blank metadata page.
        var intent = _intent == H2AgentTargetIntent.CapturedSelection ? H2AgentTargetIntent.CapturedActive : _intent;
        H2AgentTargetResolution result;
        var alreadyBound = session is null ? _selected.TryGetValue(application, out var prior)
            : _pins.TryGetValue(application + ":" + session, out prior);
        if (alreadyBound)
        {
            var matches = observed.Where(item => item.DocumentSessionId == prior.Binding!.DocumentSessionId).ToArray();
            result = matches.Length == 1 && prior.Binding!.MatchesObservation(matches[0], requireContentVersion: false)
                ? prior : new(null, matches.Length > 1 ? "ambiguous_target" : "stale_resource", false, "bound-session");
        }
        else result = _binding.ResolveOpen(application, observed, intent, DateTime.UtcNow, session);
        if (result.Resolved && (result.Source == "captured-active" || intent is H2AgentTargetIntent.CapturedActive or H2AgentTargetIntent.CapturedSelection))
        {
            var capture = _binding.CapturedContext;
            if (capture is null || DateTime.UtcNow - capture.CapturedUtc > H2AgentTargetBindingPolicy.MaxCaptureAge || !_captureValidator(capture))
                return new(null, "stale_resource", false, "captured-window");
        }
        return result;
    }

    private bool ScopeContains(string session, string? path)
    {
        if (scope?.ScopeKind is not (H2AgentResourceScopeKind.Document or H2AgentResourceScopeKind.Session)) return true;
        if (session != scope.DocumentSessionId) return false;
        if (string.IsNullOrEmpty(scope.DocumentPath)) return true;
        if (H2AgentTargetScope.TryNormalize(scope.DocumentPath, out var selectedPath))
            return H2AgentTargetScope.PathComparer.Equals(selectedPath, path);
        // Unsaved Session scope is identified by the live session, never by a guessed disk path.
        return scope.ScopeKind == H2AgentResourceScopeKind.Session && path is null
            && scope.DocumentPath.IndexOfAny(['/', '\\', ':']) < 0;
    }

    private void ValidateScope(string session, string? path)
    {
        if (!ScopeContains(session, path)) throw new ToolPreflightException("outside_resource_scope");
    }

    private void ValidateSnapshot(H2AgentTargetResolution target, string session, string path, string? selection,
        bool afterPossibleWrite, bool checkSelection = false)
    {
        var current = Observe(target.Binding!.ApplicationKind, session, path, false);
        var identityMatches = target.Binding.MatchesObservation(current, requireContentVersion: false);
        var capture = _binding.CapturedContext;
        var selectionMatches = !checkSelection || capture is not null && !string.IsNullOrWhiteSpace(capture.Selection)
            && capture.Selection != "!" && capture.Selection == selection;
        if (identityMatches && selectionMatches) return;
        if (afterPossibleWrite) throw new OfficeHostClientException("stale_resource", "Native readback no longer matches the bound resource; reconcile before another write.");
        throw new ToolPreflightException("stale_resource");
    }

    private async Task RevalidateAsync(H2ApplicationKind application, H2AgentTargetResolution target, CancellationToken ct, bool afterPossibleWrite)
    {
        var observation = await DiscoverAsync(application, ct).ConfigureAwait(false);
        var current = Resolve(application, observation.Resources, target.Binding!.DocumentSessionId);
        if (current.Resolved && target.Binding.MatchesObservation(current.Binding!, requireContentVersion: false)) return;
        if (afterPossibleWrite) throw new OfficeHostClientException("stale_resource", "Resource identity changed after dispatch; effects need reconciliation.");
        throw new ToolPreflightException(current.Code);
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
