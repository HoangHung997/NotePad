using System.Text.Json;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Tasking;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public sealed record AgentRuntimeDomainVerification(
    string DomainId,
    bool Passed,
    IReadOnlyList<string> EvidenceIds,
    string? FailureMessage = null);

public interface IAgentRuntimeDomainVerifier
{
    string DomainId { get; }

    bool CanVerify(
        global::H2AgentLab.ToolCall call,
        string rawToolOutput);

    Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        global::H2AgentLab.ToolCall call,
        string rawToolOutput,
        CancellationToken cancellationToken);
}

public sealed class AgentRuntimeDomainVerifierRouter : IAgentRuntimeVerifier
{
    public const string VerifierId = "runtime-domain-router";
    public const string MutationCriterionId = "runtime.mutation-verified";

    private readonly IReadOnlyList<IAgentRuntimeDomainVerifier> _verifiers;

    public AgentRuntimeDomainVerifierRouter(
        IEnumerable<IAgentRuntimeDomainVerifier> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        _verifiers = verifiers.ToArray();
        if (_verifiers.Count == 0)
            throw new ArgumentException(
                "At least one runtime domain verifier is required.",
                nameof(verifiers));
        if (_verifiers.GroupBy(x => x.DomainId, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new ArgumentException(
                "Runtime domain verifier IDs must be unique.",
                nameof(verifiers));
    }

    public async Task<VerificationReport?> VerifyAsync(
        AgentRuntimeVerificationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var outcomes = new List<AgentRuntimeDomainVerification>();
        var coverage = new List<VerificationCallCoverage>();
        foreach (var call in context.Calls)
        {
            if (!context.RawToolOutputs.TryGetValue(call.Id, out var raw))
                continue;

            if (context.Results.Any(r => r.ToolCallId == call.Id && r.IsError))
            {
                if (context.MutationCallIds.Contains(call.Id))
                    outcomes.Add(new("tool-execution", false, [], call.Name + " failed; correct the original error before verifying its output."));
                continue;
            }

            var beforeCount = outcomes.Count;
            var selected = _verifiers
                .Where(x => x.CanVerify(call, raw))
                .ToArray();
            if (selected.Length == 0 && context.MutationCallIds.Contains(call.Id))
                outcomes.Add(new("unverified-tool", false, [],
                    "No deterministic verifier is registered for " + call.Name + "."));
            foreach (var verifier in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await verifier.VerifyAsync(
                    context,
                    call,
                    raw,
                    cancellationToken).ConfigureAwait(false));
            }
            if (outcomes.Count > beforeCount && call.Invocation is not null)
            {
                var observed = context.Results.Single(r => r.ToolCallId == call.Id).Outcome;
                // Exact target and requested postcondition. Freshness tokens are not desired
                // output; other arguments stay part of the proof identity.
                var postcondition = JsonSerializer.Serialize(call.Arguments.EnumerateObject()
                    .Where(p => p.Name is not ("state_token" or "expectedHash" or "expected_hash"))
                    .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value));
                var postId = "post-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(call.Name + "\n" + postcondition))).ToLowerInvariant();
                var evidenceIds = observed?.EvidenceRefs.Count > 0 ? observed.EvidenceRefs
                    : context.Evidence.Select(e => e.ReferenceId).ToArray();
                coverage.Add(new(call.Invocation.InvocationId, MutationCriterionId,
                    AgentCompletionAssessment.Target(call, observed?.Resource?.Id), postId,
                    outcomes.Skip(beforeCount).All(o => o.Passed) ? VerificationCriterionStatus.Passed : VerificationCriterionStatus.Failed,
                    evidenceIds));
            }
        }

        if (outcomes.Count == 0)
            return null;

        var evidence = outcomes
            .SelectMany(x => x.EvidenceIds)
            .Concat(context.Evidence.Select(x => x.ReferenceId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();

        var failures = outcomes
            .Where(x => !x.Passed)
            .Select(x => x.DomainId + ": " + (x.FailureMessage ?? "verification failed"))
            .ToArray();

        var result = failures.Length == 0
            ? new VerificationCriterionResult(
                MutationCriterionId,
                VerificationCriterionStatus.Passed,
                evidence)
            : new VerificationCriterionResult(
                MutationCriterionId,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(
                    MutationCriterionId,
                    string.Join("; ", failures),
                    evidence));

        return new VerificationReport(
            VerifierId,
            [result],
            evidence) { CallCoverage = coverage.ToArray() };
    }
}

public sealed class FileRuntimeDomainVerifier : IAgentRuntimeDomainVerifier
{
    private readonly global::H2AgentLab.SafeWorkspace _workspace;

    public FileRuntimeDomainVerifier(global::H2AgentLab.SafeWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public string DomainId => "file";

    public bool CanVerify(global::H2AgentLab.ToolCall call, string rawToolOutput)
        => call.Name is "write_text" or "publish_artifact";

    public Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        global::H2AgentLab.ToolCall call,
        string rawToolOutput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pathArgument = call.Name == "publish_artifact"
            ? "destination"
            : "path";
        var expectedArgument = call.Name == "publish_artifact"
            ? "expected_hash"
            : "expectedHash";

        var path = RequiredString(call.Arguments, pathArgument);
        var expectedBefore = OptionalString(call.Arguments, expectedArgument);
        var afterBytes = _workspace.Read(path);
        var actualAfter = global::H2AgentLab.SafeWorkspace.Hash(afterBytes).ToLowerInvariant();

        using var resultJson = JsonDocument.Parse(rawToolOutput);
        var reportedAfter = RequiredString(resultJson.RootElement, "sha256")
            .ToLowerInvariant();

        var before = string.IsNullOrWhiteSpace(expectedBefore)
            ? Array.Empty<FileVerificationEntry>()
            : [new FileVerificationEntry(path, expectedBefore)];
        var after = new[]
        {
            new FileVerificationEntry(path, actualAfter)
        };

        var report = FileScopeVerifier.Verify(
            before,
            after,
            new FileVerificationExpectation(
                expectedChangedPaths: [path],
                expectedHashes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [path] = reportedAfter
                },
                allowedOutputPaths: [path]));

        return Task.FromResult(ToOutcome(report));
    }

    private AgentRuntimeDomainVerification ToOutcome(VerificationReport report)
        => new(
            DomainId,
            report.Passed,
            report.ReportEvidenceIds
                .Concat(report.Criteria.SelectMany(x => x.EvidenceIds))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            report.Passed
                ? null
                : string.Join("; ", report.Failures.Select(x => x.Message)));

    private static string RequiredString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException(
                $"Runtime file verifier requires string '{name}'.");
        return value.GetString()!.Trim();
    }

