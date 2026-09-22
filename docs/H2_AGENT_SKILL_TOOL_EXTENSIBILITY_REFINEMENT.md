> **SUPERSEDED AS NORMATIVE AGENT ARCHITECTURE AFTER THE CURRENT IN-FLIGHT AGENT LAB TASK FINISHES (2026-09-18).**
>
> This document is retained as design-history evidence. Its accepted useful ideas were consolidated and simplified into:
>
> - `docs/H2_AGENT_MASTER_SPEC.md`
> - `docs/H2_AGENT_MASTER_TASKS.md`
>
> In particular, the master specification intentionally removes/defer some over-designed capability/catalog layers from mandatory core work.

# H2 Agent Lab — Skill / Tool Extensibility Refinement

Status: **Required refinement during Phase 11 before Phase 12 acceptance**  
Date: 2026-09-18  
Current Agent Lab pointer when this document was written: **V2-1101 — Move Lab UI send path to AgentOrchestrator**  
Scope: **Refine extensibility architecture only. Do not rewrite Phase 10. Do not restart the Agent Lab plan from scratch.**

---

## 1. Why this refinement exists

Phase 10 already built the correct foundations for dynamic capability support:

- `ToolRegistry`;
- `DeferredToolDiscovery`;
- generic capability-provider contracts;
- MCP provider normalization;
- `H2PluginManifest`;
- `PluginCatalog`;
- `PluginManager`;
- staged install/update;
- rollback/quarantine;
- `PluginSkillCatalog`;
- version/hash evidence;
- hot ToolRegistry registration at safe boundaries.

Those parts are **REUSE**.

The remaining gap is architectural unification.

Today the system still has several capability paths that are conceptually separate:

```text
built-in skills
plugin skills
built-in tools
plugin tools
MCP tools
provider tools
```

The Agent should not need separate high-level reasoning paths for each source.

The target is:

> **One capability discovery model. Multiple capability sources.**

The Agent asks what capability is needed.

The host decides where that capability comes from, whether it is installed, whether it may be installed/updated, and which exact version is pinned for the current task.

---

## 2. Non-goals

This refinement does **not** require:

- rewriting `AgentOrchestrator`;
- replacing `ToolRegistry`;
- replacing `PluginManager`;
- rebuilding the Phase 10 plugin system;
- creating a second plugin architecture;
- implementing the full future H2-Agent-Catalog GitHub repository now;
- adding a production plugin-store UI now;
- interrupting the current V2-1101 CI/review cycle;
- changing H2 Notes production code before the Phase 12 gate.

The current Phase 11 task should finish normally.

After the current task is stable, the remaining Phase 11 work must absorb the requirements in this document before Phase 12 acceptance.

---

## 3. Target architecture

The desired runtime architecture is:

```text
                        AgentOrchestrator
                               |
                        CapabilityResolver
                               |
             +-----------------+-----------------+
             |                                   |
     InstalledCapabilityIndex              Catalog Search
             |                                   |
       +-----+------+                     CatalogSourceManager
       |            |                          |
 ToolSearchIndex  SkillSearchIndex      +------+------+
       |            |                   |      |      |
       |            |                 HTTP   GitHub   NAS /
       |            |                               Local /
       |            |                               Private
       +------+- ----+
              |
      Dynamic ToolRegistry
              |
      +-------+---------+----------------+
      |                 |                |
   built-in          plugin           providers
                                      / MCP
```

Important:

- `ToolRegistry` remains the runtime source of callable tool definitions.
- Skills remain guidance/workflow knowledge, not callable tools.
- Plugin remains the install/version/distribution unit.
- MCP remains a runtime provider protocol, not the package/update system itself.
- Catalog source is distribution metadata, not trusted executable state.

---

## 4. Tool, Skill, Plugin and Provider must remain distinct

### Tool

A callable action.

Examples:

```text
excel.read_range
excel.write_range
autocad.find_blocks
web.search
filesystem.read
```

### Skill

Guidance teaching the Agent when and how to combine capabilities.

Examples:

```text
autocad-dynamic-block-audit
legal-document-current-status-review
excel-quantity-takeoff
```

### Plugin

Installable/versioned package that may contain:

- tool definitions;
- skill content;
- provider definitions;
- MCP configuration;
- resources/templates;
- verifiers;
- declarative self-test;
- optional native helper metadata.

### Provider

