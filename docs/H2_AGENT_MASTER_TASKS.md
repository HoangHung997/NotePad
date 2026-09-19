# H2 Agent — Canonical Rebuild / Refactor Task Tracker

Status: **CANONICAL AFTER THE CURRENT IN-FLIGHT AGENT LAB TASK FINISHES**  
Date: 2026-09-18  
Architecture source of truth: `docs/H2_AGENT_MASTER_SPEC.md`

> Do not start this tracker by interrupting an Agent Lab task already in progress.
>
> Finish the task that was already active when this master tracker was introduced, get its CI/evidence stable, then stop using the old Phase-11/Phase-12 sequence as the execution source of truth and begin at `MB-00`.

---

## How to use this tracker

Rules:

1. Read `docs/H2_AGENT_MASTER_SPEC.md` completely before changing code.
2. Finish the currently in-flight legacy task first.
3. Do not continue automatically into the next old `V2-11xx` task after that.
4. Start this tracker at `MB-00`.
5. One task is `[x]` only after deterministic evidence exists.
6. Keep H2 Notes regression green.
7. No big-bang deletion.
8. A legacy component may be deleted only after its replacement path is active and parity tests pass.
9. Do not add future marketplace complexity unless a task below explicitly requires it.
10. Do not integrate into H2 Notes until final user acceptance.

---

# Stage A — Freeze, baseline and architectural reset

## [x] MB-00 — Freeze the current accepted baseline

Goal:

Capture the exact Agent Lab state before the refactor.

Required work:

- finish whatever Agent Lab task was already in progress;
- require its CI to settle;
- record exact source HEAD;
- record all current test counts;
- record current main interactive call path;
- record current v1 baseline performance/correctness artifacts;
- do not delete any compatibility code yet.

Evidence must prove the current interactive path still is or is not:

```text
LabWindow
 -> AgentOrchestratedRun
 -> AgentOrchestrator
 -> AgentRunner compatibility path
```

Deliverable:

`experiments/H2AgentLab/MB_BASELINE.md`

Acceptance:

- exact commit SHA recorded;
- CI status recorded;
- current v1/v2 runtime call graph recorded;
- no functional behavior changed.

Evidence: `experiments/H2AgentLab/MB_BASELINE.md` freezes branch HEAD `39e873dc6955de223f4a214bef9e5076a851fd81`, successful runtime CI run `35330936392`, 538 deterministic tests with zero failures, the current compatibility call graph and v1 payload/correctness baseline. MB-00 changed documentation only.

---

## [x] MB-01 — Reconcile documentation and stop old tracker drift

Goal:

Make the master documents the only Agent architecture authority.

Required work:

- update `experiments/H2AgentLab/README.md`;
- update `AGENTLAB_V2_SPEC.md` header to state it is historical/superseded;
- update `AGENTLAB_V2_TASKS.md` header to state it is historical/superseded after the completed in-flight task;
- point all new work to:
  - `docs/H2_AGENT_MASTER_SPEC.md`
  - `docs/H2_AGENT_MASTER_TASKS.md`
- do not delete old docs.

Acceptance:

- no ambiguity about which task tracker is authoritative;
- H2 Notes redesign and non-AI bug-ledger docs remain separate.

Evidence: Agent Lab README now links the Master Spec/Tasks as canonical authority; the V2 spec/tracker headers explicitly mark them historical/superseded and forbid new execution work; the two H2 Notes documents remain separately linked and explicitly not superseded.

---

# Stage B — Make the V2 machine actually boot

## [x] MB-10 — Introduce the real AgentRuntime

Goal:

Create one real V2 execution engine.

Add:

`Runtime/AgentRuntime.cs` or equivalent.

AgentRuntime must own:

- model turn start/continue;
- tool-call collection;
- tool execution;
- continuation;
- verification transition;
- repair continuation;
- final response.

It must use:

- `IAgentTransport`;
- `AgentContextManager`;
- `AgentPromptLayout`;
- `ToolRegistry`;
- `DeferredToolDiscovery`;
- `ToolExecutionScheduler`.

Do not put domain-specific Word/Excel/AutoCAD logic in AgentRuntime.

Acceptance:

- deterministic fake transport can execute:
  `user -> model -> tool call -> tool result -> model final`;
- runtime supports more than one tool round;
- runtime has no dependency on `AgentRunner`;
- cancellation works.

Evidence: `Runtime/AgentRuntime.cs` owns bounded prompt/context assembly, deferred tool search/schema loading, ToolRegistry/Scheduler execution, provider-neutral continuation, verification feedback/repair continuation and final completion gate. `Runtime/MbAgentRuntimeTests.cs` proves discovery→tool→final, multi-round verifier repair, cancellation propagation and no `AgentRunner` dependency. GitHub Actions run `35333787066`: MB-10 **4 passed, 0 failed** and full regression/publish pipeline SUCCESS.

---

## [x] MB-11 — Wire AgentOrchestrator to AgentRuntime

Evidence: normal `AgentOrchestratedRun` now constructs/runs `AgentRuntime` through `AgentOrchestrator`; compatibility `AgentRunner` remains diagnostic/A-B only. Dedicated `MbOrchestratorRuntimeTests` prove normal read-only execution uses AgentRuntime, mutating execution remains blocked without verifier evidence, and source guards reject any return to AgentRunner. GitHub Actions run `35342575027`: **3 passed, 0 failed**, full regression/transport/publish pipeline SUCCESS.

Goal:

Make AgentOrchestrator the lifecycle coordinator for the real runtime.

Modify:

- `Tasking/AgentOrchestrator.cs`;
- `Tasking/AgentOrchestratedRun.cs`.

Required end state:

```text
AgentOrchestratedRun
 -> AgentOrchestrator
 -> AgentRuntime
```

Not:

```text
AgentOrchestrator
 -> CreateCompatibilityRunner()
 -> AgentRunner
```

The v1 compatibility runner may remain accessible only through an explicit diagnostic/A-B path.

Acceptance:

- normal Lab execution does not call `CreateCompatibilityRunner()`;
- architecture test fails if normal UI path returns to AgentRunner.

---

## [x] MB-12 — Wire the provider-neutral transport into the real loop

Goal:

Use existing V2 transports in normal execution.

Supported accepted profiles should route through:

- `OllamaTransport`;
- `OpenAiResponsesTransport`;
- `OpenAiResponsesWebSocketTransport` where selected/supported;
- `ChatCompletionsTransport` fallback.

Acceptance:

- no duplicated provider HTTP implementation in the new runtime;
- tool-call identity survives start/continuation;
- usage metrics preserved;
- truncated/incomplete proposed tool calls do not execute;
- switching provider does not change AgentRuntime.

Evidence: `Transport/AgentTransportFactory.cs` routes accepted profiles to existing Ollama/Responses HTTP/Responses WebSocket/Chat-Completions transports while AgentRuntime remains provider-neutral. `MbProviderRuntimeTests` prove class routing, no provider HTTP in runtime/orchestrator/facade, tool-call identity + usage preservation, incomplete proposal rejection, and provider switching through one runtime architecture. GitHub Actions run `35343217692`: **5 passed, 0 failed**, full regression/transport/publish pipeline SUCCESS.

---

# Stage C — Real bounded context and prompt path

## [x] MB-20 — Move V2 context into pre-request execution

Evidence: normal AgentRuntime request path uses `LabSessionContextAdapter.BuildInput`, bounded recent turns/tool summaries and artifact/evidence handles; `MbContextRuntimeTests` proves 10/100/1000-turn boundedness, recent-state retention, no linear replay and no legacy `LabSession.Context()` call. GitHub Actions run `35347262818`: MB-20 **3 passed, 0 failed**; all preceding regression suites were green.

Goal:

Make bounded context real, not diagnostic-only.

Required:

Before every model start/continuation as appropriate:

- build bounded task/current state;
- use recent relevant turns;
- use bounded tool summaries;
- use compacted history;
- use artifact/evidence handles.

Legacy `LabSession.Context()` must not be the primary model context path.

Acceptance:

- test with 10 / 100 / 1000 historical turns;
- active request context remains bounded;
- oldest raw transcript is not replayed linearly;
- model still receives current task and recent relevant state.

---

## [x] MB-21 — Wire CompactionManager into runtime pressure

Goal:

