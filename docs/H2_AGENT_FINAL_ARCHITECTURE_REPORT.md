# H2 Agent — Final Architecture Report

Status: **MB-120 integration-readiness architecture baseline**  
Date: **2026-09-19**  
Canonical architecture: `docs/H2_AGENT_MASTER_SPEC.md`  
Canonical execution tracker: `docs/H2_AGENT_MASTER_TASKS.md`

## 1. Evidence baseline

This report describes the accepted Agent architecture after MB-115 and before the public H2 Notes integration boundary is frozen in MB-121.

Deterministic baseline:

- final functional source under test: `a075c65b223ca312fc3a5a2cfe398e59824a015a`;
- GitHub Actions run: `35446625544` — **SUCCESS**;
- publish bot commit: `59e3a72b7231f04a904f957db15d20db34d25918`;
- portable ZIP SHA-256: `43cf43f842a557c0b78c9fc74ef5a7f0ad08d58cfdb2e2251248434145c5cac9`;
- H2 Notes regression in the same run: **336 passed, 0 failed**;
- MB-100 through MB-115 acceptance gates all passed.

This is an architecture/correctness acceptance baseline. It is not a claim that every real model, desktop application, web endpoint, AutoCAD installation, MCP server or production workload has been exhaustively certified.

---

## 2. Final real runtime call graph

Normal interactive execution is V2-only:

```text
LabWindow
  -> AgentOrchestratedRun
     -> AgentOrchestrator
        -> AgentRuntimeFactory
           -> AgentTransportFactory
              -> IAgentTransport
           -> NormalRuntimeToolRegistry
              -> ToolRegistry
              -> domain executors / extension descriptors
           -> AgentRuntimeDomainVerifierRouter
           -> AgentRuntimeEvidenceProjector
        -> AgentRuntime
           -> AgentContextManager
           -> AgentPromptLayout
           -> DeferredToolDiscovery / ToolSearchIndex
           -> ToolExecutionScheduler
           -> ScopedAgentRuntimePermissionPolicy
           -> ArtifactStore / evidence handles
           -> verifier
           -> AgentRepairController
           -> host-owned completion gate
```

The important boundary is that `AgentRuntime` is provider-neutral and application-neutral. It does not contain Office, Desktop, AutoCAD, MCP or provider-specific HTTP logic.

For a mutating task the effective control flow is:

```text
user task
  -> bounded context + stable prompt
  -> model request with deferred tool surface
  -> tool_search
  -> selected schema load
  -> ToolRegistry executor
  -> permission/scope enforcement
  -> mutation
  -> re-observation / durable evidence
  -> deterministic verification
     -> PASS: host completion gate may complete
     -> FAIL: bounded failed-criteria repair context
              -> corrective tool call(s)
              -> verify again
```

The model cannot mark a required-verification mutation complete by saying “done”.

---

## 3. Core versus extensions

### 3.1 Core runtime

The core is intentionally small and domain-neutral:

- `Runtime/AgentRuntime.cs` — model/tool/verification/repair loop;
- `Tasking/AgentOrchestrator.cs` and `AgentOrchestratedRun.cs` — lifecycle/state/UI-facing orchestration;
- `Transport/IAgentTransport.cs` + `AgentTransportFactory.cs` — provider selection boundary;
- `Context/AgentContextManager.cs` + session adapter/compaction — bounded active context;
- `Prompting/` — stable/dynamic prompt layout and cache identity;
- `Tools/ToolRegistry.cs`, `DeferredToolDiscovery.cs`, `ToolSearchIndex.cs`, `ToolExecutionScheduler.cs`;
- `Skills/UnifiedSkillCatalog.cs` — the canonical `Skills.SkillCatalog` and skill sources;
- `Session/ArtifactStore.cs` and compaction state;
- `Verification/` — typed reports, completion gate and repair controller;
- `Extensions/` — one registration boundary;
- `Providers/` — provider-neutral capability provider contracts;
- `Plugins/` and `Catalog/` — verified package lifecycle and minimal retrieval/catalog seams.

Core policy does not depend on hard-coded Word/Excel/AutoCAD intent dictionaries.

### 3.2 First-party/reference extensions

Specialized abilities live outside core:

- filesystem;
- process/shell;
- Python sandbox;
- Office / OfficeHost;
- WebResearchHost;
- Desktop / DesktopHost;
- AutoCAD structured provider bridge;
- MCP providers;
- browser fallback where explicitly registered.

The extension bus can register/unregister tools, skills, verifiers and providers without modifying `AgentRuntime`. MB-103 proved registration, ordinary runtime discovery/use, disable and cleanup.

---

## 4. Legacy removed or retained

### 4.1 Removed from the normal architecture

The following duplicate/legacy paths have been retired:

