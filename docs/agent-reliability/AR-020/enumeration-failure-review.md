# AR-020 — enumeration failure repair

Status: IMPLEMENTED / E1_E2_VALIDATED. Full CI passed on the exact repair SHA. Required native E3 remains AWAITING_ENVIRONMENT; AR-020 is NOT DONE.

Resumes the local `H2_AR020_Enumeration_Repair.zip` draft against remote `a7eb37fab7bc5b8e6e3aa39442e82e7d4a8dc17a`, on `feature/h2-agent-reliability-ar-000`, PR #3. The native-probe and eight-case test postimages match the handed-off SHA256/blob hashes. Test registration is instead delegated from the existing H2OfficeDiscoveryTests.Run; Program.cs remains unchanged. The old local NOT_PUSHED tracker template was not copied over remote history.

## Runtime change

The existing OfficeNativeWindowProbe used to ignore the EnumWindows return value. FALSE with no collected roots could become a successful empty catalog. The guard now returns incomplete typed discovery, discards any partial root candidates before native attach, and does not invent an active session. A deliberately stopped enumeration due to the existing limit remains distinct. A successful empty enumeration remains valid. The unchanged EnumChildWindows call has a different return contract.

Primary API contracts re-read on 2026-09-22:
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumchildwindows

No new Agent engine, state store, model/provider endpoint or credential. No Office application is started/closed, no personal document is modified, no new paid model request, and no main merge.

## Validation plan registered in existing CI

Eight tests inject only the top-level enumerator on Windows/STA: failed Excel/Word enumeration; successful empty Excel/Word enumeration; failed targeted capture; typed exception without raw message; later explicit recovery without automatic retry; unsupported application before enumeration. They are E1 boundary fixtures, not native Office E3.

A guard-only mutation control retains today's injection seam but removes only the FALSE-result guard. It must compile and reproduce three exact semantic failures. It is NOT represented as a checkout of the original runtime, which lacked that constructor. The script restores the exact pre-control bytes and verifies hashes/clean status. Existing lifetime-cache controls remain. The next step rebuilds the actual positive source, repeats the AR-020 corpus three times, retains AR-012/011/010/001 and all mandatory Agent suites. Full Avalonia CI runs separately on the actual PR test-merge checkout; publish/helper startup must pass. Read each result on its own source SHA before claiming PASS.

Local container: no .NET/PowerShell and Git remote DNS unavailable. There is no real local clone; offline snapshots were hash-compared against known remote Git blobs. Authorized GitHub writes are available this session, so code is committed atomically via Git Data on the existing branch. Tests/CI for this repair have not yet completed at this documentation checkpoint.

## Acceptance debt

Native Office E3 requires the existing dedicated marker, multi-instance/view, unsaved, Save As, close/reopen, modal/busy and frozen-capture matrix; see native-runbook.md. E4 remains NOT_RUN. AR-083 remains DEFERRED_BY_USER, not certified multi-PC. Existing implementation-evidence.json describes the PRE-REPAIR source and must not be used to certify this new code.

## Verified repair checkpoint

Code `b270de5c22bfe880055975aaa35fbeebd6934f09`, focused run 35743581894: **36/36 AR-020 in each of three repetitions**, retained AR-012/011/010/001 **44/44, 25/25, 11/11, 13/13**, all independent Agent suites **74/74**. The guard-only control compiles and reproduces **0 passed / 3 expected failures**, then restores exact checkout bytes before rebuilding the positive source. The old lifetime-cache controls remain green as negative-control checks.

Full CI 35743581725 tested PR checkout `11754bda798e13e0311ec7d0c763ca52afa97fa1` with application/test sources verified equal to `b270de5c22bfe880055975aaa35fbeebd6934f09`: **719 H2 tests, 0 failures**, all required steps, Windows publish and real packaged-helper startup/IPC pass. These are not native Office operations. The measured environment and package identities are in [repair evidence](enumeration-repair-evidence.json). E3/E4 NOT_RUN; AR-083 DEFERRED_BY_USER.

Reconcile current main/branch/PR and clean working tree. The AR-020 enumeration repair is saved and E1/E2 validated; do not reapply the stale local ZIP. If new native Office evidence is supplied, inspect it under the existing AR-020 runbook and repair any demonstrated defect. Otherwise leave AR-020 IMPLEMENTED/AWAITING_ENVIRONMENT, and start only READY independent AR-030 on this same branch/PR: set the sole active implementation task to AR-030, read its approved specification/current contracts, then implement outcome obligations and user-sourced goal revisions with RC-11/12 E2 tests. AR-030 requires DONE AR-010/011, not Office E3. Do not mark AR-020 DONE, bypass dependent native gates, or repeatedly block independent core work on the absent Office device. AR-083 stays DEFERRED_BY_USER.