Use compaction automatically when context pressure requires it.

Evidence: `RuntimeCompactionCoordinator` is wired into normal `AgentOrchestratedRun`; it creates/reuses/chains bounded checkpoints, keeps raw journal durable, trims covered history from active input and exposes checkpoint evidence in typed trace. GitHub Actions run `35347797965`: MB-21 **3 passed, 0 failed** and full regression/extension/transport/publish pipeline SUCCESS.

Acceptance:

- runtime can continue long tool loops without unbounded context;
- compaction source references remain auditable;
- raw evidence is retained outside active prompt;
- repeated turns do not repeatedly replay unchanged large summaries.

---

## [x] MB-22 — Use AgentPromptLayout and cache identity in real requests

Implementation note: real AgentRuntime now derives provider/model cache identity after stable namespace metadata is merged; normal AgentOrchestratedRun supplies provider/model cache scope, dynamic task/session state remains outside the stable hash, and dedicated runtime acceptance tests are wired for verification.

Goal:

Separate stable prefix from dynamic runtime suffix in normal execution.

Acceptance:

- stable policy/tool namespace metadata stays cacheable;
- current task/session/time state does not contaminate stable cache identity;
- skill hash changes invalidate only the relevant stable identity where configured.

Evidence: `Runtime/MbPromptCacheRuntimeTests.cs` runs against normal `AgentOrchestratedRun -> AgentRuntime` and direct runtime requests. GitHub Actions run `35348776770` reported **3 passed, 0 failed**: dynamic task/session state preserves the same stable cache key, namespace metadata invalidates the stable identity, and changing only the configured skill hash invalidates only that stable identity.

---

# Stage D — Make ToolRegistry the only normal callable path

## [x] MB-30 — Replace full AgentTools.Definitions exposure

Goal:

Normal runtime must stop sending every v1 tool schema.

Initial model surface should contain only:

- `tool_search`;
- minimum stable core tool(s), if any;
- bounded namespace metadata.

Acceptance:

- first request schema bytes are smaller than full registry;
- heavy Office/Desktop/Python schemas are absent initially;
- full `AgentTools.Definitions` is not serialized into normal V2 model requests.

Evidence: `Runtime/MbInitialToolExposureTests.cs` constructs the real V1-backed ToolRegistry and captures the normal AgentRuntime start request. GitHub Actions run `35351271410` reported **2 passed, 0 failed**: first request exposes only `tool_search` plus optional `update_plan`, excludes Python/Office/Desktop detailed schemas, is materially smaller than the full v1 definitions payload, and normal runtime/orchestrated source does not serialize `AgentTools.Definitions`.

---

## [x] MB-31 — Execute tool calls through ToolRegistry

Goal:

All normal runtime tool calls resolve through:

```text
ToolRegistry
 -> ToolDescriptor
 -> executor
```

Acceptance:

- unknown tool fails closed;
- descriptor access/risk/scope data is available at execution;
- normal runtime does not use the giant `AgentTools.Execute` switch directly.

Evidence: `Runtime/MbToolRegistryExecutionTests.cs` verifies the selected descriptor executor is the only executed path, unknown model tools return a typed fail-closed `unknown_tool` result without fallback execution, descriptor access/risk/scope metadata is present at the scheduler boundary, and normal runtime/orchestrated source contains no direct `AgentTools.Execute` call. GitHub Actions run `35351897477` reported **3 passed, 0 failed** and full CI succeeded.

---

## [x] MB-32 — Make deferred tool loading real during the same task

Goal:

When model calls `tool_search`, newly selected schemas become available on continuation.

Acceptance scenario:

```text
initial request:
  tool_search only

model:
  tool_search("read formulas")

host:
  selects relevant formulas tools

continuation:
  receives newly loaded schema(s)

model:
  calls selected tool
```

Acceptance:

- no new user turn required;
- duplicate schema loads coalesce;
- registry version invalidation works.

Evidence: `Runtime/MbDeferredToolLoadingTests.cs` exercises the real runtime plus discovery/index state. GitHub Actions run `35353071661` reported **3 passed, 0 failed**: tool_search loads and executes the selected schema in the same task without a new user turn, duplicate schema loads coalesce, and ToolSearchIndex rebuilds exactly when ToolRegistry.Version changes.

---

## [x] MB-33 — Wire ToolExecutionScheduler into real calls

Goal:

Use existing scheduler semantics.

Acceptance:

- independent safe reads may overlap;
- same-resource mutations cannot overlap;
- mutating call without resource identity fails closed;
- cancellation propagates.

Evidence: `Runtime/AgentRuntime.cs` routes normal runtime calls through `ToolExecutionScheduler`; `Runtime/MbSchedulerRuntimeTests.cs` proves parallel-safe reads overlap, same-resource mutations serialize, unscoped mutation fails before executor, and cancellation reaches both executor and transport. Workflow source commit `c8881fc47ca07bc828ea64b345b4f4509958e9b7` completed through the final publish step, producing bot publish commit `cf04040de47d8c3d0847bc571731986decd26e4b` after the MB-33 CI step.

---

# Stage E — Permission / evidence / verification becomes part of the loop

## [x] MB-40 — Centralize normal runtime permission enforcement

Goal:

Permission is host policy around ToolRegistry execution, not scattered special cases.

Preserve:

- read vs mutation;
- user approval policy;
- resource scope;
- declined-scope memory where appropriate;
- stale-state guard;
- sandbox restrictions;
- secrets boundary.

Acceptance:

- one tool cannot bypass a declined mutation through a different executor;
- skill text cannot grant permissions.

Evidence: `Runtime/AgentRuntimePermission.cs` centralizes host authorization and declined-scope memory before ToolRegistry execution; `Runtime/MbPermissionRuntimeTests.cs` covers read-only mutation denial, alternate-tool bypass after a declined scope, hostile skill/model permission claims, and v1 mutation scope coverage. Regression run `35358313815` on code commit `0a155ab7d1665113c0e96fefe75fbde621de0d3d` passed MB-33, MB-40, Desktop/Office acceptance, and the complete provider transport matrix after restoring the MB-33 fail-closed invariant for unscoped mutations.

---

## [x] MB-41 — Wire artifacts/evidence into tool results

Goal:

Important tool results produce bounded context projections and durable evidence/handles.

Acceptance:

- large outputs are not injected in full;
- evidence hashes are stable;
- verifier can reference evidence IDs;
- final answer can cite/describe actual observed work without replaying giant raw outputs.

Evidence: `Runtime/AgentRuntimeEvidence.cs` projects important/large tool outputs into durable `ArtifactStore` handles; `AgentRuntime` returns evidence history and passes evidence IDs into runtime verifiers. `Runtime/MbEvidenceRuntimeTests.cs` covers bounded large-output projection, stable hashes, readable small evidence and verifier evidence references. Exact-head source commit `3bc34025f66b2c07c9c5c0aecbc7c24cbe6692cc` completed the full workflow and produced publish commit `d19af177489a6c1a1a04383cd0527ad737f82300`.

Evidence: `Runtime/AgentRuntimeEvidence.cs` stores important/large tool output in the existing `ArtifactStore` and returns bounded projections with durable SHA-256 handles; `Runtime/AgentRuntime.cs` carries typed evidence into verifier context and final runtime results; `Tools/V1ToolRegistryAdapter.cs` marks important reads and all mutations as evidence-capable; `Tasking/AgentOrchestratedRun.cs` surfaces evidence ID/hash/summary in the UI trace. `Runtime/MbEvidenceRuntimeTests.cs` covers large-output bounding/raw round-trip, stable hashes, small important observations, v1 evidence metadata, verifier evidence references, and final-answer evidence handles. Full regression run `35359705425` on source commit `3bc34025f66b2c07c9c5c0aecbc7c24cbe6692cc` completed successfully through MB-41, DesktopHost, OfficeHost, all provider transports, and package publication.

---

## [x] MB-42 — Integrate verification into normal mutating tasks

Goal:

Replace:

```text
legacy runner returned
 -> block because verifier is not wired
```

with:

```text
act
 -> reobserve
 -> verify
 -> pass/fail report
```

Acceptance:

- tool success alone cannot complete mutation;
- at least file, Python, and one structured application verifier operate in the real runtime.

