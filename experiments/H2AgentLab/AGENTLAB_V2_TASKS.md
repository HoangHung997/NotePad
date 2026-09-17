# H2 Agent Lab 2.0 — implementation task tracker

This file is the **authoritative execution checklist** for `AGENTLAB_V2_SPEC.md`.

Rules:

- Work in ID order unless a dependency says otherwise.
- `[x]` means implementation **and required evidence/tests** are complete.
- `[~]` means actively in progress; do not start a second unrelated implementation task while one is `[~]` unless it is a test/docs update needed to finish the same task.
- `[ ]` means not started.
- If new work is discovered, add a new unique ID. Never hide extra work inside another task.
- Do not delete old v1 code until a replacement task explicitly says it may be retired.
- H2 Notes production integration is forbidden until Phase 12 gate passes and the user explicitly approves it.

---

## Phase 00 — specification, baseline and guardrails

- [x] **V2-0001 — Write approved architecture specification.**  
  Evidence: `AGENTLAB_V2_SPEC.md` exists on `feature/nas-multi-device-sync` and defines reuse, transport, tool search, Office/Desktop hosts, verification, context, safety and acceptance gate.

- [x] **V2-0002 — Create authoritative task tracker.**  
  Evidence: this file exists with unique IDs, dependencies and acceptance notes.

- [x] **V2-0003 — Link spec/tracker from Agent Lab README.**  
  Evidence: README identifies v2 as the active plan and links both documents while retaining v1 historical material.

- [x] **V2-0004 — Freeze v1 behavior baseline.**  
  Evidence: `V1_BASELINE_2026-09-17.md` records the current v1 source identities and preserved 46/46 deterministic baseline from the existing evaluation evidence; no historical evidence was removed.

- [x] **V2-0005 — Add architecture compatibility guard test.**  
  Evidence: `V2ArchitectureTests.cs` verifies direct `H2Notes.Core` capability references and preserved v1 test entry points. GitHub Actions run `35233985841` built H2 Notes and H2 Agent Lab successfully, ran the H2 Notes tests, the v1 Lab self-test and the v2 architecture guard successfully before publish.

---

## Phase 01 — turn trace and measurable performance

- [x] **V2-0101 — Add typed turn trace model.**  
  Evidence: `Metrics/AgentTrace.cs` records typed turn events with task/turn IDs, append-only sequence, UTC timestamps and monotonic `Stopwatch` elapsed time with a bounded event limit.

- [x] **V2-0102 — Add provider usage/latency metrics model.**  
  Evidence: `Metrics/AgentMetrics.cs` aggregates public provider token/cache counters, bytes, model/tool calls, repairs, TTFT, total duration and optional configured cost without storing prompt/reasoning text.

- [x] **V2-0103 — Persist trace/metrics per Lab run.**  
  Evidence: `Metrics/AgentTraceStore.cs` writes bounded atomic JSON under Lab `traces/`; persisted events intentionally omit free-form detail/reasoning and redact Bearer/API-key-shaped labels. `LabWindow` persists a trace after each run.

- [x] **V2-0104 — Instrument existing v1 AgentRunner through a thin adapter.**  
  Evidence: the legacy `Run` signature is preserved; the telemetry overload marks request/connection/first-model/tool/continuation/final/error/cancel phases and records public Ollama usage while keeping the v1 execution/recovery semantics. V1 self-test stayed 18/18.

- [x] **V2-0105 — Add deterministic trace tests.**  
  Evidence: `Metrics/V2MetricsTests.cs`; CI run `35235796417` reported **6 passed, 0 failed** for monotonic ordering, metric aggregation, bounded persistence/redaction, cancellation, provider error and event safety-limit cases.

- [x] **V2-0106 — Add raw-model baseline probe.**  
  Evidence: `Metrics/RawModelBaselineProbe.cs` measures the same v1 Ollama/Chat profile and prompt with no Lab tool/session payload and emits the same trace/metrics model. It is explicitly measurement-only; Phase 02 owns the production transport.

- [x] **V2-0107 — Capture initial A/B baseline evidence.**  
  Evidence: `Metrics/V2BaselineTests.cs` and `evidence/2026-09-17-v2-phase01/INITIAL_AB_BASELINE.md`. CI run `35235796417` reported **3 passed, 0 failed** and deterministically exposed request growth: synthetic raw 86 bytes, v1 direct 13,782 bytes, v1 one-tool 28,062 bytes. The evidence explicitly forbids treating fixture timings as real model-speed results. The same run had H2 Notes 336/336, Lab v1 18/18, v2 guard 3/3, v2 metrics 6/6 and 0-warning/0-error Lab build. A stale `/bin` publish race was separately fixed in `45fe1d6fc187688c47293126e3459f3dd3952987`; run `35235928163` then completed the full workflow successfully.

