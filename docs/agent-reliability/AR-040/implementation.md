# AR-040 — owned jobs in the existing production runtime

Status: **IMPLEMENTED / E2_PASS / DONE** for the process scope required by AR-040. The project is not complete. Native AR-020/033 E3, E4 and deferred AR-083 E5 remain open.

## Exact source and evidence

Existing integration `8b1505c62e7764286b4185876098ed6e0dafaf04` was resumed, not overwritten by old conversation ZIP drafts. Namespace repair `5d17d348416c3ffcb27bad33318a091914837ef5` and registered test/fixture repair `462ab1dec0d0c723920283b0b3b9142b46d6796a` were pushed through the authorized connector. [Machine-readable evidence](acceptance.json) records focused run 35816609055, full run 35816612945, actual full checkout `a8144d5cab714399468988c42603adb34fbc07f0`, artifact identities and per-suite results.

AR-040: **57/57 in three repetitions**, retained AR-033/032/031/030/020/012/011/010/001 pass, **74/74 independent Agent suites** and **915 H2 tests / zero failures**. Full Windows publish and helper startup/IPC succeeded. Build warnings remain 38; they are not claimed fixed. Full test-merge checkout is not a merge into main.

## Production paths and boundaries

`H2LocalCommandTool` and `H2ProductionToolSession` register `start_command_job`, `poll_command_job`, `read_command_output`, `write_command_stdin`, `cancel_command_job`, `get_command_result` alongside the existing bounded `exec_command`. Global and Project use the same path. Host-owned TaskId, invocation, goal revision and expiring FullAccess grant define authority; model arguments cannot choose owner/PID or gain scoped shell access.

`ProcessShellCapabilities` owns the bounded native job and process-start identity. Poll cancellation is separate from job lifetime. Both stdout/stderr are drained past retention limits. Stdin is explicit opt-in with bounded idempotent receipts. Cancellation/timeout observes root termination, Job Object accounting and pipe EOF; uncertain effects remain unknown. Windows Job Object handles apply CancelOnHostExit to owned descendants only. They are not a sandbox for external Office/service effects or a promise of exactly-once execution.

The existing Agent journal stores native ownership before Resume and terminal receipts. The runtime observes the original start invocation at a safe boundary and invokes existing verification; separate poll calls or model prose cannot award proof. ArtifactStore retains exact bounded output with current hashes and completeness flags. Source-backed goals, unknown/partial effects and missing verifier evidence continue to block completion. No alternate engine, durable process database, daemon or ProjectRecord Agent state was added. Restart replay remains AR-041.

## Repairs and negative evidence

The first real Windows integration run failed 11 AR-040 cases in each repetition because `shell` had two different metadata descriptions. The registry's rejection was correct; the command descriptor was aligned without relaxing registry validation. Two registered composition tests cover both registration orders and require conflicting metadata to remain rejected without modifying the registry.

The next run passed 53/54 but its exact-output fixture expected only `stderr-ĐÚNG`, whereas native PowerShell also wrote progress diagnostics. The fixed fixture suppresses progress **only in that fixture script**, keeps exact 1000000 `Z` characters and exact stderr assertions, and records output/hash before assertion. A separate actual-PowerShell test explicitly emits progress and verifies that the paged output, retained artifact and observed character count agree and preserve both diagnostic and Unicode markers. Production never strips stderr or treats it as discardable noise. Earlier failures remain failures in the historical records, not relabelled control successes.

E1 contains the host policy tests; E2 uses scripted model transport, real disposable PowerShell processes/files/pipes and production archive round trips. The tests do not call a real model or use personal documents/new endpoints/credentials. Native Office acceptance and physical two-PC acceptance are not inferred from these results.

## Handoff

Continue only independent **AR-050** under tracker section 2.2 after reconciling refs, working tree and SESSION HANDOFF. Its AR-010/032 dependencies are accepted; AR-041 remains not started because its native prerequisites are not complete. Measure every actual serialized model request with provider-specific limits before send, including continuation/repair/steering; test RC-17/18 through the existing transports with scripted payloads. Do not invent model limits or switch configured endpoints. No AR-050 code is part of this checkpoint.
