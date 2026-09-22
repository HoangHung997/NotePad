using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2Notes.Core;

namespace H2Notes.Avalonia;

/// <summary>Offline copy-and-run check; never reads settings, credentials or project data.</summary>
internal static class PortablePackageDiagnostics
{
    internal static async Task<int> VerifyAsync(string output)
    {
        if (Directory.Exists(output)) throw new IOException("Choose a new verification directory.");
        Directory.CreateDirectory(output);
        try
        {
            var python = Path.Combine(AppContext.BaseDirectory, "python");
            if (WindowsPythonSandbox.RuntimeRoot != python || WindowsPythonSandbox.RuntimeProblem(python) is { })
                throw new IOException("Bundled Agent Python is missing or incomplete.");
            var helpers = await H2ProductionDiagnostics.VerifyHelpersAsync(Path.Combine(output, "helpers.json"));
            var agent = await LabEnvironment.Verify(Path.Combine(output, "agent"));
            var ocrRoot = Path.Combine(AppContext.BaseDirectory, "ocr-runtime");
            PortableOcrRuntime.EnsureRelocated(ocrRoot);
            var ocr = PortableOcrPackager.Inspect(ocrRoot);
            var passed = helpers == 0 && agent == 0 && ocr.Ready;
            File.WriteAllText(Path.Combine(output, "portable-check.json"), JsonSerializer.Serialize(new
            {
                passed, application = AppContext.BaseDirectory, bundledPython = python,
                helpersPassed = helpers == 0, documentsPythonPassed = agent == 0, ocr,
                limits = "OCR models are inventoried, not inferred by this command. No LLM, live Office document, personal data or second-machine check."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "portable-check.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            return 1;
        }
    }
}
