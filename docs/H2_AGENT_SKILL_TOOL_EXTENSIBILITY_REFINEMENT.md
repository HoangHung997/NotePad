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

## 7. Add an InstalledCapabilityIndex

The Agent should not repeatedly inspect plugin folders or query remote sources for normal tool/skill discovery.

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

The index must be rebuildable from installed/active packages and providers.

It must not become a second authoritative store that can drift permanently from the active package state.

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

## 20. Package retrieval must be a host service

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

## 21. Skill search remains progressive

Never inject every installed skill into every prompt.

Required flow:

```text
compact installed skill metadata
 -> skill search
 -> select relevant skill
 -> load exact SKILL.md
 -> cache by source/plugin/version/hash
 -> do not reload if unchanged
```

If plugin update changes the skill hash:

```text
old cache identity != new cache identity
 -> invalidate
 -> load new content only when selected
```

Existing `DeferredSkillSession` behavior should be reused where possible.

---

## 22. Tool search remains progressive

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

## 23. Update behavior

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

## 24. Offline behavior

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

## 25. Recommended Phase 11 refinement tasks

Do not rewind the tracker.

After the current V2-1101 work is stable, add/refine tasks within Phase 11 before Phase 12.

Suggested task set:

### V2-1116 — Add unified skill-source abstraction

Acceptance:

- built-in and plugin skills can be searched through one logical interface;
- source/provenance preserved;
- current built-in skill tests still pass;
- plugin skill hash/version behavior preserved.

### V2-1117 — Add InstalledCapabilityIndex

Acceptance:

- rebuild from active built-ins/plugins/providers;
- contains tool/skill/provider provenance;
- no remote request required for installed search;
- deterministic rebuild test.

### V2-1118 — Add CatalogSource + CatalogSourceManager contracts

Acceptance:

- multiple deterministic local/test sources;
- priority;
- trust metadata;
- conflict detection;
- unavailable source does not erase installed capability state.

### V2-1119 — Add CapabilityResolver

Acceptance:

- resolves installed capability first;
- can return missing-capability/catalog candidates;
- does not install without host policy;
- does not expose package content directly to model.

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
task requests specialized capability
 -> installed search insufficient
 -> catalog candidate discovered
 -> policy approves fixture package
 -> package retrieved
 -> PluginManager installs
 -> ToolRegistry refreshes at safe boundary
 -> skill loads progressively
 -> task resumes
 -> verifier passes
 -> task evidence records package/skill/tool versions
```

This is the key acceptance scenario proving the architecture.

---

## 26. Relationship to existing Phase 11 tasks

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

## 27. Relationship to Phase 12

Phase 12 acceptance must not consider extensibility complete unless the following is demonstrated:

1. installed tool discovery remains deferred;
2. installed skill discovery remains deferred;
3. built-in and plugin skills share one logical search path;
4. catalog source is configurable and not hard-coded to one GitHub repo;
5. multiple catalog sources can coexist;
6. plugin update retains rollback/quarantine safety;
7. task execution pins capability versions/hashes;
8. no update mutates an in-flight tool call;
9. missing capability can be discovered and safely installed through host policy;
10. task resumes after controlled capability installation;
11. offline installed capability execution still works;
12. task evidence records exact plugin/skill/tool/provider versions.

These should become part of V2-1201/V2-1203/V2-1205/V2-1207 acceptance coverage where appropriate.

---

## 28. Security invariants

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

## 29. Implementation reuse map

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
InstalledCapabilityIndex
ICatalogSource
CatalogSourceManager
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

## 30. Core architectural rule

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
