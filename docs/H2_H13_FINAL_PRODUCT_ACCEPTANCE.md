# H13 Final Product Acceptance Report

Status: **H2M-130 / H2M-131 / H2M-132 COMPLETE.**
H2M-133 is intentionally **BLOCKED_REAL_NAS_AND_PRODUCT_POLICY** until the required physical NAS evidence and remaining endpoint-policy decision are resolved.
H2M-134 remains **USER_ACCEPTANCE_REQUIRED**.

Exact final automated evidence for H2M-130..132: source `d15ecdca6eceb3dec2fa7783eeb69e3710812a11`, Actions run `35551667214` SUCCESS, H2 Notes **489/489**, NAS harness self-test + complete accepted Agent/provider/transport + Windows publish pipeline PASS. Published metadata commit `47c9ebb6324cd4585eda20dbdbccafa23971696a`; portable artifact SHA256 `776f087e71aed1c41e0305e9c14469f1e618e0e80c2de08771c0c9df203d187a`, 110,143,172 bytes, delivered as GitHub Actions artifact.

This report does not convert deferred physical/native checks into automated PASS claims.

---

## H2M-130 — Product correctness

### Command Center

Evidence already accepted in H11 proves the real Avalonia Command Center exposes, from source projections rather than a dashboard database:

- needs attention;
- current Agent work/waiting state;
- deterministic completed/total project task progress;
- deterministic next project task;
- latest verified activity;
- current workspace/sync health.

The five-question real-UI scenario is covered by H2M-110.

### Project Workspace

The Project Workspace remains the existing H2 project surface with:

- human project task truth in `ProjectRecord.ChecklistItems`;
- human notes in `ProjectRecord.NotesRich`;
- resource/history/evidence views rebuilt from authoritative project + Agent sources;
- Agent-first chat through `IH2AgentAdapter`;
- no copied Agent task/evidence/trace state inside `ProjectRecord`.

H2M-111 proves the existing project UI reaches the concrete production bridge and AgentRuntime without polluting the project checklist with Agent execution steps.

### Work Assistant

Accepted behavior includes:

- foreground context capture before H2 receives focus;
- explicit removable context chips;
- session/document scoped permission mapping;
- stale-target fail-closed revalidation;
- normal unscoped Agent tasks with optional later ProjectId association;
- bubble/compact completion UX using the same production Agent task;
- cancel/retry without automatic mutation replay;
- no generic Undo promise where the underlying provider has no safe undo path.

H2M-112..114 prove structured Excel, Word/Web and AutoCAD scenarios through the concrete production bridge/runtime.

### Persistence / NAS

Code-side persistence behavior includes:

- generation/hash validation;
- retained recovery journal through final validation;
- validated last-known-good fallback and write-blocked recovery state;
- bounded durable local pending snapshots for offline work;
- three-way pending replay with conflict audit;
- WorkspaceId-based logical identity;
- mapped-drive classification and mapped/UNC identity validation;
- machine-local ProjectLayout geometry and DesktopSession/window placement;
- Agent recent/evidence state machine-local rather than shared NAS project JSON.

H2M-116 proves deterministic two-PC ownership/projection behavior against one shared project root with separate machine-local roots.

**Important:** this does not replace the required physical two-PC/NAS run. See H2M-133.

### Agent integration

Production startup composes one `H2ProductionAgentAdapter`.

Normal product execution is:

`H2 UI -> IH2AgentAdapter -> H2ProductionAgentAdapter -> AgentOrchestrator -> AgentRuntime`

Ordinary H2 product code does not own provider wire payloads, ToolRegistry, MCP internals or a second Agent runtime.

Recent terminal task/evidence projection survives application restart in Agent-owned machine-local integration state.

### Legacy migration / cleanup

H2M-115 proves old project tasks, notes, multiple conversations, attachments, saved-file history and portable layout survive migration while new Agent work remains available.

H12 retires new-project execution dependencies on:

- `AiProjectContext`;
- executable `h2-actions` pseudo-actions;
- direct legacy project `AiClient`.

Historical conversations remain viewable. Legacy `h2-actions` content is preview-only history and cannot execute.

---

## H2M-131 — UX responsiveness

### Automated real-Avalonia coverage

`H2ResponsiveProductTests` exercises:

| State | Automated evidence |
| --- | --- |
| narrow | 560×820 DIP, compact project picker + bounded overlay drawer |
| short narrow | 560×600 DIP, task/note tab switching instead of crushed dual panes |
| medium | 1040×760 DIP, simultaneous readable task/note detail with splitter |
| wide desktop | 1440×860 DIP, Agent-first workspace plus bounded explicit right dock |
| multi-monitor recovery | negative-coordinate secondary monitor preserved; missing monitor falls back to reachable primary position |
| Work Assistant placement | DIP/scaling-aware monitor placement and clamping tests |

A logical large-window/wide state is exercised by the 1440×860 acceptance. Native Windows maximize/full-screen chrome behavior is not represented as a separate product layout subsystem.

### DPI

