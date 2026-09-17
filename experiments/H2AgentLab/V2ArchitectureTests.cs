using H2AgentLab.Prompting;
using H2AgentLab.Transport;
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
                || typeof(AiProfileSnapshot).Assembly != coreAssembly
                || typeof(AiPdfProcessor).Assembly != coreAssembly)
                throw new InvalidOperationException("Expected shared H2Notes.Core capabilities are not from the same referenced assembly.");
            if (!string.Equals(coreAssembly.GetName().Name, "H2Notes.Core", StringComparison.Ordinal))
                throw new InvalidOperationException("H2AgentLab no longer resolves H2Notes.Core directly.");
        });

        Test("Shared AI snapshot and endpoint capability checks stay in H2 Core", () =>
        {
            var original = new AiProfile
            {
                Id = Guid.NewGuid(),
                Name = "official-openai",
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5",
                TimeoutSeconds = 321,
                WaitForCompletion = false,
                RequestReasoningSummary = true,
                ReasoningEffort = "high"
            };
            var snapshot = AiProfileSnapshot.Create(original);
            original.Model = "mutated-after-snapshot";
            original.ReasoningEffort = "low";

            if (snapshot.Model != "gpt-5" || snapshot.ReasoningEffort != "high"
                || snapshot.TimeoutSeconds != 321 || snapshot.WaitForCompletion)
                throw new InvalidOperationException("Core AI profile snapshot did not preserve request-local settings.");
            if (!AiModelCapabilities.IsOfficialOpenAi(snapshot))
                throw new InvalidOperationException("Official OpenAI endpoint was not recognized by H2 Core.");
            if (AiModelCapabilities.ResolveReasoningEffort(snapshot) != "high")
                throw new InvalidOperationException("Core reasoning capability resolution changed unexpectedly.");

            var compatible = AiProfileSnapshot.Create(snapshot);
            compatible.BaseUrl = "https://example.test/v1";
            if (AiModelCapabilities.IsOfficialOpenAi(compatible)
                || AiModelCapabilities.GetReasoningOptions(compatible).Count != 0)
                throw new InvalidOperationException("Compatible endpoint inherited official OpenAI capability privileges.");
        });

        Test("V2 prompt keeps stable prefix ahead of all runtime context", () =>
        {
            const string sessionSentinel = "SESSION_JOURNAL_2026-09-18T01:23:45+07:00";
            const string workspaceSentinel = "WORKSPACE_MUTABLE_STATE_42";
            const string timeSentinel = "CURRENT_TIME_2026-09-18T01:23:45+07:00";
            const string userSentinel = "USER_INPUT_SENTINEL";

            var layout = AgentPromptLayout.Create(
                new AgentPromptStablePrefix(
                    BasePolicy: "BASE_POLICY_STABLE",
                    SecurityPolicy: "SECURITY_POLICY_STABLE",
                    ModelPolicy: "MODEL_POLICY_STABLE",
                    ToolNamespaceMetadata: "TOOL_NAMESPACE_STABLE"),
                new AgentPromptRuntimeContext(
                    TaskContract: "TASK_CONTRACT_DYNAMIC",
                    WorkingState: sessionSentinel + "\n" + workspaceSentinel,
                    LiveEnvironment: timeSentinel),
                userSentinel);

            if (layout.CacheBoundaryIndex != layout.StablePrefix.Count || layout.CacheBoundaryIndex != 4)
                throw new InvalidOperationException("Stable prompt cache boundary moved or is ambiguous.");
            if (!layout.Messages.Take(layout.CacheBoundaryIndex).SequenceEqual(layout.StablePrefix))
                throw new InvalidOperationException("Stable prefix is not the first contiguous message block.");

            var stableText = string.Join("\n", layout.StablePrefix.Select(x => x.Content));
            if (stableText.Contains(sessionSentinel, StringComparison.Ordinal)
                || stableText.Contains(workspaceSentinel, StringComparison.Ordinal)
                || stableText.Contains(timeSentinel, StringComparison.Ordinal)
                || stableText.Contains(userSentinel, StringComparison.Ordinal))
                throw new InvalidOperationException("Dynamic time/session/workspace/user data leaked before the stable cache boundary.");

            var dynamicText = string.Join("\n", layout.DynamicSuffix.Select(x => x.Content));
            if (!dynamicText.Contains(sessionSentinel, StringComparison.Ordinal)
                || !dynamicText.Contains(workspaceSentinel, StringComparison.Ordinal)
                || !dynamicText.Contains(timeSentinel, StringComparison.Ordinal)
                || !dynamicText.Contains(userSentinel, StringComparison.Ordinal))
                throw new InvalidOperationException("Runtime context was not preserved after the stable prefix.");
            if (layout.Messages[^1].Role != AgentTransportMessageRole.User
                || layout.Messages[^1].Content != userSentinel)
                throw new InvalidOperationException("User input must remain the final dynamic message.");
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
