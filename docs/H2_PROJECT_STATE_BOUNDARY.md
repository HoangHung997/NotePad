# H2 — Projection-Only Project State Boundary

Status: **H2M-041 architecture rule**  
Date: **2026-09-20**

## Rule

H2 must not introduce a second durable `ProjectState` database that copies information already owned by:

- `ProjectRecord` / `TaskRecord`;
- Agent task / evidence / verification state;
- workspace/sync state.

## Command Center data path

```text
ProjectRecord / TaskRecord
          +
IH2AgentAdapter
          +
workspace health
          |
          v
H2ProductProjectionService
          |
          v
H2CommandCenterQueryService
          |
          v
future Command Center UI
```

The query service owns no durable project truth and exposes only rebuildable projection lists.

## Forbidden design

Do not add a durable object such as:

```text
ProjectState
{
    Progress
    NextActions[]
    AgentRuns[]
    Evidence[]
    Risks[]
    Decisions[]
    Files[]
    ...
}
```

merely to make the UI easier to render.

If a value can be reconstructed from authoritative sources, reconstruct it.

## Allowed state

Durable product truth remains in the existing project/task/note models.

Agent execution truth remains behind `IH2AgentAdapter`.

Machine-local UI state remains local configuration/session state.

Derived Command Center cards, attention rows and activity rows are disposable query results.

## Guard

`H2ProjectStateArchitectureTests` fails if:

- a runtime `class/record/struct ProjectState` appears under `src/`;
- Command Center query service gains hidden mutable/durable state;
- Command Center query source starts writing files/databases;
- rebuild through a fresh query/projection service differs from the same authoritative inputs;
- a query mutates ProjectRecord.
