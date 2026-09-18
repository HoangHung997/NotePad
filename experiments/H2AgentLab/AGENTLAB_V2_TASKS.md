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
- `docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md` is a normative requirement source. Phase 13 is blocked until the MCP/Web/Plugin/Legal requirements represented below also pass acceptance.

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

- [x] **V2-0302 — Add agent policy/toolset version identifiers.**  
  Evidence: `AgentVersionIdentifiers.cs` defines validated, non-secret `AgentPolicyVersion`, `SafetyPolicyVersion` and `ToolsetVersion` metadata plus the current v2 contract labels. `AgentPromptStablePrefix` requires these identifiers and `AgentPromptLayout` carries them out-of-band instead of consuming prompt tokens. `AgentTrace` schema 2 and `AgentTraceStore` persist the identifiers for v2 reproducibility while v1 traces remain compatible with `Versions=null`; `AgentRunTelemetry` accepts the same immutable metadata. `V2ArchitectureTests` rejects unsafe labels and verifies versions never enter model prompt text; `V2MetricsTests` verifies the three identifiers persist without policy text. Source commit `14cb701d8d3eaa737eca670f7c906a4e7da01ad5`; GitHub Actions push run `35257216721` and PR run `35257220506` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **6/6**, metrics **7/7**, baseline **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. The workflow-generated portable commit is `ecafeb3` (`[skip ci]`).

- [x] **V2-0303 — Add prompt-cache identity builder.**  
  Evidence: `Prompting/AgentPromptCacheIdentity.cs` builds a versioned SHA-256 identity from only the cacheable stable-prefix messages, policy/safety/toolset versions, provider protocol/model/normalized endpoint and optional sorted stable skill hashes. Runtime task/time/session/workspace/user context and non-cache request controls are absent from the builder API. It emits a provider-safe `h2pc1_...` key bounded to 64 characters plus the full SHA-256 diagnostics without persisting prompt text. `V2ArchitectureTests` proves runtime context, profile display/runtime controls and skill enumeration order do not change the identity, while every stable policy/safety/model/tool/provider/endpoint/skill fixture invalidates it. Source commit `d56fbf033384adeaea1aa5ca2b31608f511caeb5`; GitHub Actions push run `35258076743` and PR run `35258081376` both completed successfully. Evidence: H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **8/8**, metrics **7/7**, baseline **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `6abe98857b43fd313737007d86aa298305717e6f` records source `d56fbf03...` and ZIP SHA256 `9b6d82fe4f4827fe865177a4dd19b3fe12be21f72b369471f1b50038a0ecce9e`.

- [x] **V2-0304 — Add bounded `AgentContextManager`.**  
  Evidence: `Context/AgentContextManager.cs` adds explicit hard budgets for total active context, task contract, current state, recent relevant turns, relevant tool summaries, compacted history, per-item size and selected-item counts. Selection is deterministic by relevance/recency/source ID, selected turn/tool provenance is retained as source IDs, emitted list items are restored to chronological order, and truncation plus selected/dropped counts are machine-readable in `AgentContextUsage`. The manager returns a bounded `AgentPromptRuntimeContext` without modifying the frozen v1 `LabSession.Context()` path. `V2ArchitectureTests` verifies total/section caps, truncation flags, deterministic provenance, selected/dropped counts and order-independent input enumeration. Source commit `8139677c93266c55d837d69747a9396044ce48b8`; GitHub Actions push run `35259116155` and PR run `35259119900` completed successfully. Push evidence: H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **9/9**, metrics **7/7**, baseline **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `47081637da34bca7c0b66593f58706535dbee8bf` records source `8139677c...` and ZIP SHA256 `47a262251204d84e71db8a5676ea0e3af5d0af9f979d7a358adbf402ab7fcb7f`.

- [x] **V2-0305 — Replace direct `session.Context()` injection in v2 path.**  
  Evidence: `Context/LabSessionContextAdapter.cs` is the v2-only bridge from durable `LabSession` history into `AgentContextManager`; it never calls the legacy `LabSession.Context()` concatenation path. Only completed `user`/`assistant` journal events are eligible as recent turns, while `script`, `recovery`, `unverified-draft` and unknown future event kinds remain durable on disk but are not blindly re-injected. `V2SessionContextTests.cs` proves bounded recent selection/source IDs, exact raw `session.json` preservation/reload, prompt consumption of the bounded snapshot, and exclusion of non-conversation journal kinds. The first CI attempt exposed a constructor named-argument compiler error and was fixed before acceptance. Source commit `bd6ff06d6f88103623d4aef5e374a236087941e0`; GitHub Actions push run `35290186199` and PR run `35290186955` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **9/9**, metrics **7/7**, baseline **3/3**, session-context **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `dddf3d02d3b913714fbe4b2a25b31f467231a8b7` records source `bd6ff06d...` and ZIP SHA256 `a80302af61207a1f3fdc1343ece365bfef59c4ac8cc2e079975d4d3fd44fd70b`.

