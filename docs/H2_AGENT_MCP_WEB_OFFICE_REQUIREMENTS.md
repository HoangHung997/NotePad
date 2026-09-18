# H2 Agent — MCP / Web / Office integration requirements

This file records additional **Agent Lab / H2 AI requirements** that must be considered before the accepted Agent Lab engine is integrated into production H2 Notes.

It is intentionally separate from `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`.

---

## 1. Goal

H2 AI must behave as a real tool-using agent, not as a one-shot chat model.

The agent must be able to:

- inspect the current state of an already-open application;
- decide whether a tool is required;
- discover and load only relevant tools;
- call multiple tools in sequence;
- observe results;
- verify mutations;
- repair failed criteria;
- use current Internet information when freshness is required;
- return a concise final answer only after the task is complete or blocked.

Target execution style:

```text
user goal
  -> understand / ground task
  -> discover relevant tools
  -> observe current state
  -> act
  -> observe again
  -> verify
  -> repair if needed
  -> final answer
```

Tool-call count must not be fixed. A difficult task may require many tool calls before completion.

---

## 2. MCP role

MCP is treated as a **tool/resource provider protocol**, not as the agent itself.

The AgentOrchestrator remains responsible for:

- deciding whether a tool is needed;
- deciding which tool to call;
- deciding call order;
- deciding when additional evidence is required;
- deciding when verification is sufficient;
- deciding whether to repair or finish.

MCP/native/app tools are normalized into the Agent Lab Tool Registry.

Preferred conceptual layering:

```text
AgentOrchestrator
    |
    +-- Context / Task / Verification / Repair / Permission
    |
    +-- ToolRegistry
          |
          +-- Built-in tools
          +-- Native hosts
          +-- MCP providers
          +-- Web research providers
          +-- File adapters
          +-- Python fallback
          +-- Desktop fallback
```

The model must not need to know whether a tool is implemented through MCP, COM, C#, Python, REST, OpenXML, named pipe, or another internal transport.

---

## 3. Generic MCP provider layer is required

Current Agent Lab architecture has ToolRegistry / deferred discovery / OfficeHost / DesktopHost, but dynamic MCP provider integration must become explicit.

Add a generic provider layer with responsibilities equivalent to:

```text
McpServerConnection
McpToolProvider
McpToolAdapter
McpResourceAdapter
McpPermissionPolicy
McpHealthState
```

Exact type names may differ.

Requirements:

- connect/disconnect/reconnect lifecycle;
- enumerate tool namespace summaries without injecting all schemas into model context;
- lazily load detailed schemas only when selected;
- normalize tools into the existing ToolRegistry;
- preserve read-only vs mutating classification;
- declare resource/session scope;
- declare parallel-safety and serialization boundaries;
- expose bounded errors;
- support cancellation and timeout;
- record provenance: server/provider/tool/version;
- prevent one MCP server from silently broadening another server's scope;
- never expose secrets in prompt/tool metadata;
- do not treat arbitrary MCP server claims as trusted verification evidence.

---

## 4. Deferred tool discovery remains mandatory

H2 must not preload every schema from Word, Excel, AutoCAD, Web, GitHub, etc.

Example:

```text
Word MCP       40 tools
Excel MCP      60 tools
AutoCAD MCP   100+ tools
Web MCP        20+ tools
other MCPs     ...
```

All schemas must not be placed in every model request.

Initial exposure should remain small, for example:

```text
tool_search
update_plan
small stable core only
```

Then:

```text
user: "read OTC blocks and export them to Excel"
  -> tool_search("AutoCAD block attributes")
  -> load only relevant AutoCAD schemas
  -> execute
  -> tool_search("Excel write structured table")
  -> load only relevant Excel schemas
```

Context/tool-schema growth must remain bounded.

---

## 5. Tool namespaces are capability families, not single tools

Names such as `Word`, `Excel`, `AutoCAD`, `Web`, `FileSystem`, `Desktop`, and `Browser` describe **tool namespaces / capability families**. They are not one monolithic tool each.

For example:

```text
word.*
  get_active_document
  read_range
  find_text
  get_spelling_errors
  replace_range
  apply_format
  save_copy
  verify_range
  ...

excel.*
  list_workbooks
  read_range
  read_formulas
  write_range
  set_formula
  recalculate
  verify_range
  ...

autocad.*
  list_documents
  list_layers
  find_blocks
  read_attributes
  update_attribute
  create_entity
  modify_entity
  plot
  verify_entity
  ...

web.*
  search
  fetch
  download
  extract
  get_metadata
  open_browser
  ...
```

The agent must be able to discover and call as many tools from these namespaces as needed for one task.