Runtime capability source.

Examples:

- MCP server;
- OfficeHost;
- WebResearchHost;
- AutoCAD native bridge;
- DesktopHost.

A provider may be installed/configured by a plugin, but provider lifecycle and plugin package lifecycle are separate concerns.

---

## 5. Add a unified skill-source abstraction

Current code has built-in `SkillCatalog` and plugin `PluginSkillCatalog`.

Do not delete them immediately.

Add a common abstraction above them.

Conceptually:

```csharp
public interface ISkillSource
{
    string SourceId { get; }

    IReadOnlyList<SkillSummary> Search(string query);

    SkillContent Read(SkillIdentity identity);
}
```

Possible implementations:

```text
BuiltInSkillSource
PluginSkillSource
FutureRemoteMetadataSkillSource   // metadata only, not executable content
```

Then expose one host-facing search surface:

```text
UnifiedSkillCatalog
        |
        v
SkillSearchIndex
```

The Agent must not need to reason:

> "Should I search SkillCatalog or PluginSkillCatalog?"

It should search one logical skill index.

### 5.1 Canonical skill discovery metadata

Based on the inspected Codex skill bundle, H2 should keep the mandatory skill discovery contract intentionally small.

The baseline metadata exposed before a skill is loaded should be:

```yaml
name: cad-integrity
description: >
  Inspect AutoCAD blocks and dynamic blocks for inconsistent attributes,
  dynamic parameters, actions, visibility states and stale block references.
  Use for block audits and ATTSYNC/dynamic-block consistency checks.
```

The important routing field is **description**, not a requirement that the skill name resemble the user's wording.

The description should explain, where useful:

- what the skill can do;
- when it should be used;
- important boundaries / when it should not be used.

Do **not** require every skill author to maintain a large mandatory list of:

```text
intents
aliases
examples
keywords
apps
domains
```

Those may exist as optional catalog/search enrichment, but they are not the canonical minimum contract.

### 5.2 Canonical skill folder structure

H2 should support a Codex-style progressive skill folder shape:

```text
skill-name/
├─ SKILL.md                 required
├─ references/              optional
├─ scripts/                 optional
├─ assets/                  optional
├─ agents/                  optional interface/invocation metadata
└─ LICENSE*                 optional
```

Rules:

- `SKILL.md` is the skill entry point.
- `references/` contains guidance that should be read only when relevant.
- `scripts/` contains deterministic/helper executables or source used by the skill; scripts do not automatically gain permission to execute.
- `assets/` contains templates/examples/static resources and must not be injected into model context unless needed.
- `agents/` or an equivalent H2 metadata file may contain UI name, short description, default prompt and invocation policy without bloating `SKILL.md`.

H2 does not need to copy Codex filenames exactly. The important requirement is the separation of discovery metadata, core instructions, optional references, executable helpers and UI/invocation metadata.

### 5.3 Progressive disclosure contract

Skill loading should have three bounded stages:

```text
Stage 1 — discovery
name + description + source/version/trust metadata

Stage 2 — selected skill
load SKILL.md only after selection

Stage 3 — on-demand resources
load only the specific references/scripts/assets needed by the current task
```

Never preload all `SKILL.md` files or all references for every installed skill.

A selected skill may explicitly tell the Agent which reference to read next.

---

## 6. Skill identity must include provenance

A skill cannot be identified only by a short name.

Use an identity concept that can distinguish:

```text
source
plugin id
plugin version
skill id
content hash
```

Conceptually:

```text
SkillIdentity
{
    SourceKind
    SourceId
    PluginId?
    PluginVersion?
    SkillId
    Sha256
}
```

Examples:

```text
built-in / coding / sha256:...
plugin / h2.autocad.productivity@1.4.0 / block-audit / sha256:...
```

Task evidence must preserve this identity.

---

## 7. Add InstalledCapabilityIndex and AvailableCapabilityIndex

The Agent should not repeatedly inspect plugin folders or query remote sources for normal tool/skill discovery.

Keep two conceptually separate indexes:

```text
InstalledCapabilityIndex
    = capabilities usable now from local active state

AvailableCapabilityIndex
    = compact metadata for capabilities that can be installed/updated
      from configured catalog sources
```

Installed search must never require network access.

Available search may use cached remote-catalog metadata and may refresh metadata only when policy/freshness requires it.

