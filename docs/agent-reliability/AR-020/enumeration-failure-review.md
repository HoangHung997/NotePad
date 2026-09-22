# AR-020 — enumeration failure repair

Status: IMPLEMENTED / VALIDATION_PENDING. Required native E3 remains AWAITING_ENVIRONMENT; AR-020 is NOT DONE.

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