Evidence: normal mutating UI tasks now require the host-owned `runtime.mutation-verified` criterion and `runtime-domain-router`; `AgentRuntimeDomainVerifierRouter` runs after real tool execution using host-only raw outputs while model context receives bounded evidence projections. `FileRuntimeDomainVerifier` re-reads workspace bytes and uses `FileScopeVerifier`; `PythonRuntimeDomainVerifier` reloads durable run evidence, re-reads every recorded output artifact so hashes are checked against actual bytes, then uses `PythonResultVerifier`; structured Word mutations are checked with `LiveWordVerifier`. Missing verification becomes a typed host block instead of model-declared completion or an unhandled UI failure. Exact source commit `cb8821f2c1e7f68ddac076b5add874a18edcbbed`, GitHub Actions run `35364524079`: MB-42 **4 passed, 0 failed**; all preceding MB suites, DesktopHost, OfficeHost, provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `2bf48744b00445e55a6ae8124b86849fe274f888`; portable ZIP SHA256 `ac3d1002ed1999aec6667c896fbcaab72608411cfb32612460fc513340ade1fc`.

---

## [x] MB-43 — Integrate bounded repair loop

Goal:

Use `AgentRepairController` in real execution.

Flow:

```text
verification FAIL
 -> build failed-criteria-only repair context
 -> model continues
 -> targeted corrective tool calls
 -> verify again
```

Acceptance:

- already-passed criteria are preserved;
- full transcript is not replayed;
- retry budget is bounded;
- repeated identical failing mutations are stopped.

Evidence: `AgentRuntime` now keeps bounded repair state, merges prior passed criteria into later verifier snapshots, injects only `AgentRepairController` failed-criteria repair context into the continuation result, enforces `MaxRepairRounds`, and canonicalizes failed mutation tool/arguments so an identical retry is rejected before the executor with `repeated_failed_mutation`. `Runtime/MbRepairRuntimeTests.cs` verifies failed-only context without full transcript replay, preservation of already-passed criteria, canonical duplicate-mutation blocking before a second executor call, and the host repair retry budget. Exact source commit `7c7acd4ee484c136755ced564bf9466c3c3d8729`, GitHub Actions run `35365463245`: MB-43 **3 passed, 0 failed**; all earlier MB suites, DesktopHost, OfficeHost, provider transport suites and self-contained publish also passed. Publish bot commit `423ca7223ab466199d18ed5aabf4c2a7f63bb523`; portable ZIP SHA256 `0a28e4447561dce7a860d487b3d0c295c4e098cf43ebc31ede81fe050dbce53c`.

---

## [x] MB-44 — Host-owned final completion gate

Goal:

Only host state may mark a mutating task complete.

Acceptance:

- model phrase “done” is insufficient;
- required verifier failures keep task non-complete;
- non-mechanically-verifiable tasks have explicit classification/evidence.

Evidence: host completion ownership remains in `AgentOrchestrator`/`AgentTaskStateMachine`: a mutating runtime may return model text such as `done`, but without accepted host verification the orchestration state becomes `Blocked`, never `Completed`. `AgentVerificationOutcome` now carries typed non-mechanical evidence, `AgentEvidenceKind.HostClassification` records the explicit host classification, and `AgentTaskCompletionGate` permits that exceptional path only when policy explicitly allows it, no concrete verifier IDs are being bypassed, and host-classification evidence is present. `AgentOrchestrator.CompleteNotMechanicallyVerifiable` is the explicit host API for that path. `Runtime/MbCompletionGateTests.cs` proves model-only completion is insufficient, a required verifier failure stays non-complete, and non-mechanical completion requires explicit host classification evidence. Exact functional source commit `c720266865beac2f60aefafbefe627bb7bcb779e`, GitHub Actions run `35368661263`: MB-44 **3 passed, 0 failed**; MB-10 through MB-43 regression suites, H2 Notes, DesktopHost, OfficeHost, provider transports, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `53d26578e49f4a9b115e12eb5a8a1d43abe29ba4`; portable ZIP SHA256 `d986b9654b502aae92b2e19731fad1b67118604526f9e58307f6c3972c34ffaa`.

---

# Stage F — Simplify Skills instead of building a second AI

## [x] MB-50 — Establish one canonical SkillCatalog

Goal:

Unify current overlapping skill paths.

Current components to reconcile:

- `SkillCatalog`;
- `DeferredSkillSession`;
- `PluginSkillCatalog`;
- `UnifiedSkillCatalog`.

Target:

```text
SkillCatalog
   +-- BuiltInSkillSource
   +-- PluginSkillSource
```

Acceptance:

- one logical search API;
- one logical read API;
- provenance retained;
- compatibility adapter allowed temporarily;
- no duplicate parser behavior after final migration.

Evidence: `H2AgentLab.Skills.SkillCatalog` is now the canonical host-facing catalog and owns the single `Search` / `Read` / `ReadResource` dispatch surface for registered sources. `BuiltInSkillSource` owns built-in metadata parsing and preserves source/hash provenance; `PluginSkillSource` remains the plugin source adapter over `PluginSkillCatalog`. Historical `UnifiedSkillCatalog` is a behavior-free subclass compatibility name, while the root `H2AgentLab.SkillCatalog` is now a thin v1 facade that constructs/registers `BuiltInSkillSource` and delegates discovery/reads to the canonical catalog instead of parsing frontmatter itself. `DeferredSkillSession` now wraps the canonical catalog and only caches selected content hashes/versions; it no longer owns a separate filesystem/parser path. Installed capability projection and the historical missing-capability continuation now accept the canonical catalog type. `Skills/MbSkillCatalogTests.cs` proves canonical built-in search/read/provenance, multi-source dispatch with plugin provenance, legacy/deferred adapter identity, and a source guard against duplicate built-in parser/catalog logic. Exact functional source commit `219e5325007d0d97a76c2de537ab2d8585109ce7`, GitHub Actions run `35371132588`: MB-50 **4 passed, 0 failed**; H2 Notes, legacy/v2 Agent suites, MB-10 through MB-44, Phase 10/11/extensibility regression, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `06e6ca8b0b4feda281215640ffa3566fded06a22`; portable ZIP SHA256 `afe24c8c7c09daeb23f413bf7575bc3f27b3f912682e1334ef7b50889ba2baff`.

---

## [x] MB-51 — Codex-style discovery only: name + description

Goal:

Keep authoring simple.

Required:

- local skill search uses bounded metadata;
- exact skill-name match not required;
- no mandatory aliases/intents/examples schema;
- no generic hard-coded domain synonym dictionary.

Acceptance:

A skill named:

`cad-integrity`

with description mentioning dynamic blocks must be selectable for:

`"check parameters and actions of these dynamic blocks"`

without the query containing `cad-integrity`.

Do not satisfy this test by adding AutoCAD-specific synonym code to Agent core.

Evidence: canonical local skill discovery ranks only bounded `name + description` metadata from `BuiltInSkillSource` / `PluginSkillSource`; full `SKILL.md` body content is not part of discovery. Minimal skill authoring requires only `name` and `description`, with description length bounded by the host parser. Generic capability ranking no longer expands terms through the former hard-coded synonym dictionary (`dynamic/parameters/actions/blocks/autocad/legal` mappings were removed), leaving domain meaning to skill/tool descriptions, model understanding, or optional specialized search extensions. `Skills/MbSkillDiscoveryTests.cs` proves the exact acceptance query `"check parameters and actions of these dynamic blocks"` selects differently named `cad-integrity` from its description, proves body-only sentinel terms are not searchable, proves minimal name+description authoring plus metadata bounds, and source-guards against domain synonym tables or mandatory aliases/intents/examples parsing. Exact functional source commit `a59e6cc46fab106b0bb21d72879b6edde9edf88e`, GitHub Actions run `35371871246`: MB-51 **4 passed, 0 failed**; H2 Notes, Phase 10/11/extensibility, MB-10 through MB-50, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `50c09c4d8e69f769fd4c73193fcee35b8b0226f1`; portable ZIP SHA256 `b2cd38882f375c77abf3af008f9ba0bf0cf19cb58969462a36b589cb92474d4a`.

---

## [x] MB-52 — Progressive skill loading

Goal:

Use:

```text
metadata
 -> SKILL.md
 -> selected reference/script/asset only when requested
```

Remove mandatory host parsing of custom:

`resource: references/...`