Maintain a rebuildable local installed-capability index.

Conceptually:

```text
InstalledCapabilityIndex

tool:
  autocad.find_blocks
    source = plugin
    plugin = h2.autocad.productivity
    pluginVersion = 1.4.0
    toolVersion = 3
    schemaVersion = 2

skill:
  block-audit
    source = plugin
    plugin = h2.autocad.productivity
    pluginVersion = 1.4.0
    sha256 = ...

provider:
  autocad.native
    source = plugin
    pluginVersion = 1.4.0
```

The installed index must be rebuildable from installed/active packages and providers.

It must not become a second authoritative store that can drift permanently from the active package state.

The available index should contain compact discovery metadata only, for example:

```text
plugin id/version
skill name
skill description
tool/capability summaries
publisher
trust/source
compatibility
permissions summary
package hash/location
update state
```

Do not place full `SKILL.md`, full references or large tool schemas into the available index.

---

## 8. Add CatalogSource abstraction

Current `PluginCatalog` represents catalog metadata.

Do not hard-code where that metadata comes from.

Add a source abstraction above it.

Conceptually:

```csharp
public interface ICatalogSource
{
    string SourceId { get; }
    CatalogSourceKind Kind { get; }
    int Priority { get; }
    PluginTrustState TrustState { get; }

    Task<CatalogSnapshot> FetchMetadataAsync(
        CancellationToken cancellationToken);
}
```

Future implementations may include:

```text
GitHubCatalogSource
HttpCatalogSource
NasCatalogSource
LocalFolderCatalogSource
PrivateCatalogSource
```

For Agent Lab acceptance, it is acceptable to use deterministic local/test catalog sources first.

The architecture must not require a single GitHub repository.

---

## 9. Add CatalogSourceManager

Multiple sources must be supported simultaneously.

Conceptual behavior:

```text
CatalogSourceManager
    |
    +-- source A priority 100
    +-- source B priority 80
    +-- source C priority 20
    |
    v
merge compact metadata
    |
    v
PluginCatalog view
```

Conflict handling must be deterministic.

At minimum define behavior for:

- same plugin ID + same version + same publisher + same hash;
- same plugin ID + same version + different hash;
- same plugin ID + same version + different publisher;
- multiple compatible versions;
- disabled source;
- unavailable source;
- stale cached source.

A priority value is not allowed to silently override a trust/security conflict.

---

## 10. Add CapabilityResolver

This is the key high-level refinement.

The Agent should ask for a capability, not assume in advance that the answer must be a tool or a skill.

The resolver may return:

```text
INSTALLED
AVAILABLE
UPDATE_AVAILABLE
BLOCKED_BY_POLICY
INCOMPATIBLE
UNSUPPORTED
```

For example, a request such as:

> "Check these AutoCAD blocks for invalid parameters and actions."

may resolve to a skill named `cad-integrity` even though the user never said that name, because its **description** semantically matches the requested work.

Current discovery mainly searches installed tools.

Add a host-owned resolver that can distinguish:

1. installed capability exists;
2. installed capability exists but is incompatible/unavailable;
3. capability may exist in an approved catalog;
4. capability is unsupported.

Conceptual flow:

```text
Agent needs capability
        |
        v
CapabilityResolver
        |
        +-- search installed tools
        +-- search installed skills
        +-- inspect active providers
        |
        v
sufficient?
  | yes
  v
return installed capability set

  no
  |
  v
search configured catalog metadata
  |
  v
candidate plugin(s)
  |
  v
host policy / trust / permissions
  |
  v
install/update if allowed
  |
  v
refresh installed capability index
  |
  v
continue original task
```

The Agent may request capability discovery.

The Agent must **not** own trust decisions, download integrity decisions, or permission policy.

---

## 11. CapabilityResolver must not auto-install arbitrary Internet code

The model may produce an intent such as:

```text
Need capability:
"AutoCAD dynamic block parameter/action inspection"
```

The host may search catalog metadata.

The host then decides:

- whether catalog source is allowed;
- whether publisher is trusted;
- whether user approval is required;
- whether permissions are acceptable;
- whether the package is compatible;
- whether the package hash/signature is valid;
- whether native helpers are allowed.

Only after host validation may PluginManager activate the package.

---

## 12. Add TaskCapabilitySnapshot

A running task must use a stable capability set.

Do not allow an automatic plugin/tool/skill update to silently change behavior in the middle of a task.