    private static string OptionalString(JsonElement node, string name)
        => node.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? ""
                : "";
}

public sealed class PythonRuntimeDomainVerifier : IAgentRuntimeDomainVerifier
{
    private readonly global::H2AgentLab.ScriptWorkspace _scripts;

    public PythonRuntimeDomainVerifier(
        global::H2AgentLab.SafeWorkspace workspace,
        string stateRoot)
    {
        _scripts = new global::H2AgentLab.ScriptWorkspace(
            workspace ?? throw new ArgumentNullException(nameof(workspace)),
            stateRoot ?? throw new ArgumentNullException(nameof(stateRoot)),
            (_, _) => Task.FromResult(false));
    }

    public string DomainId => "python";

    public bool CanVerify(global::H2AgentLab.ToolCall call, string rawToolOutput)
        => call.Name == "run_python";

    public Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        global::H2AgentLab.ToolCall call,
        string rawToolOutput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var json = JsonDocument.Parse(rawToolOutput);
        var root = json.RootElement;
        if ((root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            || (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False))
            return Task.FromResult(new AgentRuntimeDomainVerification(DomainId, false, [],
                "Python did not complete. Inspect the original tool error; no successful run was verified."));
        var runId = RequiredString(root, "runId");
        var run = _scripts.Evidence(runId);
        foreach (var artifact in run.Artifacts)
            _ = _scripts.Read(runId, artifact.Path);

        var artifacts = run.Artifacts
            .Select(x => new PythonArtifactExpectation(
                x.Path,
                x.Sha256,
                x.Bytes > 0 ? 1 : 0))
            .ToArray();

        var originalUnchanged = root.TryGetProperty(
                "originalFilesChanged",
                out var changed)
            && changed.ValueKind is JsonValueKind.True or JsonValueKind.False
            && !changed.GetBoolean();

        var assertions = new[]
        {
            new PythonAssertionResult(
                "original-files-unchanged",
                originalUnchanged,
                run.EvidenceId,
                originalUnchanged
                    ? "staged Python run preserved original workspace files"
                    : "Python result did not prove original workspace files were unchanged")
        };

        var report = PythonResultVerifier.Verify(
            run,
            new PythonVerificationExpectation(
                requiredArtifacts: artifacts,
                assertions: assertions));

        var evidence = new[] { run.EvidenceId }
            .Concat(run.Artifacts.Select(x => x.EvidenceId))
            .Concat(report.ReportEvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new AgentRuntimeDomainVerification(
            DomainId,
            report.Passed,
            evidence,
            report.Passed
                ? null
                : string.Join("; ", report.Failures.Select(x => x.Message))));
    }