- [x] **V2-0306 — Add artifact handles for large tool output.**  
  Evidence: `Session/ArtifactStore.cs` persists large UTF-8 tool/stdout/stderr/document-extract text outside active model context using opaque `h2a1_...` handles, atomic content/manifest writes, byte count and SHA-256 integrity metadata. `StoreText` returns only a bounded `AgentContextToolSummary` containing caller-provided summary + handle/hash/size metadata and never exposes the local state path or full large output; `ReadText`/ `LoadHandle` explicitly retrieve and verify stored content. `V2ArtifactStoreTests.cs` proves a 120k-character output stays out of active context while exact content remains retrievable, tampered content is rejected by hash/size verification, and summaries/handles are bounded and path-like handles rejected. Source commit `ad4a56e62928dcffb7ef4016cd0205618eec721f`; GitHub Actions push run `35290643923` and PR run `35290646835` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **9/9**, metrics **7/7**, baseline **3/3**, session-context **3/3**, artifact-store **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `29fbee68f1667a0658adca29a83b10e8c8f7398a` records source `ad4a56e6...` and ZIP SHA256 `9f34d9c9a3b588ec12275a93274c1fab829db9aee17bf001e742cf65144f40c9`.

- [x] **V2-0307 — Add compaction checkpoint model.**  
  Evidence: `Session/CompactionManager.cs` persists bounded `h2cp1_...` checkpoints containing only a compact summary plus 1..8 typed durable references to journal events, artifacts, tool results, snapshots or prior checkpoints. Checkpoints carry UTC creation time, covered sequence, summary SHA-256 and optional validated previous-checkpoint link; rendering is capped to 4,000 characters so it can feed the existing compacted-history budget. The manager never deletes or rewrites source evidence. `V2CompactionTests.cs` proves raw `session.json` bytes remain unchanged while active context uses checkpoint summary/references + recent turns, checkpoint chains round-trip, and source-less/unsafe/tampered/missing-link checkpoints are rejected. Source commit `4e5deded90e3e5937c8998825b056813834ed922`; GitHub Actions push run `35291069507` and PR run `35291074993` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **9/9**, metrics **7/7**, baseline **3/3**, session-context **3/3**, artifact-store **3/3**, compaction **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `c6aa389b58e81fefde13b6402aea167b21376a2c` records source `4e5deded...` and ZIP SHA256 `8a1b9d964d01c2c5aeded56e0a19cd63b41968899a5ee9dda980c03641473216`.

- [x] **V2-0308 — Add automatic context budget trigger.**  
  Evidence: `AgentContextManager` now emits host-owned `AgentContextPressure` metadata on every bounded snapshot with `RequiresCompaction`, estimated candidate characters, actual active characters and deterministic pressure reasons for total-budget pressure, scalar truncation, dropped recent/tool candidates, per-item truncation and compacted-history truncation. Candidate-size estimation uses saturating `long` arithmetic; the trigger never deletes or compacts durable history itself. `V2ArchitectureTests` stress-tests 500 synthetic turns: active context remains within the 1,000-character fixture budget, only the newest four equally relevant turns survive, 496 are reported dropped, pressure requests compaction with explicit reasons, and a small two-turn context does not trigger compaction. Source commit `6e61375a90f67b226ee4244f6d650a96b28aa4c7`; GitHub Actions push run `35291516190` and PR run `35291518534` both completed successfully. Push evidence: **0 warnings, 0 errors**, H2 Notes **336/336**, Lab v1 **18/18**, v2 guard **10/10**, metrics **7/7**, baseline **3/3**, session-context **3/3**, artifact-store **3/3**, compaction **3/3**, transport contract **4/4**, Ollama **6/6**, Chat **5/5**, Responses HTTP **7/7**, WebSocket **6/6**, resilience **13/13**, self-contained publish, artifact upload and `/bin` publication all succeeded. Workflow-generated portable commit `25adfd4f676be90b01c5515bd84c1ca3b01d881d` records source `6e61375a...` and ZIP SHA256 `090e8c6e3e1fbaeb891493bd994d232f374aed64eb2f2b5bd25bb00eb6db2800`.

- [x] **V2-0309 — Add context/cache tests.**  
  Evidence: `Context/V2ContextCacheTests.cs` verifies stable-prefix/cache identity equality across changing runtime context, hard context budgets from 20 through 1,000 synthetic turns, durable journal/artifact source preservation through compaction, and cache stability under thread/compaction pressure. The test command is wired through `Program.cs` and GitHub Actions. Source commit `ee42dc806968c150062e1980989ffc872b9a4f2f`; GitHub Actions PR run `35292505113` completed successfully: H2 Notes tests, Agent Lab build, v1 baseline, architecture/metrics/session/artifact/compaction/context-cache suites, every provider transport/resilience suite, self-contained Windows x64 publish and artifact upload all passed.

---

## Phase 04 — task contract and orchestrator state machine

- [x] **V2-0401 — Add `AgentTaskContract`.**  
  Fields: goal, scope, inputs, required changes, preserve constraints, outputs, acceptance criteria, risk class, verification policy.  
  Evidence: immutable host-owned contract + verification/risk types in `Tasking/AgentTaskContract.cs`; architecture guard proves snapshot isolation, normalization and mutation classification. GitHub Actions run `35293190864` passed full H2 Notes + Agent Lab build/tests/publish.

- [x] **V2-0402 — Add criterion/evidence types.**  
  Acceptance criterion cannot be silently removed after task starts; evidence references are typed.  
  Evidence: stable criterion IDs, typed durable evidence references and append-only criterion expansion/evidence APIs in `Tasking/AgentAcceptanceEvidence.cs` + `AgentTaskContract.cs`; architecture guard covers evidence typing, snapshot immutability and redefinition rejection. GitHub Actions run `35293534110` passed full build/regression/publish.

- [x] **V2-0403 — Add `AgentTaskState` state machine.**  
  Received → Grounded → Planned → Executing → Verifying → Completed/Repairing/Blocked/Cancelled/Failed.  
  Evidence: deterministic host-owned lifecycle + repair loop/terminal guards in `Tasking/AgentTaskStateMachine.cs`; architecture guard covers happy path, repair, cancellation, illegal skips and terminal lockout. GitHub Actions run `35293866199` passed full build/regression/publish.

