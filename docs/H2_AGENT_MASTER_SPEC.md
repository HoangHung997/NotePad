# H2 Agent — Master Architecture Specification

Status: **CANONICAL / NORMATIVE AFTER THE CURRENT IN-FLIGHT AGENT LAB TASK FINISHES**  
Date: 2026-09-18  
Scope: H2 Agent Lab core architecture, extension architecture, migration and acceptance direction.

> This document is the single canonical architecture specification for H2 Agent after the 2026-09-18 architecture audit.
>
> Read together with:
>
> - `docs/H2_AGENT_MASTER_TASKS.md`
>
> The following documents become historical/background after the current in-flight Agent Lab task finishes:
>
> - `experiments/H2AgentLab/AGENTLAB_V2_SPEC.md`
> - `experiments/H2AgentLab/AGENTLAB_V2_TASKS.md`
> - `docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md`
> - `docs/H2_AGENT_SKILL_TOOL_EXTENSIBILITY_REFINEMENT.md`
>
> They remain useful evidence/history but are no longer independent normative specifications.
>
> The following H2 Notes documents remain separate and are **not superseded**:
>
> - `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md`
> - `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

---

## 1. Architectural reset

The previous design accumulated many good components, but the architecture gradually became more complex than necessary.

The new rule is:

> **Build the smallest complete Agent that can boot, reason, call tools, verify work and accept extensions. Everything domain-specific is an extension.**

Use the PC analogy:

```text
PC concept                  H2 Agent concept
────────────────────────────────────────────────────────
Mainboard                   Agent core / extension contracts
CPU                         Model through IAgentTransport
RAM                         Active context / compaction
Storage                     Task state / artifacts / evidence
PCIe / USB bus              ToolRegistry / provider contracts
BIOS / boot lifecycle       AgentRuntime + AgentOrchestrator
Driver/package manager      PluginManager
Driver/manual               Skill
Expansion card              Plugin / provider / specialized tool family
Protection / PSU            Permission / sandbox / cancellation / safety
Driver repository           CatalogSource
```

The Agent core must not know every future application or workflow.

The core must provide stable extension points so later capabilities can be attached without redesigning the core.

---

## 2. Audit baseline — what the code actually does today

At the 2026-09-18 audit, Agent Lab contains many accepted V2 components:

- provider-neutral transports;
- context manager;
- compaction;
- prompt layout/cache identity;
- ToolRegistry;
- deferred tool search;
- scheduler;
- verification framework;
- OfficeHost;
- DesktopHost;
- WebResearchHost;
- AutoCAD provider contract;
- MCP provider support;
- PluginManager;
- skill/plugin extension work.

The interactive UI path now executes through the real V2 runtime:

```text
LabWindow
  -> AgentOrchestratedRun
  -> AgentOrchestrator
  -> AgentRuntime
  -> IAgentTransport
  -> deferred ToolRegistry / verifier / evidence flow
```

MB-90 removes `AgentRunner` from the production orchestration graph. The v1 runner remains only as a frozen baseline/test/live-evaluation harness while legacy cleanup continues.

The remaining migration gap is narrower:

- normal `AgentRuntimeFactory` now constructs `NormalRuntimeToolRegistry`, and callable execution is split across domain executors rather than `AgentTools.Definitions` / the giant `AgentTools.Execute` switch;
- the legacy `AgentTools` object remains temporarily as host state/approval/journal storage and for frozen v1 tests; `V1ToolRegistryAdapter` has been retired, and reusable canonical schema/risk/scope/preference metadata now comes directly from `NormalRuntimeToolRegistry`;
- the duplicate legacy skill/catalog path is retired in MB-93, and the legacy `ComputerTools` path is retired in MB-94; selected-window execution now routes through the isolated DesktopHost boundary.

Therefore the next architectural priority is **legacy cleanup behind the already-bootable AgentRuntime**, not a return to the v1 model loop.

---

## 3. Product-level core principle

The core Agent must be application-neutral.

The core does **not** intrinsically mean:

```text
Agent = Word + Excel + AutoCAD + Web + Desktop + Python + ...
```

Instead:

```text
Agent Core
   |
   +-- optional/installed FileSystem extension
   +-- optional/installed Process/Shell extension
   +-- optional/installed Office extension
   +-- optional/installed Web extension
   +-- optional/installed Desktop extension
   +-- optional/installed AutoCAD extension
   +-- optional/installed MCP providers
   +-- future Revit / Zalo / GitHub / SAP / GIS / Trading / ...