    private static string RequiredString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException(
                $"Runtime Python verifier requires string '{name}'.");
        return value.GetString()!.Trim();
    }
}

public sealed class StructuredOfficeRuntimeDomainVerifier : IAgentRuntimeDomainVerifier
{
    public string DomainId => "office-live";

    public bool CanVerify(global::H2AgentLab.ToolCall call, string rawToolOutput)
        => call.Name is
            "word.patch"
            or "word.replace_range"
            or "word.apply_format"
            or "excel.patch"
            or "excel.write_range"
            or "excel.set_formula"
            or "excel.apply_format";

    public Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        global::H2AgentLab.ToolCall call,
        string rawToolOutput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return call.Name.StartsWith("word.", StringComparison.Ordinal)
            ? Task.FromResult(VerifyWord(rawToolOutput))
            : Task.FromResult(VerifyExcel(call, rawToolOutput));
    }

    private AgentRuntimeDomainVerification VerifyWord(string rawToolOutput)
    {
        var patch = JsonSerializer.Deserialize<WordPatchResult>(
            rawToolOutput,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException(
                "Structured Word verifier could not parse patch result.");

        var expected = patch.ChangedParagraphs
            .Select(index =>
            {
                if (index < 0 || index >= patch.After.Paragraphs.Count)
                    throw new InvalidOperationException(
                        "Structured Word patch reported an invalid changed paragraph index.");
                return new LiveWordExpectedParagraph(
                    index,
                    patch.After.Paragraphs[index]);
            })
            .ToArray();

        var report = LiveWordVerifier.Verify(
            patch.Before,
            patch.After,
            new LiveWordVerificationExpectation(expected));
        return ToOutcome(report);
    }

    private AgentRuntimeDomainVerification VerifyExcel(
        global::H2AgentLab.ToolCall call,
        string rawToolOutput)
    {
        var patch = JsonSerializer.Deserialize<ExcelPatchResult>(
            rawToolOutput,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException(
                "Structured Excel verifier could not parse patch result.");

        var sheetName = OptionalString(call.Arguments, "sheetName");
        if (string.IsNullOrWhiteSpace(sheetName))
            sheetName = OptionalString(call.Arguments, "SheetName");
        if (string.IsNullOrWhiteSpace(sheetName))
            sheetName = patch.After.ActiveSheet;

        var sheet = patch.After.Sheets.SingleOrDefault(x =>
            string.Equals(x.Name, sheetName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Structured Excel verifier could not identify changed sheet.");

        var expected = patch.ChangedCells
            .Select(address =>
            {
                var cell = sheet.Cells.SingleOrDefault(x =>
                    string.Equals(x.Address, address, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"Structured Excel patch reported missing changed cell '{address}'.");
                return new LiveExcelExpectedCell(
                    sheet.Name,
                    address,
                    cell);
            })
            .ToArray();

        var report = LiveExcelVerifier.Verify(
            patch.Before,
            patch.After,
            new LiveExcelVerificationExpectation(expected));
        return ToOutcome(report);
    }

    private AgentRuntimeDomainVerification ToOutcome(VerificationReport report)
        => new(
            DomainId,
            report.Passed,
            report.ReportEvidenceIds
                .Concat(report.Criteria.SelectMany(x => x.EvidenceIds))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            report.Passed
                ? null
                : string.Join("; ", report.Failures.Select(x => x.Message)));

    private static string OptionalString(JsonElement node, string name)
        => node.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? ""
                : "";
}
