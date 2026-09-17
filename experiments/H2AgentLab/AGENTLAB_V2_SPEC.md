# H2 Agent Lab 2.0 — Codex-style provider-neutral agent harness

Status: **approved implementation specification**  
Scope: `experiments/H2AgentLab` first. **Do not integrate into H2 Notes UI until the acceptance gate in this document passes.**  
Branch during implementation: `feature/nas-multi-device-sync` unless the user explicitly changes it.  
Main principle: **reuse proven H2 Notes components when they are genuinely generic; do not duplicate stable code; do not make H2 Notes depend on unfinished Agent Lab code.**

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