directives.

Instead provide bounded skill-resource access callable by the model/runtime.

Acceptance:

- selecting a skill does not load every resource;
- model may request one relevant reference;
- scripts/assets are not trusted automatically;
- source hash/version evidence preserved.

Evidence: `SkillRuntimeToolExecutor` now provides the normal read-only runtime bridge over the canonical `Skills.SkillCatalog`: `list_skills` returns bounded name/description/provenance metadata, while `read_skill` loads exactly the selected `SKILL.md` or one explicitly requested `references/`, `scripts/`, `assets/` or `agents/` resource. Runtime projections are bounded to 32,000 characters and carry source/plugin identity, SHA-256 and derived version evidence; script/asset responses are explicitly inert data/text and this path never executes them. `V1AgentToolsExecutor` routes normal V2 skill calls through this canonical bounded executor while preserving the temporary v1 tool-schema bridge. The historical `MissingCapabilityContinuation` no longer parses `resource:` directives or auto-loads resources; after installation it loads only the selected `SKILL.md`, leaving resources for an explicit runtime/model request. Phase-11 regression fixtures were updated to natural SKILL.md prose plus explicit `ReadResource` access. `Runtime/MbProgressiveSkillLoadingTests.cs` proves the real `AgentRuntime` path `tool_search -> metadata -> SKILL.md -> one explicit reference`, proves resource bodies are absent until requested, proves script/asset reads do not execute content, proves independent skill/resource hash+version provenance, and source-guards against mandatory resource-directive parsing. Exact functional source commit `9456a9789a04ee43cc26217eb2ac1b0c11b63032`, GitHub Actions run `35373183466`: MB-52 **4 passed, 0 failed**; H2 Notes, Phase 10/11/extensibility, MB-10 through MB-51, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `c370ba41d9de8f04d59793dff11c5ea4f24746a8`; portable ZIP SHA256 `fd80cce646043a506b5a096536104c6d6e29b0697532482f976899ce709a0673`.

---

# Stage G — Extension bus and first-party cards

## [x] MB-60 — Define one simple extension registration boundary

Goal:

A provider/plugin/first-party extension can register:

- tool descriptors;
- skill source;
- verifier(s);
- provider lifecycle;
- optional metadata.

Agent core must not be edited to add a new application family.

Acceptance:

Create a deterministic fake `CalculatorExtension` or equivalent outside core.

Install/register it.

Agent uses it through ToolRegistry without changes to AgentRuntime.

Evidence: `Extensions/AgentExtension.cs` now defines one application-neutral `IAgentExtension` / `AgentExtensionRegistry` boundary backed by the existing authoritative `ToolRegistry`, canonical `Skills.SkillCatalog`, `ArtifactVerifierRegistry`, and `CapabilityProviderManager`. Extensions may register tool descriptors, one or more skill sources, verifiers, provider lifecycle implementations and normalized optional metadata without adding application-family branches to AgentRuntime. A deterministic `FirstPartyExtensions/Calculator/CalculatorExtension.cs` fixture lives outside Runtime/core-specific code and registers `calculator.add`, `calculator-basics`, `CalculatorArtifactVerifier`, `CalculatorCapabilityProvider`, and extension metadata through that single boundary. `Extensions/MbExtensionRegistrationTests.cs` proves all five surfaces register through host-owned registries, provider connect lifecycle and verifier dispatch remain host-owned, real `AgentRuntime` discovers/loads/executes `calculator.add` through ordinary `tool_search -> ToolRegistry` flow, and source guards prove AgentRuntime and the generic extension boundary contain no Calculator-specific logic. Exact functional source commit `4aa2d5b99d28b50d7ee4d1d4b042288ce89fd298`, GitHub Actions run `35374132540`: MB-60 **4 passed, 0 failed**; H2 Notes, Phase 10/11/extensibility, MB-10 through MB-52, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `233353de5062c5a10214f4990e3362e9dc16eb67`; portable ZIP SHA256 `49f9c5cf4517ee23c1dcc463a9d0a98b9186344dcaeb09e8f1b93f8ae15670cb`.

---

## [x] MB-61 — Remove app-specific ranking from generic core

Review:

- `DocumentToolPreference.cs`;
- `InteractionAdapterPreference.cs`.

Goal:

Replace hard-coded Word/Excel/Python application logic with generic provider/tool metadata or extension policy.

Keep the generic preference:

```text
structured typed interface
 > accessibility/UIA
 > screenshot/pixel
```

Acceptance:

Adding a new structured Revit-like provider would not require editing a Word/Excel switch in Agent core.

Evidence: `ToolDescriptor` now accepts optional `ToolPreferenceMetadata` containing an application-neutral `capabilityFamily`, `ToolInteractionFidelity` (`Structured`, `Accessibility`, `Visual`, `EscapeHatch`), and optional provider-declared explicit-request terms. `DocumentToolPreference` is retained only as a compatibility class name; its implementation contains no Word/Excel/Office/Python/AutoCAD/Revit knowledge and reorders only equivalent provider-declared capability-family candidates. `InteractionAdapterPreference` now chooses generic `InteractionAdapterCandidate` instances from the same metadata model, preserving `structured typed > accessibility/UIA > screenshot/pixel > escape hatch` while allowing an extension/provider to explicitly expose an escape hatch when the user's request matches its own metadata. `DeferredToolDiscovery` no longer contains the special `run_python` filter; compatibility providers declare their own preference metadata in `V1ToolRegistryAdapter`, while `StructuredOfficeCapabilityCatalog` declares itself structured outside generic core. The legacy Desktop regression and architecture guard were migrated to generic candidate fixtures instead of application switches, and `PythonFallbackRouter` no longer depends on application-intent parsing in `DocumentToolPreference`. `Tools/MbGenericToolPreferenceTests.cs` proves generic fidelity ordering, provider-declared explicit-only behavior, application-neutral adapter choice, compatibility metadata declaration, and a new Revit-like structured provider ranking correctly through normal `DeferredToolDiscovery` without any Revit/application token in Agent core preference files. Exact functional source commit `9b739f45f2ded72340bb42a3c06fbff0f35c4d58`, GitHub Actions run `35376102751`: MB-61 **4 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-60, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `de5a271d7fe69b2b565dcb47ad8e8ac23748ca7e`; portable ZIP SHA256 `efa402d6b8039a67b503f617d17c352378e4dccb7b29759b3969d60832034d9a`.

---

## [x] MB-62 — Convert general computer capability inventory to provider registration

Review:

- `Computer/GeneralComputerCapabilityCatalog.cs`.

Goal:

Avoid one central static list becoming the definition of all future machine control.

Prefer:

```text
FileSystem extension registers filesystem.*
Process/Shell extension registers process.* / shell.*
Desktop extension registers app/window/uia/input/screen.*
Browser extension registers browser.*
```

Acceptance:

AgentRuntime knows only ToolRegistry, not the list of all possible computer functions.

Evidence: `GeneralComputerCapabilityCatalog` no longer contains any computer tool definitions; it is now a compatibility projection over the authoritative `ToolRegistry`, filtered only by provider provenance. Capability ownership is split into independent first-party provider cards under `FirstPartyExtensions/Computer`: `FileSystemComputerExtension` owns `filesystem.*`, `ProcessShellComputerExtension` owns `process.* / shell.*`, `DesktopComputerExtension` owns `app.* / window.* / uia.* / input.* / screen.*`, and `BrowserComputerExtension` owns `browser.*`. Shared registration mechanics live in `ComputerCapabilityExtensionBase`, while each provider file owns its own names/descriptions/risk/access/fidelity metadata and registers through the MB-60 extension bus into ToolRegistry. Phase-10 regression `1012` was migrated from the old static master inventory to actual provider registration and provenance checks. `Computer/MbComputerExtensionRegistrationTests.cs` proves independent namespace ownership, proves partial registration does not invent absent capability families, proves a brand-new `camera.*` family appears through registry provenance without editing the compatibility catalog or AgentRuntime, proves real AgentRuntime executes `filesystem.list` through ordinary `tool_search -> ToolRegistry`, and source-guards that the compatibility catalog contains no master capability names. Exact functional source commit `f20c2f07e3ef2b0799251e1705ce123ce3c78773`, GitHub Actions run `35377085374`: MB-62 **5 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-61, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `20f319bcdde6c1c5fbd4c8f599594e1214eafda6`; portable ZIP SHA256 `be47ad319ccb1ea0934f953316d9febf5648ce6508680eda9c7e89bb06ae5891`.

