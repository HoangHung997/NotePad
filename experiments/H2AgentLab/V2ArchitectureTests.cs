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
                    Versions: AgentVersions.Current,
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

        Test("V2 policy safety and toolset versions are validated out-of-band metadata", () =>
        {
            var versions = AgentVersions.Current;
            if (versions.AgentPolicyVersion != AgentVersions.AgentPolicy
                || versions.SafetyPolicyVersion != AgentVersions.SafetyPolicy
                || versions.ToolsetVersion != AgentVersions.Toolset)
                throw new InvalidOperationException("Current v2 version identifiers are inconsistent.");

            var layout = AgentPromptLayout.Create(
                new AgentPromptStablePrefix(versions, "base", "security", "model", "tools"),
                new AgentPromptRuntimeContext(),
                "user");
            if (layout.Versions != versions)
                throw new InvalidOperationException("Prompt layout lost its policy/toolset version metadata.");

            var promptText = string.Join("\n", layout.Messages.Select(x => x.Content));
            if (promptText.Contains(versions.AgentPolicyVersion, StringComparison.Ordinal)
                || promptText.Contains(versions.SafetyPolicyVersion, StringComparison.Ordinal)
                || promptText.Contains(versions.ToolsetVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Version metadata was injected into model prompt text.");

            try
            {
                _ = new AgentVersionIdentifiers("policy ok", "safety-v1", "tools-v1");
                throw new InvalidOperationException("Unsafe version label was accepted.");
            }
            catch (ArgumentException) { }
        });

        Test("Prompt cache identity ignores runtime context and non-cache request controls", () =>
        {
            var stable = new AgentPromptStablePrefix(AgentVersions.Current, "base", "security", "model-policy", "tool-namespaces");
            var firstLayout = AgentPromptLayout.Create(stable,
                new AgentPromptRuntimeContext("task-a", "journal-a", "time-a"), "user-a");
            var secondLayout = AgentPromptLayout.Create(stable,
                new AgentPromptRuntimeContext("task-b", "journal-b", "time-b"), "user-b");
            var firstProfile = new AiProfile
            {
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5",
                Name = "profile-a",
                TimeoutSeconds = 10,
                WaitForCompletion = false,
                ReasoningEffort = "low"
            };
            var secondProfile = new AiProfile
            {
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1/",
                Model = "gpt-5",
                Name = "renamed-profile",
                TimeoutSeconds = 999,
                WaitForCompletion = true,
                RequestReasoningSummary = true,
                ReasoningEffort = "high"
            };
            var skillA = new AgentStableSkillHash("alpha", new string('a', 64));
            var skillB = new AgentStableSkillHash("beta", new string('b', 64));
            var first = AgentPromptCacheIdentityBuilder.Build(firstLayout, firstProfile, [skillB, skillA]);
            var second = AgentPromptCacheIdentityBuilder.Build(secondLayout, secondProfile, [skillA, skillB]);

            if (first.Key != second.Key || first.Sha256Hex != second.Sha256Hex)
                throw new InvalidOperationException("Dynamic context, profile display/runtime controls or skill enumeration order changed cache identity.");
            if (!first.Key.StartsWith(AgentPromptCacheIdentityBuilder.Scheme + "_", StringComparison.Ordinal)
                || first.Key.Length > AgentPromptCacheIdentityBuilder.MaxProviderCacheKeyCharacters
                || first.Sha256Hex.Length != 64)
                throw new InvalidOperationException("Prompt cache identity is not provider-safe or not a full SHA-256 digest.");
            if (first.StableSkillHashes.Select(x => x.SkillId).SequenceEqual(new[] { "alpha", "beta" }) is false)
                throw new InvalidOperationException("Stable skill hashes were not canonicalized by skill ID.");
        });

        Test("Prompt cache identity changes for every stable policy model toolset safety input", () =>
        {
            static AgentPromptLayout Layout(
                AgentVersionIdentifiers versions,
                string basePolicy = "base",
                string securityPolicy = "security",
                string modelPolicy = "model-policy",
                string toolMetadata = "tool-namespaces")
                => AgentPromptLayout.Create(
                    new AgentPromptStablePrefix(versions, basePolicy, securityPolicy, modelPolicy, toolMetadata),
                    new AgentPromptRuntimeContext("dynamic-task", "dynamic-state", "dynamic-time"),
                    "dynamic-user");

            static AiProfile Profile(AiProtocol protocol = AiProtocol.OpenAiResponses, string baseUrl = "https://api.openai.com/v1", string model = "gpt-5")
                => new() { Protocol = protocol, BaseUrl = baseUrl, Model = model };

            static string Key(AgentPromptLayout layout, AiProfile profile, params AgentStableSkillHash[] skills)
                => AgentPromptCacheIdentityBuilder.Build(layout, profile, skills).Key;

            var versions = AgentVersions.Current;
            var baseline = Key(Layout(versions), Profile(), new AgentStableSkillHash("alpha", new string('a', 64)));
            var changed = new[]
            {
                Key(Layout(versions, basePolicy: "base-2"), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions, securityPolicy: "security-2"), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions, modelPolicy: "model-policy-2"), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions, toolMetadata: "tool-namespaces-2"), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(new AgentVersionIdentifiers("agent-policy-v2.0.1", versions.SafetyPolicyVersion, versions.ToolsetVersion)), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(new AgentVersionIdentifiers(versions.AgentPolicyVersion, "safety-policy-v2.0.1", versions.ToolsetVersion)), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(new AgentVersionIdentifiers(versions.AgentPolicyVersion, versions.SafetyPolicyVersion, "toolset-bootstrap-v1.0.1")), Profile(), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions), Profile(model: "gpt-5-mini"), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions), Profile(AiProtocol.OpenAiChat), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions), Profile(baseUrl: "https://example.test/v1"), new AgentStableSkillHash("alpha", new string('a', 64))),
                Key(Layout(versions), Profile(), new AgentStableSkillHash("alpha", new string('c', 64)))
            };
            if (changed.Any(key => key == baseline))
                throw new InvalidOperationException("A stable cache input changed without invalidating prompt cache identity.");
            if (changed.Distinct(StringComparer.Ordinal).Count() != changed.Length)
                throw new InvalidOperationException("Distinct stable cache inputs unexpectedly collided in deterministic fixtures.");
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