A difficult task may use tens of structured calls before the final response.

---

## 6. Codex-like general computer-control toolset is required

In addition to app-specific structured namespaces, Agent Lab needs a **general computer-control capability set** comparable in role to the tools available to a coding/desktop agent such as Codex.

This is not one unsafe `control_computer(command)` function.

It is a bounded typed tool family that allows the AgentOrchestrator to inspect and operate the local machine when no higher-level adapter is sufficient.

Required capability groups should include equivalents of:

```text
filesystem.*
  list
  stat
  search
  read
  write
  copy
  move
  delete_with_policy
  hash
  watch
  ...

process.*
  list
  start
  wait
  terminate_with_policy
  read_exit_status
  ...

shell.*
  run_bounded
  run_build
  run_test
  run_script
  ...

app.*
  list_running_apps
  get_active_app
  launch
  activate
  wait_for_window
  ...

window.*
  enumerate
  get_bounds
  get_title
  get_process
  activate
  observe
  ...

uia.*
  inspect_tree
  find_element
  invoke
  set_value
  select
  expand
  ...

input.*
  click
  double_click
  type
  key
  chord
  scroll
  drag
  ...

screen.*
  capture
  inspect_region
  observe_after_action
  ...

browser.*
  navigate
  inspect
  click
  type
  download
  wait
  ...
```

Exact names may differ. The architectural requirement is that the agent can perform multi-step computer work without the user manually driving the machine.

### Safety boundary

The general computer-control host must be capability-scoped and permission-aware.

Requirements:
- no unrestricted arbitrary-machine access by default;
- separate read/observe from mutating actions;
- bind actions to process/window/state identity;
- stale observation protection;
- cancellation and timeout;
- explicit policy for shell/process side effects;
- no hidden access to password managers, security prompts, secrets, or protected system surfaces;
- observe-after-mutation before claiming success;
- durable evidence for important actions;
- prefer a structured app adapter before UI/pixel control.

### Relationship to structured app tools

The preferred execution path remains:

```text
structured app tool
    > app API / MCP / COM / plugin
    > direct file operation
    > UI Automation
    > screenshot / mouse / keyboard
```

The general computer-control toolset is essential, but it is the fallback when Word/Excel/AutoCAD/Web structured adapters cannot complete the operation directly.

This gives the agent Codex-like breadth without throwing away the speed and reliability of native application APIs.

---

## 7. Direct application adapters are preferred over computer use

Preference order:

```text
structured native/app adapter
    > MCP/API/COM/plugin
    > direct file adapter
    > UI Automation
    > screenshot/vision computer use
```

Computer Use is a fallback, not the default for Word, Excel, or AutoCAD when a structured adapter can safely perform the same operation.

---

## 8. Word requirements

H2 must support the **already-open Word document**, including unsaved state.

Required structured capabilities include at least:

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
```

Additional proofreading-oriented capabilities should be considered:

```text
word.get_spelling_errors
word.get_grammar_candidates
word.extract_legal_citations
```

When Word's native spelling engine is available, prefer using it as structured evidence rather than forcing the LLM to inspect an entire large document only to find basic spelling errors.

Mutations must preserve unrelated formatting/content unless the task explicitly requests broader changes.

---

## 9. Excel requirements

H2 must support the **already-open Excel workbook**, including unsaved state.

Required structured capabilities include at least:

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

A large range operation should be performed through structured calls, not UI clicking.

Wrong workbook/sheet/process/session must be rejected deterministically.

---

## 10. AutoCAD requirements

AutoCAD should expose structured tools through a production-safe host.

Preferred production architecture:

```text
Agent
  -> Tool/MCP layer
  -> local bridge / IPC
  -> C# AutoCAD plugin
  -> DocumentLock
  -> Transaction
  -> AutoCAD Database
