> **HISTORICAL / SUPERSEDED — DO NOT USE AS THE EXECUTION AUTHORITY.**  
> Since 2026-09-18, the canonical architecture is `docs/H2_AGENT_MASTER_SPEC.md` and the canonical task tracker is `docs/H2_AGENT_MASTER_TASKS.md`.  
> This document is retained only as implementation history/evidence. H2 Notes redesign and non-AI bug-ledger documents remain separate.

# H2 Agent Lab 2.0 — Codex-style provider-neutral agent harness

Status: **HISTORICAL / SUPERSEDED by H2_AGENT_MASTER_SPEC.md**  
Scope: `experiments/H2AgentLab` first. **Do not integrate into H2 Notes UI until the acceptance gate in this document passes.**  
Branch during implementation: `feature/nas-multi-device-sync` unless the user explicitly changes it.  
Main principle: **reuse proven H2 Notes components when they are genuinely generic; do not duplicate stable code; do not make H2 Notes depend on unfinished Agent Lab code.**

Supplementary normative requirements: **`docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md` is part of this specification.** Where that document adds capability or acceptance requirements not yet represented below, the task tracker must add explicit work before Phase 13; earlier completed tasks remain valid foundations but do not waive the new gates.

---

## 1. Goal

Build a production-oriented agent harness that can use the same high-level workflow with:

- OpenAI Responses API;
- Ollama native tool calling;
- OpenAI-compatible Chat Completions as fallback;
- local files and document adapters;
- live Excel/Word sessions;
- generic Windows applications through structured accessibility first and visual computer use when needed;
- deterministic verification before an action is considered complete;
- bounded context, compaction and deferred tool loading;
- detailed latency/token/tool telemetry so every optimization is measurable.

The target is not to copy private Codex internals. The target is to reproduce the **publicly observable and publicly documented harness principles** that explain why a capable model performs better inside Codex than in a naive chat/tool loop.

---

## 2. Non-goals

The first acceptance release does **not** promise:

- identical behavior to Codex;
- unrestricted computer control;
- silent execution of destructive operations;
- arbitrary browser automation without a dedicated, reviewed adapter;
- hidden use of H2 Notes user data, secrets, API keys or project memory;
- automatic integration into H2 Notes before Agent Lab acceptance passes;
- fine-tuning a model to create memory;
- exposing chain-of-thought.

---

## 3. Core architecture

```text
                           USER / LAB UI
                                |
                                v
                       AgentOrchestrator
                                |
              +-----------------+------------------+
              |                 |                  |
              v                 v                  v
        TaskContract      ContextManager      ModelSession
              |                 |                  |
              |                 |       +----------+----------+
              |                 |       |          |          |
              |                 |     OpenAI     Ollama      Chat
              |                 |    Responses    Native    Fallback
              |                 |       |          |          |
              +-----------------+-------+----------+----------+
                                |
                         ToolRegistry/Search
                                |
         +-------------+--------+---------+------------+-------------+
         |             |                  |            |             |
       Files          Office            Desktop       OCR          Python
         |          /       \        /           \      |             |
         |      Excel       Word   UIA/AX      Vision    |             |
         +-------------+--------+---------+------------+-------------+
                                |
                         Execution Layer
                                |
              +-----------------+-----------------+
              |                 |                 |
       Windows AppContainer   OfficeHost       DesktopHost
              |                 |                 |
              +-----------------+-----------------+
                                |
                      Deterministic Verifiers
                                |
                         PASS / FAIL evidence
                           |          |
                           v          v
                         Final      Repair loop
```

### 3.1 Rule of responsibility

The model chooses **what to do next**. The host owns:

- permissions and scope;
- task contract and acceptance criteria;
- transport/session lifecycle;
- tool discovery and loading;
- execution isolation;
- state observation;
- deterministic verification where possible;
- context budgeting and compaction;
- final completion gate.

A model may propose completion, but the orchestrator may only mark a mutating task `Completed` after the required verifier(s) pass or the task is explicitly classified as not mechanically verifiable.

---

## 4. What to reuse from H2 Notes

H2 Agent Lab already references `H2Notes.Core`, so stable generic capabilities should be reused instead of copied.