```

First-party extensions may ship with the product, but they remain extensions conceptually.

---

## 4. Minimum Bootable Agent

A complete minimum H2 Agent requires these responsibilities:

```text
H2 Agent Core
│
├─ AgentRuntime
├─ AgentOrchestrator / TaskContract
│
├─ Model Transport
│   └─ IAgentTransport
│
├─ Context
│   ├─ AgentContextManager
│   └─ CompactionManager
│
├─ Prompt
│   ├─ AgentPromptLayout
│   └─ AgentPromptCacheIdentity
│
├─ Tools
│   ├─ ToolRegistry
│   ├─ ToolSearchIndex
│   ├─ DeferredToolDiscovery
│   └─ ToolExecutionScheduler
│
├─ Skills
│   └─ one logical SkillCatalog
│
├─ Extensions
│   ├─ PluginManager
│   ├─ ICapabilityProvider
│   └─ minimal CatalogSource seam
│
├─ Permission / Sandbox / Cancellation
│
├─ State / Artifact / Evidence
│
└─ Verification / Repair / Completion gate
```

If those pieces work end-to-end, the Agent can boot.

Office, AutoCAD, Web and other special capabilities are not required to define the core architecture itself.

---

## 5. The new missing centerpiece: AgentRuntime

Add one real runtime/engine that owns the model-tool loop.

Name may be:

- `AgentRuntime`;
- `AgentEngine`;

but there must be one obvious runtime path.

Conceptual flow:

```text
User request
   |
   v
TaskContract
   |
   v
AgentOrchestrator
   |
   v
AgentContextManager / CompactionManager
   |
   v
Skill metadata + PromptLayout
   |
   v
DeferredToolDiscovery
   |
   v
IAgentTransport.StartAsync
   |
   +-- model text
   |
   +-- model tool call
           |
           v
       ToolRegistry
           |
           v
       permission/scope
           |
           v
       ToolExecutionScheduler
           |
           v
       tool result/evidence
           |
           v
       IAgentTransport.ContinueAsync
           |
           ... repeat ...
   |
   v
Verifier(s)
   |
   +-- PASS -> Completion gate -> Final
   |
   +-- FAIL -> AgentRepairController -> continue repair
```

The runtime may call many tools before producing the final answer.

No fixed small number of tool calls is required.

---

## 6. AgentOrchestrator responsibility

Keep `AgentOrchestrator`.

It owns host-side lifecycle and state, not provider wire protocol.

Responsibilities:

- receive task contract;
- route task class;
- own task state transitions;
- own task boundary;
- start/coordinate AgentRuntime;
- coordinate verification and repair;
- expose typed progress;
- block invalid completion;
- cancel/fail safely.

It should not:

- manually implement OpenAI/Ollama wire formats;
- carry Word/Excel/AutoCAD business logic;
- carry remote catalog ranking logic;
- know all future plugin types.

`AgentRunner` is no longer reachable from the normal production runtime. Frozen v1 baseline/test/live-evaluation harnesses may retain it temporarily until their dedicated cleanup task.

---

## 7. Model layer

Keep:

```text
IAgentTransport
OllamaTransport
ChatCompletionsTransport
OpenAiResponsesTransport
OpenAiResponsesWebSocketTransport
AgentTransportCapabilities
```

The model is replaceable.

Concept:

```text
AgentRuntime
     |
     v
IAgentTransport
   ├─ Ollama
   ├─ OpenAI Responses
   ├─ Responses WebSocket
   └─ Chat-Completions-compatible fallback
```

No core logic may depend on one future model name.

A future GPT model or local model should be selectable without redesigning tools, skills or task state.

---

## 8. Context and memory

Keep:

- `AgentContextManager`;
- `CompactionManager`;
- `AgentPromptLayout`;
- `AgentPromptCacheIdentity`;
- artifact/evidence references.

These must move into the real model request path.

The model active context must be bounded.

The model context is **not** the entire durable history.

```text
Active model context
    !=