---

## Phase 02 — transport abstraction

- [x] **V2-0201 — Define `IAgentTransport` and typed stream events.**  
  Evidence: `Transport/IAgentTransport.cs` now owns provider-neutral start/continue/cancel/dispose contracts plus typed response/text/reasoning/tool/usage/completion events; provider/network JSON types do not leak through the interface. `Transport/V2TransportContractTests.cs` verifies the lifecycle and typed start/continuation sequence. GitHub Actions run `35239473355` completed successfully, including H2 Notes 336/336, Agent Lab build/v1 regressions and the v2 transport contract test step.

- [x] **V2-0202 — Define `AgentTransportCapabilities`.**  
  Evidence: `Transport/AgentTransportCapabilities.cs` exposes independent flags for native tools, incremental continuation, WebSocket, provider compaction, prompt cache control, parallel tools, native image/file input and usage metrics, with minimal/Ollama/Chat fallback profiles. The same contract suite verifies independent capability preservation; CI run `35239473355` passed.

- [x] **V2-0203 — Wrap current Ollama behavior in `OllamaTransport`.**  
  Evidence: `Transport/OllamaTransport.cs` isolates native Ollama `/api/chat` streaming behind `IAgentTransport`, reuses H2 Core `AiClient.Endpoint` safety, preserves native image input, explicit thinking settings, provider thinking replay on active tool turns, completed tool-call identity/arguments and public token/cache counters while rejecting native files and incomplete tool results before execution. `Transport/V2OllamaTransportTests.cs` verifies direct answers, thinking true/false, tool continuation, usage, image/file boundaries and endpoint safety. GitHub Actions run `35241305423` completed successfully: H2 Notes **336/336**, Lab build/v1 regressions, v2 architecture/metrics/baseline/transport-contract tests and the new Ollama transport suite all passed, then self-contained publish and `/bin` publish succeeded.

- [x] **V2-0204 — Wrap existing Chat Completions behavior in `ChatCompletionsTransport`.**  
  Evidence: `Transport/ChatCompletionsTransport.cs` isolates `/chat/completions` streaming behind `IAgentTransport`, preserves provider tool-call IDs plus fragmented function names/JSON arguments, explicit tool-result replay, H2 image/file wire shapes, bearer authentication, H2 Core endpoint safety and capability-driven reasoning effort while rejecting truncated tool proposals before execution. `Transport/V2ChatCompletionsTransportTests.cs` covers direct reasoning/text streaming, fragmented tool continuation, multimodal shapes, truncation and unsafe endpoint rejection. GitHub Actions run `35244902978` passed H2 Notes **336/336**, Agent Lab build/v1 regressions, v2 architecture/metrics/baseline/transport-contract/Ollama suites and the new Chat Completions transport suite, then publish succeeded.

- [x] **V2-0205 — Implement OpenAI Responses HTTP transport.**  
  Evidence: `Transport/OpenAiResponsesTransport.cs` implements the public Responses HTTP/SSE path behind `IAgentTransport`, using H2 Core endpoint/reasoning checks, bearer auth, typed text/reasoning-summary/tool/usage events, `prompt_cache_key`, parallel tool declarations, native image/file inputs and `store:false` stateless function-call replay with `reasoning.encrypted_content`; it deliberately does **not** use undocumented Codex headers. `Transport/V2OpenAiResponsesTransportTests.cs` covers streaming/usage, stateless tool continuation, native inputs, incomplete-response safety and endpoint safety. `Transport/AiProfileSnapshotExtensions.cs` keeps active request settings immutable until V2-0210 extracts a shared Core helper. GitHub Actions run `35246360154` completed successfully, including H2 Notes **336/336**, Agent Lab build/v1 regressions, all v2 transport suites, self-contained publish and `/bin` publish.