| H2 Notes component | Reuse? | How in Agent Lab 2.0 |
| --- | --- | --- |
| `AiProfile`, `AiProtocol` | **Yes** | Provider/model settings shared with Lab transport abstractions. |
| `AiClient.Endpoint`, endpoint safety helpers | **Yes** | Reuse URL/HTTPS/LAN validation. Do **not** reuse the one-shot chat loop as the new agent transport. |
| `AiModelCapabilities` | **Yes** | Reasoning options and provider/model capability checks. Extend only through H2 Core when the capability is genuinely generic. |
| `AiDocuments` | **Yes** | Safe file sniffing, size limits, DOCX/XLSX text extraction, attachment hashing. |
| `AiPdf`, `AiPdfProcessor` | **Yes** | PDF/image preprocessing and OCR pipeline where applicable. |
| `PortableOcrRuntime`, `PortableOcrPackager` | **Yes** | Portable local OCR runtime and diagnostics. |
| `AiPageLayout` | **Yes** | Layout extraction path for document verification/inspection when useful. |
| `ProjectWorkspaceStore.AtomicWrite` patterns | **Pattern reuse only** | Use atomic-write concepts for Agent Lab state/journal; avoid coupling Lab state to H2 project workspace schema. |
| `AiMemoryStore` | **Design reuse, not direct dependency for Lab task memory** | H2 memory is tied to `SheetState`/projects. Agent Lab gets its own generic task/thread memory, but follows the same source-of-truth + rebuildable-local-index philosophy. |
| `AiProjectContext` | **Design reuse only** | Reuse bounded-history/retrieval concepts, not H2 project-specific JSON. |
| H2 Markdown renderer/UI | **No direct dependency in v2 core** | Lab UI may reuse presentation ideas later; orchestration must remain UI-independent. |

### 4.1 No duplication rule

If Agent Lab needs a capability already implemented safely in `H2Notes.Core`, first attempt to extract/refactor that capability into a generic H2 Core service rather than copying it into `experiments/H2AgentLab`.

No destructive refactor of stable H2 Notes behavior is allowed merely to help the Lab. Every shared-core change must keep H2 Notes tests passing.

---

## 5. Provider/transport design

Create a transport layer independent of orchestration.

```text
Transport/
  IAgentTransport.cs
  AgentTransportCapabilities.cs
  OpenAiResponsesTransport.cs
  OllamaTransport.cs
  ChatCompletionsTransport.cs
```

### 5.1 `IAgentTransport`

Responsibilities:

- create/start a model turn;
- continue the same turn after tool results;
- surface streamed text/reasoning-summary/tool calls as typed events;
- expose provider usage/latency/cache metrics;
- cancel safely;
- expose capabilities, never make the orchestrator infer them from model name alone.

The orchestrator must not construct provider JSON directly.

### 5.2 OpenAI Responses path

Use public API behavior only:

- Responses API instead of Chat Completions for the primary OpenAI path;
- support Responses WebSocket mode when the selected official endpoint/model supports it;
- keep one turn-scoped session so WebSocket/continuation state survives multiple model calls in a single user turn;
- use incremental continuation/previous response semantics rather than resending the entire growing transcript when supported;
- optionally prewarm a turn with a non-generating request when supported;
- support prompt caching controls when publicly available;
- fall back cleanly to HTTP Responses without changing task semantics;
- never copy private/internal Codex-only request headers or undocumented server contracts.

### 5.3 Ollama path

- native `/api/chat` tool calling;
- replay provider-supplied active-turn thinking/tool-call state exactly as required by Ollama protocol, but do not persist hidden reasoning as normal chat history;
- preserve current explicit `OllamaThinking` behavior;
- use host-side context compaction/tool search because Ollama does not provide Codex's server-side session machinery.

### 5.4 Chat Completions fallback

- retained only for providers that do not support Responses;
- no assumption that every OpenAI-compatible endpoint supports the same tool schema/reasoning fields;
- capability flags are explicit.

---

## 6. Prompt and cache architecture

The current Lab mixes static rules with changing environment/session data. V2 must separate them.

### 6.1 Stable prefix

```text
BASE AGENT POLICY             stable
SECURITY / PERMISSION POLICY  stable
MODEL-SPECIFIC POLICY         stable for model/profile
TOOL NAMESPACE METADATA       stable for toolset version
---------------------------   cache boundary
TASK CONTRACT                 dynamic
CURRENT WORKING STATE         dynamic
TIME / LIVE ENVIRONMENT       dynamic
USER INPUT                    dynamic
```