- `Tools/V1ToolRegistryAdapter.cs`;
- normal execution through the giant `AgentTools.Definitions` / `AgentTools.Execute` switch;
- root legacy `SkillCatalog.cs` and `LabSkill`;
- `Tools/DeferredSkillSession.cs`;
- historical behavior-free `UnifiedSkillCatalog` alias;
- `ComputerTools.cs` and the legacy `--computer-worker` path;
- `MissingCapabilityContinuation`;
- generic hard-coded domain synonym logic.

### 4.2 Retained only for compatibility/baseline purposes

- `AgentRunner.cs` remains only as a frozen v1 baseline/live-evaluation harness. The normal UI path cannot route to it.
- `AgentTools.cs` remains as host state/service plumbing and frozen-v1 compatibility. The normal V2 callable surface is owned by `NormalRuntimeToolRegistry`, not its giant switch.
- `LabSession.Context()` may still exist for frozen legacy diagnostics, but the normal V2 request path uses the bounded context adapter/manager.

These retained pieces are not architectural authorities for new work.

---

## 5. Context metrics

Canonical hard bounds currently guarded by MB-102:

| Metric | Bound |
|---|---:|
| Active context | 24,000 characters |
| Recent turns | 8 |
| Tool summaries | 8 |
| Serialized provider request acceptance ceiling | 40,000 bytes |

Measured in successful run `35446625544`:

| Fixture | Historical turns | Active chars | Provider request bytes | Candidate chars | Session events |
|---|---:|---:|---:|---:|---:|
| history-10 | 10 | 4,872 | 6,091 | 5,331 | 12 |
| history-100 | 100 | 5,821 | 7,053 | 13,431 | 102 |
| history-1000 | 1,000 | 5,837 | 7,069 | 13,439 | 1,002 |
| large-tool-artifacts | 120 | 9,858 | 11,139 | 16,146 | 138 |

Observations:

- request size grows only marginally from 100 to 1,000 historical turns;
- the 1,000-turn request remains far below the 24,000-character context budget and 40,000-byte request gate;
- repeated large raw tool outputs exceeded 300 KB in the MB-102 fixture but remained outside active context as durable artifact/evidence handles;
- compaction and evidence storage preserve raw/auditable state outside the active model prompt.

---

## 6. Tool/schema metrics

### 6.1 Built-in normal runtime

The accepted normal built-in registry contains **19 callable descriptors**.

The canonical runtime initial exposure is **`tool_search` plus the optional minimum stable core `update_plan` schema**. Because the normal built-in registry contains `update_plan`, its built-in initial callable schema count is **2**. MB-30 additionally requires the serialized initial schema bytes to be less than half of the full canonical registry schema bytes. Detailed capability schemas are loaded only after selection and only for the same task continuation.

This yields different counts by design:

- registry inventory: 19 built-in normal descriptors;
- normal built-in initial provider schema exposure: 2 schemas (`tool_search` + `update_plan`);
- MB-101 focused UI fixtures: `tool_search` only because their fixture registry intentionally omits `update_plan`;
- selected schemas: loaded on demand and coalesced.

The model therefore does not receive the full Office/Desktop/Python/file schema inventory at task start.

### 6.2 Extension schemas

Extension/provider schemas remain deferred where possible:

- MB-103: a new extension tool is discoverable and executable without `AgentRuntime` edits;
- MB-113: AutoCAD exposes a structured typed surface, not arbitrary command execution;
- MB-114: MCP detailed schemas are not injected until selected;
- MB-115: newly installed plugin tools can hot-register at a safe boundary.

---

## 7. Correctness and completion safety

### 7.1 Minimum Bootable Agent

MB-100 acceptance corpus: **12/12 passed**.

Covered cases:

1. direct response;
2. multi-tool task;
3. dynamic `tool_search` schema loading;
4. tool error -> recovery;
5. mutation -> verification PASS;
6. mutation -> verification FAIL -> repair -> PASS;
7. long context;
8. cancellation;
9. provider switch;
10. plugin tool registration;
11. progressive skill loading;
12. install-and-continue.

### 7.2 Core correctness gate

MB-105 result:

- supported deterministic corpus: **14/14 = 100.00%**;
- required minimum: 90%;
- false-completion violations: **0/2 = 0%**;
- completion-policy suite exit: success.

“100%” here means 100% of the defined deterministic supported acceptance corpus, not universal model accuracy.

### 7.3 Permission/scope gate

MB-104 result:

- **10/10 passed**;
- boundary violations: **0**.

Coverage includes host runtime policy, filesystem, process/shell, plugin/package boundaries, provider scopes, DesktopHost, OfficeHost, web scope, native-helper IPC and secret metadata handling.

---

## 8. Verification failure rate

There is not yet a statistically meaningful **production/live verification failure rate**. No production telemetry corpus has been accepted as representative, so this report does not invent one.

Two deterministic safety measurements are available:

1. **Synthetic repair fixture:** MB-101 intentionally makes the first verifier invocation fail and the second pass. That fixture therefore has a designed verification failure rate of **1/2 = 50%**, followed by successful bounded repair. This is test construction, not a field reliability number.
2. **False-completion safety:** MB-105 has **0/2 false-completion violations = 0%** for the two required negative completion cases.