- [x] **V2-0206 — Implement OpenAI Responses continuation state.**  
  Evidence: `OpenAiResponsesTransport` now has explicit `Stateless` and `StoredContinuation` modes. Privacy-preserving stateless remains the default (`store:false` + local output replay + encrypted reasoning). Stored continuation is opt-in and restricted to the official `https://api.openai.com/v1/` endpoint; it uses `store:true`, captures the completed response ID and sends later tool results as only new `function_call_output` input with `previous_response_id`, without resending the prior system/user/function-call transcript. Tests also prove compatible endpoints cannot silently claim official stored continuation and a missing response ID never falls back by replaying data unexpectedly. GitHub Actions run `35247235754` completed successfully: H2 Notes **336/336**, Agent Lab build/v1 and every v2 transport suite passed, followed by self-contained artifact and `/bin` publish.

- [x] **V2-0207 — Implement Responses WebSocket turn session.**  
  Evidence: `Transport/OpenAiResponsesWebSocketTransport.cs` adds an official-endpoint, turn-scoped `wss://api.openai.com/v1/responses` transport. One socket is reused across Start + all tool continuations; the first request sends full canonical input, while continuations send only new `function_call_output` items plus `previous_response_id` with `store:false`. It falls back to HTTP only when WebSocket connection setup fails before any request write; any disconnect/error after a successful write is treated as ambiguous and is never auto-replayed. `AgentTransportCapabilities.OpenAiResponsesWebSocket` exposes WebSocket + incremental continuation explicitly. `V2ResponsesWebSocketTransportTests.cs` verifies same-socket reuse, minimal continuation payload, safe pre-write fallback, no post-write replay, and official-endpoint restriction. GitHub Actions run `35250100777` completed successfully: H2 Notes tests, all prior Agent Lab v1/v2 transport suites, the new WebSocket suite, self-contained publish and `/bin` publish all passed.

- [x] **V2-0208 — Add WebSocket prewarm where supported.**  
  Evidence: `OpenAiResponsesWebSocketTransport` now performs best-effort `response.create` prewarm with `generate:false` on the official Responses WebSocket path. A successful warmup remains on the same turn socket and the real generation references the warm response with empty duplicate input; a warmup error never changes task semantics—the real request keeps the original full input. A broken warmup connection may be recreated because generate=false cannot produce model/tool effects, while the existing no-replay rule still applies after a real generation write. `AgentTraceKind.PrewarmStart/PrewarmFinish` records warmup timing/status without reasoning text. `V2ResponsesWebSocketTransportTests` verifies successful reuse, failure fallback semantics and trace ordering. GitHub Actions run `35250947951` completed successfully: H2 Notes, Lab build/v1, all transport suites including WebSocket/prewarm, publish and `/bin` publish all passed.

- [x] **V2-0209 — Add provider transport tests.**  
  Evidence: `Transport/V2ProviderTransportResilienceTests.cs` adds one cross-provider safety matrix on top of the existing successful-wire/continuation suites. Ollama, Chat Completions and Responses HTTP are each tested for cancellation with exactly one request, malformed provider JSON with no tool/completion exposure, and premature stream disconnect with no retry/completion. Responses WebSocket is separately tested for cancellation after one write, malformed frames and premature end, with proof that no HTTP fallback replay occurs after an ambiguous write. The matrix also asserts continuation capability flags while the provider-specific suites continue to verify successful tool continuation wire shapes. GitHub Actions run `35251742605` completed successfully, including H2 Notes tests, all Agent Lab v1/v2 transport suites, the new resilience matrix, self-contained publish and `/bin` publish.

- [x] **V2-0210 — Reuse H2 Core endpoint/model capability checks.**  
  Evidence: `H2Notes.Core` now owns the public `AiProfileSnapshot` contract and official OpenAI/Gemini endpoint capability checks. Agent Lab's snapshot adapter delegates to Core instead of mirroring the `AiProfile` field list; Ollama and Chat Completions use that shared snapshot, Responses HTTP/WebSocket reuse Core official-endpoint classification, and Ollama named reasoning effort goes through `AiModelCapabilities.ResolveReasoningEffort` before the legacy bool fallback. `V2ArchitectureTests` verifies snapshot isolation and that compatible endpoints cannot inherit official-provider capabilities; `V2OllamaTransportTests` verifies `ReasoningEffort=max` maps to the verified Ollama `high` wire value. GitHub Actions run `35255521158` completed successfully: builds had **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, and the self-contained Windows x64 artifact upload succeeded.

---

## Phase 03 — stable prompt, cache and context foundation

