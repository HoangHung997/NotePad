# AR-033 — verdict consistency repair under validation

Status: ACTIVE / VALIDATING. Not DONE. The canonical execution checkpoint remains in H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md.

Resumed existing AR-033 implementation on `b44306681f13e5ae1919e022658e95c0574b2ef6`, not the older AR-032 plan. This session did not create the intervening seven implementation commits. Main remains `1283bc13e07c3cd47d04886166de3dfc595422c0`; branch is `feature/h2-agent-reliability-ar-000`, PR #3 draft/unmerged.

The additional source/test repair is committed at `84acbc456026ace4f20d435aed294d7b5099b2c7`. A verifier's Failed or NotVerified aggregate criterion previously could be replaced by all-Passed per-call details. The new preflight rejects a contradictory report before modifying accepted criterion/attempt proof. Valid mixed-target batches remain supported when their details account for the aggregate failure; a subsequent exact-target correction can resolve its own predecessor.

Seven new registered tests cover four read-only/mutating contradiction combinations, one valid mixed-target correction, and two concrete production file cases with scripted transport and an explicitly injected verifier. The latter execute the temporary file write and must block the false proof without fabricating a Completed or Verified outcome. No test assertion was removed. The readonly workflow restores the exact current class after its prior-source control and rebuilds before positive tests.

Validation run `35798845676` is in progress at this checkpoint. Proposed counts in the workflow are assertions, not observed PASS. Full Avalonia CI must be inspected on a source-equivalent checkout after this write. Local container has no dotnet/PowerShell and its source is a verified offline snapshot, not the user's working tree. User-PC tree is NOT_ACCESSIBLE.

AR-033 specifies E2/E3. Fixture success alone will not close its native acceptance gate. Real Office/provider E3 and real H2 UI/model/tool E4 remain NOT_RUN; AR-020 native gate is pending and AR-083 remains DEFERRED_BY_USER. No automatic retry of uncertain writes, new engine/store, ProjectRecord Agent state, model call, credential, endpoint or personal-document test is introduced.

Next: inspect the new control and positive logs/artifacts, repair any real failure, run all retained AR and Agent suites plus full Avalonia publish/helper IPC, then save exact evidence and update the canonical SESSION HANDOFF. Keep native acceptance separate. Do not replay any one-use source integration bootstrap.