Durable session/task/project memory
```

Rules:

- task contract: bounded;
- current working state: bounded;
- recent relevant turns: bounded;
- tool summaries: bounded;
- compacted history: bounded;
- large raw outputs live in artifacts/evidence;
- full old journal is not replayed every request.

Legacy `LabSession.Context()` must not remain the primary production context assembler after migration.

---

## 9. Tool architecture

Keep `ToolRegistry` as the central extension bus.

A tool descriptor contains generic callable information:

```text
name
namespace
description
schema
access type
risk
parallel safety
executor
resource scope
provenance/version
verification-evidence capability
```

The core must not know whether a tool came from:

- built-in implementation;
- first-party extension;
- plugin;
- MCP server;
- local native helper;
- future external provider.

The model sees tools through one registry/discovery model.

---

## 10. Deferred tool discovery

Keep:

- `ToolSearchIndex`;
- `DeferredToolDiscovery`.

Normal first request:

```text
tool_search
+ minimal stable core tool(s)
+ compact namespace metadata
```

Do not send the full tool schema surface every turn.

Flow:

```text
model needs capability
 -> tool_search("read formulas from active workbook")
 -> host returns relevant selected tool schemas
 -> next model continuation receives only newly selected schemas
```

Tool search may remain deterministic lexical/BM25-style.

Tool callable names/descriptions are normally precise enough.

Do not add embeddings to core tool search unless real evidence later proves they are necessary.

---

## 11. Tool execution

Keep `ToolExecutionScheduler`.

Rules:

- independent read-only tools may run in parallel;
- mutations to the same resource serialize;
- mutating operations require resource identity/scope;
- tool cancellation propagates;
- result size is bounded;
- tool results are untrusted data;
- important results become evidence/artifact references.

The runtime must execute through `ToolDescriptor.Executor`, not a giant switch over every future tool.

---

## 12. Skills — simple Codex-style model

The canonical minimum skill contract is:

```text
skill-name/
├─ SKILL.md
├─ references/    optional
├─ scripts/       optional
├─ assets/        optional
└─ agents/        optional UI/invocation metadata
```

Discovery metadata before loading the skill:

```yaml
name: cad-integrity
description: >
  Inspect AutoCAD dynamic blocks, attributes, parameters, actions
  and visibility-state consistency.
```

The important field is description.

Do not require exact skill-name matching.

Do not require every skill author to fill a large mandatory schema of aliases/intents/examples.

Progressive disclosure:

```text
1. name + description + provenance
2. selected SKILL.md
3. only the specific references/scripts/assets requested when needed
```

The model decides which skill guidance is useful.

The host does not need to become a second semantic AI.

---

## 13. Skill resource behavior

Do not invent a mandatory H2-specific `resource:` directive syntax.

Instead:

```text
model selects skill
 -> read SKILL.md
 -> SKILL.md says which reference/helper is relevant
 -> model requests that resource through a bounded host tool
```

Scripts contained in a skill are not automatically trusted executable authority.

They still go through:

- permission;
- sandbox/execution policy;
- scope;
- cancellation;
- evidence/verification.

---

## 14. One logical skill catalog

Today there are overlapping concepts:

- legacy `SkillCatalog`;
- `DeferredSkillSession`;
- `PluginSkillCatalog`;
- `UnifiedSkillCatalog`.

End state should have one logical host-facing catalog.

Concept:

```text
SkillCatalog
   |
   +-- BuiltInSkillSource
   +-- PluginSkillSource
   +-- future additional skill source
```

A compatibility adapter is allowed during migration.

After parity tests pass, duplicate legacy paths should be retired.

---

## 15. Extensions

An extension may provide any combination of:

- tools;
- skills;
- provider definitions;
- native helper;
- MCP configuration;
- verifier;
- templates/resources;
- scripts/assets.

A plugin is one install/version/distribution mechanism for extensions.

Not every skill must be a plugin.

Not every plugin must contain a skill.

Not every provider must expose a skill.

Not every plugin must contain executable code.

---

## 16. PluginManager

Keep and reuse `PluginManager`.

Its stronger safety/versioning behavior is valuable:

- manifest validation;
- archive/payload hash checks;
- compatibility;
- permission delta;
- path safety;
- versioned staging;
- self-test;
- atomic activation;
- rollback;
- quarantine;
- ToolRegistry registration;
- previous-version retention.

Do not replace it with direct Git checkout/runtime execution.

A remote repository is distribution/source metadata, not runtime authority.

---

## 17. Catalog — intentionally minimal now

The core needs only a small seam for external package discovery.

Concept:

```text
ICatalogSource
  -> search compact metadata