At task start, capture/pin a capability snapshot.

Conceptually:

```text
TaskCapabilitySnapshot
{
    RegistryVersion
    ProviderVersions[]
    PluginVersions[]
    ToolVersions[]
    SkillHashes[]
    CapturedUtc
}
```

Example:

```text
Task starts with:

plugin:
  h2.autocad.productivity@1.4.0

skill:
  block-audit@sha256:ABC

tool:
  autocad.find_blocks@3

schema:
  v2
```

An update to 1.5.0 appearing during this task must not replace these definitions mid-call.

---

## 13. Controlled capability refresh boundary

Reuse the existing Phase 10 rule that the ToolRegistry must not mutate during an in-flight tool call.

Extend it to the whole capability layer.

Allowed refresh points include:

- before task start;
- after a completed tool call when no executor is in flight;
- after explicit installation requested by the current task;
- after task completion;
- after provider reconnect at a safe boundary.

Not allowed:

- replacing a tool executor during its call;
- switching plugin version while verifier is consuming evidence from the previous version;
- changing skill instructions silently after the task contract is already based on an older skill;
- updating provider scope while a mutation is active.

---

## 14. Explicit exception: task-driven capability installation

A task may discover that a required capability is missing.

In that case, installation can occur during the task only through an explicit transition:

```text
task running
 -> capability missing
 -> pause execution state
 -> catalog search
 -> policy/approval
 -> staged install
 -> safe registry refresh
 -> capture updated TaskCapabilitySnapshot revision
 -> continue original task
```

The trace must show that the capability set changed.

Do not make the transition invisible.

---

## 15. Task evidence must record exact capability versions

For reproducibility/debugging, task evidence should be able to record:

```text
Plugin:
h2.autocad.productivity@1.4.0

Skill:
block-audit@sha256:abc...

Tool:
autocad.find_blocks
toolVersion=3
schemaVersion=2

Provider:
autocad.native@1.4.0

RegistryVersion:
42
```

This evidence should be bounded and structured.

Do not store huge tool schemas or full skill documents inside every task record.

---

## 16. Built-in capabilities remain valid

Do not force every existing built-in capability into an external plugin immediately.

Built-in tools and skills remain supported.

Treat them as sources in the same logical system:

```text
BuiltInToolSource
BuiltInSkillSource
```

This allows gradual migration later.

The Agent should not need special reasoning merely because a capability is built-in.

---

## 17. Plugin tool behavior

Plugin tools continue to use `PluginManager`.

Required flow remains:

```text
catalog metadata
 -> package retrieval
 -> archive/hash validation
 -> manifest validation
 -> compatibility
 -> permission delta
 -> staging
 -> self-test
 -> tool/skill validation
 -> atomic activation
 -> ToolRegistry refresh
```

Do not replace this with a simpler unsafe downloader.

---

## 18. MCP behavior

MCP remains a capability provider.

`McpToolProvider` should continue normalizing provider-advertised tools/resources into the common ToolRegistry model.

When an MCP server advertises changed tools/schema/version:

```text
provider reconnect/refresh
 -> compact metadata refresh
 -> invalidate affected loaded schemas
 -> safe ToolRegistry refresh
```

Updating the MCP server executable/package itself is **not** the responsibility of MCP runtime protocol.

That update should go through plugin/provider package distribution policy.

---

## 19. Future H2-Agent-Catalog compatibility

A future repository such as:

```text
HoangHung997/H2-Agent-Catalog
```

may be used as one catalog/distribution source.

AI Lab must **not** depend on that repository name.

The runtime should depend only on configured catalog-source metadata.

Conceptually:

```json
{
  "catalogSources": [
    {
      "id": "h2-official",
      "enabled": true,
      "type": "http-index",
      "indexUrl": "...",
      "trust": "official",
      "priority": 100
    }
  ]
}
```

Later the URL may point to:

- GitHub;
- another Git server;
- HTTP/CDN;
- NAS;
- company internal server.

No AgentOrchestrator redesign should be required.

---

## 20. Local vs remote skill/capability discovery

Installed and remote discovery should not be treated identically.

### 20.1 Installed skill set

For a normal local set of tens or hundreds of skills, expose bounded metadata such as:

```text
name
description
source
version/hash
availability
```

The selection layer/model can semantically choose among those compact descriptions.

