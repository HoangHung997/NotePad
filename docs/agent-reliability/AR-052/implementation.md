# AR-052 — Model/context rebase from canonical Agent state

Validated code: `a2263e47a804d2f9f386296b1330b8a1c4231e2b`.

## Implemented behavior

- Resume keeps the original TaskId and durable thread/history.
- Every resume uses a fresh TurnId and fresh provider start request; provider-specific continuation/response IDs are never transferred between backends.
- Canonical AgentGoalState is restored from durable goal/evidence snapshots, preserving verified outcomes and mutation revision history.
- AR-041 unresolved effects block before model/provider allocation.
- Mutation resume requires a fresh active permission scope; expired or revoked grants are not restored.
- Explicit model/provider selection is honored per resumed turn. Provider failure never causes silent fallback; an explicit later rebase uses another fresh turn on the same TaskId.
- Scoped history exposes bounded durable locators for earlier exact facts, without turning historical text into current authority or verification.
- Durable steering receipts acknowledge reconnects without applying the same correction twice.
- Old transcript text is not replayed into the new provider request.

## Validation

Exact source `a2263e47a804d2f9f386296b1330b8a1c4231e2b`.

- AR-052 focused: 8/8.
- retained AR-041: 6/6.
- retained AR-042: 6/6.
- retained AR-051: 37/37.
- retained AR-031: 39/39.
- retained AR-050: 50/50.
- full H2: 1331/1331.
- required Agent suites: 75/75.
- dedicated run 36237795203 / job 108392677499: SUCCESS.
- full Avalonia CI 36237795173 / job 108392643192: SUCCESS.
- all 19/19 workflow identities on the exact SHA: SUCCESS.
- Windows x64 self-contained publish and packaged DesktopHost/OfficeHost IPC: PASS.

## Acceptance boundary

E1/E2 are PASS. Real configured-model/provider E4 is DEFERRED_BY_USER / AWAITING_ENVIRONMENT until final-build testing. No E4 PASS, provider auto-fallback, opaque continuation reuse or mutation replay claim is made.