IPackageRetriever
  -> retrieve selected package bytes

PluginManager
  -> verify / install / activate
```

Initial implementation may support only one configured source.

The source location must be configuration, not hard-coded inside AgentOrchestrator.

Future:

- multiple sources;
- source priority;
- enterprise catalogs;
- semantic/vector indexing;
- dependency resolver;
- stable/beta/dev channels;

are extension points, not mandatory core complexity today.

---

## 18. Remote discovery flow

The normal flow is model-driven:

```text
model searches local tools/skills
       |
       v
sufficient?
  | yes -> continue work
  |
  no
  v
catalog_search(query)
       |
       v
host returns compact candidates
       |
       v
model selects/proposes candidate
       |
       v
host policy / user approval
       |
       v
PackageRetriever
       |
       v
PluginManager
       |
       v
safe registry/skill refresh
       |
       v
model continues original task
```

The user should not have to restate the original goal after installation.

The host still owns all trust/permission decisions.

---

## 19. Do not overbuild semantic search

For small local skill sets:

```text
name + description
-> model selection
```

is enough.

For very large remote catalogs later:

```text
lexical search
+ optional semantic/vector search extension
-> top N candidates
-> model rerank
```

Do not hard-code domain synonyms such as:

```text
dynamic -> parameter/action/visibility
legal -> law/regulation
autocad -> block/cad
```

inside generic Agent core.

Domain meaning belongs to:

- model understanding;
- skill descriptions;
- plugin metadata;
- specialized optional search extension.

---

## 20. Capability indexes

The following are already authoritative for installed state:

```text
ToolRegistry          -> installed callable tools
SkillCatalog          -> installed skills
ProviderManager       -> active providers
PluginManager         -> active plugins
```

Therefore a separate `InstalledCapabilityIndex` must not become a second source of truth.

Allowed choices:

1. remove it; or
2. keep only as a derived/rebuildable cache/projection.

Likewise `AvailableCapabilityIndex` is optional.

For a small catalog, direct compact metadata search is sufficient.

Only introduce a heavier index when measured catalog scale requires it.

---

## 21. CapabilityResolver

The current resolver architecture is too ambitious for the core.

A generic host should not try to decide semantically whether an installed set is sufficient for the user's whole task.

The model is better placed to decide what it still needs.

The end-state resolver, if retained, should be thin:

```text
lookup installed metadata
lookup catalog metadata
report candidate status
apply host policy
```

It should not become an AI-like central planner.

It must contain no domain-specific synonym knowledge.

---

## 22. MissingCapabilityContinuation

The current all-in-one continuation concept should not remain as a mandatory core class.

It currently combines too many responsibilities:

- resolution;
- download;
- install;
- skill selection;
- skill resource parsing/loading;
- index rebuild;
- capability snapshot revision;
- trace;
- continuation.

It also risks assuming every newly installed capability must include a selected skill.

Replace with normal AgentRuntime flow:

```text
model asks catalog_search
 -> host returns candidates
 -> model requests install
 -> host installs safely
 -> runtime refreshes extension surface
 -> model continues
