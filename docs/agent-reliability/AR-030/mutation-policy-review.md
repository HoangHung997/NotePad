# AR-030 — independent host-policy repair

Status: **ACTIVE / REPAIR_REQUIRED, not DONE**. Only the first-mutation host-policy defect is repaired and tested. The separate incomplete/ambiguous evidence defect remains in AgentGoalState.

## Scope

Code `c9162ad7422b58dc8475d711e9b6fc3b2c3a1980` preserves every pre-existing required verifier, and keeps a predeclared mutation criterion's exact meaning/evidence instead of duplicating it. Three cases are registered through H2AgentGoalProposalBoundaryTests. No engine/store/permissions/UI redesign. AgentGoalState has not changed; the earlier safety-denied source write was not retried through a different encoding, endpoint or source-writing workflow.

## Actual execution

Focused run **35771775587** on `c9162ad7422b58dc8475d711e9b6fc3b2c3a1980`: **33/33 AR-030 x3**, including the three new policy cases. A one-class prior-contract control compiles and reproduces **0 passed / 3 expected failures**, then restores exact bytes/hash and a clean checkout. Existing extraction-only control also remains. Retained AR-020/012/011/010/001: **36/44/25/11/13**, and **74/74** Agent suites.

Full CI **35771775450** tested checkout `5953e9172139f03c955c55c11ef1d026edeef055` (GitHub test merge, not merged main), source equal to `c9162ad7422b58dc8475d711e9b6fc3b2c3a1980`: **752 H2 tests / 0 failures**, all mandatory steps, Windows publish and helper startup/IPC pass. This is not a claim that unresolved evidence handling is correct. E1 policy fixtures and E2 existing concrete/file-runtime regression are not E3/E4 native Office/model acceptance.

## Remaining work and authoritative handoff

Keep AR-030 ACTIVE/REPAIR_REQUIRED on the existing branch and PR #3. The mutation-policy fix is committed and tested; do not reapply the original five-file ZIP. Review the four-file H2_AR030_Remaining_Evidence_Repair patch against c9162ad7422b58dc8475d711e9b6fc3b2c3a1980. Resolve the outstanding source-write restriction before publishing the evidence-resolver change; do not bypass it via encoding, another endpoint or an in-CI source writer. Then compile, run the six evidence cases and prior-goal-state counterexamples, retain the three policy tests/RC-11/12/regressions and full CI on the actual new SHA. The current green CI is not evidence that missing/ambiguous references are fixed. Do not advance AR-031 or close AR-020/E3 or AR-083/E5.

The remaining local patch has four files and six unexecuted evidence-reference cases. Original five-file delivery is stale after this split; do not overwrite the new registration/workflow. Original sources/review and previous CI remain historical evidence for their own SHAs. [Exact execution manifest](mutation-policy-evidence.json). The canonical tracker records both committed policy fix and LOCAL_ONLY remainder. AR-020 native E3 remains pending; E4 NOT_RUN; AR-083 DEFERRED_BY_USER.