- [x] **V2-0301 — Split static base policy from dynamic runtime context.**  
  Evidence: `Prompting/AgentPromptLayout.cs` introduces separate `AgentPromptStablePrefix` and `AgentPromptRuntimeContext` types. The provider-neutral layout always emits stable base/security/model/tool-namespace system messages before a declared `CacheBoundaryIndex`, then task/current-state/live-environment context and finally user input. The frozen v1 `AgentRunner` prompt assembly remains unchanged for A/B comparison until its explicit migration task. `V2ArchitectureTests` uses distinct time/session/workspace/user sentinels and fails if any runtime sentinel appears in the stable prefix. Source commit `af4b52644c36e8c0b8979743a033953527346de3`; GitHub Actions push run `35256240260` and PR run `35256246638` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **5/5**, metrics **6/6**, baseline **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained artifact upload and `/bin` publication all succeeded. The workflow-generated portable commit is `a4e15ffbdf274b4864a98f74d4431e7785ef1ebc` (`[skip ci]`).

- [~] **V2-0302 — Add agent policy/toolset version identifiers.**  
  Used for cache identity and trace reproducibility.

- [ ] **V2-0303 — Add prompt-cache identity builder.**  
  Acceptance: deterministic hash changes only when stable policy/model/toolset/safety inputs change.

- [ ] **V2-0304 — Add bounded `AgentContextManager`.**  
  Inputs: task contract, current state, recent relevant turns, relevant tool summaries, compacted history; output has explicit budgets.

- [ ] **V2-0305 — Replace direct `session.Context()` injection in v2 path.**  
  Raw LabSession journal remains persisted but not blindly injected.

- [ ] **V2-0306 — Add artifact handles for large tool output.**  
  Long stdout/stderr/document extracts stored out-of-context; model sees concise summary + retrievable handle.

- [ ] **V2-0307 — Add compaction checkpoint model.**  
  Raw history preserved, active context can be replaced by summary + durable source references.

- [ ] **V2-0308 — Add automatic context budget trigger.**  
  Acceptance: active context remains bounded as synthetic conversation grows to hundreds of turns.

- [ ] **V2-0309 — Add context/cache tests.**  
  Verify stable prefix equality, budget enforcement, source preservation and no linear unbounded growth.

---

## Phase 04 — task contract and orchestrator state machine

- [ ] **V2-0401 — Add `AgentTaskContract`.**  
  Fields: goal, scope, inputs, required changes, preserve constraints, outputs, acceptance criteria, risk class, verification policy.

- [ ] **V2-0402 — Add criterion/evidence types.**  
  Acceptance criterion cannot be silently removed after task starts; evidence references are typed.

- [ ] **V2-0403 — Add `AgentTaskState` state machine.**  
  Received → Grounded → Planned → Executing → Verifying → Completed/Repairing/Blocked/Cancelled/Failed.

- [ ] **V2-0404 — Enforce no mutation completion without verification.**  
  Deterministic test: direct transition Executing→Completed is rejected for mutating tasks.

- [ ] **V2-0405 — Add fast-path router.**  
  Classes: Direct, Retrieval, Action, ComplexAgent. Direct path must not load Office/Desktop/Python schemas.

- [ ] **V2-0406 — Add `AgentOrchestrator` skeleton around existing runner/execution pieces.**  
  Existing AgentRunner retained as compatibility path until migration completes.

- [ ] **V2-0407 — Add orchestrator deterministic tests.**  
  Cover direct/retrieval/action/repair/cancel/blocked transitions.

---

## Phase 05 — tool registry and deferred tool discovery

- [ ] **V2-0501 — Define tool descriptor/namespace/registry types.**  
  Include risk, mutating/read-only, parallel capability, schema version and runtime executor reference.

- [ ] **V2-0502 — Register existing v1 tools through registry adapters.**  
  No functional deletion yet.

- [ ] **V2-0503 — Build lexical/BM25-style `ToolSearchIndex`.**  
  No embedding dependency. Cache by registry version.

- [ ] **V2-0504 — Expose `tool_search` as initial callable tool.**  
  Acceptance: model initially sees only stable core + namespace descriptions, not every detailed tool schema.

- [ ] **V2-0505 — Load discovered tool schemas for subsequent model call.**  
  Acceptance: exact selected schemas are traceable; duplicates coalesced.

- [ ] **V2-0506 — Add parallel read-only execution support.**  
  Serialize overlapping mutations to same resource.