Do not put current time, recent activity, run logs or mutable workspace state before the stable cacheable prefix.

### 6.2 Prompt cache identity

Create an internal cache identity from at least:

- agent policy version;
- provider/model;
- tool-registry version;
- safety policy version;
- loaded skill hashes where they are part of the stable prefix.

Provider-specific prompt cache fields are used only when supported.

---

## 7. Tool registry and deferred tool search

The current Lab sends the full `AgentTools.Definitions` on every step. V2 replaces this with a registry.

```text
Tools/
  ToolRegistry.cs
  ToolDescriptor.cs
  ToolNamespace.cs
  ToolSearchIndex.cs
```

### 7.1 Initial tools

The model initially sees only a small stable set:

- `tool_search`;
- core planning/status tool if needed;
- names/descriptions of namespaces such as `files`, `office`, `desktop`, `python`, `skills`, `ocr`.

### 7.2 Deferred loading

`tool_search` returns only the relevant callable schemas for the next model request. The host search index uses deterministic lexical/BM25-style ranking; embeddings are not required for tool discovery.

Loaded tool descriptors are cached by registry version.

### 7.3 Parallel calls

Each tool declares whether it supports parallel execution. Read-only independent calls may run concurrently. Mutations to the same resource are serialized.

---

## 8. Task contract and state machine

Every task that reads/mutates external state gets a host-owned contract.

```text
AgentTaskContract
- TaskId
- UserGoal
- Scope
- Inputs
- RequiredChanges[]
- PreserveConstraints[]
- OutputRequirements[]
- AcceptanceCriteria[]
- RiskClass
- VerificationPolicy
```

The host may create an initial contract deterministically and allow the model to clarify/expand it, but the model cannot silently remove user requirements.

### 8.1 State machine

```text
Received
  -> Grounded
  -> Planned
  -> Executing
  -> Verifying
     -> Completed
     -> Repairing -> Executing
  -> Blocked / Cancelled / Failed
```

A mutating task cannot transition from `Executing` directly to `Completed`.

### 8.2 Evidence

Each acceptance criterion stores evidence references:

- source snapshot/hash;
- tool result ID;
- verifier report;
- screenshot/state ID;
- artifact hash;
- test/build result.

---

## 9. Context manager and compaction

Raw history remains on disk; active model context stays bounded.

```text
ContextManager
- task contract
- current plan/state
- recent relevant user/model turns
- recent relevant tool results
- verifier failures
- selected artifacts/memory
- compacted historical summary
```

### 9.1 Rules

- never append unbounded raw tool output to every future request;
- stdout/stderr and document extracts are stored as artifacts and represented by short summaries/handles;
- the model can explicitly request more using a read tool;
- keep recent turns verbatim inside a token budget;
- compact older history into a summary plus durable source references;
- retain source data so compaction never becomes the only evidence;
- context size is a budget, not a target to fill.

### 9.2 Lab memory

Agent Lab gets generic memory distinct from H2 Notes project memory:

- task/thread facts;
- artifact/run references;
- decisions and verified outcomes;
- summaries/checkpoints.

Canonical task/thread state remains in Lab state storage; any local search index is rebuildable.

---

## 10. Execution layers

### 10.1 Python sandbox

Keep and evolve existing:

- `ScriptWorkspace`;
- `WindowsPythonSandbox`;
- staged copies;
- no-network AppContainer;
- process/time/memory/output limits;
- publish only after verification/approval.

Change: `run_python` becomes an **escape hatch**, not the default Office workflow.

### 10.2 OfficeHost

New helper process:

```text
H2AgentLab.OfficeHost
```

Runs separately from Avalonia UI and model transport. Initial Windows implementation may use Office COM/ROT automation when Office is installed.

#### Excel tools

At minimum:

- list/open live workbooks;
- identify active workbook/sheet/selection;
- read range values/formulas/styles/merges/hidden state;
- apply structured range patch;
- recalculate when Excel supports it;
- save copy/export result;
- create before/after snapshots;
- verify target/preserve constraints.

The live adapter must be capable of reading unsaved state from the already-open workbook.

#### Word tools

At minimum:

- identify active document and selection;
- inspect paragraphs/runs/styles/tables/sections/header/footer metadata;
- replace/insert text through structured operations;
- apply formatting patches;
- save a copy;
- verify OpenXML/content preservation where possible;
- optionally render/observe the document when visual fidelity is an acceptance criterion.