---

## [x] MB-63 — Keep first-party extension implementations

Preserve and adapt:

- Office;
- Web;
- Desktop;
- AutoCAD;
- MCP;
- FileSystem;
- Process/Shell;
- Python sandbox.

Goal:

They become reference cards on the extension bus.

Do not delete working capability merely because it is no longer “core”.

Evidence: the working first-party implementations were preserved and adapted as reference cards on the MB-60 extension bus instead of being rewritten or deleted. Existing MB-62 cards remain the Desktop (`DesktopComputerExtension`), FileSystem (`FileSystemComputerExtension`) and Process/Shell (`ProcessShellComputerExtension`) references. New thin cards reuse existing seams: `OfficeFirstPartyExtension` delegates to `StructuredOfficeCapabilityCatalog`; `WebFirstPartyExtension` registers the existing `WebResearchHost`; `AutoCadFirstPartyExtension` delegates to `AutoCadProviderPolicy.BuildDescriptors`; `McpFirstPartyExtension` registers the existing `McpToolProvider`; and `PythonSandboxFirstPartyExtension` projects only the preserved v1 Python namespace through `V1ToolRegistryAdapter` while keeping the supplied executor path to `AgentTools -> ScriptWorkspace -> WindowsPythonSandbox`. `CapabilityProviderToolRegistryAdapter` and the new generic `CapabilityProviderManager.LoadProviderToolsAsync` allow any registered `ICapabilityProvider` (including Web and MCP) to lazily project selected schemas into ToolRegistry; the existing `LoadMcpToolsAsync` API remains as a compatibility wrapper. `Extensions/MbFirstPartyExtensionCardsTests.cs` proves all eight requested families have cards on one bus, Web/MCP lazily load and execute through the provider-neutral adapter, the Python card preserves its compatibility executor plus AppContainer/no-network security profile, real AgentRuntime discovers/executes an AutoCAD card through ordinary ToolRegistry flow, and source guards confirm the original Office/Web/Desktop/AutoCAD/MCP/FileSystem/Process-Shell/Python implementation files still exist while AgentRuntime contains no first-party application references. Exact functional source commit `8fc65a1c0d3881cee75298e09e8ecd8ab1f5e2ae`, GitHub Actions run `35378292318`: MB-63 **5 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-62, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `38731020ae2257b850d0910536acd1453b72d068`; portable ZIP SHA256 `a70fc54d4dcf4ec8c6ffb2d002bfc2795de9c72847ceb5d9c5fc0ebc13e065dc`.

---

# Stage H — Remove over-design from capability/catalog layer

## [x] MB-70 — Remove generic domain synonym intelligence

Required:

Delete/replace generic core maps like:

```text
audit -> inspect/check/review
dynamic -> parameter/action/visibility
blocks -> autocad/cad
legal -> law/regulation
```

from capability core.

Acceptance:

- no AutoCAD/legal domain synonym dictionary in generic capability code;
- tests rely on realistic descriptions/model selection, not hidden domain mapping.

Evidence: generic capability ranking is now explicitly lexical-only: `CapabilityRanking.SemanticTerms` was removed/renamed to `LexicalTerms`, and both `InstalledCapabilityIndex`, `AvailableCapabilityIndex`, and `CapabilityResolver` match only normalized tokens present in capability IDs/descriptions/compact package metadata. No domain-expansion table is consulted by capability core; meaning must come from realistic tool/skill/plugin descriptions, model reasoning, or an optional specialized search extension outside this core. `Capabilities/MbDomainNeutralCapabilitySearchTests.cs` proves negative behavior (`dynamic`, `audit`, `legal`, and `autocad` do not magically match unrelated `parameters/actions/visibility`, `inspect/check/review`, `law/regulation`, or `CAD` terms), proves realistic installed AutoCAD/legal descriptions remain discoverable when the query and metadata actually share relevant terms, proves available package search works from compact realistic metadata, and source-guards `CapabilityIndexes.cs` + `CapabilityResolver.cs` against synonym helpers or the prohibited domain mappings. Exact functional source commit `0fa8a8afce87a0d1cf91ad9ee6615e209c0a5f56`, GitHub Actions run `35379228605`: MB-70 **4 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-63, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `a69390cf3033ef60f51bbbeee21dd37321563675`; portable ZIP SHA256 `a4b2834519c7bee4f7c6eeaaacd8aec9411a8fa4d551a1e5134c6b4f13ceaa4f`.

---

## [x] MB-71 — Simplify InstalledCapabilityIndex

Decision:

Choose one:

A. remove it; or  
B. keep only as a derived projection/cache.

It must not duplicate authoritative state or truncate correctness.

Authoritative state remains:

- ToolRegistry;
- SkillCatalog;
- ProviderManager;
- PluginManager.

Acceptance:

- no correctness depends on a hard `Search("", 100)` inventory limit;
- unrelated registry changes do not invalidate task semantics.

Evidence: chose **B — derived live projection only**. `InstalledCapabilityIndex` no longer owns a cached `InstalledCapabilityRecord[] _records`; `Records` and `Search` project current authoritative `ToolRegistry`, canonical `SkillCatalog`, and provider metadata on demand. `Rebuild` remains only as a compatibility binder for callers holding a provider snapshot, while new `Bind(..., Func<IReadOnlyList<ProviderProvenance>>)` supports live provider state; `MissingCapabilityContinuation` binds once to its authoritative registry/catalog/provider accessor and no longer rebuilds an installed cache after plugin installation. To eliminate the historical `skills.Search("", 100)` correctness cap, canonical skills now expose an optional exact `IInstalledSkillMetadataSource.SnapshotMetadata()` seam: built-in, plugin and Calculator sources implement it, `SkillCatalog.SnapshotMetadata()` merges exact source snapshots without a search-result limit, and unsupported search-only sources fail closed rather than silently truncate inventory. `Capabilities/MbInstalledCapabilityProjectionTests.cs` proves registry/provider changes are reflected without rebuild while an unrelated tool does not alter the selected primary capability, proves a target skill placed after 150 entries remains discoverable even though the source's ordinary empty search returns only 100, proves unsupported exact snapshots fail visibly, and source-guards against `_records`, `skills.Search("", 100)`, or post-install cache rebuilds. Exact functional source commit `99ef51d3dc80132e7a8c9b03c30e30238b0bd2c9`, GitHub Actions run `35380649917`: MB-71 **4 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-70, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `2ae242ef89432d1700a6add3789f49698b240f21`; portable ZIP SHA256 `448611e4ebbaca3dd6efa1ba73d25acf590a9f4c07810c54088289241f3b1da3`.

---

## [x] MB-72 — Simplify AvailableCapabilityIndex

Goal:

Do not require a heavy available index for a small catalog.

Allowed:

- compact in-memory search;
- optional derived cache.

Do not add vector DB now.

Acceptance:

- remote metadata search works for current acceptance scale;
- architecture leaves room for future semantic search extension.

Evidence: `AvailableCapabilityIndex` is now a small derived/search projection instead of an authoritative remote catalog store. It binds to a live `Func<IReadOnlyList<AvailableCapabilityRecord>>` view (normally `CatalogSourceManager.CachedView().Entries`), normalizes/validates only compact metadata at search time, and keeps `Rebuild(...)` solely as a compatibility binder for fixed test/legacy snapshots. `CapabilityResolver` binds the available view to `CatalogSourceManager` and no longer copies refreshed metadata back into the index with `_available.Rebuild(...)`; authoritative remote metadata remains in catalog source caches. The default search stays lightweight deterministic lexical ranking, while `IAvailableCapabilitySearchStrategy` / `LexicalAvailableCapabilitySearchStrategy` provide a narrow replaceable ranking seam for a future specialized/semantic search extension without changing catalog storage or adding a vector database now. `Capabilities/MbAvailableCapabilitySearchTests.cs` proves live catalog changes appear without rebuilding the index, compact lexical search finds a target across 501 catalog records without historical small-candidate truncation, a custom search strategy can replace ranking through the seam, and source guards confirm the available index has no authoritative `AvailableCapabilityRecord[] _records`, the resolver does not duplicate refreshed catalog metadata into it, `CatalogSourceManager` remains the metadata cache owner, and no vector infrastructure was introduced. Exact functional source commit `16a08dbfd70108ba7721dafb95ccff87b175033e`, GitHub Actions run `35381899950`: MB-72 **4 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-71, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `497fec7dee1ed6d4ed33aee0c7e8b92d69f1f7d0`; portable ZIP SHA256 `72983c6c26c5b705af4a576d9cf8180f0f4ded3063fc85bc0f88f20e30120c5d`.