```

No special giant continuation object is necessary.

---

## 23. Capability pinning/evidence

The idea of reproducibility is correct.

But do not pin the entire machine/registry inventory for every task.

A task should record **capabilities it actually uses**:

```text
selected model/provider
selected skill hashes
tool + tool version when called
provider version for called tool
plugin version for used extension
schema version
important policy/version identifiers
```

Installing an unrelated plugin should not invalidate an unrelated running task.

Evidence may append per tool call.

A small task capability context may be kept, but avoid full-registry snapshots as a correctness requirement.

---

## 24. Safe refresh boundary

Keep the important rule:

> Do not mutate a callable implementation while that call is in flight.

Registry/provider/plugin refresh is allowed only at safe boundaries.

Examples:

- before task start;
- after a completed tool call;
- after explicit install when no conflicting tool call is active;
- after task completion;
- after provider reconnect.

No full H2 UI restart should be required for a safe extension refresh when avoidable.

---

## 25. Permission, safety and sandbox

These are core.

Host owns:

- permission scope;
- read vs mutation;
- current resource identity;
- user approval where required;
- secrets boundary;
- filesystem scope;
- process allow-lists;
- sandbox limits;
- cancellation;
- stale-state protection;
- destructive-operation policy.

A skill or plugin cannot grant itself more authority by instruction text.

---

## 26. State / artifacts / evidence

Keep:

- durable task state;
- ArtifactStore;
- evidence references;
- trace/telemetry;
- recovery state.

Large outputs stay outside active context.

The model sees bounded summaries/handles.

Important mutations and verifier results must have durable evidence.

---

## 27. Verification and repair

Keep the verification framework.

The runtime must actually use it.

Required high-level loop:

```text
Observe
 -> Act
 -> Observe again
 -> Verify
 -> FAIL? build bounded repair context
 -> Repair failed criteria only
 -> Verify again
 -> PASS
 -> Final
```

The model cannot declare a mutating task complete merely because a tool returned success.

Repair context should protect already-passed criteria and avoid replaying the full transcript.

---

## 28. First-party extension families

The following existing work is valuable and should remain:

### File / process / shell

- filesystem capabilities;
- process capabilities;
- shell/build/test/script capabilities.

### Office

- OfficeHost;
- structured Word tools;
- structured Excel tools;
- live unsaved-state workflows.

### Web

- WebResearchHost;
- freshness policy;
- WebEvidence;
- legal relationship verification.

### Desktop

- DesktopHost;
- UI Automation;
- screenshot/vision fallback;
- stale-state protection;
- observe-after-act.

### AutoCAD

- structured AutoCAD provider/native bridge contract;
- typed entity/document operations;
- verification;
- no arbitrary command execution as default mutation.

### MCP

- provider-neutral capability-provider layer;
- MCP server lifecycle;
- MCP tool/resource normalization.

These are **reference/first-party extensions**, not definitions of the mainboard.

---

## 29. Structured-adapter preference

Keep this generic principle:

```text
structured typed app/provider interface
  > app API / MCP / native plugin
  > direct file adapter
  > UI Automation/accessibility
  > screenshot / mouse / keyboard
```

Do not hard-code Word/Excel-specific ranking rules into generic core when the same result can be expressed through provider/tool metadata.

Future providers should be able to declare quality/capability characteristics instead of requiring core edits.

---

## 30. General computer-control tools

The Agent may have broad computer-control capability.

But core does not need one hard-coded static master list of every future desktop action.

Prefer providers registering families into ToolRegistry:

```text
FileSystemProvider -> filesystem.*
ProcessProvider    -> process.* / shell.*
DesktopProvider    -> app.* / window.* / uia.* / input.* / screen.*
BrowserProvider    -> browser.*
```

No monolithic unrestricted `control_computer(command)` tool.

---

## 31. KEEP / MODIFY / RETIRE matrix

### 31.1 KEEP — core foundations

Keep and wire into the real runtime:

```text
Transport/
  IAgentTransport.cs
  AgentTransportCapabilities.cs
  OllamaTransport.cs
  ChatCompletionsTransport.cs
  OpenAiResponsesTransport.cs
  OpenAiResponsesWebSocketTransport.cs

Context/
  AgentContextManager.cs

Session/
  CompactionManager.cs
  ArtifactStore.cs

Prompting/
  AgentPromptLayout.cs
  AgentPromptCacheIdentity.cs

Tools/
  ToolRegistry.cs
  ToolSearchIndex.cs
  DeferredToolDiscovery.cs
  ToolExecutionScheduler.cs

Tasking/
  AgentTaskContract.cs
  AgentTaskStateMachine.cs
  AgentAcceptanceEvidence.cs

Verification/
  VerificationReport.cs
  VerificationCompletionGate.cs
  AgentRepairController.cs
  verifier implementations

Plugins/
  H2PluginManifest.cs
  PluginManager.cs