- [x] **V2-0404 — Enforce no mutation completion without verification.**  
  Deterministic test: direct transition Executing→Completed is rejected for mutating tasks.  
  Evidence: `AgentTaskCompletionGate` + protected `Complete(...)` API reject direct completion, failed verification and missing required verifiers; architecture guard proves verified mutation is the only completion path. GitHub Actions run `35294254411` passed full build/regression/publish.

- [x] **V2-0405 — Add fast-path router.**  
  Classes: Direct, Retrieval, Action, ComplexAgent. Direct path must not load Office/Desktop/Python schemas.  
  Evidence: deterministic `AgentFastPathRouter` classifies Direct/Retrieval/Action/ComplexAgent before heavy schema loading; Direct has schema mode None and zero namespaces, Retrieval is files-only, Action/Complex defer discovery. GitHub Actions run `35294624217` passed full build/regression/publish.

- [x] **V2-0406 — Add `AgentOrchestrator` skeleton around existing runner/execution pieces.**  
  Existing AgentRunner retained as compatibility path until migration completes.  
  Evidence: `Tasking/AgentOrchestrator.cs` owns v2 contract/router/state/context boundaries, append-only contract updates and explicit v1 compatibility factory without modifying `AgentRunner`; architecture guard verifies the boundary. GitHub Actions run `35294966469` passed full build/regression/publish.

- [x] **V2-0407 — Add orchestrator deterministic tests.**  
  Cover direct/retrieval/action/repair/cancel/blocked transitions.  
  Evidence: dedicated `Tasking/V2OrchestratorTests.cs` suite is wired through `Program.cs` and GitHub Actions; Direct/Retrieval/Action/Repair/Cancel/Blocked cases all passed in GitHub Actions run `35295323240`, together with full H2 Notes regression, Agent Lab build, v1 baseline, all v2 suites, Windows x64 publish and artifact upload.

---

## Phase 05 — tool registry and deferred tool discovery

- [x] **V2-0501 — Define tool descriptor/namespace/registry types.**  
  Include risk, mutating/read-only, parallel capability, schema version and runtime executor reference.  
  Evidence: `Tools/ToolRegistry.cs` defines namespaces, descriptors, access/risk/parallel/schema metadata, executor references and versioned registry; architecture guard verifies normalization/versioning/duplicate protection. GitHub Actions run `35296169905` passed full build/regression/publish.

- [x] **V2-0502 — Register existing v1 tools through registry adapters.**  
  No functional deletion yet.  
  Evidence: `Tools/V1ToolRegistryAdapter.cs` projects every `AgentTools.Definitions` schema into v2 registry metadata while execution delegates back to `AgentTools.Execute`; architecture guard checks exact name-set equality and mutation metadata. GitHub Actions run `35296523430` passed full build/regression/publish.

- [x] **V2-0503 — Build lexical/BM25-style `ToolSearchIndex`.**  
  No embedding dependency. Cache by registry version.  
  Evidence: `Tools/ToolSearchIndex.cs` provides deterministic Unicode lexical/BM25 ranking, exact-name boost and registry-version cache invalidation; architecture guard covers ranking, unchanged-cache reuse and invalidation. GitHub Actions run `35297813401` passed full build/regression/publish.

- [x] **V2-0504 — Expose `tool_search` as initial callable tool.**  
  Acceptance: model initially sees only stable core + namespace descriptions, not every detailed tool schema.  
  Evidence: `DeferredToolDiscovery.BuildInitialExposure` exposes only `tool_search` + optional stable `update_plan`, while namespace descriptions advertise deferred capabilities. Architecture guard proves file/Python/Desktop schemas do not leak initially. GitHub Actions run `35298186826` passed full build/regression/publish.

- [x] **V2-0505 — Load discovered tool schemas for subsequent model call.**  
  Acceptance: exact selected schemas are traceable; duplicates coalesced.  
  Evidence: per-task `DeferredToolDiscovery` tracks loaded schema identities and deterministic `ToolSchemaLoadRecord` batches; `ExecuteToolSearch` returns selected/newly-loaded identities and next-request schemas. Architecture guard verifies traceability and duplicate coalescing. GitHub Actions run `35298480630` passed full build/regression/publish.

- [x] **V2-0506 — Add parallel read-only execution support.**  
  Serialize overlapping mutations to same resource.  
  Evidence: `ToolExecutionScheduler` runs parallel-safe reads concurrently, requires mutation resource identity and serializes overlapping mutation keys; architecture guard proves read overlap and mutation exclusion. GitHub Actions run `35298862819` passed full build/regression/publish.

- [x] **V2-0507 — Migrate SkillCatalog into deferred registry model.**  
  Preserve progressive `SKILL.md` loading; remember skill hash/version within a task so the model does not reread unchanged guidance repeatedly.  
  Evidence: `DeferredSkillSession` delegates validation/content to v1 `SkillCatalog`, records per-task SHA-256 versions, suppresses unchanged content and invalidates on source stamp change; skills remain deferred registry tools. GitHub Actions run `35299208295` passed full build/regression/publish.