---

## [x] MB-73 — Thin or remove CapabilityResolver

Goal:

The model decides what it needs; host reports availability and enforces policy.

If retained, CapabilityResolver should only:

- search installed metadata;
- search catalog metadata;
- report status;
- apply host policy.

It must not become a semantic central planner.

Acceptance:

- no domain-specific synonyms;
- no assumption that every missing capability is a skill;
- a plugin containing only tools can be installed/useful.

Evidence: `CapabilityResolver` was retained only as a thin host metadata/status layer. It first queries the derived installed projection; if no installed candidate exists it queries compact available package metadata, refreshes catalog metadata when needed, and reports only `INSTALLED / AVAILABLE / BLOCKED_BY_POLICY / INCOMPATIBLE / UNSUPPORTED` plus package provenance. It no longer scores/selects child skills, explodes a package into semantic skill candidates, assumes every missing capability is a skill, downloads package bytes, invokes `PluginManager`, or performs installation. Available results remain package-level so the model/runtime may decide which tool/skill/provider inside a selected extension is useful later. Tool-only and provider-only packages are first-class candidates with `SkillId = null`; multi-skill packages remain one package candidate rather than host-side semantic planning. `MissingCapabilityContinuation` was kept temporarily compatible with package-level candidates only and remains scheduled for retirement in MB-74. `Capabilities/MbThinCapabilityResolverTests.cs` proves installed metadata short-circuits catalog work, tool-only packages resolve as available, multi-skill packages remain package-level, compatibility/policy statuses are host-owned, provider-only packages do not invent skills, and source guards ensure no domain synonym knowledge, skill-child ranking, package retrieval or install logic lives in CapabilityResolver. Exact functional source commit `0e87964042c0c146d0214edf34f30cd9cd76e4e0`, GitHub Actions run `35382748768`: MB-73 **6 passed, 0 failed**; architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-72, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication succeeded. Publish bot commit `2e7f6fcb81994621b3c7efcad08d9c6f2449a1af`; portable ZIP SHA256 `a0fdad9853769b9966ad0c4e8eaa211a654dd74a02f793d6870d449b2a94ed69`. A supplementary test-only MB-73 commit `0277bb0d7e4f7c138bf0c5afdc9fbdc7fd4ae56b` strengthened provider-only compact-metadata coverage; GitHub Actions run `35383298339` also completed **success** with MB-73 **6 passed, 0 failed** and the full regression matrix green. Its repository ZIP publication correctly skipped as stale because the docs-only tracker close had already advanced the branch, so the published binary evidence remains the exact unchanged functional product build from source `0e87964042c0c146d0214edf34f30cd9cd76e4e0`.

---

## [x] MB-74 — Retire MissingCapabilityContinuation

Goal:

Remove the giant one-off workflow class.

Replace with ordinary runtime interaction:

```text
model -> catalog_search
model -> install request
host -> PluginManager
runtime -> safe refresh
model -> continue
```

Acceptance:

- plugin may contain only tools;
- plugin may contain only skills;
- plugin may contain provider + tools;
- original task continues without special skill-required result type.

Evidence: the one-off `Capabilities/MissingCapabilityContinuation.cs` workflow was deleted and replaced by ordinary runtime tools in `Catalog/CatalogRuntimeTools.cs`. `catalog_search` exposes compact remote package metadata through the thin `CapabilityResolver.SearchCatalogAsync(...)` seam; `plugin_install` accepts only the exact plugin ID/version selected by the model while host-owned trust policy, approval, immutable retrieval and `PluginManager` verification/activation remain outside model arguments. Installation returns package-level tool/skill/provider metadata without auto-selecting or auto-loading skill content. Newly registered tools become discoverable in the same task through normal `tool_search` registry-version refresh, while newly installed skills appear through the canonical `SkillCatalog` / `PluginSkillSource` path and are read only after explicit model selection. `Capabilities/MbRuntimeCapabilityInstallTests.cs` drives one real `AgentRuntime` turn through tool-only, skill-only and provider-plus-tool plugin fixtures, proves the original task continues with exactly one `StartAsync` call and no user restatement, proves host approval is absent from the callable install schema, and source-guards retirement of the special continuation type. The historical Phase-11 1122 acceptance path now uses the same ordinary catalog/install flow; MB-52 and MB-71 source guards were updated to treat the removed source as intentional rather than a required runtime dependency. Exact functional source commit `255b54e060e071e45ac6bf7ceccd0bd5c6430835`, GitHub Actions run `35402814765`: MB-74 **2 passed, 0 failed**; MB-70/71/72/73, architecture guard, H2 Notes, Phase 10/11/extensibility, MB-10 through MB-63, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository portable ZIP publication all succeeded. Publish bot commit `e4a97bde210763cd6fbd5fd2eb39d4f0653052f2`; repository portable ZIP SHA256 `ac6381614eb1d5bdc634bff0a52c6bc4eb469049f6e52abbb70579846a87f88f`.

---

## [x] MB-75 — Simplify task capability pinning

Review:

- `TaskCapabilitySnapshot.cs`.

Goal:

Record only used/selected capabilities, not entire installed machine state.

Acceptance:

Installing an unrelated plugin during a safe boundary does not invalidate a task that never used it.

Evidence still records:

- used tool version/schema;
- used provider version;
- used plugin version;
- used skill hash;
- model/provider identity.

Evidence: `TaskCapabilitySnapshot.cs` now captures explicit task-local `TaskCapabilitySelection` rather than treating the full installed machine as task state. `CaptureUsed(...)` resolves exact versions only for used tool names, used provider IDs, used/selected plugin IDs and selected skill hashes; model/provider identity plus important policy/version identifiers are recorded alongside them. `TaskCapabilityPinGuard` no longer compares `ToolRegistry.Version` as a correctness condition: unrelated registry/provider/plugin/skill additions are ignored, while a changed used tool schema/version, used provider, used plugin, selected skill hash, model identity or pinned policy is rejected until an explicit usage revision. The old capture/revision overloads remain only as compatibility helpers and scope themselves to selected plugin capability rather than whole-machine inventory. `Capabilities/MbTaskCapabilityPinningTests.cs` proves used-only capture/evidence, proves an unrelated plugin/registry change does not invalidate the active task, proves a used capability change is rejected until revision, and source-guards against reintroducing whole-registry version pinning. Historical Phase-11 acceptance 1120 was updated to the same usage-based semantics. Exact functional source commit `8cf8b9a3ad55e247a5a3ace8a1f059de1da90ba1`, GitHub Actions run `35416789846`: MB-75 **4 passed, 0 failed**; H2 Notes, architecture guard, Phase 10/11/extensibility, MB-10 through MB-74, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `355f7f65b5700e17cf3891f3b6855a813d7e1d44`; repository portable ZIP SHA256 `ebddc4fe9de13f747dfdfdd248bbb5e402d9dc8d7d3a5666d55dd6a0a1dda612`.

---

# Stage I — Minimal external catalog/install path only

## [x] MB-80 — Keep one minimal ICatalogSource seam

Goal:

Support configurable external metadata without committing to a marketplace platform.

Minimum source contract:

- source identity;
- search/list compact metadata;
- retrieve/resolve selected package location.

One working source is enough for core acceptance.

Possible acceptance source:

- deterministic local folder; or
- simple HTTP/GitHub index fixture.

Do not require full multi-source arbitration yet.