The live adapter must be capable of reading unsaved state from the already-open document.

### 10.3 DesktopHost

New helper process:

```text
H2AgentLab.DesktopHost
```

Observation combines:

- window/process identity;
- screenshot;
- DPI/window bounds;
- compact UI Automation tree when available;
- stable short-lived element/state tokens.

Actions include:

- click/double-click;
- type/set text;
- key press/chord;
- scroll;
- drag;
- wait.

Every state-changing action batch must be followed by a new observation before success can be claimed.

### 10.4 Adapter preference order

```text
structured app adapter
    > accessibility/UI Automation
    > screenshot/vision computer use
```

Use the highest-level reliable interface available. Do not click pixels in Excel if the structured Excel adapter can perform and verify the same change safely.

---

## 11. Verification layer

Model self-review is supplemental evidence, never the sole completion gate for mechanically verifiable work.

```text
Verification/
  VerificationReport.cs
  ExcelVerifier.cs
  WordVerifier.cs
  FileVerifier.cs
  CodeVerifier.cs
  DesktopVerifier.cs
```

### 11.1 Excel verifier examples

Compare source/target according to task contract:

- values;
- formulas;
- font fields;
- bold/italic/underline;
- fills;
- borders;
- number formats;
- alignment;
- merged ranges;
- worksheets;
- hidden state;
- dimensions where relevant;
- exact changed scope.

### 11.2 Word verifier examples

- OpenXML validity;
- required text edits;
- paragraph/run styles;
- tables;
- sections/margins;
- headers/footers;
- unchanged original regions;
- rendering/image comparison when visual fidelity matters.

### 11.3 Desktop verifier

Uses post-action observation and task-specific expected state. A click completing without OS error is not verification.

### 11.4 Repair loop

Verifier failures are converted to concise, structured repair context. Example:

```text
Criterion C5 failed: Tasks!A4 italic changed true -> false.
Repair only this failure without regressing C1-C4/C6.
```

The model does not need the entire previous transcript to repair one failed criterion.

---

## 12. Recovery

Keep the useful `RecoverySupervisor` concepts:

- invalid JSON/tool args;
- missing file/resource;
- repeated unchanged failed call;
- stale hash/state;
- denied permissions;
- Python/runtime errors;
- uncertain side effects.

Split recovery into:

1. runtime/protocol recovery;
2. semantic verification repair.

Never blindly retry an uncertain write/click. Observe/re-read first.

---

## 13. Safety and permission model

Keep current principles:

- selected workspace boundary;
- no secret-directory traversal;
- staged-copy execution for arbitrary Python;
- approvals for external side effects according to configured mode;
- source hash/state checks before overwrite;
- backup where replacement is allowed;
- no hidden network from sandbox;
- explicit DesktopHost/OfficeHost scope;
- user denial is final for the current scope/turn unless the user changes it.

Live Office/Desktop adapters are outside the Python AppContainer and therefore need their own narrow capability/approval boundary.

---

## 14. Observability and performance

Create a durable trace per user turn.

Mandatory timestamps:

```text
T0 send
T1 task/context ready
T2 model request started
T3 response headers / connection ready
T4 first model event
T5 tool start
T6 tool finish
T7 continuation request
T8 verifier start
T9 verifier finish
T10 final
```

Mandatory counters where provider supplies them:

- input tokens;
- cached input tokens;
- cache write tokens if applicable;
- output tokens;
- reasoning tokens/summary only when provider exposes public counts;
- model calls;
- tool calls;
- bytes uploaded/downloaded;
- first-token latency;
- total latency;
- repair count;
- API cost estimate if pricing data is configured explicitly.

No performance claim is accepted without trace evidence.

---

## 15. Fast paths

Not every user prompt should invoke the full agent state machine.

Router classes:

- `Direct`: pure response/rewrite, no external state;
- `Retrieval`: read/search only;
- `Action`: one or few bounded mutations with verification;
- `ComplexAgent`: multi-step plan/tool/repair flow.

Direct requests should be close to raw provider latency.

---

## 16. Test and acceptance strategy

### 16.1 Regression layers