- [x] **V2-0508 — Tool-search regression/performance tests.**  
  Verify relevant-tool ranking, stable cache, bounded schema tokens and mutation serialization.  
  Evidence: dedicated `Tools/V2ToolRegistryTests.cs` suite verifies representative tool ranking, registry-version cache stability, 1,000-query lexical performance, bounded deferred schema payload and same-resource mutation serialization. GitHub Actions run `35301446171` passed the dedicated tool-registry suite plus full H2 Notes regression, Agent Lab build, v1 baseline, every v2 suite, Windows x64 publish and artifact upload.

---

## Phase 06 — verification framework

- [x] **V2-0601 — Add `VerificationReport` / `VerificationFailure`.**  
  Machine-readable pass/fail per criterion with evidence IDs.  
  Evidence: `Verification/VerificationReport.cs` provides per-criterion Passed/Failed/NotVerified results, typed failures, evidence IDs, coverage checks and aggregate pass state; architecture guard passed in GitHub Actions run `35304076438`, together with full regression/publish.

- [x] **V2-0602 — Add file/hash/scope verifier.**  
  Check exact changed files, expected hashes and unintended output.  
  Evidence: `Verification/FileScopeVerifier.cs` compares before/after snapshots for exact changed paths, expected SHA-256 values and unintended created outputs; architecture guard passed in GitHub Actions run `35304688210`, together with full regression/publish.

- [x] **V2-0603 — Add generic artifact verifier contract.**  
  Allows domain verifiers without coupling orchestrator to Excel/Word.  
  Evidence: `Verification/IArtifactVerifier.cs` defines domain-neutral target/request/verifier/registry abstractions with criterion scoping; architecture guard passed in GitHub Actions run `35304979625`, together with full regression/publish.

- [x] **V2-0604 — Add `AgentRepairController`.**  
  Convert failed criteria into concise repair context; preserve already-passed criteria.  
  Evidence: `Verification/AgentRepairController.cs` emits bounded failure-only repair context plus explicit passed-criterion preservation; architecture guard passed in GitHub Actions run `35305244484`, together with full regression/publish.

- [x] **V2-0605 — Integrate existing RecoverySupervisor below verifier layer.**  
  Runtime/protocol recovery remains distinct from semantic repair.  
  Evidence: `Verification/VerificationRecoveryCoordinator.cs` wraps existing `RecoverySupervisor` below semantic verification and keeps runtime recovery state independent from verifier failures; architecture guard passed in GitHub Actions run `35305569774`, together with full regression/publish.

- [x] **V2-0606 — Add verification-gate tests.**  
  Model saying “done” must not pass if verifier fails.  
  Evidence: `Verification/VerificationCompletionGate.cs` derives completion from verifier reports, `AgentOrchestrator.CompleteVerified` keeps failed tasks in Verifying, and dedicated `Verification/V2VerificationTests.cs` covers failed/missing/not-verified criteria, all-pass completion, file-scope failures, repair preservation and runtime-vs-semantic recovery separation. GitHub Actions run `35305906715` passed the dedicated verification suite plus full H2 Notes regression, Agent Lab build, v1 baseline, every v2 suite, Windows x64 publish and artifact upload.

---

## Phase 07 — document/file tools and H2 Notes reuse

- [x] **V2-0701 — Wrap `AiDocuments` as Lab document-inspection service.**  
  Reuse H2 Core safety/size/hash/Office extraction rather than duplicate readers.  
  Evidence: `Documents/LabDocumentInspectionService.cs` is a thin adapter over `AiDocuments.Read`; architecture guard proves extracted text/hash/MIME/safety stay identical to H2 Core. GitHub Actions run `35306375793` passed full regression/publish.

- [x] **V2-0702 — Reuse H2 PDF/OCR pipeline in Lab adapter.**  
  `AiPdfProcessor`/portable OCR only when task requires PDF/image text/layout.  
  Evidence: `Documents/LabPdfOcrService.cs` delegates attachment preparation/layout/runtime status to H2 Core `AiPdfProcessor`; architecture guard validates direct PDF routing without a separate Lab OCR path. GitHub Actions run `35306657854` passed full regression/publish.

- [x] **V2-0703 — Add deterministic closed-XLSX snapshot model.**  
  Values/formulas/styles/merges/sheets/hidden state needed by verifier.  
  Evidence: `Documents/ClosedWorkbookSnapshot.cs` captures deterministic sheet/cell/formula/style/merge/hidden-row/hidden-column state behind H2 Core safety validation; OpenXML fixture guard passed after typed SDK-v3 enum normalization. GitHub Actions run `35308041247` passed full regression/publish.

- [x] **V2-0704 — Add deterministic closed-DOCX snapshot model.**  
  Paragraph/run/style/table/section/header/footer fields needed by verifier.  
  Evidence: `Documents/ClosedWordSnapshot.cs` captures deterministic body paragraph/run formatting, tables, sections/margins and header/footer relationships/content behind H2 Core safety validation; OpenXML fixture guard passed. GitHub Actions run `35308613906` passed full regression/publish.

- [x] **V2-0705 — Add `ExcelVerifier` for closed files.**  
  Evidence: `Verification/ExcelVerifier.cs` validates exact target cell snapshots while preserving non-target cells, sheet order, merges and hidden state; architecture guard rejects unrelated formula/hidden-state regressions. GitHub Actions run `35308962233` passed full regression/publish.

- [x] **V2-0706 — Add `WordVerifier` for closed files.**  
  Evidence: `Verification/WordVerifier.cs` validates exact body/header/footer target paragraphs and protects non-target body, tables, sections/margins and header/footer content; architecture guard rejects unrelated header/table regressions. GitHub Actions run `35309306112` passed full regression/publish.

