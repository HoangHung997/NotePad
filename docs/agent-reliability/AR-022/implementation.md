# AR-022 — Excel batch writes and postconditions

## Scope

AR-022 hardens the existing structured Excel live-mutation path. It does not add a second Office engine, promise COM transaction atomicity, or change the approved H2 Agent architecture.

## Implemented behavior

- Preflight the complete Excel patch batch before the first native write.
- Reject empty/oversized, invalid address, duplicate target, value+formula conflict, clear+set conflict, and no-op patch shapes before effect.
- Preflight protected cells and merged-range non-anchor targets before effect.
- Carry stable logical operation, batch, and chunk identity in patch results.
- Classify mutation effects as Applied, PartiallyApplied, Failed, or OutcomeUnknown from readback.
- Retain exact AppliedCells, UnappliedCells, and UnknownCells. Repair candidates contain only cells proven unapplied; unknown effects cannot be replayed automatically.
- Use Excel ContentToken for mutation freshness. Selection/focus-only changes do not invalidate it; real content changes do.
- Preserve legacy callers that send only StateToken by requiring an exact current StateToken first, then deriving the current ContentToken internally.
- Scope recalculation to workbook, worksheet, or A1 range and verify recalculated values by live readback.

## Repair history

Initial source commit: `090519c480882011fde9093e14c4f5f2fa9748f7`.
Compile/shadowing repair: `a0b442c4cd669744c7fc6927feeedc341cfb8404`.

The a0b checkpoint exposed real retained regressions: invalid-count native preflight did not match fixture/server, and AR-012 legacy callers without ContentToken were rejected before a valid write. Commit `df0ae549d4146fd667fc3e3445eb5a06cc096d0b` restored the exact legacy preflight/state-token contracts while keeping the new AR-022 whole-batch/readback safety.

## Validation

Exact validated code: `df0ae549d4146fd667fc3e3445eb5a06cc096d0b`.

- AR-022 focused: 8 passed, 0 failed.
- OfficeHost: 18 passed, 0 failed, including 0805B.
- Retained: AR-021 14/14; AR-020 36/36; AR-012 44/44; AR-001 13/13.
- Full H2: 1300/1300.
- Required Agent suites: 75/75.
- Dedicated run 36213894993 / job 108326259949: SUCCESS.
- Full Avalonia CI 36213894968 / job 108325894609: SUCCESS.
- All 14 pull-request workflows on the exact SHA: SUCCESS, 0 failed.
- Self-contained Windows x64 publish and packaged DesktopHost/OfficeHost IPC: PASS.

## Acceptance boundary

E1 and E2 are PASS. Native real-Excel E3 is DEFERRED_BY_USER until the user tests the final full build. E3 is not claimed passed. E5 remains separately deferred.

No personal document, credential, new external endpoint, or destructive external side effect was used during validation.