Providers/
  CapabilityProvider.cs
  McpServerConnection.cs
  McpToolProvider.cs
  CapabilityProviderManager.cs

ScriptWorkspace.cs
WindowsPythonSandbox.cs
```

### 31.2 KEEP AS EXTENSIONS

Keep but treat as extension/reference capability:

```text
Office/
Desktop/
Web/
Cad/
Computer/FilesystemCapabilities.cs
Computer/ProcessShellCapabilities.cs
Documents/
```

### 31.3 MODIFY

```text
Tasking/AgentOrchestrator.cs
  -> own real AgentRuntime lifecycle, not compatibility runner as main path

Tasking/AgentOrchestratedRun.cs
  -> call AgentRuntime

LabWindow.cs
  -> use real V2 runtime and typed progress

Skills/UnifiedSkillCatalog.cs
  -> now contains the single canonical SkillCatalog; the historical UnifiedSkillCatalog alias is retired

Plugins/PluginSkillCatalog.cs
  -> internal helper owned only by PluginSkillSource, not a competing host-facing catalog

Tools/DocumentToolPreference.cs
Tools/InteractionAdapterPreference.cs
  -> remove app/domain hard-coding from generic core

Tasking/AgentCapabilityRefreshCoordinator.cs
  -> retain only safe extension refresh responsibility

Capabilities/TaskCapabilitySnapshot.cs
  -> shrink to used-capability evidence/pins, not full registry inventory

Catalog/CatalogSources.cs
  -> keep minimal source seam; defer unnecessary marketplace arbitration

Catalog/PackageRetriever.cs
  -> keep retrieval seam; production source implementations later

Computer/GeneralComputerCapabilityCatalog.cs
  -> move toward provider registration rather than core master inventory
```

### 31.4 SIMPLIFY / OPTIONAL CACHE

```text
Capabilities/InstalledCapabilityIndex
Capabilities/AvailableCapabilityIndex
Capabilities/CapabilityResolver
```

Rules:

- no domain hard-coded synonyms;
- not authoritative stores;
- remove if ToolRegistry/SkillCatalog/catalog metadata already solve the problem;
- if retained, keep as small derived search/cache projections only.

### 31.5 RETIRE AFTER PARITY

Do not delete before replacement tests pass.

```text
AgentRunner.cs
  -> retire from main runtime; retain only frozen A/B baseline if useful

AgentTools.cs
  -> split/migrate responsibilities into registry providers; retire giant switch

Tools/V1ToolRegistryAdapter.cs
  -> retired in MB-92 after canonical descriptor consumers moved to NormalRuntimeToolRegistry

SkillCatalog.cs
  -> retired in MB-93 after built-in/UI/v1 parity moved to H2AgentLab.Skills.SkillCatalog

Tools/DeferredSkillSession.cs
  -> retired in MB-93; progressive reads/hash identity are owned by canonical SkillCatalog/runtime skill tools

ComputerTools.cs
  -> retired in MB-94 after selected-window parity moved to DesktopHost / SelectedDesktopWindowController

LabSession.Context()
  -> no longer primary model-context path
```

### 31.6 RETIRE / REPLACE DESIGN

```text
Capabilities/MissingCapabilityContinuation.cs
  -> replace with ordinary model-driven catalog_search/install/refresh/continue loop