- [x] **V2-0707 — Make structured document tools preferred over `run_python`.**  
  Python remains escape hatch for unsupported transforms.  
  Evidence: `Tools/DocumentToolPreference.cs` is applied inside `DeferredToolDiscovery.Search/SearchAndLoad`; structured Office/file tools outrank Python for document intent while explicit Python/custom-transform intent is preserved. GitHub Actions run `35309660295` passed full regression/publish.

- [x] **V2-0708 — Closed-file acceptance tests.**  
  Fixtures must include formulas, italic/bold, fills, merges, tables, headers/footers and preservation checks.  
  Evidence: dedicated `Documents/V2ClosedFileTests.cs` builds real XLSX/DOCX fixtures and verifies formulas, bold/italic, solid fills, merges, hidden row/column/sheet state, tables, sections, headers/footers, H2 Core extraction reuse, deterministic snapshots and negative preservation regressions. Word verification compares package-local relationships through logical section/type slots. GitHub Actions run `35310362232` passed the dedicated closed-file suite plus full H2 Notes regression, Agent Lab build, v1 baseline, all v2 suites, Windows x64 publish and artifact upload.

---

## Phase 08 — live OfficeHost

- [x] **V2-0801 — Create `H2AgentLab.OfficeHost` helper project.**  
  Separate process, STA, named-pipe/JSON-RPC boundary, no model/API key access.  
  Evidence: isolated `H2AgentLab.OfficeProtocol` + `H2AgentLab.OfficeHost` projects, synchronous STA named-pipe JSON-RPC server and process-boundary test; OfficeHost project has no H2Notes.Core/AgentLab model dependency. CI run `35312968425`: PASS 0801.

- [x] **V2-0802 — Add OfficeHost process lifecycle/timeout/restart handling.**  
  Evidence: `Office/OfficeHostClient.cs` owns process start/kill, per-call timeout, crash detection and fresh-process restart without blind request replay. CI run `35312968425`: PASS 0802.

- [x] **V2-0803 — Implement live Excel discovery.**  
  List running workbooks, active workbook/sheet/selection; stable session IDs.  
  Evidence: COM backend attaches running Excel, enumerates workbooks and derives stable session IDs from Excel instance/workbook identity; process fixture verifies active unsaved workbook stability. CI run `35312968425`: PASS 0803.

- [x] **V2-0804 — Implement live Excel structured read snapshot.**  
  Must observe unsaved edits in the open workbook.  
  Evidence: live COM snapshot reads unsaved values/formulas/styles, active sheet/selection, merges, visibility and hidden rows/columns with deterministic state token and bounded cell count. CI run `35312968425`: PASS 0804.

- [x] **V2-0805 — Implement live Excel structured patch.**  
  Targeted values/formulas/styles with before snapshot and task-scope validation.  
  Evidence: exact single-cell patch operations require fresh session/state token + permission, return before/after snapshots and changed-cell scope, and reject stale/denied mutation. CI run `35312968425`: PASS 0805.

- [x] **V2-0806 — Implement Excel recalc/save-copy path.**  
  Never silently overwrite original during acceptance testing.  
  Evidence: live backend exposes calculate + Excel `SaveCopyAs`; save destination must be a new absolute local path different from original and existing files are refused. CI run `35312968425`: PASS 0806.

- [x] **V2-0807 — Implement live Excel verifier.**  
  Compare before/after contract, including preservation fields.  
  Evidence: `Verification/LiveExcelVerifier.cs` checks target cell state and preservation of non-target cells/sheet structure/merge/hidden fields. CI run `35312968425`: PASS 0807.

- [x] **V2-0808 — Add Excel live unsaved-state acceptance fixture.**  
  Human/manual fixture allowed for Office installation dependency, but result must be machine-verified.  
  Evidence: end-to-end separate-process fixture exposes unsaved Excel marker/state and deterministic token in CI; `V2OfficeLiveAcceptance` provides machine-verifiable non-fixture COM acceptance on a workstation with Office. CI run `35312968425`: PASS 0808.

- [x] **V2-0809 — Implement live Word discovery/active document/selection.**  
  Evidence: COM backend attaches running Word, enumerates documents and active selection with stable session IDs. CI process fixture verifies active unsaved selection identity. Run `35312968425`: PASS 0809.

- [x] **V2-0810 — Implement live Word structured read snapshot.**  
  Must observe unsaved edits.  
  Evidence: live COM snapshot reads unsaved paragraphs/format runs, tables, sections/page setup, headers/footers and selection into deterministic state token with hard size bounds. CI run `35312968425`: PASS 0810.

- [x] **V2-0811 — Implement live Word text/format patch.**  
  Evidence: paragraph-scoped text/bold/italic/underline mutation requires permission + fresh state token and returns before/after evidence; stale state is rejected. CI run `35312968425`: PASS 0811.

- [x] **V2-0812 — Implement Word save-copy/export path.**  
  Evidence: COM backend clones current formatted live document into a separate document, copies page/header/footer state, supports DOCX save/PDF export, and refuses overwrite/original destination. CI run `35312968425`: PASS 0812.

- [x] **V2-0813 — Implement live Word verifier.**  
  Evidence: `Verification/LiveWordVerifier.cs` checks target paragraphs and preserves non-target paragraphs, tables, sections, headers and footers. CI run `35312968425`: PASS 0813.

- [x] **V2-0814 — Add Word live unsaved-state acceptance fixture.**  
  Evidence: separate-process fixture machine-verifies unsaved Word paragraph/selection/state token in CI; `V2OfficeLiveAcceptance` supports machine verification against a real open unsaved Word document on an Office workstation. CI run `35312968425`: PASS 0814.

