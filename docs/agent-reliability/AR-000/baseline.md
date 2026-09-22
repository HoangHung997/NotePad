# AR-000 — actual baseline / traceability

Status: **E0 BASELINE ACCEPTED WITH CI RED**. Not a new specification and not product acceptance.

Code: `1283bc13e07c3cd47d04886166de3dfc595422c0`. Capture checkout: `0ed36c1b56ffeeb78cfe1447db2eacee545a4294`. Branch: `feature/h2-agent-reliability-ar-000`. PR: #3.
[Capture run](https://github.com/HoangHung997/NotePad/actions/runs/35689629807) · [Baseline CI](https://github.com/HoangHung997/NotePad/actions/runs/35687637186) · [Manifest](baseline.json).
Only AR-000 is closed; the canonical [SESSION HANDOFF](../../H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md) starts AR-001 as NOT_STARTED.

## Ownership and non-overwrite checks

Remote was inspected twice: only main, no open PR or active AR owner. Branch was created from current main, not a research SHA.
The capture refuses dirty worktrees, changed master/tracker blobs, advanced main, unexpected branch changes and any runtime/test diff.
No user-PC working tree is accessible. The assistant container had no checkout, dotnet or PowerShell; no local .NET PASS is claimed.
The CI checkout is the available executable repository environment. Script/workflow are AR-000 documentation tooling only.
All existing Master/README bytes are retained as a prefix; only AR cross-references are appended. Historical checkpoints and checked tasks are not re-awarded.

## Current call graph (E0 source map, not a new execution trace)

```text
Project AiChatPanel.Agent / Global WorkAssistant
 -> H2 application composition -> same IH2AgentAdapter / H2ProductionAgentAdapter
 -> SnapshotContext + custom AgentContextInput
 -> AgentOrchestrator -> AgentRuntimeFactory -> AgentRuntime
 -> AgentTransportFactory -> selected configured provider transport
 -> deferred NormalRuntimeToolRegistry + H2ProductionToolSession
 -> host permission/resource validation -> concrete domain executor
 -> domain verifier/router + bounded evidence/artifacts
 -> Agent-owned task/thread archive -> H2 UI projections

LabWindow -> AgentOrchestratedRun
 -> LabSessionContextAdapter + RuntimeCompactionCoordinator
 -> same AgentOrchestrator / AgentRuntimeFactory / AgentRuntime primitives
 -> transport -> registry/scheduler -> verifier/repair
 -> LabSession + ArtifactStore (not ProjectRecord)
```

Shared runtime does **not** yet prove identical context, compaction or all-request payload budgeting: B08 remains open.
Coordinator owns shared H2 event order/queue/lease/barriers; it is not an Agent engine or evidence database.
Production Coordinator cutover/authentication and physical acceptance remain open in H2M-133. No queue/storage runtime changes were made.

## Ten SPEC section 2.2 observations rechecked on current code

Every row has exact file/blob/line/needle anchors in the manifest. A matched observation is E0 evidence, not a passing regression for the defective behavior.

| ID | Current observation | Owning AR task(s) |
|---|---|---|
| B01 | Production StablePrefix requests search_skills; the canonical callable is list_skills. | AR-001 |
| B02 | Excel schema and adapter permit 200 patches; COM backend rejects above 128. No boundary fix is included here. | AR-001 |
| B03 | Registry exposes read_tool_output but the exact-name architecture guard omits it; existing CI fails at this guard. | AR-001 |
| B04 | Excel read operations fall back to a whole-session snapshot, not explicit range paging; cumulative UsedRange exceeds a 5000-cell bound. | AR-021 |
| B05 | Excel token basis includes active sheet and selection; Word token basis includes selection. UI movement and content revision are not separated. | AR-020, AR-021 |
| B06 | Excel validates each cell address inside the mutating loop. A later invalid cell can follow an earlier applied write; no destructive live reproduction was performed. | AR-022, AR-041 |
| B07 | Foreground Office enrichment has a three-second timeout and returns null on selected failures; execution OfficeHost timeout is sixty seconds. | AR-011, AR-020 |
| B08 | Lab uses LabSessionContextAdapter and RuntimeCompactionCoordinator; H2 builds a separate AgentContextInput and snapshots the last 32 turns. Shared primitives do not prove equal production compaction/full-wire budgeting. | AR-010, AR-050, AR-051, AR-052 |
| B09 | Recent-task archive Load converts interrupted nonterminal records to Failed. Durable summaries/replay are not proof of resumable in-flight work or uncertain-write reconciliation. | AR-031, AR-040, AR-041, AR-052 |
| B10 | Production registers HTTP fetch/download/extract/metadata/feed only. Search and a real browser backend are deliberately not advertised without configuration. | AR-060 |

## Capability and execution inventory

| Surface | Present source / available execution | Acceptance boundary |
|---|---|---|
| Global and Project | Same production adapter; nullable ProjectId, exact target paths, scoped permission checks | Existing concrete bridge tests use scripted transport; not E4 |
| Core files / evidence / skills | Canonical registry, deferred discovery, read_tool_output and progressive skill reader | E1/E2 fixtures are not native document acceptance; B01/B03 open |
| Live Word / Excel | Concrete isolated OfficeHost COM backend and readback verifier; no fixture backend constructed by Domains | Helper build is not installed Office or live workbook proof; E3/E4 NOT_RUN |
| Closed-file Python/artifacts | Registered core tools for copied input, inspection and publication | No new run or personal document was used; current packaged capability must be preflighted |
| Shell/process | Existing local command capability; no durable-job acceptance asserted | AR-040/041 remain; no command/model executed by this audit |
| Desktop | Existing selected-window/DesktopHost path and verifier | Actual target/permission/capture needed; E3/E4 NOT_RUN |
| Web | Concrete fetch/download/extract/metadata/feed wiring | Real search/browser not configured in composition; B10 / AR-060 |
| CAD | Closed-file tools conditionally registered only with FullAccess and detected executable | No blanket live-CAD readiness; E3/E4 NOT_RUN |
| Plugins/MCP | Existing extension/provider architecture and historical fixture tests | No new provider, endpoint, credential, package install or native lifecycle acceptance |
| Shared storage/queue | Existing Coordinator contracts/store; H2M-133F and production cutover debts remain | AR-083 DEFERRED_BY_USER; local NAS self-test is not E5 |

User model/profile, Office installation, CAD installation and real UI session cannot be inspected here. They were not changed.
No paid model call, new endpoint, outbound user message or destructive personal-data test was performed. One-PC Office/model acceptance is still mandatory.

## Actual CI baseline — not green

Run `35687637186`, code `1283bc13e07c3cd47d04886166de3dfc595422c0`: solution build succeeds (31 warnings, 0 errors); H2 tests **590/590**;
DesktopHost safety **4/4**; Agent v1 **18/18**; architecture guard **34 passed, 1 failed**.
Counts were read from decoded job `106617792240`; step conclusions and zero artifacts are re-read from GitHub API during capture.
Failure: `Canonical normal runtime registry deleted or invented callable tools.`
Later Agent/Office/transport/MB suites and publish were skipped. NAS artifact upload fails because no package exists;
a portable upload step reporting success with no files is not publish success. Artifact inventory is **0**.
The local NAS protocol self-test passes only its fixture protocol, not the user's mixed transport topology.
AR-001 owns B01/B02/B03, any necessary CI restoration and nonempty publish smoke; no guard was disabled here.

## First AR-001 action and retained debt

On existing branch feature/h2-agent-reliability-ar-000, first inspect git status --short --branch and fetch origin without reset; reconcile newer main/checkpoint/PR work, then run the v2 guard command recorded in this manifest to reproduce B03. Execute AR-001 only: canonical skill name, read_tool_output exact-set guard, shared Excel 128/129/200 verdict, RC-02 and full CI/publish smoke. Do not start paging/memory or create a duplicate branch.

```powershell
dotnet restore H2Notes.Avalonia.slnx
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build
dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --v2-guard-test .artifacts/agent-reliability/AR-001/guard
```

Use the existing full Avalonia CI after fixes. Check exact checkout SHA, every required suite and actual nonempty publish/smoke evidence.
Run RC-02, canonical name/registry tests, large-output/foreign-handle cases, and 128/129/200-cell consistent preflight verdicts without truncation.
MB-124–127 stay open. AR-083 stays DEFERRED_BY_USER and blocks certified multi-PC claims, not independent one-PC implementation.

## Source owners

- `App.axaml.cs`: `src/H2Notes.Avalonia/App.axaml.cs`
- `AiChatPanel.Agent.cs`: `src/H2Notes.Avalonia/Controls/AiChatPanel.Agent.cs`
- `WorkAssistantCompactWindow.Conversation.cs`: `src/H2Notes.Avalonia/WorkAssistantCompactWindow.Conversation.cs`
- `LabWindow.cs`: `experiments/H2AgentLab/LabWindow.cs`
- `AgentRuntimeFactory.cs`: `experiments/H2AgentLab/Runtime/AgentRuntimeFactory.cs`
- `AgentRuntime.cs`: `experiments/H2AgentLab/Runtime/AgentRuntime.cs`
- `AgentOrchestrator.cs`: `experiments/H2AgentLab/Tasking/AgentOrchestrator.cs`
- `AgentTransportFactory.cs`: `experiments/H2AgentLab/Transport/AgentTransportFactory.cs`
- `H2ProductionToolSession.cs`: `experiments/H2AgentLab/Integration/H2ProductionToolSession.cs`
- `H2ProductionToolSession.Desktop.cs`: `experiments/H2AgentLab/Integration/H2ProductionToolSession.Desktop.cs`
- `AgentRuntimeDomainVerification.cs`: `experiments/H2AgentLab/Runtime/AgentRuntimeDomainVerification.cs`
- `ArtifactStore.cs`: `experiments/H2AgentLab/Session/ArtifactStore.cs`
- `RuntimeCompactionCoordinator.cs`: `experiments/H2AgentLab/Session/RuntimeCompactionCoordinator.cs`
- `H2ProductionAgentBridgeTests.cs`: `tests/H2Notes.Tests/H2ProductionAgentBridgeTests.cs`
- `H2SyncCoordinatorContracts.cs`: `src/H2Notes.Core/H2SyncCoordinatorContracts.cs`
- `H2CoordinatorSqliteStore.cs`: `src/H2Notes.Coordinator/H2CoordinatorSqliteStore.cs`

This report records one baseline; the AR tracker alone is the active execution checkpoint.