- [ ] **V2-0507 — Migrate SkillCatalog into deferred registry model.**  
  Preserve progressive `SKILL.md` loading; remember skill hash/version within a task so the model does not reread unchanged guidance repeatedly.

- [ ] **V2-0508 — Tool-search regression/performance tests.**  
  Verify relevant-tool ranking, stable cache, bounded schema tokens and mutation serialization.

---

## Phase 06 — verification framework

- [ ] **V2-0601 — Add `VerificationReport` / `VerificationFailure`.**  
  Machine-readable pass/fail per criterion with evidence IDs.

- [ ] **V2-0602 — Add file/hash/scope verifier.**  
  Check exact changed files, expected hashes and unintended output.

- [ ] **V2-0603 — Add generic artifact verifier contract.**  
  Allows domain verifiers without coupling orchestrator to Excel/Word.

- [ ] **V2-0604 — Add `AgentRepairController`.**  
  Convert failed criteria into concise repair context; preserve already-passed criteria.

- [ ] **V2-0605 — Integrate existing RecoverySupervisor below verifier layer.**  
  Runtime/protocol recovery remains distinct from semantic repair.

- [ ] **V2-0606 — Add verification-gate tests.**  
  Model saying “done” must not pass if verifier fails.

---

## Phase 07 — document/file tools and H2 Notes reuse

- [ ] **V2-0701 — Wrap `AiDocuments` as Lab document-inspection service.**  
  Reuse H2 Core safety/size/hash/Office extraction rather than duplicate readers.

- [ ] **V2-0702 — Reuse H2 PDF/OCR pipeline in Lab adapter.**  
  `AiPdfProcessor`/portable OCR only when task requires PDF/image text/layout.

- [ ] **V2-0703 — Add deterministic closed-XLSX snapshot model.**  
  Values/formulas/styles/merges/sheets/hidden state needed by verifier.

- [ ] **V2-0704 — Add deterministic closed-DOCX snapshot model.**  
  Paragraph/run/style/table/section/header/footer fields needed by verifier.

- [ ] **V2-0705 — Add `ExcelVerifier` for closed files.**

- [ ] **V2-0706 — Add `WordVerifier` for closed files.**

- [ ] **V2-0707 — Make structured document tools preferred over `run_python`.**  
  Python remains escape hatch for unsupported transforms.

- [ ] **V2-0708 — Closed-file acceptance tests.**  
  Fixtures must include formulas, italic/bold, fills, merges, tables, headers/footers and preservation checks.

---

## Phase 08 — live OfficeHost

- [ ] **V2-0801 — Create `H2AgentLab.OfficeHost` helper project.**  
  Separate process, STA, named-pipe/JSON-RPC boundary, no model/API key access.

- [ ] **V2-0802 — Add OfficeHost process lifecycle/timeout/restart handling.**

- [ ] **V2-0803 — Implement live Excel discovery.**  
  List running workbooks, active workbook/sheet/selection; stable session IDs.

- [ ] **V2-0804 — Implement live Excel structured read snapshot.**  
  Must observe unsaved edits in the open workbook.

- [ ] **V2-0805 — Implement live Excel structured patch.**  
  Targeted values/formulas/styles with before snapshot and task-scope validation.

- [ ] **V2-0806 — Implement Excel recalc/save-copy path.**  
  Never silently overwrite original during acceptance testing.

- [ ] **V2-0807 — Implement live Excel verifier.**  
  Compare before/after contract, including preservation fields.

- [ ] **V2-0808 — Add Excel live unsaved-state acceptance fixture.**  
  Human/manual fixture allowed for Office installation dependency, but result must be machine-verified.

- [ ] **V2-0809 — Implement live Word discovery/active document/selection.**

- [ ] **V2-0810 — Implement live Word structured read snapshot.**  
  Must observe unsaved edits.

- [ ] **V2-0811 — Implement live Word text/format patch.**

- [ ] **V2-0812 — Implement Word save-copy/export path.**

- [ ] **V2-0813 — Implement live Word verifier.**

- [ ] **V2-0814 — Add Word live unsaved-state acceptance fixture.**

- [ ] **V2-0815 — OfficeHost safety/permission tests.**  
  Wrong process/document/session, stale state, timeout, crash and user denial.

---

## Phase 09 — DesktopHost / computer use

- [ ] **V2-0901 — Create `H2AgentLab.DesktopHost` helper project.**  
  Separate from UI/model/Python sandbox.