Evidence: `ICatalogSource` now exposes one small external-package seam: source identity/provenance, compact metadata fetch/search, and exact `ResolvePackageLocationAsync(...)`; it does not retrieve, execute, verify or install package bytes. A deterministic `LocalFolderCatalogSource` reads compact `catalog.json` metadata from a configured root, validates bounded metadata and safe relative package paths, searches through the existing lightweight available-capability ranking, and resolves only an exact selected plugin/version/hash/location. The source root remains constructor/configuration data and is absent from `AgentOrchestrator`. `CatalogSourceManager.ResolvePackageLocationAsync(...)` routes an exact selected source and cross-checks resolved hash against cached metadata. The ordinary `plugin_install` runtime flow now re-resolves the selected package location through the catalog source before handing it to `IPackageRetriever`, preserving the catalog/retrieval boundary. Existing multi-source merge support remains compatible but is not required by this acceptance path. `Catalog/MbMinimalCatalogSourceTests.cs` proves configured local list/search/resolve, traversal rejection, location-only resolution with no retrieval/install side effect, and the architectural source guard. Exact functional source commit `08aae788829fe8cce9c7c14cf563b7993ede1041`, GitHub Actions run `35417188124`: MB-80 **4 passed, 0 failed**; H2 Notes, architecture guard, Phase 10/11/extensibility, MB-10 through MB-75, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `fb57bf6d475cace036e98c39733497c7ca3bebb3`; repository portable ZIP SHA256 `11a9c2a24bdbb6cbdbb39f799d86f9ca1c1e511f6156cf49d2206f3e195f29bc`.

---

## [x] MB-81 — Keep IPackageRetriever + PluginManager separation

Flow:

```text
catalog metadata
 -> user/host selects package
 -> IPackageRetriever
 -> staged bytes
 -> PluginManager verification/install
```

Acceptance:

- bad hash rejected;
- oversize rejected;
- cancel/timeout works;
- package bytes never execute before PluginManager verification.

Evidence: `Catalog/PackageRetriever.cs` remains a byte-staging boundary only: `LocalPackageRetriever` copies from approved source roots into private staging, enforces a total retrieval deadline, explicit cancellation, configured maximum size and exact expected archive SHA256, deletes failed staging artifacts, and never references or invokes plugin installation. `PluginManager` remains the sole verification/install/activation authority and has no dependency on `IPackageRetriever`. `Catalog/MbPackageRetrieverBoundaryTests.cs` proves successful immutable staging/hash verification, bad archive hash rejection with no staged survivor, oversize rejection, explicit cancellation, deterministic total timeout, and that merely staging a plugin cannot register a tool, resolve an executor or activate a plugin. The invalid-payload fixture reaches `PluginManager` only after retrieval and is rejected on payload verification before executor resolution or registry activation. A source guard prevents retrieval/install responsibility from being merged. Exact functional source commit `9f846d260146f380af309e5aa865491adf9160eb` (including the MB-81 guard refinement), GitHub Actions run `35417590780`: MB-81 **5 passed, 0 failed**; MB-70 through MB-80, H2 Notes, architecture guard, Phase 10/11/extensibility, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `11e383b3aaf6de3793d22e2b66b44b94c702b89a`; repository portable ZIP SHA256 `699d0df13eff2e62452ea4a83765d99a7d1ec0adef30b6d6b09561c9c14d3808`.

---

## [x] MB-82 — End-to-end install-and-continue test

Scenario:

1. Agent tries local tools/skills.
2. Model determines required capability is absent.
3. Model calls catalog search.
4. Candidate returned.
5. Host policy approves.
6. Package retrieved.
7. PluginManager installs.
8. safe tool/skill refresh.
9. model continues original task.
10. tool/skill is used.
11. verifier passes.
12. user does not repeat request.

This proves the expansion slot.

No semantic marketplace engine required.

Evidence: `Capabilities/MbEndToEndInstallContinueTests.cs` exercises the complete expansion slot in one real `AgentRuntime` request. The scripted model first runs `tool_search` for the target operation and observes no matching local tool, loads ordinary discovery tools, then runs `list_skills` and observes no matching local skill. It calls `catalog_search` against the configured `LocalFolderCatalogSource`, receives compact metadata for `fixture.verified-state` including the exact callable identity `fixture.set_value`, and requests `plugin_install`; host-owned DeveloperLocal policy/user approval remain outside model arguments. The catalog re-resolves the exact package location/hash, a counting wrapper proves `IPackageRetriever` stages the package exactly once, and `PluginManager` verifies/installs/activates it. Without a new user turn, the same runtime observes the newly installed skill through canonical `SkillCatalog`, reads its selected `SKILL.md`, re-runs `tool_search` so registry-version refresh loads the new tool schema, executes the newly installed mutating tool exactly once, and a host `IAgentRuntimeVerifier` independently confirms the resulting fixture state and returns PASS for criterion `mb82.state-applied`. Completion policy requires verifier `mb82-state-verifier`, and the runtime finishes only after that PASS. The transport asserts exactly one `StartAsync` call, proving the user never restates the request. The first CI attempt exposed a fixture metadata omission (tool ID absent from compact `toolSummaries`); commit `87d9482381a16dd4071395688817f3d38115960a` corrected the candidate metadata while keeping it compact. GitHub Actions run `35419850856`: MB-82 **1 passed, 0 failed**; MB-70 through MB-81, H2 Notes, architecture guard, Phase 10/11/extensibility, DesktopHost, OfficeHost, all provider transports, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `3c87799e992b20677fe32d2d8fbdf7a7241c54a0`; repository portable ZIP SHA256 `65b6678083a989f2d0e03e78af042169087e2173076d792b3b7f53590494a6d4`.

---

# Stage J — Legacy cleanup

## [x] MB-90 — Retire AgentRunner from normal runtime

After real runtime parity:

- normal UI no longer instantiates it;
- keep frozen baseline harness only if still needed for A/B report;
- otherwise move to legacy/test or remove.

Acceptance:

repository guard fails if production UI path references AgentRunner.

Evidence: normal `LabWindow` execution remains `LabWindow -> AgentOrchestratedRun -> AgentOrchestrator -> AgentRuntime`; production `AgentOrchestrator` no longer stores or accepts an `AgentRunner` factory and no longer exposes `CreateCompatibilityRunner()`. The frozen v1 `AgentRunner` implementation remains available only to baseline/test/live-evaluation harnesses so the existing A/B baseline can still run while later legacy cleanup proceeds. `Runtime/MbRetireAgentRunnerTests.cs` adds a repository source guard that rejects real C# `AgentRunner` code tokens outside the allowed legacy harness files, plus explicit normal-UI/source assertions and reflection checks that the production orchestrator API cannot construct or return `AgentRunner`. Existing MB-11, V2 architecture and Phase 11 test 1101 were aligned so they now enforce the retired production seam rather than requiring it. Final functional source commit `ef1d1438f9058aef77ec3a3bce746407038c36cd`, GitHub Actions run `35420762765`: MB-90 **3 passed, 0 failed**; H2 Notes, v1 self-test and A/B baseline, V2 architecture/Phase 10/11/extensibility, MB-10 through MB-82, DesktopHost, OfficeHost, all provider transports, provider resilience matrix, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `646d11cdd462dc16af2921f6c634e8c1485fdf44`; repository portable ZIP SHA256 `7c56df4e9bee3b6723d28feda976f61c5745a8869a01deda218a53b1667c6e99`.

---

## [x] MB-91 — Retire AgentTools giant switch

Migrate any remaining useful tool implementation into real providers/executors.

Then:

- stop using `AgentTools.Definitions`;
- stop using giant `AgentTools.Execute` in normal runtime.

Acceptance:

ToolRegistry is sole normal callable surface.

Evidence: normal `AgentRuntimeFactory` now constructs `NormalRuntimeToolRegistry.Create(tools)` and no longer routes model calls through `V1ToolRegistryAdapter.Create`, `AgentTools.Definitions`, or the giant `AgentTools.Execute` switch. `Tools/NormalRuntimeToolRegistry.cs` owns the normal callable schemas and dispatches the retained surface through six focused executors (`skills`, `core`, `python`, `files`, `office`, `desktop`) while preserving the existing host workspace/approval/journal state as temporary plumbing for later cleanup. The registry exposes the 19 retained callable names directly through `ToolRegistry`, including progressive skill discovery, plan/status, sandboxed Python/artifact flow, workspace file operations, structured Word inspection, and selected-window accessibility actions. `Runtime/MbRetireAgentToolsSwitchTests.cs` proves the complete normal callable surface is registered through domain executors, callable schemas are independent of `AgentTools.Definitions`, direct file mutation/read and skill/plan execution work through the new registry, and a production source guard rejects `AgentTools.Definitions`, `V1ToolRegistryAdapter.Create`, and giant-switch `.Execute(call)` routing from the normal runtime path. Final functional source commit `006d1b76c144d85eb977b5f6e7c9101ddd1c35e8`, GitHub Actions run `35421916078`: MB-91 **3 passed, 0 failed**; H2 Notes, v1 baseline/self-tests, V2 architecture/Phase 10/11/extensibility, MB-10 through MB-90, DesktopHost, OfficeHost, all provider transports, provider resilience matrix, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `91ff59616e92a146ef3f603e96a23c7b0728feeb`; repository portable ZIP SHA256 `86b4bd6a54e92f98c6be78fb6484e85e5054f4367415044ffe114e5c15235ed5`.