H2 stores and restores window geometry through DIP/scaling-aware helpers. Automated tests cover DIP/scaling math and monitor clamping.

**Native 100/125/150/200% Windows desktop interaction is not directly certified by headless CI.** Prior visual evidence recorded native scale 1.25 for a real app render, but this report does not extrapolate that to all DPI values.

### Keyboard

Keyboard behavior remains covered by existing project/editor/composer tests, including:

- selection/undo preservation;
- Enter/Tab/Escape interaction in picker/composer surfaces;
- keyboard-driven mention/model-picker behaviors;
- rich editor keyboard editing paths.

### Vietnamese / IME

Unicode Vietnamese text round-trips through H2 project/editor/chat/Agent/tool tests and generated Office content.

**Native Windows IME composition under real keyboard input remains NOT_NATIVE_VERIFIED.** Automated Unicode text tests are not described as native IME evidence.

### H2M-131 conclusion

Responsive product structure is covered across narrow/medium/wide logical sizes and multi-monitor/DIP ownership rules. Native multi-DPI and native IME remain explicit final-user/native-device limitations rather than hidden gaps.

---

## H2M-132 — Resource / performance

A dedicated `H2FinalPerformanceTests` suite measures on the GitHub Windows CI runner:

- first MainWindow construction/show/layout as a startup proxy;
- 25 Project Workspace switches;
- 20 Command Center refreshes;
- 100 Work Assistant Agent-progress presentation refreshes;
- 250 idle dispatcher pumps with Work Assistant bubble plus managed-memory delta;
- ProjectWorkspaceStore save for 250 projects / 1,250 tasks;
- ProjectGrid first layout for 1,000 projects / 10,000 tasks and visual count.

These are regression measurements, **not user-hardware benchmarks**. Broad time/memory ceilings exist only to catch catastrophic regressions; no optimization is justified from CI numbers alone.

Exact Windows CI metrics from source `d15ecdca6eceb3dec2fa7783eeb69e3710812a11`, Actions run `35551667214`:

| Measurement | Result |
| --- | ---: |
| MainWindow startup proxy | 58.34 ms |
| Command Center refresh ×20 | 5.59 ms total |
| Project Workspace switch ×25 | 133.01 ms total |
| Agent progress presentation refresh ×100 | 1.25 ms total |
| Work Assistant bubble idle ×250 dispatcher pumps | 0.11 ms total |
| Work Assistant managed-memory delta in idle proxy | 230,168 bytes |
| Workspace save: 250 projects / 1,250 tasks | 4,167.04 ms |
| ProjectGrid layout: 1,000 projects / 10,000 tasks | 567.04 ms |
| ProjectGrid visual elements at 10,000 tasks | 27 |

The pre-existing large-data regression in the same run also measured 2,706 ms for its older 1,000-project/10,000-task first-layout fixture, still with 27 visual elements. The difference between fixtures is why these values are regression observations, not a hardware benchmark or SLA.

An older established large-data baseline in the regression suite already verifies bounded visuals for 1,000 projects / 10,000 tasks rather than materializing all rows into the visual tree.

---

## H2M-133 — Data integrity gate

### Code-side items already resolved

The bug ledger records code-side fixes for recovery journal retention, diagnostics, last-known-good recovery, durable pending snapshots and WorkspaceId/alias safety.

Legacy migration has:

- source preservation;
- immutable pre-import/transfer backup paths;
- non-destructive migration;
- H11 proof that historical data remains readable after migration.

### Blocking real-NAS items

The following cannot be closed by temp-folder simulation:

- H2-NONAI-001 — real second-PC convergence after generation/hash races;
- H2-NONAI-004 — actual share locking/rename/flush/read-after-commit semantics;
- H2-NONAI-006 — real NAS/SMB regression coverage.

The dedicated `tools/H2Notes.NasAcceptance` harness and `docs/H2_NAS_REAL_ACCEPTANCE.md` exist, but the physical two-PC run is still deferred.

### Remaining endpoint policy decision

H2-NONAI-008 has a deliberate fail-safe implementation:

- friendly mapped/LAN endpoint or verified resolved alias;
- same WorkspaceId required before alias use;
- no mid-transaction endpoint switch;
- if unavailable, durable local pending/offline mode.

The broader optional **configured secure Remote/VPN alias failover** is not implemented and was explicitly carried to H2M-133 for final product-policy resolution.

Therefore H2M-133 must remain open until:

1. physical two-PC/NAS acceptance evidence is recorded; and
2. the user either accepts the current fail-safe LAN/offline policy as the production boundary or requests implementation of secure Remote/VPN alias failover.

---

## H2M-134 — User acceptance

H2M-134 cannot be completed automatically.

Final user approval must explicitly cover:

- Command Center;
- Project Workspace;
- Work Assistant;
- legacy migration/history behavior;
- Agent behavior;
- physical NAS evidence/result;
- native DPI/IME limitations;
- Remote/VPN endpoint policy decision;
- any other remaining limitations recorded at H2M-133.
