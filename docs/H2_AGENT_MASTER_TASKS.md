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

## [~] MB-20 — Move V2 context into pre-request execution

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

## [ ] MB-21 — Wire CompactionManager into runtime pressure

Goal:

Use compaction automatically when context pressure requires it.

Acceptance:

- runtime can continue long tool loops without unbounded context;
- compaction source references remain auditable;
- raw evidence is retained outside active prompt;
- repeated turns do not repeatedly replay unchanged large summaries.

---

## [ ] MB-22 — Use AgentPromptLayout and cache identity in real requests

Goal:

Separate stable prefix from dynamic runtime suffix in normal execution.

Acceptance:

- stable policy/tool namespace metadata stays cacheable;
- current task/session/time state does not contaminate stable cache identity;
- skill hash changes invalidate only the relevant stable identity where configured.

---

# Stage D — Make ToolRegistry the only normal callable path

## [ ] MB-30 — Replace full AgentTools.Definitions exposure

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

---

## [ ] MB-31 — Execute tool calls through ToolRegistry

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

---

## [ ] MB-32 — Make deferred tool loading real during the same task

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

---

## [ ] MB-33 — Wire ToolExecutionScheduler into real calls

Goal:

Use existing scheduler semantics.

Acceptance:

- independent safe reads may overlap;
- same-resource mutations cannot overlap;
- mutating call without resource identity fails closed;
- cancellation propagates.

---

# Stage E — Permission / evidence / verification becomes part of the loop

## [ ] MB-40 — Centralize normal runtime permission enforcement

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

---

## [ ] MB-41 — Wire artifacts/evidence into tool results

Goal:

Important tool results produce bounded context projections and durable evidence/handles.

Acceptance:

- large outputs are not injected in full;
- evidence hashes are stable;
- verifier can reference evidence IDs;
- final answer can cite/describe actual observed work without replaying giant raw outputs.

---

## [ ] MB-42 — Integrate verification into normal mutating tasks

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

---

## [ ] MB-43 — Integrate bounded repair loop

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

---

## [ ] MB-44 — Host-owned final completion gate

Goal:

Only host state may mark a mutating task complete.

Acceptance:

- model phrase “done” is insufficient;
- required verifier failures keep task non-complete;
- non-mechanically-verifiable tasks have explicit classification/evidence.

---

# Stage F — Simplify Skills instead of building a second AI

## [ ] MB-50 — Establish one canonical SkillCatalog

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

---

## [ ] MB-51 — Codex-style discovery only: name + description

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

---

## [ ] MB-52 — Progressive skill loading

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

---

# Stage G — Extension bus and first-party cards

## [ ] MB-60 — Define one simple extension registration boundary

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

---

## [ ] MB-61 — Remove app-specific ranking from generic core

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

---

## [ ] MB-62 — Convert general computer capability inventory to provider registration

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

---

## [ ] MB-63 — Keep first-party extension implementations

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

---

# Stage H — Remove over-design from capability/catalog layer

## [ ] MB-70 — Remove generic domain synonym intelligence

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

---

## [ ] MB-71 — Simplify InstalledCapabilityIndex

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

---

## [ ] MB-72 — Simplify AvailableCapabilityIndex

Goal:

Do not require a heavy available index for a small catalog.

Allowed:

- compact in-memory search;
- optional derived cache.

Do not add vector DB now.

Acceptance:

- remote metadata search works for current acceptance scale;
- architecture leaves room for future semantic search extension.

---

## [ ] MB-73 — Thin or remove CapabilityResolver

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

---

## [ ] MB-74 — Retire MissingCapabilityContinuation

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

---

## [ ] MB-75 — Simplify task capability pinning

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

---

# Stage I — Minimal external catalog/install path only

## [ ] MB-80 — Keep one minimal ICatalogSource seam

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

---

## [ ] MB-81 — Keep IPackageRetriever + PluginManager separation

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

---

## [ ] MB-82 — End-to-end install-and-continue test

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

---

# Stage J — Legacy cleanup

## [ ] MB-90 — Retire AgentRunner from normal runtime

After real runtime parity:

- normal UI no longer instantiates it;
- keep frozen baseline harness only if still needed for A/B report;
- otherwise move to legacy/test or remove.

Acceptance:

repository guard fails if production UI path references AgentRunner.

---

## [ ] MB-91 — Retire AgentTools giant switch

Migrate any remaining useful tool implementation into real providers/executors.

Then:

- stop using `AgentTools.Definitions`;
- stop using giant `AgentTools.Execute` in normal runtime.

Acceptance:

ToolRegistry is sole normal callable surface.

---

## [ ] MB-92 — Retire V1ToolRegistryAdapter

Delete only after AgentTools no longer supplies normal tools.

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