Exact name matching is not sufficient.

### 20.2 Large remote catalog

A remote catalog may eventually contain thousands of skills/plugins.

Do not send all descriptions to the model.

Use:

```text
user/task capability query
 -> lexical/filter search over compact metadata
 -> semantic similarity search when useful
 -> top N candidates only
 -> model rerank / CapabilityResolver
 -> selected package metadata
```

The semantic layer is an **indexing/search optimization for large catalogs**, not a requirement that every skill author manually provide many aliases/intents.

The canonical authoring contract remains `name + description + SKILL.md`.

### 20.3 Search fallback behavior

Recommended resolution:

```text
search InstalledCapabilityIndex
        |
        +-- sufficient -> use installed capability
        |
        +-- insufficient
               |
               v
        search cached AvailableCapabilityIndex
               |
               +-- candidate found -> evaluate/install
               |
               +-- no candidate / metadata stale
                       |
                       v
                refresh configured catalog metadata
                       |
                       v
                rebuild AvailableCapabilityIndex
                       |
                       v
                search again
```

Catalog refresh downloads **metadata only**.

Plugin/skill package bytes are fetched only after a candidate has been selected and host policy allows installation.

---

## 21. Package retrieval must be a host service

Separate catalog search from package download.

Conceptually:

```text
ICatalogSource
    -> metadata only

IPackageRetriever
    -> fetch immutable package bytes/file

PluginManager
    -> verify/install/activate
```

This prevents the catalog abstraction from becoming an execution/download god object.

The retriever must support:

- cancellation;
- timeout;
- bounded download size;
- hash verification;
- temporary staging;
- resumability only if safely implemented;
- no secret leakage into prompts/logs.

For Phase 11, a deterministic local/test implementation is sufficient if production network retrieval is not yet scheduled.

The abstraction itself must exist before Phase 12 if the external catalog architecture is part of the accepted design.

---

## 22. Skill search remains progressive

Never inject every installed skill into every prompt.

Required flow:

```text
compact metadata: name + description + provenance
 -> semantic/model selection
 -> select relevant skill
 -> load exact SKILL.md
 -> load only required references/scripts/assets
 -> cache by source/plugin/version/hash
 -> do not reload unchanged content
```

The search implementation must not require the query to match the skill name.

For example:

```text
query:
"audit dynamic block parameters and actions"

skill name:
"cad-integrity"

description:
"Inspect AutoCAD dynamic blocks, attributes, parameters,
actions and visibility-state consistency..."
```

must be discoverable.

For a small installed skill set, compact description-based model selection may be sufficient.

For a large available remote catalog, use lexical + semantic retrieval to reduce candidates before model reranking.

If plugin update changes the skill hash:

```text
old cache identity != new cache identity
 -> invalidate
 -> load new content only when selected
```

Existing `DeferredSkillSession` behavior should be reused where possible.

---

## 23. Tool search remains progressive

Never expose all installed plugin/MCP tool schemas by default.

Continue using:

```text
small initial tool surface
 -> tool_search
 -> selected detailed schema load
```

When registry/provider version changes:

- loaded-schema cache must detect invalidation;
- stale schema must not remain callable under a new implementation version without re-resolution.

Reuse `DeferredToolDiscovery` and its registry-version behavior.

---

## 24. Update behavior

Updates should not occur merely because a newer version exists.

Separate:

```text
Discover update
Approve/update policy
Download
Install
Activate
```

Possible policy modes may include:

```text
manual only
trusted official auto-download but manual activate
trusted official auto-update at idle boundary
organization-managed
developer/local mode
```

Exact production policy can be finalized later.

Phase 11 must at least preserve the architectural separation.

---

## 25. Offline behavior

Remote catalog access must not be required to execute already-installed capabilities.

If network/catalog is unavailable:

```text
installed tools still work
installed skills still work
active provider/local helper still works if locally available
ToolRegistry still works
local InstalledCapabilityIndex still works
```

Only these functions degrade:

- discovering new remote plugins;
- checking for updates;
- downloading packages.

Catalog outage is not Agent outage.

---

## 26. Recommended Phase 11 refinement tasks

Do not rewind the tracker.

After the current V2-1101 work is stable, add/refine tasks within Phase 11 before Phase 12.

Suggested task set:

### V2-1116 — Add unified skill-source abstraction and canonical skill envelope

Acceptance:

- built-in and plugin skills can be searched through one logical interface;
- discovery uses at least `name + description + provenance`, not exact-name matching;
- supports progressive `SKILL.md -> references/scripts/assets` loading;
- source/provenance preserved;
- current built-in skill tests still pass;
- plugin skill hash/version behavior preserved;
- deterministic test proves a differently named skill can be selected from its description.

### V2-1117 — Add InstalledCapabilityIndex + AvailableCapabilityIndex

Acceptance:

- installed index rebuilds from active built-ins/plugins/providers;
- available index rebuilds from compact cached catalog metadata;
- contains tool/skill/provider provenance;
- installed search requires no remote request;
- available index does not store full SKILL.md/reference/tool-schema payloads;
- deterministic rebuild/search tests.

### V2-1118 — Add CatalogSource + CatalogSourceManager contracts

Acceptance:

- multiple deterministic local/test sources;
- priority;
- trust metadata;
- conflict detection;
- unavailable source does not erase installed capability state.

### V2-1119 — Add CapabilityResolver and semantic catalog candidate search

Acceptance:

- resolves installed capability first;
- skill selection is description-aware and not exact-name dependent;
- can return cached remote catalog candidates;
- large-catalog path supports lexical + semantic candidate reduction before model rerank;
- may refresh catalog metadata when cached metadata is absent/stale;
- does not download package bytes just to search metadata;
- does not install without host policy;
- does not expose unselected package content directly to model.

### V2-1120 — Add TaskCapabilitySnapshot and safe refresh boundaries

Acceptance:

- task pins plugin/tool/skill/provider identity;
- automatic update cannot swap versions during in-flight call;
- explicit task-driven install produces a traceable snapshot revision;
- evidence records exact versions/hashes.

### V2-1121 — Add package-retriever abstraction and deterministic staged retrieval tests

Acceptance:

- local fixture retriever is enough initially;
- hash/download-size/cancel behavior tested;
- downloaded package still goes through existing PluginManager verification;
- no direct code execution from catalog source.

### V2-1122 — Add end-to-end missing-capability continuation test

Scenario:

```text
task requests:
"audit AutoCAD dynamic block parameters/actions"

installed skill names do not match that phrase
 -> installed description-based search insufficient
 -> cached remote catalog finds differently named candidate by metadata/semantic match
 -> if metadata is stale/missing, refresh configured catalog metadata
 -> policy approves fixture package
 -> PackageRetriever fetches immutable package
 -> PluginManager verifies + installs
 -> InstalledCapabilityIndex / ToolRegistry refresh at safe boundary
 -> selected SKILL.md loads
 -> only required reference/resource loads
 -> task resumes without user repeating request
 -> verifier passes
 -> task evidence records package/skill/tool/provider versions
```

This is the key acceptance scenario proving the architecture.

---

## 27. Relationship to existing Phase 11 tasks

Do not delay the current V2-1101 work solely to implement this document.

Recommended order:

```text
finish current V2-1101
 -> continue core UI/orchestrator Phase 11 work
 -> integrate refinement tasks before Phase 12 gate
```

The most closely related existing task is:

```text
V2-1114 — Integrate dynamic provider/plugin capability refresh into AgentOrchestrator
```

V2-1114 should be expanded/reconciled with:

- unified skill source;
- installed capability index;
- capability resolver;
- task capability snapshot;
- controlled refresh boundaries.

Do not reduce V2-1114 to only "reload ToolRegistry after provider restart."

---

## 28. Relationship to Phase 12

Phase 12 acceptance must not consider extensibility complete unless the following is demonstrated:

1. installed tool discovery remains deferred;
2. installed skill discovery remains deferred;
3. built-in and plugin skills share one logical search path;
4. skill discovery is driven by bounded `name + description` metadata and does not require exact-name matching;
5. selected skill loading follows `metadata -> SKILL.md -> on-demand references/scripts/assets`;
6. large remote-catalog discovery can reduce candidates lexically/semantically before model rerank;
7. catalog source is configurable and not hard-coded to one GitHub repo;
8. multiple catalog sources can coexist;
9. plugin update retains rollback/quarantine safety;
10. task execution pins capability versions/hashes;
11. no update mutates an in-flight tool call;
12. missing capability can be discovered and safely installed through host policy;
13. package bytes are downloaded only after candidate selection/policy;
14. task resumes after controlled capability installation;
15. offline installed capability execution still works;
16. task evidence records exact plugin/skill/tool/provider versions.