- [x] **V2-0815 — OfficeHost safety/permission tests.**  
  Wrong process/document/session, stale state, timeout, crash and user denial.  
  Evidence: dedicated process-boundary acceptance suite rejects wrong session, stale state and denied mutation; timeout/crash kills the old host and next call starts a fresh STA host. CI run `35312968425`: PASS 0815; suite result 15 passed, 0 failed.

---

## Phase 09 — DesktopHost / computer use

- [x] **V2-0901 — Create `H2AgentLab.DesktopHost` helper project.**  
  Separate from UI/model/Python sandbox.  
  Evidence: isolated `H2AgentLab.DesktopProtocol` + `H2AgentLab.DesktopHost` process, STA named-pipe RPC, Agent Lab client and project-boundary acceptance. GitHub Actions run `35316212135`: PASS 0901.

- [x] **V2-0902 — Implement safe app/window enumeration.**  
  Block sensitive/system/password-manager/security windows according to explicit policy.  
  Evidence: `DesktopSafetyPolicy` blocks terminal/system/security/password-manager processes and sensitive title terms; DesktopHost self-test passed 3/3 and process acceptance passed in run `35316212135`: PASS 0902.

- [x] **V2-0903 — Implement `observe` screenshot + bounds + DPI + foreground metadata.**  
  Evidence: Win32/UIA backend captures bounded PNG, SHA-256, window bounds, DPI, foreground/process identity and deterministic state_id. CI run `35316212135`: PASS 0903.

- [x] **V2-0904 — Add compact UI Automation tree to observation.**  
  Short-lived element tokens bound to state/window.  
  Evidence: bounded UIA traversal excludes password controls and emits short-lived element tokens tied to exact state/window/runtime-id; old tokens are rejected after a new observation. CI run `35316212135`: PASS 0904.

- [x] **V2-0905 — Implement click/double-click/key/type/scroll/drag/wait actions.**  
  Evidence: DesktopHost implements UIA/Win32 click, double-click, structured type, bounded safe keys, scroll, drag and wait; fixture exercises every action with fresh observations. CI run `35316212135`: PASS 0905.

- [x] **V2-0906 — Enforce observe-after-mutation.**  
  A mutating action result cannot be final evidence without a newer observation.  
  Evidence: mutation results carry mutation_id + prior observation sequence and `DesktopEvidenceGate` accepts evidence only when a newer observation explicitly reports that mutation_id. CI run `35316212135`: PASS 0906.

- [x] **V2-0907 — Add stale-state protection.**  
  Action with stale `state_id` is rejected or requires re-observation.  
  Evidence: host re-fingerprints window screenshot/UIA/bounds before action; state_id mismatch, expired observation or stale element token is rejected. Resize fixture proves old coordinates/state cannot be reused. CI run `35316212135`: PASS 0907.

- [x] **V2-0908 — Add vision input adapter for screenshot observations.**  
  Only providers/models with image input receive pixels; UIA fallback remains available.  
  Evidence: `DesktopVisionInputAdapter` branches on `AgentTransportCapabilities.NativeImageInput`; non-vision transports receive UIA text only, vision-capable transports receive validated PNG pixels. CI run `35316212135`: PASS 0908.

- [x] **V2-0909 — Preserve structured-adapter priority.**  
  Tests prove Excel/Word requests prefer OfficeHost and do not fall straight to pixel clicking.  
  Evidence: `InteractionAdapterPreference` enforces OfficeStructured > DesktopAutomation for Word/Excel intent, with Python only as explicit/fallback escape hatch. CI run `35316212135`: PASS 0909.

- [x] **V2-0910 — Dedicated desktop test window acceptance suite.**  
  Verify observe→act→observe, no coordinate guessing after resize, cancel/denial.  
  Evidence: `Desktop/V2DesktopHostTests.cs` runs a separate-process deterministic desktop fixture and covers observe→act→observe, resize invalidation, cancellation/restart and permission denial. GitHub Actions run `35316212135`: Phase 09 suite 10 passed, 0 failed.

---

## Phase 10 — Python/runtime migration, MCP providers and extensibility foundations

- [x] **V2-1001 — Adapt `ScriptWorkspace` to v2 artifact/evidence IDs.**  
  Evidence: new runs persist run-level evidence IDs plus per-output artifact/evidence IDs; legacy manifests are normalized deterministically on load and publish propagates source evidence. CI run `35319749812`: PASS 1001.

- [x] **V2-1002 — Preserve WindowsPythonSandbox security regression suite.**  
  Evidence: `PythonSandboxSecurityProfile` exposes the preserved AppContainer/no-network/1-process/768MB/120s/128MB contract and secret environment keys remain excluded. CI run `35319749812`: PASS 1002.

- [x] **V2-1003 — Make `run_python` deferred/escape-hatch only.**  
  Evidence: `DeferredToolDiscovery` filters `run_python` when structured/non-Python tools satisfy ordinary intent, while explicit Python/custom-transform intent can still load it. CI run `35319749812`: PASS 1003.

- [x] **V2-1004 — Add Python result verifier requirements.**  
  Exit code 0 alone is insufficient; artifact or explicit task assertions required.  
  Evidence: `Verification/PythonResultVerifier.cs` requires artifact expectations and/or explicit assertions in addition to process exit state. CI run `35319749812`: PASS 1004.