- [ ] **V2-0902 — Implement safe app/window enumeration.**  
  Block sensitive/system/password-manager/security windows according to explicit policy.

- [ ] **V2-0903 — Implement `observe` screenshot + bounds + DPI + foreground metadata.**

- [ ] **V2-0904 — Add compact UI Automation tree to observation.**  
  Short-lived element tokens bound to state/window.

- [ ] **V2-0905 — Implement click/double-click/key/type/scroll/drag/wait actions.**

- [ ] **V2-0906 — Enforce observe-after-mutation.**  
  A mutating action result cannot be final evidence without a newer observation.

- [ ] **V2-0907 — Add stale-state protection.**  
  Action with stale `state_id` is rejected or requires re-observation.

- [ ] **V2-0908 — Add vision input adapter for screenshot observations.**  
  Only providers/models with image input receive pixels; UIA fallback remains available.

- [ ] **V2-0909 — Preserve structured-adapter priority.**  
  Tests prove Excel/Word requests prefer OfficeHost and do not fall straight to pixel clicking.

- [ ] **V2-0910 — Dedicated desktop test window acceptance suite.**  
  Verify observe→act→observe, no coordinate guessing after resize, cancel/denial.

---

## Phase 10 — Python/runtime migration and fallback

- [ ] **V2-1001 — Adapt `ScriptWorkspace` to v2 artifact/evidence IDs.**

- [ ] **V2-1002 — Preserve WindowsPythonSandbox security regression suite.**

- [ ] **V2-1003 — Make `run_python` deferred/escape-hatch only.**

- [ ] **V2-1004 — Add Python result verifier requirements.**  
  Exit code 0 alone is insufficient; artifact or explicit task assertions required.

- [ ] **V2-1005 — Add fallback test: unsupported Office transform → Python → deterministic verifier.**

---

## Phase 11 — end-to-end v2 loop and UI

- [ ] **V2-1101 — Move Lab UI send path to AgentOrchestrator.**  
  Keep a temporary v1 diagnostic mode only if needed for A/B comparison.

- [ ] **V2-1102 — Add UI progress based on typed trace events.**  
  Show meaningful phase/tool/verifier progress without exposing chain-of-thought.

- [ ] **V2-1103 — Add task/criterion/evidence inspection panel.**  
  User can see what is required, what passed and what failed.

- [ ] **V2-1104 — Add cancel behavior across transport/tool/helper processes.**

- [ ] **V2-1105 — Add restart/resume of durable task state.**  
  Never resume an uncertain mutation by blindly repeating it; observe current state first.

- [ ] **V2-1106 — Add compaction UI/diagnostic counters.**

- [ ] **V2-1107 — Run complete deterministic regression suite.**  
  Must include existing v1 tests, new v2 tests and shared H2 Core tests.

---

## Phase 12 — acceptance gate before H2 Notes integration

- [ ] **V2-1201 — Build final 30+ task acceptance corpus.**

- [ ] **V2-1202 — Run each accepted model/provider configuration >= 3 times per task.**

- [ ] **V2-1203 — Produce correctness report by task class.**

- [ ] **V2-1204 — Produce performance/cost report A(raw)/B(v1)/C(v2).**

- [ ] **V2-1205 — Verify permission/scope safety gate: zero violations.**

- [ ] **V2-1206 — Verify >=90% supported-task correctness gate.**

- [ ] **V2-1207 — Verify context does not grow unbounded with long threads.**

- [ ] **V2-1208 — Verify open Excel/Word unsaved-state workflows.**

- [ ] **V2-1209 — Verify DesktopHost observe-after-act workflows.**

- [ ] **V2-1210 — User acceptance decision.**  
  **Do not integrate into H2 Notes before the user explicitly approves this gate.**

---

## Phase 13 — future H2 Notes integration (blocked until V2-1210)

- [ ] **V2-1301 — Design H2 Notes adapter boundary around accepted Agent Lab engine.**
- [ ] **V2-1302 — Reuse H2 shared project memory/context router without coupling Agent Lab core to H2 UI.**
- [ ] **V2-1303 — Map H2 project actions to accepted structured tools/verification.**
- [ ] **V2-1304 — Preserve H2 multi-PC/NAS sync and permissions.**
- [ ] **V2-1305 — Run H2 Notes full regression/CI and staged integration acceptance.**

---

## Current execution pointer

**Active task:** `V2-0302 — Add agent policy/toolset version identifiers`.
