# H11 Product Acceptance — H2M-110..116

Status: deterministic/product acceptance complete for H11. Physical two-PC/NAS acceptance remains explicitly deferred to H2M-133.

## Exact CI evidence

Functional source: `09f58804e4029e8947e4ee136c66b607a15c450d`

GitHub Actions run: `35548442975` — SUCCESS

H2 Notes: **480 passed, 0 failed**

Dedicated H11 scenarios:

- H2M-110 PASS — real Avalonia Command Center UI answers attention / working / next / Agent-waiting / sync-health within the automated 5-second budget.
- H2M-111 PASS — existing Project AI UI -> concrete `H2ProductionAgentAdapter` -> accepted AgentRuntime, with project association, progress/evidence projection and no execution-plan pollution of H2 checklist.
- H2M-112 PASS — foreground Excel Work Assistant runs with no MainWindow, preserves workbook/session context, uses structured Excel tools, verifies mutation, remains ProjectId-optional and collapses to bubble.
- H2M-113 PASS — unsaved Word session uses structured Word + authoritative Web evidence fixture, scoped mutation and verifier evidence, with zero pixel fallback.
- H2M-114 PASS — AutoCAD selected block path uses structured query/read/bounded mutation/verify sequence and does not target unrelated entities.
- H2M-115 PASS — legacy tasks, notes, multiple AI conversations, attachments, saved-file history and portable layout survive migration; new Agent task executes through the concrete bridge afterward.
- H2M-116 PASS — deterministic two-PC projection with one shared ProjectWorkspaceStore root plus distinct machine-local Agent/layout roots keeps project truth consistent and prevents PC2 from projecting PC1's local running Agent task.

Full NAS harness + Agent MB-10..121 + Office/Web/Desktop/AutoCAD/MCP/plugin/provider/transport + Windows publish pipeline also passed.

Published portable artifact source: `09f58804e4029e8947e4ee136c66b607a15c450d`

Publish metadata commit: `d44712d2782d6d6217df138275cddb464dec1d46`

Portable artifact:

- bytes: `110145006`
- SHA256: `8955382513c34e265737da5b3b4e1fd38e6a6459ba79dd7f9864a381af2dc86e`
- delivery: GitHub Actions artifact only because the ZIP is too close to GitHub's 100 MB repository blob limit.

## Limitation carried forward

`DEFERRED_REAL_NAS`

H2M-116 does **not** claim that a real pair of physical PCs has been exercised yet. The user explicitly deferred physical two-PC testing until the application is more complete.

Therefore H2M-133 must reopen and require the real NAS/two-PC data-integrity run before final production acceptance. H11 only proves deterministic/local multi-PC ownership and projection behavior.
