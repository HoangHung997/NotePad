# AR-033 — whole-goal completion and verified alternate recovery

Status: IMPLEMENTED / AWAITING_ENVIRONMENT. E1/E2 passed on the exact source recorded below; required native E3 remains NOT_RUN. AR-033 is not DONE.

Extends the existing AgentRuntime report/attempt assessment and verifier router. A run-local assessment indexes actual invocations and verifier reports; it is not a second engine, journal or database. All active source-backed obligations and required verifier identities remain required. An unrelated verifier/target cannot erase earlier verification debt. Mutation execution and proof are tracked separately. Running/unknown/partial effects remain blockers; no resume/replay is introduced.

Host-only per-call coverage binds a criterion, target, postcondition, invocation and observed evidence. Explicit alternate resolutions must match both observed calls and current revision; strings in tool JSON or model prose are not resolutions. Exact ordinary retries retain the old argument/discovery checks and are evaluated after verification. Unverifiable work is blocked or labelled unverified, never converted to Verified. The generic router proves mechanical readback only, not arbitrary task semantics.

The final gate rechecks referenced ArtifactStore handles for current hashes; it never opens an arbitrary historical path. Typed completion/resolution projections use the existing Agent task summary/archive and progress renderer; no ProjectRecord field or UI layout change. Superseded/waived user criteria do not remove uncertain side effects.

E1 tests cover cross-verifier, per-target, missing/conflicting proof and invalid alternate cases. E2 cases use production Global/Project adapters, actual local file tools/readback and archive roundtrip, with an explicitly scripted transport and labelled criterion verifier. These cases and retained AR corpora have been executed as recorded below. They are not native Office/model/UI evidence; real app/provider RC-11/13/14 still require E3. E3/E4 NOT_RUN; AR-020 native E3 pending; AR-083 DEFERRED_BY_USER.

## Reviewed implementation checkpoint — native gate remains open

Runtime/test repair `84acbc456026ace4f20d435aed294d7b5099b2c7`; focused checkout `f5ede2d0e24896be3bd329ea8cce34e20d7f5ee1`; full checkout `7299e90178ad286082c4a5e0e5e334c9e63daaa8`. [Exact evidence](acceptance.json). AR-033: **42/42 in three repetitions**, all retained AR corpora, **74/74 Agent suites** and **858 H2 tests / zero failures**. All full-CI steps including Windows publish and helper startup/IPC pass. Build warnings remain 35.

The additional review found that a Failed/NotVerified verifier summary could be overwritten by all-Passed per-call details. Reports now reject that contradiction before accepting proof. The unchanged tests against the prior assessment produce 36 passes / 6 expected failures, including two concrete false-completion cases; current source produces 42/42. A mixed-target failed batch and its exact corrective write remain valid. Initial control-harness failure due to Windows checkout line endings is recorded separately, not relabelled as a successful negative test.

Only the existing assessment/runtime, goal state, verifier and Agent archive remain authorities. Per-target corrective proof cannot erase unrelated failures or waive Unknown/Running effects. Model text and historical evidence do not grant permission or verify current work. E2 uses scripted transport and a labelled injected verifier with real disposable file tools; it is not unrestricted native semantic verification.

**Not DONE:** AR-033 requires E2/E3. Real app/provider RC-11/13/14 remain AWAITING_ENVIRONMENT; E4 NOT_RUN and AR-083 DEFERRED_BY_USER. No personal documents, real model calls or new endpoints/credentials were used. The implementation is ready for independent downstream core work under tracker section 2.2.

On the existing branch and PR #3, reconcile current refs, working tree and SESSION HANDOFF. AR-033 implementation has E2 evidence but remains AWAITING_ENVIRONMENT for native E3. Start only independent AR-040 (dependencies AR-011/031 accepted): extend the existing process service with stable job IDs, bounded poll/output/cancel and actual owned-process tests RC-19/20/21; do not add a second engine/store or replay uncertain writes. Keep AR-033/020 native gates and AR-083 deferred; retain AR-033/032/031/030/020/012/011/010/001 regression.

One-use AR-033 source/repair/checkpoint writers were removed in `3a3d30c5f43c8a79605639d62b54a0bb7fea428d`. The current consistency regression workflow is read-only; the older read-only proof-control workflow remains historical and pins its own source explicitly. No application/test source changed after the validated repair. Later documentation/workflow-triggered CI is separate and is not assumed to pass without inspecting its result.