```

Also remove mandatory H2-specific `resource:` parsing as a skill-format requirement.

---

## 32. Duplicate responsibilities to clean up

### Skill duplication

Current overlap:

```text
SkillCatalog
DeferredSkillSession
PluginSkillCatalog
UnifiedSkillCatalog
```

Target: one logical skill system with source adapters.

### Runtime duplication

Current overlap:

```text
AgentRunner v1
AgentOrchestrator
AgentOrchestratedRun
IAgentTransport implementations
```

Target: one real V2 AgentRuntime plus optional frozen v1 comparator.

### Tool duplication

Current overlap:

```text
AgentTools.Definitions / giant Execute switch
ToolRegistry / descriptors / executors
```

Target: ToolRegistry path only for normal runtime.

### Context duplication

Current overlap:

```text
LabSession.Context()
AgentContextManager
CompactionManager
```

Target: AgentContextManager/Compaction for model context; LabSession remains durable journal only.

### Trace/progress duplication

Review:

```text
Metrics.AgentTrace
Tasking.AgentTraceEventStream
journal events
```

Do not force one type if they serve different concerns, but avoid three independent sources of truth for the same runtime event.

---

## 33. What must NOT be added to core now

Do not add these as mandatory core requirements before real measured need:

- vector database;
- embedding service;
- large semantic marketplace engine;
- complex multi-catalog conflict arbitration;
- generic dependency solver;
- marketplace UI;
- stable/beta/dev release channel system;
- automatic Internet plugin installation;
- rich intent/alias schema required for every skill;
- domain synonym dictionaries;
- special continuation class for every install case;
- built-in knowledge of future apps.

Create extension seams, not future complexity.

---

## 34. Desired source layout

Long-term logical layout:

```text
experiments/H2AgentLab/
│
├─ Runtime/
│   └─ AgentRuntime.cs
│
├─ Tasking/
│   ├─ AgentOrchestrator.cs
│   ├─ AgentTaskContract.cs
│   └─ AgentTaskStateMachine.cs
│
├─ Transport/
│
├─ Context/
├─ Prompting/
├─ Session/
│
├─ Tools/
│   ├─ ToolRegistry.cs
│   ├─ ToolSearchIndex.cs
│   ├─ DeferredToolDiscovery.cs
│   └─ ToolExecutionScheduler.cs
│
├─ Skills/
│   ├─ SkillCatalog.cs
│   └─ sources/
│
├─ Extensions/
│   ├─ Plugins/
│   ├─ Providers/
│   └─ Catalog/
│
├─ Security/
├─ Verification/
├─ Metrics/
│
└─ FirstPartyExtensions/
    ├─ FileSystem/
    ├─ ProcessShell/
    ├─ Office/
    ├─ Web/
    ├─ Desktop/
    ├─ AutoCAD/
    └─ MCP/
```

Physical moves are optional until useful.

Do not churn namespaces/files solely to match this drawing.

Behavioral boundaries matter more than folder aesthetics.

---

## 35. Core acceptance gate

A Minimum Bootable Agent passes only if the real runtime proves all of these:

1. normal UI request does not execute through v1 `AgentRunner`;
2. model transport is selected through `IAgentTransport`;
3. active context is actually built through bounded V2 context logic;
4. first model request does not include full tool registry;
5. tool_search can load new schemas during the same task;
6. tool calls execute through ToolRegistry executors/scheduler;
7. plugin/provider tools can register without Agent core edits;
8. skills are discovered by name + description and load progressively;
9. mutation permission/scope is host-enforced;
10. important tool outputs are evidence/artifacts;
11. verification can fail a model-claimed success;
12. repair can continue from bounded failed-criterion context;
13. final completion gate is host-owned;
14. cancellation reaches model/tool/helper processes;
15. context remains bounded across long-running multi-tool tasks;
16. provider/model can be changed without redesigning the loop.

---

## 36. Reference extension acceptance gate

Separately prove first-party extension quality:

- FileSystem/process/shell safety;
- Office live unsaved Word/Excel;
- WebResearchHost freshness/evidence;
- legal source verification;
- Desktop observe-after-act;
- structured adapter preference;
- AutoCAD structured provider boundary;
- MCP lifecycle/scope;
- plugin install/update/rollback/quarantine.

These tests prove the extension bus is useful.

They do not redefine the core architecture.

---

## 37. Relationship to H2 Notes

Do not integrate Agent Lab into H2 Notes until:

1. core acceptance gate passes;
2. required reference extension acceptance passes;
3. user explicitly accepts integration.

At Phase 13, read:

- `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md`
- `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

The Agent engine should then be adapted into H2 Notes.

H2 Notes must not reimplement the Agent core.

---

## 38. Final architectural rule

The entire architecture should remain understandable in one sentence:

> **H2 Agent is a small, provider-neutral, tool-using, verifiable runtime with an extension bus; specialized abilities are installed or registered around it rather than hard-coded into it.**

And the implementation priority is:

```text
Make the machine boot
      ↓
prove the extension bus
      ↓
then add more cards/plugins/skills
```

Do not build the marketplace before the Agent itself is running through the final V2 runtime.