1. deterministic unit tests for state machines/tool routing/context budgets;
2. mocked-provider protocol tests;
3. real sandbox/file tests;
4. real Office host tests on fixtures/copies;
5. real DesktopHost tests in a dedicated test window/application;
6. live model acceptance tests, kept separate from deterministic program tests.

### 16.2 Baseline comparison

For each provider/model used in acceptance:

- A: raw model/API prompt without Lab tools;
- B: current Agent Lab v1 loop;
- C: Agent Lab v2.

Record latency/tokens/tool calls and independent correctness.

### 16.3 Acceptance suite

At least 30 representative tasks, each repeated at least three times per accepted configuration:

- direct chat/rewrite;
- file discovery/read/edit;
- Excel closed file;
- Excel currently open with unsaved changes;
- Word closed file;
- Word currently open with unsaved changes;
- PDF/image/OCR;
- generic Windows UI;
- recovery from wrong path/tool/runtime errors;
- verifier-triggered repair;
- long thread/context compaction;
- cancellation/network interruption;
- Ollama local;
- OpenAI Responses.

### 16.4 Gate before H2 Notes integration

Required:

- zero unauthorized scope/permission violations in acceptance suite;
- >= 90% correct completion for declared-supported tasks without manual repair;
- deterministic verifier for every mechanically verifiable accepted task class;
- direct/no-tool path near raw provider overhead;
- context size does not grow linearly without bound with thread age;
- open Excel/Word tests prove reading unsaved live state;
- DesktopHost mutation tests prove observe-after-act behavior;
- cancellation and uncertain-write behavior preserve evidence and do not silently retry;
- user explicitly approves integration after seeing results.

---

## 17. Source/layout plan

Target structure:

```text
experiments/H2AgentLab/
  Agent/
    AgentOrchestrator.cs
    AgentTaskContract.cs
    AgentTaskState.cs
    AgentContextManager.cs
    AgentRepairController.cs
    AgentFinalizer.cs
  Transport/
    IAgentTransport.cs
    AgentTransportCapabilities.cs
    OpenAiResponsesTransport.cs
    OllamaTransport.cs
    ChatCompletionsTransport.cs
  Tools/
    ToolRegistry.cs
    ToolDescriptor.cs
    ToolSearchIndex.cs
    Files/
    Office/
    Desktop/
    Python/
    Skills/
  Verification/
    VerificationReport.cs
    ExcelVerifier.cs
    WordVerifier.cs
    FileVerifier.cs
    DesktopVerifier.cs
  Metrics/
    AgentTrace.cs
    AgentMetrics.cs
  Session/
    AgentThreadState.cs
    CompactionManager.cs
    ArtifactStore.cs
    JournalStore.cs
```

Existing `AgentRunner`, `AgentTools`, `ScriptWorkspace`, `WindowsPythonSandbox`, `SkillCatalog`, `RecoveryPolicy` stay in place until their responsibilities are migrated and covered by tests. No big-bang deletion/refactor.

---

## 18. Implementation discipline

The authoritative task checklist is `AGENTLAB_V2_TASKS.md`.

Rules:

1. Work in task ID order unless a task explicitly lists another dependency.
2. One task is marked `[x]` only after its acceptance/evidence is complete.
3. When a task finishes, update the tracker in the **same development sequence** before moving to the next task.
4. Do not delete a superseded component until the task tracker says the replacement is accepted.
5. If a task discovers new work, add a new uniquely numbered task under the correct phase; do not silently broaden an existing task.
6. Every architectural change must keep deterministic Lab tests and H2 Notes tests passing before it can be marked complete.
7. No integration into `src/H2Notes.Avalonia` until the final Agent Lab gate passes and the user explicitly authorizes integration.

---

## 19. Normative MCP / Web / Office / extensibility addendum

The requirements in `docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md` are mandatory for the accepted Agent Lab engine.

This addendum reconciles that document with the architecture above. It does **not** invalidate completed OfficeHost/DesktopHost work. Those components remain accepted lower-level providers, but final Agent Lab acceptance also requires the provider, web, legal-research and extension layers below.

The final execution model remains:

```text
user goal
  -> host-owned task contract
  -> bounded context
  -> capability/tool discovery
  -> observe current state
  -> act through the highest-level safe adapter
  -> observe again
  -> deterministic verification
  -> focused repair
  -> concise final answer
```

Tool-call count is not fixed. A complex task may use many sequential or parallel structured calls while active context and loaded schemas remain bounded.