---

## [x] MB-92 — Retire V1ToolRegistryAdapter

Delete only after AgentTools no longer supplies normal tools.

Evidence: `Tools/V1ToolRegistryAdapter.cs` and `V1AgentToolsExecutor` are removed. Canonical schemas/risk/access/resource-scope/evidence/preference metadata are now reused directly from `NormalRuntimeToolRegistry.Populate(...)` / `PopulateNamespace(...)`; the Python first-party extension and the affected V2/MB regression fixtures no longer depend on the retired adapter or `AgentTools.Definitions`. `Runtime/MbRetireV1ToolRegistryAdapterTests.cs` proves the exact 19-tool canonical surface, Python namespace-only reuse with supplied executor/provenance, and a repository source guard against calls to the retired adapter/executor. Final functional source commit `0e06e39d96d2cd798d8f07c05cf39aa394cc70f4`, GitHub Actions run `35426587329`: MB-92 **3 passed, 0 failed**; H2 Notes, v1 baseline/self-tests, V2 architecture/Phase 10/11/extensibility, MB-10 through MB-91, DesktopHost, OfficeHost, all provider transports, provider resilience matrix, self-contained publish and repository ZIP publication all succeeded. Publish bot commit `a16c58e53ce5c646aebf4d7c086abab1153cfd1d`; repository portable ZIP SHA256 `43fe7f9ee88d7bf7209af10933b45879b4d9816b6c137b2266cc76ccacdb5a72`.

---

## [ ] MB-93 — Retire duplicate SkillCatalog path

After canonical skill parity:

- old `SkillCatalog.cs` removed or converted to a thin source implementation;
- `DeferredSkillSession` merged/removed if redundant;
- PluginSkillCatalog remains only if useful internally to PluginSkillSource.

---

## [ ] MB-94 — Retire legacy ComputerTools after DesktopHost parity

Do not remove until:

- selected-window read/action parity exists;
- permission tests pass;
- real runtime uses DesktopHost/provider tools.

---

## [ ] MB-95 — Review trace/journal duplication

Review:

- Metrics.AgentTrace;
- Tasking.AgentTraceEventStream;
- LabSession journal.

Goal:

Define:

- telemetry trace;
- user-visible typed progress;
- durable journal;

without multiple competing truth sources.

Do not merge types just for aesthetics.

---

# Stage K — Core acceptance gate

## [ ] MB-100 — Build Minimum Bootable Agent acceptance corpus

At least cover:

- direct response;
- multi-tool task;
- dynamic tool_search schema loading;
- tool error -> recovery;
- mutation -> verification PASS;
- mutation -> verification FAIL -> repair -> PASS;
- long context;
- cancellation;
- provider switch;
- plugin tool registration;
- progressive skill loading;
- install-and-continue.

---

## [ ] MB-101 — Prove normal UI path is fully V2

Hard acceptance:

Normal user run must use:

```text
AgentOrchestrator
 -> AgentRuntime
 -> IAgentTransport
 -> bounded context
 -> deferred ToolRegistry
 -> verifier/repair
```

No hidden fallback to v1 except an explicit diagnostic mode.

---

## [ ] MB-102 — Context boundedness gate

Run long deterministic cases equivalent to:

- 10 turns;
- 100 turns;
- 1000 turns;
- repeated large tool outputs.

Acceptance:

- active context bounded;
- bytes/request do not grow linearly with total history;
- artifact/raw evidence retained out of prompt.

---

## [ ] MB-103 — Extension-bus gate

Acceptance:

- add one new fake/test extension without modifying AgentRuntime;
- register tools and optionally skill;
- model discovers/uses it;
- uninstall/disable removes it cleanly.

---

## [ ] MB-104 — Permission/scope gate

Require zero known boundary violations in corpus.

Cover:

- filesystem;
- process/shell;
- plugin;
- provider;
- Desktop;
- Office;
- Web;
- native helper;
- secrets metadata.

---

## [ ] MB-105 — Core correctness gate

Target:

- >=90% supported-task correctness on the agreed core corpus;
- no false “completed” on known verifier failures.

---

# Stage L — Reference extension acceptance

## [ ] MB-110 — Office acceptance

Cover:

- open Excel unsaved state;
- open Word unsaved state;
- structured mutation;
- preservation;
- native spelling/citation path;
- verifier.

---

## [ ] MB-111 — Web/freshness/legal acceptance

Cover:

- current lookup required;
- authoritative evidence;
- bounded web artifact;
- typed legal relationship;
- no “newer means replaced” inference.

---

## [ ] MB-112 — Desktop/computer-use acceptance

Cover:

- safe window enumeration;
- UIA;
- typed actions;
- stale-state guard;
- observe-after-act;
- vision fallback;
- sensitive-app blocks.

---

## [ ] MB-113 — AutoCAD extension acceptance

Cover:

- structured document/entity discovery;
- read attributes/layers;
- bounded mutation;
- state token;
- verify;
- no arbitrary command execution as default path.

---

## [ ] MB-114 — MCP acceptance

Cover:

- connect/disconnect/reconnect;
- tool/resource discovery;
- deferred schemas;
- scope/permission;
- provider version/provenance;
- cancellation/timeout.

---

## [ ] MB-115 — Plugin package lifecycle acceptance

Cover:

- install;
- update;
- hash failure;
- permission delta;
- self-test failure;
- rollback;
- quarantine;
- safe hot registration.

This remains valuable, but it is an extension-system test, not the definition of Agent core.

---

# Stage M — H2 Notes integration readiness

## [ ] MB-120 — Produce final architecture report

Report:

- real runtime call graph;
- core vs extensions;
- legacy removed/retained;
- context metrics;
- tool schema metrics;
- correctness;
- verification failure rate;
- provider matrix;
- known limitations.

---

## [ ] MB-121 — Freeze Agent public integration boundary

Define what H2 Notes may call:

- start task;
- observe progress;
- cancel;
- provide approval;
- inspect task/evidence;
- load project context;
- receive final result.

Do not expose internal model/provider details unnecessarily to H2 UI.

---

## [ ] MB-122 — User acceptance gate

User reviews:

- Minimum Bootable Agent;
- reference extension behavior;
- limitations;
- performance;
- safety.

No H2 Notes production integration without explicit user approval.

---

## [ ] MB-123 — Phase 13 preparation

Only after MB-122:

Read:

1. `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md`
2. `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

Then plan H2 Notes integration.

---

# Final deletion rule

No file should be deleted merely because this tracker labels it legacy.

Deletion sequence:

```text
replacement implemented
 -> deterministic parity test
 -> real UI/runtime uses replacement
 -> CI green
 -> architecture guard prevents regression
 -> delete legacy path
```

This specifically applies to:

- `AgentRunner.cs`;
- `AgentTools.cs`;
- `SkillCatalog.cs`;
- `V1ToolRegistryAdapter.cs`;
- `ComputerTools.cs`;
- legacy context path.

---

# Final task-order principle

The new order is intentionally:

```text
BOOT THE AGENT
    ↓
USE THE REAL V2 COMPONENTS
    ↓
SIMPLIFY SKILL/TOOL EXTENSIONS
    ↓
PROVE ONE EXTERNAL EXTENSION INSTALL FLOW
    ↓
CLEAN LEGACY
    ↓
ACCEPT CORE
    ↓
ACCEPT REFERENCE EXTENSIONS
    ↓
H2 NOTES
```

Do not return to:

```text
build more marketplace abstractions
before
the real AgentRuntime is using the transports/context/tools already written
```
