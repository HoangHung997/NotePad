# H2 Agent — MB-122 User Acceptance Gate

Status: **APPROVED — PHASE 13 PREPARATION AUTHORIZED**  
Date: **2026-09-19**  
Canonical architecture: `docs/H2_AGENT_MASTER_SPEC.md`  
Evidence report: `docs/H2_AGENT_FINAL_ARCHITECTURE_REPORT.md`  
Public integration boundary: `docs/H2_AGENT_PUBLIC_INTEGRATION_BOUNDARY.md`

> This document is the human approval gate between the accepted Agent architecture and Phase 13 H2 Notes integration preparation.
>
> **The user explicitly approved MB-122 on 2026-09-19. Phase 13 preparation is authorized; implementation must still obey the H2 Product Master storage/data-integrity gates.**

---

## 1. Minimum Bootable Agent review

Accepted deterministic evidence:

- MB-100 Minimum Bootable Agent corpus: **12/12 passed**.
- MB-101 normal UI path is fully V2; no hidden normal fallback to `AgentRunner`.
- MB-102 context remains bounded for 10 / 100 / 1000 historical turns and repeated large tool outputs.
- MB-103 extension bus can register/use/disable/unregister capabilities without editing `AgentRuntime`.
- MB-104 permission/scope gate: **10/10 passed, 0 boundary violations**.
- MB-105 core correctness: **14/14 = 100.00%** on the accepted deterministic core corpus; **0 false-completion violations** in required negative cases.

Minimum Bootable Agent behavior covered by the accepted corpus:

1. direct response;
2. multi-tool execution;
3. deferred `tool_search` schema loading;
4. tool failure -> recovery;
5. mutation -> verification PASS;
6. mutation -> verification FAIL -> bounded repair -> PASS;
7. long bounded context;
8. cancellation;
9. provider switching;
10. plugin tool registration;
11. progressive skill loading;
12. install-and-continue.

Review conclusion: **deterministic MBA gate is green**.

---

## 2. Reference extension behavior review

Accepted reference-extension gates:

| Extension | Gate result | Accepted behavior |
|---|---:|---|
| Office | 6/6 | unsaved-state handling, structured mutation, preservation, native language evidence, verifier |
| Web / freshness / legal | 5/5 | freshness requirement, authoritative evidence, bounded artifacts, typed legal relationship evidence |
| Desktop / computer use | 7/7 | safe window scope, UIA, typed actions, stale-state, observe-after-act, vision fallback, sensitive-app blocks |
| AutoCAD | 6/6 | typed document/entity discovery, attributes/layers, bounded mutation, state token, verification, no arbitrary-command default |
| MCP | 6/6 | connect/reconnect, discovery, deferred schemas, scope policy, provenance, cancellation/timeout |
| Plugin lifecycle | 5/5 | install, update safety, integrity/self-test failure, rollback, quarantine, safe hot registration |

Review conclusion: **required reference-extension acceptance gates are green**.

---

## 3. Performance / boundedness review

Accepted context limits:

- active context hard bound: **24,000 characters**;
- recent turns: **8**;
- tool summaries: **8**;
- serialized provider-request acceptance ceiling: **40,000 bytes**.

Measured MB-102 evidence:

| Fixture | Active chars | Provider request bytes |
|---|---:|---:|
| 10 historical turns | 4,872 | 6,091 |
| 100 historical turns | 5,821 | 7,053 |
| 1,000 historical turns | 5,837 | 7,069 |
| repeated large tool artifacts | 9,858 | 11,139 |

Large raw tool outputs stay outside the active prompt as durable artifact/evidence handles.

Tool exposure:

- normal built-in registry: **19 callable descriptors**;
- initial built-in provider exposure: **2 schemas** (`tool_search` + `update_plan`);
- detailed schemas load only after selection during the same task.

Review conclusion: **accepted deterministic boundedness/performance gates are green**.

---

## 4. Safety review

Accepted safety properties:

- host-owned read/mutation permission enforcement;
- scope identity at ToolRegistry/scheduler boundary;
- no skill text can grant itself authority;
- no model-only phrase can mark a required-verification mutation complete;
- important outputs become durable evidence/artifact handles;
- verifier failure can block completion and invoke bounded repair;
- cancellation propagates to model/tool/helper paths;
- DesktopHost uses safe-window policy, state IDs, short-lived element tokens and observe-after-act evidence;
- OfficeHost/DesktopHost native helpers are isolated from normal model/runtime internals;
- plugin/package integrity, permission delta, rollback and quarantine are tested;
- MCP scope/provenance/timeout/cancellation are tested;
- secrets are excluded from approved metadata projections;
- MB-104 consolidated result: **0 known boundary violations in the accepted corpus**.

Review conclusion: **accepted deterministic safety gate is green**.

---

## 5. Known limitations requiring explicit acceptance

The following remain intentionally true and are **not** hidden defects in the acceptance claim:

1. H2 Notes production integration has **not** started.
2. Gemini AgentRuntime tool transport is unsupported and fails closed.
3. The standalone Lab UI exposes Ollama and OpenAI-compatible Chat Completions directly; not every runtime transport is exposed in that UI.
4. Deterministic acceptance is not exhaustive certification for every real model/application/service.
5. AutoCAD acceptance uses the typed bridge fixture; it does not certify every AutoCAD version/drawing.
6. Web/legal acceptance proves policy semantics; live source quality depends on the configured backend.
7. DesktopHost acceptance does not guarantee UI Automation compatibility with every Windows application.
8. Office acceptance does not guarantee pixel-perfect layout for arbitrary documents/printers/fonts.
9. MCP acceptance uses deterministic fixtures; third-party MCP servers may behave differently.
10. No statistically meaningful production verification-failure rate exists yet; it must be measured after integration.
11. Marketplace complexity remains intentionally deferred.

Review conclusion: **these limitations must be consciously accepted before Phase 13 integration preparation**.

---

## 6. Frozen H2 Notes integration boundary

After approval, H2 Notes may depend only on:

1. `LoadProjectContext(...)`
2. `StartTaskAsync(...)`
3. `ObserveProgress(...)`
4. `InspectTask(...)`
5. `Cancel(...)`
6. `ProvideApproval(...)`
7. `WaitForFinalResultAsync(...)`

H2 Notes must not directly depend on `AiProfile`, transports, `AgentRuntime`, `AgentOrchestrator`, `AgentTools`, `ToolRegistry`, provider/plugin implementation objects, raw prompt/cache internals, or model-specific request/response types.

---

## 7. Frozen evidence snapshot

The user-review package is frozen against the last functional Agent source and its full green pipeline:

- final functional source commit: `0a7ea1e6586bc5e70e336536603a8044fab654d6`;
- GitHub Actions run: `35448436223` — **SUCCESS**;
- MB-121 public integration boundary: **5 passed, 0 failed**;
- all accepted Agent regression/acceptance suites through MB-120: green in the same run;
- DesktopHost / OfficeHost / provider transport / resilience suites: green;
- publish bot commit: `22f5861f5c2c9d1a9d3f8d1aff7f2d6327f88bdc`;
- repository portable ZIP SHA256: `408fc95425ba0d67ec0b091ecf24ce61ec0e3945e47688513797afb2a6e54760`;
- repository `bin/LATEST.txt` points to the same functional source and SHA256.

After that functional run, the branch received only MB-121 closure and MB-122 acceptance-package documentation commits marked `[skip ci]`; no Agent runtime/extension/integration implementation changed before this review gate.

Review conclusion: **the acceptance package is internally consistent with the latest functional green artifact**.

---

## 8. User decision

Current machine state:

```text
MB-122 = APPROVED_BY_USER
AGENT_INTEGRATION_GATE = PASSED
MB-123_PHASE13_PREPARATION_ALLOWED = YES
H2_NOTES_IMPLEMENTATION = SUBJECT_TO_H2_PRODUCT_MASTER_GATES
```

Approval evidence:

- explicit user approval received on 2026-09-19;
- approval authorizes closing MB-122 and proceeding to MB-123 Phase 13 preparation;
- this approval does not waive storage/data-integrity, migration, NAS, or product acceptance gates defined by the canonical H2 Product Master tracker.

Decision recorded: **APPROVED**.

MB-122 is closed. MB-123 Phase 13 preparation may proceed. Any later H2 implementation remains governed by the canonical H2 Product Master specification/task tracker and the independent non-AI bug ledger.