- [x] **V2-1005 — Add fallback test: unsupported Office transform → Python → deterministic verifier.**  
  Evidence: fallback router preserves structured Office priority, selects Python only when structured support is insufficient, and deterministic artifact/assertion verification gates the fallback result. CI run `35319749812`: PASS 1005.

- [x] **V2-1006 — Define generic capability-provider/MCP contracts and provenance.**  
  Provider identity, server/tool/schema/version provenance, namespace summaries and health state must be host-owned and provider-neutral.  
  Evidence: `Providers/CapabilityProvider.cs` defines provider-neutral lifecycle/tool/resource contracts, provenance and host-owned health/scope policy. CI run `35319749812`: PASS 1006.

- [x] **V2-1007 — Implement MCP connection lifecycle.**  
  Connect/disconnect/reconnect, cancellation, timeout, bounded errors and deterministic health transitions; no secret values in prompt/tool metadata.  
  Evidence: `McpServerConnection` + bounded stdio JSON-RPC transport negotiate initialize, reconnect after transient disconnect, enforce timeout/cancel, bound diagnostics and expose environment keys without secret values. CI run `35319749812`: PASS 1007.

- [x] **V2-1008 — Normalize MCP tools/resources into ToolRegistry with deferred schemas.**  
  Compact namespace metadata first; detailed schemas/resources load only after selection and are invalidated by provider/registry version.  
  Evidence: `McpToolProvider` emits compact summaries/resources and `McpToolRegistryAdapter` registers only selected detailed schemas with provider provenance. CI run `35319749812`: PASS 1008.

- [x] **V2-1009 — Enforce MCP scope/permission/parallel/trust boundaries.**  
  Preserve read-only vs mutating classification, provider/resource/session scope, serialization keys and parallel safety; one provider cannot broaden another provider's scope; provider claims are not trusted verifier evidence.  
  Evidence: conservative MCP defaults + `CapabilityProviderPolicy/Manager` enforce scope/access/serialization metadata and all MCP/plugin tool results default to `CanProvideVerificationEvidence=false`. CI run `35319749812`: PASS 1009.

- [x] **V2-1010 — Add typed `filesystem.*` capability family.**  
  List/stat/search/read/write/copy/move/hash/watch and policy-controlled delete with explicit workspace/scope boundaries and durable evidence.  
  Evidence: `Computer/FilesystemCapabilities.cs` is rooted in `SafeWorkspace`, hash-guards mutations/deletes, backups deletes and provides deterministic watch/diff tokens. CI run `35319749812`: PASS 1010.

- [x] **V2-1011 — Add bounded `process.*` and `shell.*` capability families.**  
  List/start/wait/exit-status and policy-controlled terminate; bounded shell/build/test/script execution with timeout, cancellation, side-effect policy and evidence.  
  Evidence: `ProcessShellCapabilities` uses executable allow-lists + separated arguments, workspace-bound working dirs, secret-env stripping, timeout/output bounds and only terminates host-started processes. CI run `35319749812`: PASS 1011.

- [x] **V2-1012 — Normalize `app.*` / `window.*` / `uia.*` / `input.*` / `screen.*` / `browser.*` capability families.**  
  Reuse DesktopHost where applicable; browser capability remains fallback behind structured WebResearchHost; no monolithic unrestricted computer-control tool.  
  Evidence: `GeneralComputerCapabilityCatalog` defines typed families/backings and marks browser as WebResearch/browser fallback; acceptance asserts all required namespaces and no monolithic control tool. CI run `35319749812`: PASS 1012.

- [x] **V2-1013 — Add H2 Plugin Manifest, catalog metadata and rebuildable capability index.**  
  Separate Tool/Skill/Plugin concepts; catalogs expose compact discovery metadata before package download or schema/skill loading.  
  Evidence: `Plugins/H2PluginManifest.cs` validates bounded manifests, trust/catalog metadata and rebuildable tool/skill capability index without installing discovered packages. CI run `35319749812`: PASS 1013.

- [x] **V2-1014 — Add staged plugin install/update activation.**  
  Validate manifest, hash/signature/source identity, agent compatibility, capability/permission delta, paths/conflicts/native helpers and self-test; use a versioned store and atomic activation/hot ToolRegistry registration at controlled boundaries.  
  Evidence: `PluginManager` verifies archive/payload hashes, trust/policy/compatibility/paths/helpers/hooks, uses versioned staging + declarative self-test, hot-registers only at non-in-flight boundaries and updates active pointer after successful registration. CI run `35319749812`: PASS 1014.

- [x] **V2-1015 — Add plugin update discovery, rollback/quarantine and progressive skill/version evidence tests.**  
  Failed updates keep the previous version; broader permissions require renewed policy/approval; changed plugin/skill hashes invalidate caches; task evidence records exact plugin/skill/tool versions.  
  Evidence: update metadata discovery, failed-self-test retention, permission-delta approval, rollback/quarantine diagnostics and `PluginSkillCatalog` version/hash cache identity are covered by real ZIP fixtures; task evidence records plugin/skill/tool versions. CI run `35319749812`: PASS 1015; Phase 10 suite 15 passed, 0 failed.

---

## Phase 11 — end-to-end v2 loop, WebResearchHost, structured providers and UI

- [~] **V2-1101 — Move Lab UI send path to AgentOrchestrator.**  
  Keep a temporary v1 diagnostic mode only if needed for A/B comparison.  
  Phase 11 implementation + dedicated 15-case acceptance suite added; full CI verification in progress.

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

- [ ] **V2-1108 — Add first-class `WebResearchHost` provider.**  
  Structured capability family must cover `web.search`, `web.fetch`, `web.download`, `web.extract`, `web.get_metadata` and bounded `web.open_browser` fallback, with cancellation/timeout and deferred ToolRegistry exposure.

