using H2Notes.Core;

namespace H2AgentLab;

/// <summary>
/// Small migration guard that keeps Agent Lab 2.0 development incremental.
/// It intentionally does not replace the v1 suites; it proves the Lab still has
/// a live compile/runtime dependency on H2Notes.Core and that the preserved v1
/// deterministic suites remain callable while new v2 components are introduced.
/// </summary>
public static class V2ArchitectureTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var failed = 0;

        void Test(string name, Action action)
        {
            try
            {
                action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        Test("Agent Lab keeps direct H2Notes.Core capability types", () =>
        {
            var coreAssembly = typeof(AiProfile).Assembly;
            if (typeof(AiDocuments).Assembly != coreAssembly
                || typeof(AiModelCapabilities).Assembly != coreAssembly
                || typeof(AiPdfProcessor).Assembly != coreAssembly)
                throw new InvalidOperationException("Expected shared H2Notes.Core capabilities are not from the same referenced assembly.");
            if (!string.Equals(coreAssembly.GetName().Name, "H2Notes.Core", StringComparison.Ordinal))
                throw new InvalidOperationException("H2AgentLab no longer resolves H2Notes.Core directly.");
        });

        Test("Preserved v1 deterministic suites remain callable", () =>
        {
            Func<string[], Task<int>> general = LabTests.Run;
            Func<string, Task<int>> recovery = RecoveryTests.Run;
            Func<string, Task<int>> skills = SkillTests.Run;
            _ = general ?? throw new InvalidOperationException("LabTests.Run missing.");
            _ = recovery ?? throw new InvalidOperationException("RecoveryTests.Run missing.");
            _ = skills ?? throw new InvalidOperationException("SkillTests.Run missing.");
        });

        Test("Existing execution and safety primitives remain present", () =>
        {
            _ = typeof(AgentRunner);
            _ = typeof(AgentTools);
            _ = typeof(SafeWorkspace);
            _ = typeof(ScriptWorkspace);
            _ = typeof(WindowsPythonSandbox);
            _ = typeof(SkillCatalog);
            _ = typeof(RecoveryPolicy);
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed. This is a migration guard only; run the full v1 suites separately.");
        var report = Path.Combine(root, "v2-architecture-guard.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}
