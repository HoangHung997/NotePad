# H2M-100..105 — Product architecture guard

These tasks intentionally remove/defer duplicate product subsystems rather than adding new durable models.

## Guarded rules

- **H2M-100:** no runtime `ProjectState` aggregate and no ProjectRecord embedding Agent task/evidence/verification truth.
- **H2M-101:** no `AIInboxStore`; attention is rebuilt by `H2ProductProjectionService.BuildNeedsAttention`.
- **H2M-102:** no `ResearchStore`; research/evidence remains an Agent-evidence projection inspected through the existing evidence surface.
- **H2M-103:** no formal `ProjectDecision` subsystem; notes/pinned knowledge remain sufficient until separately approved.
- **H2M-104:** no `QuickWorkSession`; Work Assistant starts the same Agent adapter with `ProjectId = null` and may attach later.
- **H2M-105:** no durable/model-authored project percentage; overview uses deterministic `ProjectProgressCalculator` completed/total task counts.

`H2ProductArchitectureGuardTests` scans runtime source/type surfaces and fails if any forbidden subsystem appears or if projection views gain persistence responsibilities.