```

Python/COM may be useful as prototype or auxiliary adapters, but production mutation should prefer the native .NET plugin when available.

Example tool families:

```text
autocad.list_documents
autocad.get_active_document
autocad.list_layers
autocad.find_blocks
autocad.read_attributes
autocad.read_entities
autocad.update_attribute
autocad.create_entity
autocad.modify_entity
autocad.plot
autocad.verify_entity
```

No arbitrary command execution should become the default mutation mechanism when a bounded typed tool can do the job.

---

## 11. WebResearchHost is required

Agent Lab currently has no complete first-class WebResearchHost requirement. This must be added.

WebResearchHost is distinct from DesktopHost.

Required capability families:

```text
web.search
web.fetch
web.download
web.extract
web.get_metadata
web.open_browser   # fallback when structured access is insufficient
```

Use cases:

- current news;
- weather;
- current prices;
- technical standards;
- laws/regulations;
- replacement/amendment status;
- product/vendor documentation;
- any request containing freshness intent such as "today", "latest", "current", "still valid", "replaced", "newest".

---

## 12. Freshness policy

The agent must not answer freshness-sensitive requests solely from model memory.

Examples requiring current external lookup:

```text
"today"
"latest"
"current"
"currently valid"
"has it been replaced"
"new regulation"
"new standard"
"weather this afternoon"
"latest news"
```

These requests must set or infer something equivalent to:

```text
FreshnessRequired = true
```

and require WebResearchHost or another current authoritative data provider.

---

## 13. Web evidence and provenance

Web results must not be treated as unstructured trusted text only.

Store structured evidence equivalent to:

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

Large pages/PDFs should be stored outside active prompt context, using ArtifactStore or an equivalent durable evidence store.

The model context should receive only bounded summaries + evidence handles unless full content is explicitly required for the next step.

This is required to prevent context growth.

---

## 14. Legal/regulatory research requirements

Finding a newer document is not enough to claim that it replaces an older one.

The agent must distinguish relationships such as:

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

Represent relationships structurally where possible.

Conceptual result:

```text
LegalDocumentStatus
  DocumentId
  Jurisdiction
  Issuer
  IssueDate
  EffectiveDate
  Status
  Relationships[]
    RelationType
    RelatedDocumentId
    EffectiveDate
    EvidenceIds[]
```

The agent must prefer authoritative/official sources for legal-effect conclusions.

Search-engine snippets or secondary articles alone are insufficient evidence for final claims about replacement, amendment, repeal, or effective status when authoritative material is available.

---

## 15. Canonical Word + legal research acceptance scenario

This use case is mandatory before final acceptance.

### User situation

- Microsoft Word is already open.
- A document contains normal prose, formatting, tables, and legal/regulatory references.
- The document may contain unsaved edits.
- Some spelling errors exist.
- Some legal references may have newer amending/replacing/supplementing documents.

### User request

Equivalent to:

> "Check the spelling in the Word document I have open. Also check whether the legal documents referenced in it have newer documents that replace, amend, or supplement them, and update the document correctly."

### Required agent behavior

1. Identify the active Word document without pixel clicking.
2. Read current **unsaved** Word state.
3. Create a bounded task contract and acceptance criteria.
4. Detect spelling candidates.
5. Extract legal-document references.
6. Decide that Internet research is required.
7. Use WebResearchHost.
8. Prefer authoritative sources.
9. Determine each document's actual legal relationship/status.
10. Preserve evidence IDs and source provenance.
11. Produce a proposed Word patch.
12. Modify only approved/required ranges.
13. Preserve unrelated formatting/content.
14. Re-read the live Word document.
15. Verify every changed criterion.
16. Repair only failed criteria if necessary.
17. Return a concise final summary of:
    - spelling corrections;
    - legal references checked;
    - replaced/amended/supplemented items;
    - sources/evidence;
    - any unresolved uncertainty.

### Required negative behaviors

The agent must NOT:

- assume a newer document automatically replaces an older one;
- rely only on model memory for current legal status;
- overwrite the original silently when the task policy requires a copy/approval;
- lose unsaved Word edits;
- use Computer Use when OfficeHost can safely complete the task;
- report success only because a tool returned success;
- inject complete Word document + complete web pages + all tool schemas into every model turn.

### Desired acceptance metric

For this scenario:

```text
Desktop pixel/computer-use calls = 0
```

when OfficeHost + WebResearchHost provide sufficient structured capabilities.

---

## 16. Observe -> Act -> Verify rule

For every structured application mutation:

```text
observe
  -> act
  -> observe again
  -> verify
```

A tool returning `success=true` is not sufficient completion evidence.

Examples:

Word:
```text
replace_range
 -> read_range
 -> verify text/style/preservation
```

Excel:
```text
set_formula
 -> read_formula
 -> verify workbook/sheet/cell
```

AutoCAD:
```text
update_attribute
 -> query entity again
 -> verify handle/layer/value/state
```

---

## 17. Repair context must stay small

When verification fails, do not replay the entire task/transcript.

Example:

```text
Criterion C5 failed:
Legal citation paragraph 17 still references superseded text.
Evidence: evidence:web:123, evidence:word:456

