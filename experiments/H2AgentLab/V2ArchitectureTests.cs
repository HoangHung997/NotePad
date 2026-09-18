using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Documents;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2AgentLab.Tools;
using H2AgentLab.Verification;
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

        Test("AgentContextManager enforces explicit budgets and deterministic provenance", () =>
        {
            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 600,
                MaxTaskContractCharacters = 60,
                MaxCurrentStateCharacters = 100,
                MaxRecentTurnsCharacters = 160,
                MaxToolSummariesCharacters = 140,
                MaxCompactedHistoryCharacters = 100,
                MaxCharactersPerItem = 40,
                MaxRecentTurns = 2,
                MaxToolSummaries = 1
            };
            var turns = Enumerable.Range(0, 20)
                .Select(i => new AgentContextTurn(
                    $"turn-{i}",
                    i % 2 == 0 ? AgentTransportMessageRole.User : AgentTransportMessageRole.Assistant,
                    $"TURN-{i}-" + new string((char)('a' + (i % 20)), 120),
                    i,
                    i == 19 ? 1 : i == 18 ? 0.9 : 0.1))
                .ToArray();
            var tools = Enumerable.Range(0, 10)
                .Select(i => new AgentContextToolSummary(
                    $"tool-{i}",
                    "fixture_tool",
                    $"TOOL-{i}-" + new string((char)('k' + (i % 10)), 120),
                    i,
                    i == 3 ? 1 : 0.2))
                .ToArray();
            var input = new AgentContextInput(
                TaskContract: "TASK-" + new string('t', 500),
                CurrentState: "STATE-" + new string('s', 500),
                RecentTurns: turns,
                ToolSummaries: tools,
                CompactedHistory: "HISTORY-" + new string('h', 500));

            var first = new AgentContextManager(budget).Build(input);
            var reversed = new AgentContextManager(budget).Build(input with
            {
                RecentTurns = turns.Reverse().ToArray(),
                ToolSummaries = tools.Reverse().ToArray()
            });

            var runtimeLength = (first.RuntimeContext.TaskContract?.Length ?? 0)
                + (first.RuntimeContext.WorkingState?.Length ?? 0);
            if (first.Usage.TotalCharacters != runtimeLength || runtimeLength > budget.MaxTotalCharacters)
                throw new InvalidOperationException("Context total character budget was not enforced exactly.");
            if (first.Usage.TaskContractCharacters > budget.MaxTaskContractCharacters
                || first.Usage.CurrentStateCharacters > budget.MaxCurrentStateCharacters
                || first.Usage.RecentTurnsCharacters > budget.MaxRecentTurnsCharacters
                || first.Usage.ToolSummariesCharacters > budget.MaxToolSummariesCharacters
                || first.Usage.CompactedHistoryCharacters > budget.MaxCompactedHistoryCharacters)
                throw new InvalidOperationException("A context section exceeded its explicit budget.");
            if (!first.Usage.TaskContractTruncated || !first.Usage.CurrentStateTruncated || !first.Usage.CompactedHistoryTruncated)
                throw new InvalidOperationException("Context truncation evidence was not reported.");
            if (!first.RecentTurnSourceIds.SequenceEqual(new[] { "turn-18", "turn-19" })
                || !first.ToolSummarySourceIds.SequenceEqual(new[] { "tool-3" }))
                throw new InvalidOperationException("Context manager did not preserve deterministic relevant source provenance.");
            if (first.Usage.SelectedRecentTurns != 2 || first.Usage.DroppedRecentTurns != 18
                || first.Usage.SelectedToolSummaries != 1 || first.Usage.DroppedToolSummaries != 9)
                throw new InvalidOperationException("Context selected/dropped counts do not match the bounded selection.");
            var working = first.RuntimeContext.WorkingState ?? "";
            if (first.RecentTurnSourceIds.Any(id => !working.Contains($"[turn:{id}]", StringComparison.Ordinal))
                || first.ToolSummarySourceIds.Any(id => !working.Contains($"[tool:{id}]", StringComparison.Ordinal)))
                throw new InvalidOperationException("Selected context source IDs are not present in emitted runtime context.");
            if (first.RuntimeContext != reversed.RuntimeContext
                || !first.RecentTurnSourceIds.SequenceEqual(reversed.RecentTurnSourceIds)
                || !first.ToolSummarySourceIds.SequenceEqual(reversed.ToolSummarySourceIds))
                throw new InvalidOperationException("Context output depends on input enumeration order instead of relevance/sequence/source identity.");
        });

        Test("AgentContextManager automatically triggers compaction under long-thread pressure", () =>
        {
            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 1_000,
                MaxTaskContractCharacters = 100,
                MaxCurrentStateCharacters = 100,
                MaxRecentTurnsCharacters = 620,
                MaxToolSummariesCharacters = 80,
                MaxCompactedHistoryCharacters = 80,
                MaxCharactersPerItem = 80,
                MaxRecentTurns = 4,
                MaxToolSummaries = 1
            };
            var turns = Enumerable.Range(0, 500)
                .Select(i => new AgentContextTurn(
                    $"turn-{i}",
                    i % 2 == 0 ? AgentTransportMessageRole.User : AgentTransportMessageRole.Assistant,
                    $"TURN_{i:D3}_" + new string('x', 220),
                    i))
                .ToArray();

            var stressed = new AgentContextManager(budget).Build(new AgentContextInput(RecentTurns: turns));
            if (stressed.Usage.TotalCharacters > budget.MaxTotalCharacters
                || stressed.Pressure.ActiveCharacters != stressed.Usage.TotalCharacters)
                throw new InvalidOperationException("Long-thread active context exceeded or misreported its hard budget.");
            if (!stressed.Pressure.RequiresCompaction
                || stressed.Pressure.CandidateCharacters <= budget.MaxTotalCharacters)
                throw new InvalidOperationException("Long-thread pressure did not automatically request compaction.");
            if (!stressed.Pressure.Reasons.Contains("recent-turns-dropped", StringComparer.Ordinal)
                || !stressed.Pressure.Reasons.Contains("recent-turn-item-truncated", StringComparer.Ordinal))
                throw new InvalidOperationException("Compaction trigger did not explain dropped/truncated recent-turn pressure.");
            if (stressed.Usage.SelectedRecentTurns != 4 || stressed.Usage.DroppedRecentTurns != 496)
                throw new InvalidOperationException("Long-thread bounded selection counts are wrong.");
            if (!stressed.RecentTurnSourceIds.SequenceEqual(new[] { "turn-496", "turn-497", "turn-498", "turn-499" }))
                throw new InvalidOperationException("Long-thread context did not retain the newest equally relevant turns deterministically.");
            var active = stressed.RuntimeContext.WorkingState ?? "";
            if (active.Contains("TURN_000_", StringComparison.Ordinal))
                throw new InvalidOperationException("Old raw history leaked into bounded long-thread context.");

            var small = new AgentContextManager(budget).Build(new AgentContextInput(
                RecentTurns:
                [
                    new AgentContextTurn("small-1", AgentTransportMessageRole.User, "short question", 1),
                    new AgentContextTurn("small-2", AgentTransportMessageRole.Assistant, "short answer", 2)
                ]));
            if (small.Pressure.RequiresCompaction || small.Pressure.Reasons.Count != 0)
                throw new InvalidOperationException("Small bounded context triggered unnecessary compaction.");
        });

        Test("AgentTaskContract snapshots host-owned task requirements", () =>
        {
            var inputs = new List<string> { " input-a ", "input-b" };
            var requiredChanges = new List<string> { "change-a" };
            var criteria = new List<AgentAcceptanceCriterion>
            {
                new("criterion-a", "criterion-a"),
                new("criterion-b", "criterion-b")
            };
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                " Update the target safely ",
                " workspace:/fixture ",
                inputs,
                requiredChanges,
                ["preserve-a"],
                ["output-a"],
                criteria,
                AgentTaskRiskClass.Medium,
                new AgentVerificationPolicy(
                    requireVerification: true,
                    allowNotMechanicallyVerifiable: false,
                    requiredVerifierIds: [" build ", "tests", "build"]));

            inputs[0] = "MUTATED_AFTER_CONSTRUCTION";
            requiredChanges.Add("MUTATED_CHANGE");
            criteria.Clear();

            if (contract.UserGoal != "Update the target safely" || contract.Scope != "workspace:/fixture")
                throw new InvalidOperationException("Task contract did not normalize required scalar fields.");
            if (!contract.Inputs.SequenceEqual(new[] { "input-a", "input-b" })
                || !contract.RequiredChanges.SequenceEqual(new[] { "change-a" })
                || !contract.AcceptanceCriteria.Select(x => x.CriterionId).SequenceEqual(new[] { "criterion-a", "criterion-b" }))
                throw new InvalidOperationException("Caller mutation changed an accepted task contract snapshot.");
            if (!contract.VerificationPolicy.RequiredVerifierIds.SequenceEqual(new[] { "build", "tests" })
                || !contract.VerificationPolicy.RequireVerification
                || contract.VerificationPolicy.AllowNotMechanicallyVerifiable)
                throw new InvalidOperationException("Task verification policy was not preserved deterministically.");
            if (!contract.IsMutating || contract.RiskClass != AgentTaskRiskClass.Medium)
                throw new InvalidOperationException("Task mutation/risk classification is inconsistent.");

            var readOnly = new AgentTaskContract(
                Guid.NewGuid(), "Read the target", "workspace:/fixture", null, null, null, null,
                [new AgentAcceptanceCriterion("return-facts", "return requested facts")], AgentTaskRiskClass.ReadOnly,
                new AgentVerificationPolicy(requireVerification: false));
            if (readOnly.IsMutating)
                throw new InvalidOperationException("Read-only contract was classified as mutating.");

            try
            {
                _ = new AgentTaskContract(
                    Guid.Empty, "goal", "scope", null, null, null, null, null,
                    AgentTaskRiskClass.ReadOnly, new AgentVerificationPolicy(false));
                throw new InvalidOperationException("Empty task ID was accepted.");
            }
            catch (ArgumentException) { }
        });

        Test("Acceptance criteria preserve identity and use typed durable evidence", () =>
        {
            var source = new AgentAcceptanceCriterion("keep-formulas", "Existing formulas remain unchanged.");
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Edit workbook safely",
                "workspace:/fixture",
                ["book.xlsx"],
                ["update target cells"],
                ["preserve formulas"],
                ["edited workbook"],
                [source],
                AgentTaskRiskClass.Medium,
                new AgentVerificationPolicy(requiredVerifierIds: ["excel-verify"]));

            var evidence = new AgentEvidenceReference(
                AgentEvidenceKind.TestBuildResult,
                "ci:35293190864",
                new string('a', 64),
                "full regression passed");
            var withEvidence = contract.WithCriterionEvidence("keep-formulas", evidence);

            if (contract.AcceptanceCriteria[0].Evidence.Count != 0)
                throw new InvalidOperationException("Recording evidence mutated the prior task contract snapshot.");
            if (withEvidence.AcceptanceCriteria.Count != 1
                || withEvidence.AcceptanceCriteria[0].Requirement != source.Requirement
                || withEvidence.AcceptanceCriteria[0].Evidence.Count != 1
                || withEvidence.AcceptanceCriteria[0].Evidence[0].Kind != AgentEvidenceKind.TestBuildResult
                || withEvidence.AcceptanceCriteria[0].Evidence[0].ReferenceId != "ci:35293190864")
                throw new InvalidOperationException("Typed acceptance evidence was not preserved.");

            var expanded = withEvidence.ExpandAcceptanceCriteria(
                [new AgentAcceptanceCriterion("output-exists", "Expected output artifact exists.")]);
            if (expanded.AcceptanceCriteria.Count != 2
                || !expanded.AcceptanceCriteria.Select(x => x.CriterionId)
                    .SequenceEqual(new[] { "keep-formulas", "output-exists" }))
                throw new InvalidOperationException("Contract criterion expansion did not preserve existing requirements.");

            try
            {
                _ = expanded.ExpandAcceptanceCriteria(
                    [new AgentAcceptanceCriterion("keep-formulas", "Silently changed requirement")]);
                throw new InvalidOperationException("Existing acceptance criterion was silently redefined.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("cannot be silently redefined", StringComparison.Ordinal))
            {
            }

            try
            {
                _ = new AgentEvidenceReference(AgentEvidenceKind.ArtifactHash, "artifact-1", "not-a-sha");
                throw new InvalidOperationException("Invalid evidence SHA-256 was accepted.");
            }
            catch (ArgumentException)
            {
            }
        });

        Test("AgentTaskStateMachine enforces deterministic lifecycle and repair loop", () =>
        {
            var machine = new AgentTaskStateMachine();
            if (machine.State != AgentTaskState.Received || machine.IsTerminal)
                throw new InvalidOperationException("Task state machine did not start in Received.");

            machine.TransitionTo(AgentTaskState.Grounded, "inputs inspected");
            machine.TransitionTo(AgentTaskState.Planned, "plan accepted");
            machine.TransitionTo(AgentTaskState.Executing, "execution started");
            machine.TransitionTo(AgentTaskState.Verifying, "changes produced");
            machine.TransitionTo(AgentTaskState.Repairing, "verification failed");
            machine.TransitionTo(AgentTaskState.Executing, "repair attempt");
            machine.TransitionTo(AgentTaskState.Verifying, "re-verify");
            var readOnlyCompletionContract = new AgentTaskContract(
                Guid.NewGuid(), "Read-only lifecycle fixture", "workspace:/fixture",
                null, null, null, null,
                [new AgentAcceptanceCriterion("observed", "Requested state was observed.")],
                AgentTaskRiskClass.ReadOnly,
                new AgentVerificationPolicy(requireVerification: false));
            machine.Complete(
                readOnlyCompletionContract,
                new AgentVerificationOutcome(passed: false),
                "read-only completion");

            if (!machine.IsTerminal || machine.State != AgentTaskState.Completed)
                throw new InvalidOperationException("Verified task did not reach terminal Completed state.");
            if (machine.History.Count != 8
                || machine.History.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(0, 8).Select(x => (long)x)) is false)
                throw new InvalidOperationException("Task transition history is not deterministic.");

            try
            {
                machine.TransitionTo(AgentTaskState.Executing);
                throw new InvalidOperationException("Terminal Completed task accepted a new transition.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Illegal task transition", StringComparison.Ordinal))
            {
            }

            var cancelled = new AgentTaskStateMachine();
            cancelled.TransitionTo(AgentTaskState.Cancelled, "user cancelled");
            if (!cancelled.IsTerminal || cancelled.State != AgentTaskState.Cancelled)
                throw new InvalidOperationException("Cancellation did not produce terminal state.");

            var illegal = new AgentTaskStateMachine();
            try
            {
                illegal.TransitionTo(AgentTaskState.Planned);
                throw new InvalidOperationException("Received task skipped Grounded.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Illegal task transition", StringComparison.Ordinal))
            {
            }
        });

        Test("Mutating task cannot complete without verification", () =>
        {
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Modify the fixture",
                "workspace:/fixture",
                ["input.txt"],
                ["change target"],
                ["preserve unrelated content"],
                ["updated artifact"],
                [new AgentAcceptanceCriterion("builds", "Updated artifact passes build verification.")],
                AgentTaskRiskClass.Medium,
                new AgentVerificationPolicy(
                    requireVerification: true,
                    allowNotMechanicallyVerifiable: false,
                    requiredVerifierIds: ["build"]));

            var machine = new AgentTaskStateMachine();
            machine.TransitionTo(AgentTaskState.Grounded);
            machine.TransitionTo(AgentTaskState.Planned);
            machine.TransitionTo(AgentTaskState.Executing);

            try
            {
                machine.TransitionTo(AgentTaskState.Completed);
                throw new InvalidOperationException("Mutating task completed directly from Executing.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("verification gate", StringComparison.OrdinalIgnoreCase))
            {
            }

            machine.TransitionTo(AgentTaskState.Verifying);

            try
            {
                machine.Complete(contract, new AgentVerificationOutcome(passed: false));
                throw new InvalidOperationException("Mutating task completed after failed verification.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("cannot complete without successful verification", StringComparison.Ordinal))
            {
            }

            try
            {
                machine.Complete(contract, new AgentVerificationOutcome(passed: true));
                throw new InvalidOperationException("Mutating task completed without its required verifier.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("missing required verifier", StringComparison.Ordinal))
            {
            }

            machine.Complete(
                contract,
                new AgentVerificationOutcome(passed: true, verifierIds: ["build"]),
                "required verifier passed");

            if (machine.State != AgentTaskState.Completed || !machine.IsTerminal)
                throw new InvalidOperationException("Verified mutating task did not complete.");
        });

        Test("Fast path router classifies direct retrieval action and complex tasks without heavy direct schemas", () =>
        {
            static AgentTaskContract Contract(
                string goal,
                IEnumerable<string>? inputs = null,
                IEnumerable<string>? changes = null,
                AgentTaskRiskClass risk = AgentTaskRiskClass.ReadOnly)
                => new(
                    Guid.NewGuid(),
                    goal,
                    "workspace:/fixture",
                    inputs,
                    changes,
                    null,
                    null,
                    [new AgentAcceptanceCriterion("done", "Requested outcome is satisfied.")],
                    risk,
                    new AgentVerificationPolicy(requireVerification: changes is not null));

            var router = new AgentFastPathRouter();

            var direct = router.Route(Contract("Explain the already-grounded result"));
            if (direct.RouteClass != AgentTaskRouteClass.Direct
                || direct.SchemaMode != AgentRouteSchemaMode.None
                || direct.InitialToolNamespaces.Count != 0)
                throw new InvalidOperationException("Direct route loaded or requested tool schema material.");

            var retrieval = router.Route(
                Contract("Read the supplied source", inputs: ["artifact-handle"]),
                new AgentTaskRoutingSignals(NeedsExternalRetrieval: true));
            if (retrieval.RouteClass != AgentTaskRouteClass.Retrieval
                || retrieval.SchemaMode != AgentRouteSchemaMode.RetrievalOnly
                || !retrieval.InitialToolNamespaces.SequenceEqual(new[] { "files" }))
                throw new InvalidOperationException("Retrieval route did not stay files-only.");

            var action = router.Route(
                Contract("Modify the target", changes: ["update target"], risk: AgentTaskRiskClass.Medium));
            if (action.RouteClass != AgentTaskRouteClass.Action
                || action.SchemaMode != AgentRouteSchemaMode.DeferredToolSearch
                || action.InitialToolNamespaces.Count != 0)
                throw new InvalidOperationException("Action route eagerly loaded tool namespaces.");

            var complex = router.Route(
                Contract("Investigate and execute a multi-step task"),
                new AgentTaskRoutingSignals(NeedsExternalRetrieval: true, NeedsAction: true, NeedsComplexPlanning: true));
            if (complex.RouteClass != AgentTaskRouteClass.ComplexAgent
                || complex.SchemaMode != AgentRouteSchemaMode.DeferredToolSearch
                || complex.InitialToolNamespaces.Count != 0)
                throw new InvalidOperationException("Complex route did not defer tool schema discovery.");

            if (direct.InitialToolNamespaces.Any(x =>
                    x.Equals("office", StringComparison.OrdinalIgnoreCase)
                    || x.Equals("desktop", StringComparison.OrdinalIgnoreCase)
                    || x.Equals("python", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Direct route exposed Office/Desktop/Python schemas.");
        });

        Test("AgentOrchestrator skeleton preserves v1 compatibility path and host boundaries", () =>
        {
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Explain grounded fixture state",
                "workspace:/fixture",
                null,
                null,
                null,
                null,
                [new AgentAcceptanceCriterion("answered", "Requested explanation is returned.")],
                AgentTaskRiskClass.ReadOnly,
                new AgentVerificationPolicy(requireVerification: false));

            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(contract);

            if (session.Contract.TaskId != contract.TaskId
                || session.StateMachine.State != AgentTaskState.Received
                || session.Route.RouteClass != AgentTaskRouteClass.Direct
                || orchestrator.ContextManager is null)
                throw new InvalidOperationException("Orchestrator did not preserve contract/route/state/context boundaries.");

            orchestrator.Ground(session, "fixture grounded");
            orchestrator.Plan(session, "simple plan");
            if (session.StateMachine.State != AgentTaskState.Planned)
                throw new InvalidOperationException("Orchestrator did not advance through host-owned state transitions.");

            using var compatibility = orchestrator.CreateCompatibilityRunner();
            if (compatibility.GetType() != typeof(AgentRunner))
                throw new InvalidOperationException("Existing AgentRunner v1 compatibility path was not retained.");
        });

        Test("Tool registry descriptors preserve risk access parallel schema and executor metadata", () =>
        {
            var files = new ToolNamespace("files", "Workspace file tools.");
            var executor = new DelegatingToolExecutor(
                "fixture-executor",
                (call, ct) => ValueTask.FromResult("ok:" + call.Name));
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = "read_file",
                    description = "Read one file.",
                    parameters = new { type = "object" }
                }
            });
            var descriptor = new ToolDescriptor(
                "read_file",
                files,
                "Read one supported workspace file.",
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                supportsParallel: true,
                schemaVersion: "v1",
                callableSchema: schema,
                executor: executor);

            var registry = new ToolRegistry();
            registry.Register(descriptor);

            if (registry.Version != 2)
                throw new InvalidOperationException("Registry version did not advance for namespace + tool registration.");
            if (!registry.TryGet("READ_FILE", out var resolved) || resolved != descriptor)
                throw new InvalidOperationException("Tool lookup is not normalized/deterministic.");
            if (resolved.IsMutating
                || !resolved.SupportsParallel
                || resolved.Risk != AgentToolRisk.Low
                || resolved.SchemaVersion != "v1"
                || resolved.Executor.ExecutorId != "fixture-executor")
                throw new InvalidOperationException("Tool descriptor lost access/risk/parallel/schema/executor metadata.");
            if (!registry.Namespaces.Select(x => x.Name).SequenceEqual(new[] { "files" })
                || !registry.GetNamespace("files").Select(x => x.Name).SequenceEqual(new[] { "read_file" }))
                throw new InvalidOperationException("Registry namespace projection is inconsistent.");

            var version = registry.Version;
            registry.RegisterNamespace(new ToolNamespace("files", "Workspace file tools."));
            if (registry.Version != version)
                throw new InvalidOperationException("Idempotent namespace registration changed registry version.");

            try
            {
                registry.Register(descriptor);
                throw new InvalidOperationException("Duplicate tool registration was accepted.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already registered", StringComparison.Ordinal))
            {
            }
        });

        Test("V1 tool registry adapter preserves every existing callable schema", () =>
        {
            var executor = new DelegatingToolExecutor(
                "v1-fixture",
                (call, ct) => ValueTask.FromResult("fixture:" + call.Name));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);

            var definitionNames = JsonSerializer.SerializeToElement(AgentTools.Definitions)
                .EnumerateArray()
                .Select(x => x.GetProperty("function").GetProperty("name").GetString()!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var registryNames = registry.Tools.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            if (!registryNames.SequenceEqual(definitionNames))
                throw new InvalidOperationException("V1 registry adapter deleted or invented tool definitions.");
            if (registry.Tools.Any(x => x.SchemaVersion != "v1" || x.Executor != executor))
                throw new InvalidOperationException("V1 registry adapter lost schema version or executor identity.");
            if (!registry.TryGet("read_file", out var readFile)
                || readFile.Namespace.Name != "files"
                || readFile.IsMutating
                || !readFile.SupportsParallel)
                throw new InvalidOperationException("read_file registry metadata is incorrect.");
            if (!registry.TryGet("write_text", out var writeText)
                || !writeText.IsMutating
                || writeText.SupportsParallel)
                throw new InvalidOperationException("write_text mutation metadata is incorrect.");
            if (!registry.TryGet("click_control", out var click)
                || click.Namespace.Name != "desktop"
                || click.Risk != AgentToolRisk.High
                || !click.IsMutating)
                throw new InvalidOperationException("desktop mutation metadata is incorrect.");
        });

        Test("ToolSearchIndex ranks lexically and caches by registry version", () =>
        {
            var executor = new DelegatingToolExecutor(
                "search-fixture",
                (call, ct) => ValueTask.FromResult("ok"));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);
            var search = new ToolSearchIndex(registry);

            var read = search.Search("read_file workspace file");
            if (read.Count == 0 || read[0].Descriptor.Name != "read_file")
                throw new InvalidOperationException("BM25 tool search failed to rank exact read_file intent first.");
            var rebuilds = search.RebuildCount;
            _ = search.Search("inspect window control");
            if (search.RebuildCount != rebuilds)
                throw new InvalidOperationException("Tool search rebuilt an unchanged registry corpus.");

            var fixtureNamespace = new ToolNamespace("fixture", "Synthetic registry-version fixture.");
            registry.Register(new ToolDescriptor(
                "fixture_probe",
                fixtureNamespace,
                "Unique quasarprobe diagnostic tool.",
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                true,
                "v1",
                JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name = "fixture_probe",
                        description = "Unique quasarprobe diagnostic tool.",
                        parameters = new { type = "object" }
                    }
                }),
                executor));

            var probe = search.Search("quasarprobe");
            if (search.RebuildCount != rebuilds + 1
                || search.CachedRegistryVersion != registry.Version
                || probe.Count == 0
                || probe[0].Descriptor.Name != "fixture_probe")
                throw new InvalidOperationException("Tool search cache did not invalidate on registry version change.");
        });

        Test("Deferred tool discovery initially exposes only tool_search plus stable core", () =>
        {
            var executor = new DelegatingToolExecutor(
                "initial-exposure-fixture",
                (call, ct) => ValueTask.FromResult("ok"));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);
            var discovery = new DeferredToolDiscovery(registry);
            var initial = discovery.BuildInitialExposure();

            var callableNames = initial.CallableSchemas
                .Select(DeferredToolDiscovery.SchemaName)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            if (!callableNames.SequenceEqual(new[] { "tool_search", "update_plan" }))
                throw new InvalidOperationException(
                    "Initial v2 tool exposure contains detailed schemas beyond tool_search + stable core: "
                    + string.Join(", ", callableNames));
            if (callableNames.Any(x => x is "read_file" or "run_python" or "inspect_window" or "click_control"))
                throw new InvalidOperationException("Heavy file/Python/Desktop schema leaked into initial model tool surface.");

            var namespaces = initial.Namespaces.Select(x => x.Name).ToArray();
            foreach (var expected in new[] { "desktop", "files", "office", "python", "skills" })
                if (!namespaces.Contains(expected, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Initial namespace descriptions omitted '{expected}'.");
            if (initial.Namespaces.Any(x => string.IsNullOrWhiteSpace(x.Description)))
                throw new InvalidOperationException("Initial namespace description is empty.");

            var searchSchema = initial.CallableSchemas.Single(x =>
                DeferredToolDiscovery.SchemaName(x) == DeferredToolDiscovery.SearchToolName);
            var parameters = searchSchema.GetProperty("function").GetProperty("parameters");
            if (!parameters.GetProperty("required").EnumerateArray()
                    .Any(x => x.GetString() == "query"))
                throw new InvalidOperationException("tool_search schema does not require a query.");
        });

        Test("Deferred schema loading is traceable and coalesces duplicates", () =>
        {
            var executor = new DelegatingToolExecutor(
                "load-fixture",
                (call, ct) => ValueTask.FromResult("ok"));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);
            var discovery = new DeferredToolDiscovery(registry);

            var first = discovery.SearchAndLoad("read_file", 1);
            var firstNames = first.CallableSchemas.Select(DeferredToolDiscovery.SchemaName).ToArray();
            if (!first.Trace.SelectedNames.SequenceEqual(new[] { "read_file" })
                || !first.Trace.NewlyLoadedNames.SequenceEqual(new[] { "read_file" })
                || !firstNames.SequenceEqual(first.Trace.NewlyLoadedNames))
                throw new InvalidOperationException("First discovered schema is not exactly traceable.");

            var repeated = discovery.SearchAndLoad("read_file", 1);
            if (!repeated.Trace.SelectedNames.SequenceEqual(new[] { "read_file" })
                || repeated.Trace.NewlyLoadedNames.Count != 0
                || repeated.CallableSchemas.Count != 0)
                throw new InvalidOperationException("Repeated tool discovery resent a duplicate schema.");

            var args = JsonSerializer.SerializeToElement(new
            {
                query = "click_control",
                max_results = 1
            });
            var json = discovery.ExecuteToolSearch(new ToolCall("search-1", "tool_search", args));
            using var result = JsonDocument.Parse(json);
            if (!result.RootElement.GetProperty("newlyLoaded").EnumerateArray()
                    .Any(x => x.GetString() == "click_control"))
                throw new InvalidOperationException("Runtime tool_search did not load selected callable schema.");

            if (discovery.LoadTrace.Count != 3
                || !discovery.LoadTrace.Select(x => x.Sequence).SequenceEqual(new long[] { 0, 1, 2 })
                || discovery.LoadTrace.Any(x => x.RegistryVersion != registry.Version))
                throw new InvalidOperationException("Deferred schema load trace is incomplete or non-deterministic.");
            if (discovery.LoadedSchemaNames.Count(x => x == "read_file") != 1
                || discovery.LoadedSchemaNames.Count(x => x == "click_control") != 1)
                throw new InvalidOperationException("Loaded schema identity set contains duplicates.");
        });

        Test("Tool execution scheduler parallelizes safe reads and serializes overlapping mutations", () =>
        {
            static JsonElement Schema(string name) => JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description = "scheduler fixture",
                    parameters = new { type = "object" }
                }
            });

            static void RaiseMax(ref int max, int value)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref max);
                    if (observed >= value) return;
                }
                while (Interlocked.CompareExchange(ref max, value, observed) != observed);
            }

            var readActive = 0;
            var readMax = 0;
            var twoReadsEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var readExecutor = new DelegatingToolExecutor(
                "parallel-read-fixture",
                async (call, ct) =>
                {
                    var active = Interlocked.Increment(ref readActive);
                    RaiseMax(ref readMax, active);
                    if (active >= 2) twoReadsEntered.TrySetResult(true);
                    await Task.WhenAny(twoReadsEntered.Task, Task.Delay(1_000, ct));
                    Interlocked.Decrement(ref readActive);
                    return call.Id;
                });
            var readDescriptor = new ToolDescriptor(
                "read_probe",
                new ToolNamespace("fixture", "Scheduler fixture."),
                "Parallel read probe.",
                AgentToolRisk.Low,
                AgentToolAccess.ReadOnly,
                supportsParallel: true,
                "v1",
                Schema("read_probe"),
                readExecutor);

            using (var scheduler = new ToolExecutionScheduler())
            {
                var emptyArgs = JsonSerializer.SerializeToElement(new { });
                var results = scheduler.ExecuteBatchAsync(
                    [
                        new ToolExecutionRequest(readDescriptor, new ToolCall("r1", "read_probe", emptyArgs)),
                        new ToolExecutionRequest(readDescriptor, new ToolCall("r2", "read_probe", emptyArgs))
                    ],
                    CancellationToken.None).GetAwaiter().GetResult();

                if (readMax < 2 || !results.Select(x => x.Output).SequenceEqual(new[] { "r1", "r2" }))
                    throw new InvalidOperationException("Parallel-safe read calls did not overlap or preserve result order.");
            }

            var mutationActive = 0;
            var mutationMax = 0;
            var mutationExecutor = new DelegatingToolExecutor(
                "mutation-fixture",
                async (call, ct) =>
                {
                    var active = Interlocked.Increment(ref mutationActive);
                    RaiseMax(ref mutationMax, active);
                    await Task.Delay(60, ct);
                    Interlocked.Decrement(ref mutationActive);
                    return call.Id;
                });
            var mutationDescriptor = new ToolDescriptor(
                "mutate_probe",
                new ToolNamespace("fixture", "Scheduler fixture."),
                "Mutation probe.",
                AgentToolRisk.Medium,
                AgentToolAccess.Mutating,
                supportsParallel: true,
                "v1",
                Schema("mutate_probe"),
                mutationExecutor);

            using (var scheduler = new ToolExecutionScheduler())
            {
                var emptyArgs = JsonSerializer.SerializeToElement(new { });
                _ = scheduler.ExecuteBatchAsync(
                    [
                        new ToolExecutionRequest(
                            mutationDescriptor,
                            new ToolCall("m1", "mutate_probe", emptyArgs),
                            "file:/same"),
                        new ToolExecutionRequest(
                            mutationDescriptor,
                            new ToolCall("m2", "mutate_probe", emptyArgs),
                            "file:/same")
                    ],
                    CancellationToken.None).GetAwaiter().GetResult();

                if (mutationMax != 1)
                    throw new InvalidOperationException("Overlapping mutations to the same resource executed concurrently.");

                try
                {
                    _ = scheduler.ExecuteBatchAsync(
                        [new ToolExecutionRequest(
                            mutationDescriptor,
                            new ToolCall("m3", "mutate_probe", emptyArgs))],
                        CancellationToken.None).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Mutating call without resource identity was accepted.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("requires a resource key", StringComparison.Ordinal))
                {
                }
            }
        });

        Test("Deferred skill session preserves progressive loading and hash/version cache", () =>
        {
            var skillsRoot = Path.Combine(root, "v2-skill-cache-fixture");
            if (Directory.Exists(skillsRoot)) Directory.Delete(skillsRoot, recursive: true);
            var skillDir = Path.Combine(skillsRoot, "fixture-skill");
            Directory.CreateDirectory(skillDir);
            var skillPath = Path.Combine(skillDir, "SKILL.md");
            File.WriteAllText(skillPath, string.Join(Environment.NewLine, new[]
            {
                "---",
                "name: fixture-skill",
                "description: Deterministic fixture guidance for deferred skill loading.",
                "---",
                "# Fixture",
                "Read this guidance progressively."
            }));

            var catalog = new SkillCatalog(skillsRoot);
            var session = new DeferredSkillSession(catalog);

            var first = session.Read("fixture-skill", "SKILL.md");
            if (first.Unchanged || string.IsNullOrWhiteSpace(first.Content)
                || first.Sha256.Length != 64
                || !first.Version.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidOperationException("First progressive skill read did not return content/hash/version.");

            var second = session.Read("fixture-skill", "SKILL.md");
            if (!second.Unchanged || second.Content is not null
                || second.Sha256 != first.Sha256
                || second.Version != first.Version)
                throw new InvalidOperationException("Unchanged skill guidance was resent instead of using cached hash/version.");

            File.AppendAllText(skillPath, Environment.NewLine + "Changed guidance invalidates the task cache.");
            var third = session.Read("fixture-skill", "SKILL.md");
            if (third.Unchanged || string.IsNullOrWhiteSpace(third.Content)
                || third.Sha256 == first.Sha256
                || third.Version == first.Version)
                throw new InvalidOperationException("Changed skill source failed to invalidate cached guidance.");

            if (session.LoadedVersions.Count != 1
                || session.LoadedVersions.Values.Single() != third.Version)
                throw new InvalidOperationException("Per-task skill version memory is inconsistent.");

            var executor = new DelegatingToolExecutor(
                "skill-registry-fixture",
                (call, ct) => ValueTask.FromResult("ok"));
            var registry = new ToolRegistry();
            V1ToolRegistryAdapter.Populate(registry, executor);
            var skillTools = registry.GetNamespace("skills").Select(x => x.Name).ToArray();
            if (!skillTools.SequenceEqual(new[] { "list_skills", "read_skill" }))
                throw new InvalidOperationException("SkillCatalog tools were not preserved in deferred registry namespace.");
        });

        Test("VerificationReport is machine-readable per criterion with typed failures and evidence IDs", () =>
        {
            var report = new VerificationReport(
                "fixture-verifier",
                [
                    new VerificationCriterionResult(
                        "c1",
                        VerificationCriterionStatus.Passed,
                        evidenceIds: ["evidence:hash:1"]),
                    new VerificationCriterionResult(
                        "c2",
                        VerificationCriterionStatus.Failed,
                        evidenceIds: ["evidence:tool:2"],
                        failure: new VerificationFailure(
                            "c2",
                            "Expected value did not match.",
                            ["evidence:tool:2", "evidence:snapshot:3"]))
                ],
                reportEvidenceIds: ["report:fixture"]);

            if (report.Passed)
                throw new InvalidOperationException("Verification report passed despite failed criterion.");
            if (!report.Covers(new[] { "c1", "c2" }) || report.Covers(new[] { "c1", "missing" }))
                throw new InvalidOperationException("Verification report criterion coverage is inconsistent.");
            if (report.Failures.Count != 1
                || report.Failures[0].CriterionId != "c2"
                || !report.Failures[0].EvidenceIds.SequenceEqual(new[] { "evidence:tool:2", "evidence:snapshot:3" }))
                throw new InvalidOperationException("Verification failure/evidence projection is incorrect.");

            var passing = new VerificationReport(
                "fixture-verifier",
                [
                    new VerificationCriterionResult("c1", VerificationCriterionStatus.Passed, ["e1"]),
                    new VerificationCriterionResult("c2", VerificationCriterionStatus.Passed, ["e2"])
                ]);
            if (!passing.Passed)
                throw new InvalidOperationException("All-passed verification report did not pass.");

            try
            {
                _ = new VerificationCriterionResult("c1", VerificationCriterionStatus.Failed);
                throw new InvalidOperationException("Failed criterion without failure detail was accepted.");
            }
            catch (ArgumentException)
            {
            }
        });

        Test("FileScopeVerifier detects exact changes hashes and unintended outputs", () =>
        {
            static string Hash(char c) => new string(c, 64);

            var before = new[]
            {
                new FileVerificationEntry("input/a.txt", Hash('a')),
                new FileVerificationEntry("input/b.txt", Hash('b'))
            };
            var after = new[]
            {
                new FileVerificationEntry("input/a.txt", Hash('c')),
                new FileVerificationEntry("input/b.txt", Hash('b')),
                new FileVerificationEntry("output/result.txt", Hash('d'))
            };
            var expected = new FileVerificationExpectation(
                expectedChangedPaths: ["input/a.txt", "output/result.txt"],
                expectedHashes: new Dictionary<string, string>
                {
                    ["input/a.txt"] = Hash('c'),
                    ["output/result.txt"] = Hash('d')
                },
                allowedOutputPaths: ["output/result.txt"]);

            var pass = FileScopeVerifier.Verify(before, after, expected);
            if (!pass.Passed
                || pass.Criteria.Count != 3
                || pass.Criteria.Any(x => x.Status != VerificationCriterionStatus.Passed))
                throw new InvalidOperationException("Expected file/hash/scope fixture did not pass.");

            var badAfter = after.Append(new FileVerificationEntry("output/rogue.txt", Hash('e')));
            var fail = FileScopeVerifier.Verify(before, badAfter, expected);
            if (fail.Passed
                || fail.Failures.Count < 2
                || !fail.Failures.Any(x => x.CriterionId == FileScopeVerifier.ExactChangesCriterionId)
                || !fail.Failures.Any(x => x.CriterionId == FileScopeVerifier.UnintendedOutputCriterionId))
                throw new InvalidOperationException("Unexpected changed/output scope was not rejected.");

            var wrongHash = new FileVerificationExpectation(
                expectedChangedPaths: ["input/a.txt", "output/result.txt"],
                expectedHashes: new Dictionary<string, string>
                {
                    ["input/a.txt"] = Hash('f')
                },
                allowedOutputPaths: ["output/result.txt"]);
            var hashFail = FileScopeVerifier.Verify(before, after, wrongHash);
            if (!hashFail.Failures.Any(x => x.CriterionId == FileScopeVerifier.ExpectedHashesCriterionId))
                throw new InvalidOperationException("Expected hash mismatch was not reported.");
        });

        Test("Artifact verifier contract stays domain-neutral and criterion-scoped", () =>
        {
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Verify generated artifact",
                "workspace:/fixture",
                null,
                ["produce artifact"],
                null,
                ["artifact"],
                [
                    new AgentAcceptanceCriterion("content", "Artifact content is correct."),
                    new AgentAcceptanceCriterion("format", "Artifact format is preserved.")
                ],
                AgentTaskRiskClass.Low,
                new AgentVerificationPolicy(requireVerification: true));

            var target = new ArtifactVerificationTarget(
                "artifact-1",
                "application/vnd.test",
                new string('a', 64),
                "run:fixture");
            var request = new ArtifactVerificationRequest(contract, target, ["content"]);
            if (!request.CriterionIds.SequenceEqual(new[] { "content" })
                || request.Target.ArtifactId != "artifact-1")
                throw new InvalidOperationException("Artifact verification request lost scoped criteria/target.");

            var registry = new ArtifactVerifierRegistry();
            registry.Register(new FixtureArtifactVerifier());
            var resolved = registry.Resolve(target);
            if (resolved.Count != 1 || resolved[0].VerifierId != "fixture-artifact")
                throw new InvalidOperationException("Artifact verifier registry did not resolve compatible verifier.");

            var report = resolved[0].VerifyAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (!report.Passed || !report.Covers(new[] { "content" }))
                throw new InvalidOperationException("Generic artifact verifier contract did not produce report.");

            try
            {
                _ = new ArtifactVerificationRequest(contract, target, ["unknown"]);
                throw new InvalidOperationException("Unknown acceptance criterion was accepted for artifact verification.");
            }
            catch (ArgumentException)
            {
            }
        });

        Test("AgentRepairController emits concise failed-criterion context and preserves passed criteria", () =>
        {
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Repair fixture",
                "workspace:/fixture",
                null,
                ["repair output"],
                null,
                ["artifact"],
                [
                    new AgentAcceptanceCriterion("c1", "Keep title unchanged."),
                    new AgentAcceptanceCriterion("c2", "Set Tasks!A4 italic=false."),
                    new AgentAcceptanceCriterion("c3", "Preserve formulas.")
                ],
                AgentTaskRiskClass.Low,
                new AgentVerificationPolicy(requireVerification: true));

            var report = new VerificationReport(
                "fixture-verifier",
                [
                    new VerificationCriterionResult("c1", VerificationCriterionStatus.Passed, ["snap:c1"]),
                    new VerificationCriterionResult(
                        "c2",
                        VerificationCriterionStatus.Failed,
                        ["snap:c2"],
                        new VerificationFailure(
                            "c2",
                            "Tasks!A4 italic changed true -> false.",
                            ["before:c2", "after:c2"])),
                    new VerificationCriterionResult("c3", VerificationCriterionStatus.Passed, ["snap:c3"])
                ]);

            var repair = new AgentRepairController().Build(contract, report);
            if (!repair.FailedCriterionIds.SequenceEqual(new[] { "c2" })
                || !repair.PassedCriterionIds.SequenceEqual(new[] { "c1", "c3" }))
                throw new InvalidOperationException("Repair controller lost failed/passed criterion identity.");
            if (!repair.PromptContext.Contains("FAILED c2", StringComparison.Ordinal)
                || !repair.PromptContext.Contains("Tasks!A4 italic changed true -> false.", StringComparison.Ordinal)
                || !repair.PromptContext.Contains("Preserve passed criteria: c1, c3", StringComparison.Ordinal)
                || repair.PromptContext.Contains("full transcript", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Repair context is missing concise failure/preservation guidance.");
            if (repair.PromptContext.Length > AgentRepairController.MaxRepairContextCharacters)
                throw new InvalidOperationException("Repair context exceeded its hard bound.");

            try
            {
                _ = new AgentRepairController().Build(
                    contract,
                    new VerificationReport(
                        "fixture-verifier",
                        [new VerificationCriterionResult("c1", VerificationCriterionStatus.Passed)]));
                throw new InvalidOperationException("Repair context was created without any failed criterion.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("at least one failed criterion", StringComparison.Ordinal))
            {
            }
        });

        Test("Runtime RecoverySupervisor stays below semantic verification repair", () =>
        {
            var contract = new AgentTaskContract(
                Guid.NewGuid(),
                "Verify runtime recovery separation",
                "workspace:/fixture",
                null,
                ["produce verified result"],
                null,
                ["result"],
                [new AgentAcceptanceCriterion("semantic", "Result content is semantically correct.")],
                AgentTaskRiskClass.Low,
                new AgentVerificationPolicy(requireVerification: true));

            var semanticReport = new VerificationReport(
                "semantic-fixture",
                [
                    new VerificationCriterionResult(
                        "semantic",
                        VerificationCriterionStatus.Failed,
                        ["semantic:evidence"],
                        new VerificationFailure("semantic", "Content mismatch.", ["semantic:evidence"]))
                ]);

            var coordinator = new VerificationRecoveryCoordinator();
            var args = JsonSerializer.SerializeToElement(new { path = "missing.txt", offset = "0" });
            var call = new ToolCall("runtime-1", "read_file", args);
            var runtimeFailure = JsonSerializer.Serialize(new
            {
                recovery = new
                {
                    code = "not_found",
                    message = "missing runtime input",
                    recoverable = true,
                    next = "inspect actual files"
                }
            });

            coordinator.ObserveRuntimeCall(call, runtimeFailure);
            var pending = coordinator.GetState(semanticReport);
            if (!pending.HasRuntimeRecoveryPending || !pending.HasSemanticVerificationFailures)
                throw new InvalidOperationException("Runtime + semantic failures were not tracked independently.");

            coordinator.ObserveRuntimeCall(call, "{\"content\":\"runtime recovered\"}");
            var recoveredRuntime = coordinator.GetState(semanticReport);
            if (recoveredRuntime.HasRuntimeRecoveryPending || !recoveredRuntime.HasSemanticVerificationFailures)
                throw new InvalidOperationException("Runtime recovery incorrectly cleared semantic verification failure.");

            var repair = coordinator.BuildSemanticRepair(contract, semanticReport);
            if (!repair.FailedCriterionIds.SequenceEqual(new[] { "semantic" })
                || !repair.PromptContext.Contains("Content mismatch.", StringComparison.Ordinal))
                throw new InvalidOperationException("Semantic repair was not derived from verifier failure after runtime recovery.");
        });

        Test("Lab document inspection delegates safety hash and extraction to H2 Core AiDocuments", () =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("H2-0701 fixture");
            var expected = AiDocuments.Read("fixture.txt", bytes);
            var actual = new LabDocumentInspectionService().Inspect("fixture.txt", bytes);

            if (actual.Name != expected.Name
                || actual.MimeType != expected.MimeType
                || actual.Sha256 != expected.Sha256.ToLowerInvariant()
                || actual.ByteLength != bytes.Length
                || actual.Text != expected.Text
                || actual.Notice != expected.Notice
                || actual.IsPdf != expected.IsPdf
                || actual.IsImage != expected.IsImage)
                throw new InvalidOperationException("Lab inspection diverged from shared H2 Core AiDocuments result.");

            try
            {
                _ = new LabDocumentInspectionService().Inspect("fixture.exe", bytes);
                throw new InvalidOperationException("Lab adapter bypassed H2 Core unsupported-file safety.");
            }
            catch (InvalidDataException)
            {
            }
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

    private sealed class FixtureArtifactVerifier : IArtifactVerifier
    {
        public string VerifierId => "fixture-artifact";

        public bool CanVerify(ArtifactVerificationTarget target)
            => string.Equals(target.MediaType, "application/vnd.test", StringComparison.Ordinal);

        public ValueTask<VerificationReport> VerifyAsync(
            ArtifactVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = request.CriterionIds
                .Select(id => new VerificationCriterionResult(
                    id,
                    VerificationCriterionStatus.Passed,
                    ["artifact:" + request.Target.ArtifactId]))
                .ToArray();
            return ValueTask.FromResult(new VerificationReport(VerifierId, results));
        }
    }
}