These should become part of V2-1201/V2-1203/V2-1205/V2-1207 acceptance coverage where appropriate.

---

## 29. Security invariants

The following are mandatory:

- remote catalog discovery is not trust;
- catalog metadata is not executable permission;
- model cannot bypass host install policy;
- package hash validation remains mandatory;
- broader update permissions require new approval/policy;
- native helper/lifecycle hook restrictions remain enforced;
- no ZIP/path traversal;
- no tool-name conflict silently overriding an existing trusted tool;
- no secret values exposed to model/catalog metadata;
- no full remote skill/tool content loaded unless selected/installed;
- no registry mutation during active executor call.

---

## 30. Implementation reuse map

### REUSE AS-IS OR EXTEND

```text
ToolRegistry
DeferredToolDiscovery
ToolSearchIndex
PluginManager
H2PluginManifest
PluginCatalog metadata model
PluginSkillCatalog
DeferredSkillSession
CapabilityProvider contracts
McpToolProvider
McpToolRegistryAdapter
provider provenance
plugin rollback/quarantine
plugin self-test
permission delta validation
registry versioning
```

### ADD / REFINE

```text
ISkillSource
UnifiedSkillCatalog / SkillSearchIndex
canonical name+description skill discovery metadata
progressive SKILL.md/references/scripts/assets loader
InstalledCapabilityIndex
AvailableCapabilityIndex
ICatalogSource
CatalogSourceManager
lexical/semantic available-catalog search
CapabilityResolver
TaskCapabilitySnapshot
IPackageRetriever
safe task-level capability refresh protocol
missing-capability install-and-resume flow
```

### FUTURE / DISTRIBUTION REPOSITORY

```text
H2-Agent-Catalog GitHub repository
production GitHub catalog publisher
public/private catalog hosting
plugin-store UI
stable/beta/dev release channels
publisher signing infrastructure
organization catalog management
```

Do not confuse FUTURE distribution work with the runtime abstractions required now.

---

## 31. Core architectural rule

The final Agent architecture should satisfy:

> **The Agent asks for a capability, not for a hard-coded implementation source.**

And:

> **Tool/skill/plugin/provider origin is host-managed provenance, not model-side special-case logic.**

The target flow is:

```text
User goal
   |
   v
AgentOrchestrator
   |
   v
CapabilityResolver
   |
   +-- installed tool/skill/provider sufficient
   |       |
   |       v
   |    execute
   |
   +-- insufficient
           |
           v
       catalog search
           |
           v
       host policy
           |
           v
       package retrieval
           |
           v
       existing PluginManager
           |
           v
       safe capability refresh
           |
           v
       continue original task
```

This refinement should be completed before Phase 12 acceptance, without discarding the successful Phase 10 work.

---

## 32. Codex skill-bundle design findings adopted by H2

The inspected Codex skill bundle reinforces these H2 decisions:

### Adopt

- `SKILL.md` as the skill entry point.
- Cheap pre-load discovery metadata centered on `name + description`.
- Description-based semantic routing rather than exact skill-name matching.
- Progressive disclosure.
- `references/` loaded only when relevant.
- `scripts/` as optional deterministic helpers, still gated by H2 execution permissions.
- `assets/` kept outside prompt context until needed.
- Optional separate interface/invocation metadata.
- Local/repository skill sources.
- Ability for a skill/package to exist primarily as guidance without requiring a new native tool.

### Keep H2 stronger than the inspected bundle

Do not give up existing H2 safety/versioning features:

- versioned plugin store;
- package SHA-256 verification;
- staged install;
- self-test;
- permission delta checks;
- rollback;
- quarantine;
- provider/tool provenance;
- task capability version pinning;
- multiple catalog sources;
- offline installed capability use.

### Do not copy blindly

H2 should not:

- require exact skill-name matching;
- execute a skill directly from a remote repository branch;
- overwrite an active package in place;
- make GitHub a runtime dependency;
- load all SKILL.md/reference content during discovery;
- treat scripts bundled with a skill as automatically trusted executable code.

The target is:

```text
Codex-style skill authoring/disclosure
+
H2 PluginManager safety/versioning
+
H2 CapabilityResolver
+
multi-source catalog
+
semantic discovery for large catalogs
```