Repair C5 only.
Preserve C1-C4/C6.
```

This is part of the core goal that H2 Agent context must not grow without bound.

---

## 18. Phase integration requirements

Before Phase 08-12 can be considered complete, the tracker/spec should explicitly account for:

- generic MCP provider layer;
- Codex-like general computer-control toolset (filesystem/process/shell/app/window/UIA/input/screen/browser);
- WebResearchHost;
- freshness policy;
- web evidence/provenance;
- legal-status relationship verification;
- Word spelling/citation extraction;
- structured adapter priority;
- the canonical Word + legal research acceptance scenario above.

Before Phase 13 H2 Notes integration:

1. Read this entire file.
2. Reconcile it with the final Agent Lab spec/tracker.
3. Ensure the accepted engine can execute this scenario end-to-end.
4. Ensure context/tool-schema growth remains bounded.
5. Ensure H2 Notes integration does not downgrade these capabilities to one-shot chat behavior.

---

## 19. Core architectural principle

The target system is:

```text
small agent core
+ bounded active context
+ deferred tool discovery
+ MCP/native/app capability providers
+ current web research
+ deterministic verification
+ repair loop
= long-running, Codex-like work agent
```

The target is **not**:

```text
ever-growing prompt
+ every tool schema
+ full conversation history
+ model self-review
= slow one-shot assistant
```


---

## 20. Dynamic Plugin / Skill Catalog and update system

H2 Agent must not hard-code its complete future capability set into the application binary.

The system needs an installable/updateable extension layer so new tools, MCP providers, native adapters, skills, workflows, templates and verifiers can be added later without rebuilding the full H2 application.

### 20.1 Package concepts

Keep these concepts separate:

```text
Tool
  = one callable capability/action

Skill
  = reusable instructions/workflow telling the agent when/how to combine tools

Plugin
  = installable package that may contain:
      tools / MCP configuration
      skills
      native helper metadata
      verifier definitions
      resources/templates
      optional lifecycle hooks
```

A skill is not a replacement for a tool.
A tool performs an action.
A skill teaches the agent how to use available capabilities for a recognizable user goal.

### 20.2 H2 Plugin Manifest

Every installable extension should have a bounded machine-readable manifest similar in spirit to:

```json
{
  "id": "h2.autocad.productivity",
  "name": "AutoCAD Productivity",
  "version": "1.4.0",
  "minAgentVersion": "2.0.0",
  "publisher": "example",
  "packageHash": "sha256:...",
  "capabilities": [
    "autocad.find_blocks",
    "autocad.read_attributes",
    "autocad.update_attribute"
  ],
  "skills": [
    "quantity-takeoff",
    "block-audit"
  ],
  "providers": [
    "mcp",
    "native-host"
  ],
  "permissions": [
    "autocad.read",
    "autocad.write"
  ]
}
```

Exact schema may differ.

### 20.3 Plugin Catalog / Repository

H2 needs a catalog abstraction with one or more configured sources.

Possible sources:

```text
Official H2 catalog
Team/private catalog
Git repository catalog
Local folder catalog
Enterprise/internal catalog
```

Each catalog entry should expose only compact discovery metadata first:

```text
plugin id
name
version
summary
publisher
capability keywords
skill keywords
compatibility
trust/signature state
package hash
download location
```

Do not download or inject the full package simply because it exists in a catalog.

### 20.4 Search-before-install behavior

When the current ToolRegistry cannot satisfy the task, the agent may search available catalogs.

Example:

```text
User:
"Audit all dynamic block attributes in AutoCAD and export to Excel."

Current registry:
no specialized dynamic-block audit skill

Agent:
  -> search installed tools
  -> search plugin catalog metadata
  -> find h2.autocad.dynamic-block-audit
  -> inspect manifest + permissions + compatibility
  -> request/install according to policy
  -> load package
  -> discover its tools/skills
  -> continue the original task
```

The agent must not blindly install the first search result.

### 20.5 Installation policy

Installation is a host decision, not a model-only decision.

Policy should support modes such as:

```text
Disabled
Allow trusted official packages
Ask before new publisher/package
Allow organization-approved catalog
Developer mode for local packages
```

A model may propose installation, but the host verifies policy, trust, integrity and compatibility.

### 20.6 Integrity and trust

Before activation, verify at minimum:

- package ID/version;
- cryptographic package hash;
- publisher/source identity where available;
- supported Agent version;
- declared capabilities;
- requested permissions;
- native executable/helper presence;
- duplicate/conflicting tool names;
- forbidden paths or traversal;
- unsupported lifecycle hooks;
- manifest/schema validity.

Downloaded code must never execute merely because the model discovered it.

Untrusted native binaries/scripts require explicit policy/approval and isolation appropriate to their capability.

### 20.7 Staged activation

Installation/update should be staged:

```text
download
 -> verify hash/signature/manifest
 -> unpack to versioned staging directory
 -> validate tools/skills
 -> run package self-test / compatibility probe
 -> register package
 -> atomically activate version