- [ ] **V2-1109 — Add `FreshnessPolicy` and freshness-required completion gate.**  
  Intents such as today/latest/current/still-effective/replaced/new-law/weather/news/current-price must require a current approved provider; model memory alone cannot complete them.

- [ ] **V2-1110 — Add durable `WebEvidence` provenance and bounded web artifact flow.**  
  Preserve evidence ID, URL, title, publisher, publish/effective/fetch times, source type, content hash and relevant excerpt; large HTML/PDF/downloads remain outside active context behind artifact/evidence handles.

- [ ] **V2-1111 — Add typed legal/regulatory status model and verifier.**  
  Distinguish REPLACED/AMENDED/SUPPLEMENTED/PARTIALLY_REPEALED/REPEALED/STILL_EFFECTIVE/NOT_YET_EFFECTIVE/UNKNOWN; prefer authoritative official sources and reject unsupported “newer means replaced” conclusions.

- [ ] **V2-1112 — Expand structured Word/Excel provider namespaces in ToolRegistry.**  
  Word must expose active document/selection, range/find/outline/paragraph/run/style/table/section/header-footer operations, replace/insert/format/save/export/verify plus native spelling/grammar candidates and legal-citation extraction; Excel must expose granular range/formula/style/merge/hidden/write/recalc/save/verify capabilities.

- [ ] **V2-1113 — Add production AutoCAD provider/native-plugin bridge contract.**  
  Target boundary: Agent → Tool/MCP provider → local IPC → C# AutoCAD plugin → DocumentLock → Transaction → Database; expose typed list/read/mutate/plot/verify families and deterministic scope/safety tests instead of arbitrary command execution as default mutation.

- [ ] **V2-1114 — Integrate dynamic provider/plugin capability refresh into AgentOrchestrator.**  
  Refresh capability metadata only at controlled task boundaries; never mutate the tool surface during an in-flight tool call; helper/provider restart must not require restarting the full H2 UI when avoidable.

- [ ] **V2-1115 — Add canonical Word + legal-research end-to-end deterministic scenario.**  
  Open/unsaved Word state → spelling candidates + legal citations → FreshnessPolicy → WebResearchHost authoritative evidence → typed legal relationships → scoped Word patch → re-read → verify/repair. Acceptance metric: Desktop pixel/computer-use calls = 0 when OfficeHost + WebResearchHost are sufficient.

---

## Phase 12 — acceptance gate before H2 Notes integration

- [ ] **V2-1201 — Build final 30+ task acceptance corpus.**  
  Corpus must explicitly include MCP provider lifecycle/scope, plugin install/update/rollback, filesystem/process/shell safety, WebResearchHost/freshness/legal research, AutoCAD structured-provider boundaries and the canonical Word + legal-research scenario.

- [ ] **V2-1202 — Run each accepted model/provider configuration >= 3 times per task.**

- [ ] **V2-1203 — Produce correctness report by task class.**  
  Report separate classes for core agent, MCP/providers, plugins/skills, filesystem/process/shell, Office, Desktop, Web/freshness, legal status and AutoCAD structured-provider flows.

- [ ] **V2-1204 — Produce performance/cost report A(raw)/B(v1)/C(v2).**  
  Include tool-schema/context size, provider reconnect/search latency, web bytes/artifact usage and plugin/skill discovery overhead where relevant.

- [ ] **V2-1205 — Verify permission/scope safety gate: zero violations.**  
  Scope includes MCP/provider isolation, plugin permissions, filesystem/process/shell side effects, Office/Desktop/Web/browser boundaries, AutoCAD bridge scope and secret-safe metadata.

- [ ] **V2-1206 — Verify >=90% supported-task correctness gate.**

- [ ] **V2-1207 — Verify context does not grow unbounded with long threads.**  
  Also verify bounded provider/tool schemas, plugin/skill catalogs and Web page/PDF evidence: full documents/pages/packages must not be replayed every turn.

- [ ] **V2-1208 — Verify open Excel/Word unsaved-state workflows.**  
  Include structured Word spelling/citation extraction and legal-reference patching while preserving unsaved edits and unrelated formatting/content.

- [ ] **V2-1209 — Verify DesktopHost observe-after-act workflows.**  
  Also prove structured-adapter priority: canonical Word + legal research must use zero Desktop pixel/computer-use calls when OfficeHost + WebResearchHost are sufficient.

- [ ] **V2-1210 — User acceptance decision.**  
  Valid only after all MCP/Web/Plugin/Legal/AutoCAD/canonical-scenario requirements from `docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md` are evidenced.  
  **Do not integrate into H2 Notes before the user explicitly approves this gate.**

---

## Phase 13 — future H2 Notes integration (blocked until V2-1210 and the normative MCP/Web/Plugin/Legal gate)

- [ ] **V2-1301 — Design H2 Notes adapter boundary around accepted Agent Lab engine.**
- [ ] **V2-1302 — Reuse H2 shared project memory/context router without coupling Agent Lab core to H2 UI.**
- [ ] **V2-1303 — Map H2 project actions to accepted structured tools/verification.**
- [ ] **V2-1304 — Preserve H2 multi-PC/NAS sync and permissions.**
- [ ] **V2-1305 — Run H2 Notes full regression/CI and staged integration acceptance.**

---

## Current execution pointer

**Active task:** `V2-1101 — Move Lab UI send path to AgentOrchestrator`.