### 19.1 Provider neutrality

MCP is a provider protocol, never the agent itself.

`AgentOrchestrator` remains responsible for:

- whether a tool is required;
- provider/tool selection;
- call order;
- evidence sufficiency;
- verification and repair;
- permission/scope enforcement;
- final completion.

The model must not need to know whether a capability is implemented through MCP, COM, C#, OpenXML, Python, REST, named pipe, plugin IPC or another transport.

---

## 20. Generic capability-provider and MCP layer

Agent Lab requires an explicit provider layer above concrete transports.

Conceptual contracts include:

```text
ICapabilityProvider
McpServerConnection
McpToolProvider
McpResourceAdapter
McpPermissionPolicy
McpHealthState
ProviderProvenance
```

Exact type names may differ.

### 20.1 Required MCP lifecycle

The MCP/provider layer must support:

- connect, disconnect and bounded reconnect;
- cancellation and timeout;
- health state and last bounded error;
- compact namespace/capability summaries before schemas;
- lazy detailed schema loading only after selection;
- dynamic registration into `ToolRegistry`;
- read-only vs mutating classification;
- provider/resource/session scope declarations;
- parallel-safety and serialization boundaries;
- provenance: provider/server/tool/schema/version;
- secret-safe metadata;
- deterministic removal/refresh of provider tools.

One provider must not silently broaden another provider's scope.

Provider claims are **not** automatically trusted verifier evidence. Verification remains host-owned.

### 20.2 Deferred discovery remains mandatory

Initial model exposure remains intentionally small:

```text
tool_search
update_plan
compact namespace metadata
```

Large Word/Excel/AutoCAD/Web/plugin schemas are loaded only when selected. Registry/provider/plugin refreshes invalidate the relevant search/schema cache deterministically.

---

## 21. General computer-control capability families

DesktopHost is the UI/vision foundation, but the accepted engine also needs Codex-like typed capability families for broader computer work.

Required families, whether implemented by built-ins, native hosts or approved plugins/providers:

```text
filesystem.*
process.*
shell.*
app.*
window.*
uia.*
input.*
screen.*
browser.*
```

Representative capabilities include:

- filesystem list/stat/search/read/write/copy/move/hash/watch and policy-controlled delete;
- process list/start/wait/exit-status and policy-controlled terminate;
- bounded shell/build/test/script execution;
- app list/active/launch/activate/wait-for-window;
- window enumerate/bounds/title/process/activate/observe;
- UIA inspect/find/invoke/set/select/expand;
- input click/double-click/type/key/chord/scroll/drag;
- screen capture/region/observe-after-action;
- browser navigate/inspect/click/type/download/wait where structured web access is insufficient.

### 21.1 Safety

These capabilities must be scope- and permission-aware:

- no unrestricted machine access by default;
- read/observe separated from mutation;
- process/window/state identity binding;
- stale-state rejection;
- cancellation and timeout;
- explicit shell/process side-effect policy;
- protected password/security/system surfaces blocked;
- observe-after-mutation before completion evidence;
- durable evidence for important side effects.

Preference remains:

```text
structured app adapter
  > native API / MCP / COM / plugin
  > direct file adapter
  > UI Automation
  > screenshot / mouse / keyboard
```

---

## 22. WebResearchHost, freshness and web evidence

A first-class `WebResearchHost` is required and is distinct from DesktopHost.

Required structured families:

```text
web.search
web.fetch
web.download
web.extract
web.get_metadata
web.open_browser   # fallback only when structured access is insufficient
```

### 22.1 Freshness policy

The task/router layer must infer an explicit equivalent of `FreshnessRequired = true` for intents such as:

- today/current/latest/newest;
- currently valid/still effective;
- has it been replaced/amended/repealed;
- new law/regulation/standard;
- current weather/news/price;
- current vendor/product documentation.

A freshness-required task cannot complete from model memory alone. It requires current authoritative data from WebResearchHost or another approved current provider.

### 22.2 Durable web evidence

Web results are structured evidence, not trusted prompt text only.

Required fields include the equivalent of:

```text
WebEvidence
  EvidenceId
  Url
  Title
  Publisher
  PublishedAt
  EffectiveAt
  FetchedAt
  SourceType
  ContentHash
  RelevantExcerpt
```