For production integration, H2 Notes should record verifier attempts, failed criteria, repair rounds and final completion state so a real operational failure rate can later be calculated without conflating expected test failures with incidents.

---

## 9. Model transport/provider matrix

| Profile / transport | AgentRuntime status | Evidence |
|---|---|---|
| Ollama native | Supported | `OllamaTransport`, transport/runtime suites PASS |
| OpenAI-compatible Chat Completions | Supported | `ChatCompletionsTransport`, transport/runtime suites PASS |
| OpenAI Responses HTTP/SSE | Supported | `OpenAiResponsesTransport`, transport suites PASS |
| OpenAI Responses WebSocket | Supported when explicitly selected | `OpenAiResponsesWebSocketTransport`, WebSocket suites PASS |
| Gemini Agent tool transport | **Not supported** | factory throws `NotSupportedException`; no silent fallback to v1 |

Provider selection is isolated in `AgentTransportFactory`; `AgentRuntime` does not switch on provider names/protocols.

The standalone Lab UI currently exposes Ollama local/LAN and OpenAI-compatible Chat Completions directly. Responses transports exist in the runtime/factory and test matrix but are not yet exposed as full standalone Lab UI choices.

---

## 10. Extension acceptance matrix

| Gate | Result | What it proves |
|---|---:|---|
| MB-103 Extension bus | 4/4 | register/use/disable/unregister without core edits |
| MB-110 Office | 6/6 | structured Office inspection/mutation/verification/preservation |
| MB-111 Web/freshness/legal | 5/5 | current-evidence requirement, source authority, typed legal relationship evidence |
| MB-112 Desktop/computer-use | 7/7 | safe window scope, bounded UIA/pixels, stale-state, permission, observe-after-act |
| MB-113 AutoCAD | 6/6 | typed structured discovery/read/mutate/verify surface; no arbitrary command default |
| MB-114 MCP | 6/6 | lifecycle/reconnect, tools/resources, deferred schemas, scope policy, provenance, timeout/cancel |
| MB-115 Plugin lifecycle | 5/5 | install/hot-register, update safety, integrity/self-test, rollback, quarantine |

---

## 11. Known limitations

1. **No H2 Notes production integration yet.** MB-121 must freeze the public integration boundary and MB-122 requires explicit user acceptance before production integration.
2. **Gemini AgentRuntime tool transport is unsupported.** The factory fails closed instead of silently switching to the legacy runner.
3. **The standalone Lab UI does not expose every transport implemented by the factory.** It currently offers Ollama and Chat-compatible API profiles directly.
4. **Acceptance is deterministic, not exhaustive real-world certification.** Several extension gates use controlled fixtures/backends/bridges. Real external applications, web services, AutoCAD installations and MCP servers can introduce additional behavior.
5. **AutoCAD CI acceptance uses the typed bridge fixture.** It proves architecture/policy/schema/verification behavior, not every native AutoCAD version or drawing.
6. **Web legal/freshness acceptance proves policy semantics with deterministic evidence fixtures.** Availability and quality of live public sources still depend on the configured web backend.
7. **DesktopHost acceptance proves the security/state model and fixture behavior, not universal UI Automation compatibility with every Windows application.**
8. **Office acceptance proves structured preservation and verification paths, not arbitrary pixel-perfect Word layout across all documents/printers/fonts.**
9. **MCP acceptance uses a deterministic RPC fixture.** Individual third-party MCP servers can still violate expectations or expose different schemas/latency/failure modes.
10. **No production verification failure rate is established yet.** Operational telemetry must be collected after integration rather than inferred from synthetic repair tests.
11. **Marketplace complexity remains intentionally deferred.** There is no requirement for a large embedding/vector marketplace, generic dependency solver or automatic Internet plugin installation in core.

---

## 12. Integration-readiness conclusion

The accepted architecture is now:

> **A small provider-neutral AgentRuntime with bounded context, deferred ToolRegistry discovery, host-owned permission and verification, durable evidence, bounded repair, and an extension/provider bus around the core.**

The Minimum Bootable Agent and required reference extension gates are green. The remaining work is integration governance rather than another core rewrite:

```text
MB-120 final architecture report
  -> MB-121 freeze public H2 Notes integration boundary
  -> MB-122 explicit user acceptance
  -> MB-123 Phase 13 preparation
```

Do not bypass MB-121/122 by wiring H2 Notes directly to internal transport, ToolRegistry, verifier, provider or plugin implementation details.


## AR-001 current-surface correction (2026-09-22)

The 19-callable/six-executor measurements above are the historical MB-120 snapshot, not the current registry inventory. The already-shipped `read_tool_output` adds one read-only evidence tool and one evidence executor: current inventory is 20 callable descriptors / seven executors. Initial exposure remains `tool_search` plus `update_plan` (two schemas); no additional tool is eagerly exposed. AR-001 updates exact-set guards and tests chunk/foreign-handle behavior; it does not re-award any old MB acceptance.
