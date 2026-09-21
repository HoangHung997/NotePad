# H12 Legacy Cleanup — H2M-120..124

Status: implementation candidate; close only after exact-head CI is green.

## H2M-120 — AiProjectContext execution dependency

The retired `AiProjectContext` type/file remains absent.

New project Agent requests are grounded only through bounded `H2AgentTaskContext` in `AiChatPanel.Agent.cs`. The project Agent path must not reference `AiLegacyRequestContext`, `AiClient`, `SecretVault` or `AiProjectActions`.

`AiLegacyRequestContext` remains narrowly named compatibility code for standalone/direct legacy chat, PDF/OCR request preparation and historical tests. It is not part of the project Agent path.

## H2M-121 — AiProjectActions pseudo-action execution

Legacy ```h2-actions``` blocks are historical conversation data only.

The project chat renderer may parse them to display an old proposal/audit, but it no longer:

- validates the proposal for execution;
- opens an apply/confirmation dialog;
- calls `AiProjectActions.Apply`;
- emits `ProjectActionsRequested`;
- auto-applies model text under any permission mode.

Standalone direct-AI prompt construction no longer includes `AiProjectActions.Instructions`, so the remaining compatibility model path is not asked to generate project pseudo-actions.

New project mutation authority is exclusively the typed `IH2ProjectToolHost` surface behind the Agent adapter.

## H2M-122 — direct project AiClient runtime

`AiChatPanel.Send()` retains the hard project-first router:

`project scope -> SendProjectAgent() -> IH2AgentAdapter`

The legacy `SendLegacy()` path is standalone/notebook compatibility only and fails closed on project scope before creating an `AiClient`.

Lower-level provider/client code remains because standalone old chat, settings/model discovery and document compatibility still use it.

## H2M-123 — ProjectAiWindow decision

Decision: **REUSE as detached Agent workspace**.

The existing detached window is useful because it moves the exact same `AiChatPanel _chat` between Project Workspace and a separate desktop window. It does not create a second chat session, Agent adapter, Agent runtime or task store.

User-facing labels now call this surface **Agent dự án** / detached Agent workspace rather than presenting it as a second legacy project-AI runtime.

## H2M-124 — obsolete future-design documents

Historical evidence is preserved; no design-history files are deleted.

- `H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md` is explicitly marked SUPERSEDED / HISTORICAL.
- `APPROVED_PRODUCT_SPEC.md` is explicitly marked HISTORICAL / CURRENT-IMPLEMENTATION BASELINE.
- `H2_PRODUCT_CURRENT_BASELINE.md` is explicitly marked HISTORICAL / CURRENT-IMPLEMENTATION EVIDENCE / NOT FUTURE ARCHITECTURE.

Canonical future H2 product authority remains:

1. `docs/H2_PRODUCT_MASTER_SPEC.md`
2. `docs/H2_PRODUCT_MASTER_TASKS.md`
3. independent data/NAS defect authority: `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

Agent master documents remain independent Agent authority and are not superseded by this cleanup.

## Acceptance guard

`H2LegacyCleanupTests` enforces H2M-120..124 at source/runtime level, while `ProjectActionUiTests` proves that old h2-actions history remains viewable but non-executable.
