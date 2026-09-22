# AR-033 — whole-goal completion and verified alternate recovery

Status: ACTIVE / NOT_RUN. This file describes source under validation, not successful tests.

Extends the existing AgentRuntime report/attempt assessment and verifier router. A run-local assessment indexes actual invocations and verifier reports; it is not a second engine, journal or database. All active source-backed obligations and required verifier identities remain required. An unrelated verifier/target cannot erase earlier verification debt. Mutation execution and proof are tracked separately. Running/unknown/partial effects remain blockers; no resume/replay is introduced.

Host-only per-call coverage binds a criterion, target, postcondition, invocation and observed evidence. Explicit alternate resolutions must match both observed calls and current revision; strings in tool JSON or model prose are not resolutions. Exact ordinary retries retain the old argument/discovery checks and are evaluated after verification. Unverifiable work is blocked or labelled unverified, never converted to Verified. The generic router proves mechanical readback only, not arbitrary task semantics.

The final gate rechecks referenced ArtifactStore handles for current hashes; it never opens an arbitrary historical path. Typed completion/resolution projections use the existing Agent task summary/archive and progress renderer; no ProjectRecord field or UI layout change. Superseded/waived user criteria do not remove uncertain side effects.

E1 tests define cross-verifier, per-target, missing/conflicting proof and invalid alternate cases. E2 cases use production Global/Project adapters, actual local file tools/readback and archive roundtrip, with a explicitly scripted transport and labelled criterion verifier. They are not native Office/model/UI evidence. RC-11/13/14 and the retained AR corpora must be executed on the exact saved SHA. E3/E4 NOT_RUN; AR-020 native E3 pending; AR-083 DEFERRED_BY_USER.
