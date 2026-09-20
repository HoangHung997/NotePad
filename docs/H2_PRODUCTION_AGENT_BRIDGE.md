# H2M-093 — Production H2 ↔ Agent bridge

Status: implementation candidate; close only after exact-head CI is green.

## Production composition

H2 Notes now composes one concrete `H2ProductionAgentAdapter` from the H2 Agent assembly at the Avalonia composition root.

H2 product/UI code continues to depend on `IH2AgentAdapter`. Runtime, transport, ToolRegistry and provider internals stay inside the Agent assembly.

The production adapter resolves the current H2 AI profile/API key lazily per task from machine-local settings/vault and executes through:

`H2 UI -> IH2AgentAdapter -> H2ProductionAgentAdapter -> AgentOrchestrator -> AgentRuntime`

Project chat and Work Assistant both use this same adapter instance.

## Unscoped quick work

`StartTaskAsync(ProjectId = null)` is supported directly by the production adapter. A bounded workspace/context snapshot is carried with the task, but no fake project is created.

A completed or running unscoped task may later receive a ProjectId correlation via `AttachProject`. Attachment updates correlation only; no Agent summary/evidence/trace is copied into `ProjectRecord`.

## Durable task/evidence projection

The Agent assembly owns a bounded machine-local integration archive under its state root:

`agent-runtime/integration/recent-tasks-v1.json`

It stores only the bounded H2-facing task summary and evidence references needed to rehydrate recent/terminal task projections after restart. It is not shared NAS project state and is not an H2 project database.

On load, any non-terminal record left by a previous crashed/exited process is projected as failed/interrupted rather than falsely shown as still running.

The archive is capped at 200 recent records and 4 MB on read. Correlation attachment and evidence lookup survive adapter restart.

## Runtime and safety

Each task receives a host-owned `AgentTaskContract`, bounded `AgentContextInput`, host permissions and the accepted `AgentRuntime` completion/verification gate.

Read-only H2 tasks use read-only Agent tools. Mutation approval continues through the Agent tool permission/approval callback; a still-active host-issued scoped grant may satisfy a no-second-confirmation scope.

No `QuickWorkSession`, giant `ProjectState`, H2 Agent task database, or copied shared evidence store is introduced.

## Deterministic acceptance

`H2ProductionAgentBridgeTests` instantiate the concrete production adapter, but replace only the provider transport with a deterministic CI transport. The execution still traverses the real:

- deferred `tool_search`;
- `read_file` ToolRegistry execution;
- AgentRuntime continuation;
- evidence projection;
- host completion gate.

The acceptance also proves unscoped quick work, later project attachment without ProjectRecord mutation, durable task/evidence queries after adapter restart, and product-source architecture guards.