```

A failed install/update must leave the previous working version intact.

### 20.8 Versioned local package store

Use a versioned local cache/store, conceptually:

```text
plugins/
  h2.autocad.productivity/
    1.3.0/
    1.4.0/
    active.json

skills/
  cache/index...
```

Do not overwrite the currently active version in-place during update.

Keep enough metadata to rollback.

### 20.9 Update discovery

H2 should periodically or on-demand query only catalog metadata for newer compatible versions.

Example:

```text
installed: h2.autocad.productivity 1.3.0
catalog:   h2.autocad.productivity 1.4.0
               |
               -> inspect changelog / permissions / capability delta
               -> validate policy
               -> staged download/update
```

A new version that requests broader permissions must not silently inherit approval from an older narrower version.

### 20.10 Capability index

Installed package metadata feeds a rebuildable local capability index.

The index maps:

```text
intent / keywords / namespaces
  -> plugin
  -> skill
  -> tool descriptors
```

Example:

```text
"AutoCAD dynamic block attributes"
 -> plugin h2.autocad.productivity
 -> skill dynamic-block-audit
 -> tools:
      autocad.find_dynamic_blocks
      autocad.read_dynamic_properties
      autocad.read_attributes
      autocad.verify_entities
```

The model sees compact search results first.
Detailed schemas and SKILL instructions are loaded only when selected.

### 20.11 Skill loading must remain progressive

Skills should follow the same bounded-context principle already used by Agent Lab.

Do not inject all installed `SKILL.md` files into every prompt.

Use:

```text
skill catalog metadata
 -> skill search
 -> selected skill summary
 -> load SKILL.md only when needed
 -> remember skill id/version/hash within the task
 -> do not reread unchanged skill every turn
```

If a plugin update changes a skill hash/version, invalidate the cached skill content deterministically.

### 20.12 Tools and skills may update independently

A plugin may ship:

- new tools;
- new skill instructions;
- revised verifier behavior;
- new templates;
- compatibility fixes.

The agent must record exact package/skill/tool versions in task evidence so a completed run can be reproduced/audited.

Example evidence:

```text
plugin: h2.autocad.productivity@1.4.0
skill: dynamic-block-audit@sha256:...
tool: autocad.read_attributes schema v3
```

### 20.13 Hot registration without restarting the entire H2 app

Where safe, newly activated plugins should be registered into the ToolRegistry dynamically.

The active task may refresh capability metadata at a controlled boundary.

Do not mutate the tool surface in the middle of an in-flight tool call.

Native-host changes that require process restart may restart only the relevant helper/plugin host rather than the full H2 UI when possible.

### 20.14 Rollback / quarantine

If a new package causes:

- crashes;
- invalid schemas;
- verifier regressions;
- permission violations;
- repeated tool failures;
- compatibility breakage;

H2 must be able to:

```text
disable package
rollback to previous version
quarantine bad version
retain diagnostics
continue with remaining capabilities
```

### 20.15 Catalog security rule

Catalog discovery is not trust.

Search results from Internet/Git repositories must not automatically become executable capability.

The path is:

```text
discover metadata
 -> validate source
 -> validate manifest
 -> validate package integrity
 -> apply policy/approval
 -> stage
 -> self-test
 -> activate
```

### 20.16 Example long-term flow

```text
User gives unfamiliar specialized task
        |
        v
Agent searches installed ToolRegistry
        |
        +-- capability exists -> use it
        |
        +-- capability insufficient
              |
              v
        search Plugin/Skill Catalog
              |
              v
        find candidate extension
              |
              v
        host checks:
          compatibility
          trust
          permissions
          package hash
              |
              v
        install/update if policy allows
              |
              v
        rebuild local capability index
              |
              v
        tool_search selects relevant tools
              |
              v
        load selected SKILL.md
              |
              v
        Agent continues original task
```

This is the intended extensibility model for H2 Agent.

---

## 21. Architectural target for extensibility

The long-term architecture should support:

```text
AgentOrchestrator
     |
     +-- ToolSearchIndex
     +-- SkillSearchIndex
     +-- PluginCatalog
     +-- PluginManager
     +-- PermissionPolicy
     +-- Verification
     |
     +-- Dynamic ToolRegistry
            |
            +-- built-in tools
            +-- installed plugins
            +-- MCP providers
            +-- OfficeHost
            +-- AutoCADHost
            +-- WebResearchHost
            +-- DesktopHost
```

The product should be able to gain new specialist capabilities later without redesigning the agent core.
