# AR-071 — Task-local compatibility adapter trial

Validated code: `d414490715b5c88bc8e67dcfdc072672c1a474e0`.

AR-071 is an optional compatibility trial zone selected under the user's standing approval to proceed with required decisions. It reuses PluginManager package admission/trust/self-test and never creates a second catalog or production runtime.

## E2 behavior

- Candidate ZIP is admitted/staged through PluginManager and discarded after the trial.
- Stable active plugin version is observed before/after and never automatically replaced.
- Only explicitly approved workspace inputs are copied into a bounded SafeWorkspace copy.
- Environment manifest pins candidate archive/payload/manifest hashes, application/version/API, dependencies and permission fingerprint.
- Capability probe runs on every trial, including cache reuse.
- Cache is task-local and reusable only with exact archive + host + dependencies + capability fingerprint; application-version change invalidates reuse.
- Candidate executes only against the trial copy.
- Readback/diff evidence is hash/size based and bounded.
- Scope escape, bad archive hash, failed declarative self-test and execution failure all fail closed and leave the stable version/original input unchanged.
- No promotion operation is exposed.

## Validation

Dedicated run `36287350092` / job `108530505517`: SUCCESS.
- AR-071: 6/6
- AR-064: 77/77
- AR-082: 5/5
- AR-042: 6/6
- AR-041: 6/6
- full H2: 1376/1376
- required Agent suites: 75/75
- all exact-SHA workflows: 27/27 SUCCESS

Full Avalonia CI `36287350090` / job `108530568063`: SUCCESS, including self-contained win-x64 publish, bundled Python, exact portable manifest, clean-profile verification and packaged helper IPC.

Same-SHA AR-090 audit `36287350013` / job `108530505587` also SUCCESS. The final AR-090 report remains intentionally stale until the next turn because this turn is AR-071 only.

## Acceptance boundary

E1/E2 are PASS. E3 real third-party app/version trial is DEFERRED_BY_USER / AWAITING_ENVIRONMENT. No arbitrary adapter compatibility, auto-promotion, service/executable modification, second catalog or universal sandbox claim is made.