Large HTML/PDF/download bodies are stored in `ArtifactStore` or an equivalent durable store. Active context receives bounded summaries and evidence handles unless more content is explicitly requested.

---

## 23. Legal/regulatory verification and structured Office namespaces

### 23.1 Legal status model

A newer document is not automatically a replacement.

The engine must structurally distinguish:

```text
REPLACED
AMENDED
SUPPLEMENTED
PARTIALLY_REPEALED
REPEALED
STILL_EFFECTIVE
NOT_YET_EFFECTIVE
UNKNOWN
```

A `LegalDocumentStatus`-equivalent result must preserve:

- document ID/jurisdiction/issuer;
- issue/effective dates;
- status;
- typed relationships to related documents;
- effective date of each relationship;
- evidence IDs.

Authoritative/official sources are preferred for legal-effect conclusions. Search snippets or secondary articles alone are insufficient when official material is available.

### 23.2 Word structured capability family

The live Word provider must ultimately expose capabilities equivalent to:

```text
word.list_documents
word.get_active_document
word.get_selection
word.read_outline
word.read_range
word.find_text
word.read_paragraphs
word.read_runs
word.read_styles
word.read_tables
word.read_sections
word.read_headers_footers
word.replace_range
word.insert_text
word.apply_format
word.save_copy
word.export
word.verify_range
word.get_spelling_errors
word.get_grammar_candidates
word.extract_legal_citations
```

Native Word spelling evidence should be preferred for ordinary spelling detection when available.

### 23.3 Excel structured capability family

The live Excel provider must expose equivalents of:

```text
excel.list_workbooks
excel.get_active_workbook
excel.get_active_sheet
excel.get_selection
excel.read_range
excel.read_formulas
excel.read_styles
excel.read_merges
excel.read_hidden_state
excel.write_range
excel.set_formula
excel.apply_format
excel.recalculate
excel.save_copy
excel.verify_range
```

Large range work must use structured calls rather than UI clicking.

### 23.4 AutoCAD provider boundary

Production AutoCAD mutation should prefer:

```text
Agent
 -> ToolRegistry/MCP/provider
 -> local bridge/IPC
 -> C# AutoCAD plugin
 -> DocumentLock
 -> Transaction
 -> AutoCAD Database
```

The provider surface should support typed discovery/read/mutate/plot/verify operations rather than arbitrary command execution as the default mutation path.

---

## 24. Dynamic Plugin / Skill Catalog and update system

The accepted engine must support future capabilities without rebuilding the full H2 application.

Keep these concepts separate:

```text
Tool   = callable capability
Skill  = reusable workflow/instructions for combining capabilities
Plugin = installable package containing tools/providers/skills/verifiers/resources/helpers
```

### 24.1 Manifest and catalog

An installable package has bounded machine-readable metadata equivalent to:

- ID/name/version/min agent version;
- publisher/source;
- package hash/signature/trust state;
- capability and skill keywords;
- providers;
- requested permissions;
- native helper declarations;
- compatibility metadata.

Catalogs expose compact metadata first. Supported source abstractions may include official, organization/private, Git, local-folder or enterprise catalogs.

Discovery does not imply trust or installation.

### 24.2 Installation/update policy

Host policy, not model text, decides whether installation/update may proceed.

Required modes include equivalents of:

- Disabled;
- trusted official packages;
- ask for new publisher/package;
- organization-approved catalog;
- developer/local-package mode.

Before activation validate at least:

- package ID/version and manifest schema;
- cryptographic hash/signature/source identity where available;
- agent compatibility;
- capabilities and permission delta;
- path traversal/forbidden paths;
- conflicting tool names;
- native helpers/hooks;
- package self-test.

Downloaded code never executes merely because it was discovered.

### 24.3 Staged versioned activation

Use a versioned package store:

```text
download metadata/package
 -> verify
 -> unpack to versioned staging
 -> validate tools/skills/verifiers
 -> self-test/compatibility probe
 -> register
 -> atomically activate
```

Failed install/update leaves the previous working version intact.

New broader permissions require new policy/approval.

Support:

- metadata-only update discovery;
- rollback;
- quarantine;
- diagnostics retention;
- hot ToolRegistry registration at controlled boundaries;
- helper-only restart when possible;
- no tool-surface mutation during an in-flight tool call.

### 24.4 Capability and skill indexes

