using H2AgentLab.Tools;

namespace H2AgentLab.Verification;

public sealed record PythonArtifactExpectation(
    string Path,
    string? Sha256 = null,
    long? MinBytes = null);

public sealed record PythonAssertionResult(
    string AssertionId,
    bool Passed,
    string EvidenceId,
    string Detail);

public sealed record PythonVerificationExpectation(
    IReadOnlyList<PythonArtifactExpectation> RequiredArtifacts,
    IReadOnlyList<PythonAssertionResult> Assertions)
{
    public PythonVerificationExpectation(
        IEnumerable<PythonArtifactExpectation>? requiredArtifacts = null,
        IEnumerable<PythonAssertionResult>? assertions = null)
        : this(
            (requiredArtifacts ?? Array.Empty<PythonArtifactExpectation>()).ToArray(),
            (assertions ?? Array.Empty<PythonAssertionResult>()).ToArray())
    {
        if (RequiredArtifacts.Count == 0 && Assertions.Count == 0)
            throw new ArgumentException(
                "Python verification requires at least one artifact expectation or explicit task assertion.");

        foreach (var artifact in RequiredArtifacts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Path);
            if (artifact.Sha256 is { Length: > 0 } sha
                && (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))))
                throw new ArgumentException("Expected Python artifact SHA-256 is invalid.", nameof(requiredArtifacts));
            if (artifact.MinBytes is < 0)
                throw new ArgumentOutOfRangeException(nameof(requiredArtifacts));
        }

        foreach (var assertion in Assertions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assertion.AssertionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(assertion.EvidenceId);
        }
    }
}

public static class PythonResultVerifier
{
    public const string VerifierId = "python-result";
    public const string ExitCriterionId = "python.exit";
    public const string ArtifactCriterionId = "python.artifacts";
    public const string AssertionCriterionId = "python.assertions";

    public static VerificationReport Verify(
        global::H2AgentLab.ScriptRunEvidence run,
        PythonVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expectation);

        var exitPassed = run.ExitCode == 0;
        var exit = Result(
            ExitCriterionId,
            exitPassed,
            [run.EvidenceId],
            exitPassed ? null : $"Python exited with code {run.ExitCode}.");

        var artifactFailures = new List<string>();
        var artifactEvidence = new List<string>();
        foreach (var expected in expectation.RequiredArtifacts)
        {
            var artifact = run.Artifacts.SingleOrDefault(x =>
                string.Equals(x.Path, expected.Path, StringComparison.Ordinal));
            if (artifact is null)
            {
                artifactFailures.Add(expected.Path + ": missing");
                continue;
            }

            artifactEvidence.Add(artifact.EvidenceId);
            if (!string.IsNullOrWhiteSpace(expected.Sha256)
                && !string.Equals(
                    artifact.Sha256,
                    expected.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                artifactFailures.Add(expected.Path + ": SHA-256 mismatch.");
            if (expected.MinBytes is long min && artifact.Bytes < min)
                artifactFailures.Add(expected.Path + $": expected >= {min} bytes, actual {artifact.Bytes}.");
        }

        var artifacts = Result(
            ArtifactCriterionId,
            artifactFailures.Count == 0,
            artifactEvidence,
            artifactFailures.Count == 0 ? null : string.Join("; ", artifactFailures));

        var assertionFailures = expectation.Assertions
            .Where(x => !x.Passed)
            .Select(x => x.AssertionId + ": " + x.Detail)
            .ToArray();
        var assertions = Result(
            AssertionCriterionId,
            assertionFailures.Length == 0,
            expectation.Assertions.Select(x => x.EvidenceId),
            assertionFailures.Length == 0 ? null : string.Join("; ", assertionFailures));

        return new VerificationReport(
            VerifierId,
            [exit, artifacts, assertions],
            new[] { run.EvidenceId }
                .Concat(run.Artifacts.Select(x => x.EvidenceId))
                .Concat(expectation.Assertions.Select(x => x.EvidenceId)));
    }

    private static VerificationCriterionResult Result(
        string id,
        bool passed,
        IEnumerable<string> evidence,
        string? failure)
        => passed
            ? new(id, VerificationCriterionStatus.Passed, evidence)
            : new(
                id,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(id, failure ?? "Python verification failed.", evidence));
}

public enum PythonFallbackDecision
{
    StructuredOffice = 0,
    PythonEscapeHatch = 1,
    Unsupported = 2
}

public static class PythonFallbackRouter
{
    public static PythonFallbackDecision Choose(
        string query,
        bool structuredOfficeSupportsTask,
        bool pythonAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (structuredOfficeSupportsTask)
            return PythonFallbackDecision.StructuredOffice;

        if (pythonAvailable)
            return PythonFallbackDecision.PythonEscapeHatch;

        return PythonFallbackDecision.Unsupported;
    }

    public static bool ShouldExposeRunPython(
        string query,
        IReadOnlyList<ToolSearchResult> selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(selected);

        if (DocumentToolPreference.IsExplicitPythonIntent(query))
            return true;

        var hasRunPython = selected.Any(x => x.Descriptor.Name == "run_python");
        if (!hasRunPython) return false;

        return !selected.Any(x =>
            x.Descriptor.Name != "run_python"
            && x.Descriptor.Namespace.Name != "python");
    }
}