Installed metadata feeds rebuildable local indexes:

```text
intent/keywords/namespaces
 -> plugin
 -> skill
 -> tool descriptors
```

Skills load progressively:

```text
metadata
 -> search
 -> summary
 -> selected SKILL.md
 -> cache task-local id/version/hash
```

Unchanged skills are not reread every turn. Changed plugin/skill hashes invalidate caches deterministically.

Task evidence records exact plugin/skill/tool versions used.

---

## 25. Canonical Word + legal-research acceptance scenario

Before Phase 13, the engine must pass the canonical scenario from the supplementary requirements:

- Word is already open and may contain unsaved edits;
- the document contains prose, tables, formatting and legal references;
- spelling errors exist;
- legal references may have later amending/replacing/supplementing documents.

The agent must:

1. identify the active Word document without pixel clicking;
2. read unsaved live state;
3. create bounded task/acceptance criteria;
4. detect spelling candidates;
5. extract legal citations;
6. infer freshness/current research requirement;
7. use WebResearchHost;
8. prefer authoritative sources;
9. determine typed legal relationships/status;
10. preserve evidence/provenance;
11. produce a scoped Word patch;
12. modify only approved/required ranges;
13. preserve unrelated formatting/content;
14. re-read live Word state;
15. verify every changed criterion;
16. repair only failed criteria;
17. return a concise evidence-backed summary.

Required negative properties:

- no model-memory-only legal status;
- no “newer means replaced” shortcut;
- no silent original overwrite;
- no loss of unsaved Word edits;
- no completion from tool success alone;
- no full-document + full-web-page + all-schema replay each turn;
- **Desktop pixel/computer-use calls = 0 when OfficeHost + WebResearchHost are sufficient.**

---

## 26. Revised acceptance gate and Phase 13 rule

Phase 12 acceptance must additionally prove:

- generic MCP provider lifecycle/deferred schema/scope/provenance;
- general computer capability safety for filesystem/process/shell/app/window/UIA/input/screen/browser;
- WebResearchHost freshness routing and bounded evidence;
- legal relationship/status verification with authoritative-source preference;
- structured Word spelling/citation extraction;
- structured Word/Excel namespace routing;
- AutoCAD provider/native-plugin boundary at least through its accepted provider contract and deterministic safety/verification tests;
- plugin manifest/catalog/install/update/rollback/quarantine/hot-registration behavior;
- bounded context across web pages, plugin/skill catalogs and provider schemas;
- canonical Word + legal scenario end-to-end.

**V2-1210 user acceptance is valid only after all of the above are evidenced.**

No Phase 13 H2 Notes integration may begin earlier. H2 Notes integration must preserve the accepted long-running agent behavior; it must not collapse the engine back to one-shot chat.

---

## 27. Skill / Tool Extensibility Refinement

`docs/H2_AGENT_SKILL_TOOL_EXTENSIBILITY_REFINEMENT.md` is normative before Phase 12 acceptance.

Phase 10 foundations are reused. Do not replace `ToolRegistry`, `DeferredToolDiscovery`, `PluginManager`, MCP normalization, rollback/quarantine or existing provider provenance.

Required additional architecture:

- one logical skill discovery surface over built-in and plugin sources;
- canonical pre-load skill metadata centered on name + description + provenance;
- progressive loading: metadata → selected SKILL.md → only required references/scripts/assets;
- provenance-bearing skill identity: source, plugin/version when applicable, skill ID and SHA-256;
- rebuildable `InstalledCapabilityIndex` for locally usable tool/skill/provider capabilities;
- compact `AvailableCapabilityIndex` from cached catalog metadata only;
- configurable multi-source catalog abstraction/manager with deterministic trust/conflict handling;
- host-owned `CapabilityResolver` that searches installed first and catalog metadata only when needed;
- `TaskCapabilitySnapshot` pinning registry/provider/plugin/tool/skill versions and hashes for a running task;
- controlled capability refresh only at safe boundaries;
- explicit task-driven install transition when a missing capability is required;
- separate `IPackageRetriever` between catalog metadata and PluginManager;
- offline installed capabilities remain fully usable when remote catalogs are unavailable;
- task evidence records exact plugin/skill/tool/provider identities without embedding huge schemas or skill bodies.

Phase 12 cannot begin until V2-1116 through V2-1122 pass their deterministic acceptance suite.

