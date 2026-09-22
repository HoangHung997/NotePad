# AR-001 — contract repair and CI restoration

Source baseline: `e573394963ea44398217721ed5f200f899234d31`. Existing branch `feature/h2-agent-reliability-ar-000`, PR #3. Runtime baseline is unchanged from AR-000 main before this patch.

B01: production prompt uses canonical skill name constants. B02: ExcelPatchLimits owns 1..128 for schema, adapter, IPC client/server and both backends; oversize batches are rejected before writing or native discovery, never truncated. The optional enlarged fixture tests 128 distinct cells and unrelated-cell preservation. B03: exact-set guards include the already-shipped read_tool_output; historical MB-120 measurements remain intact and current 20 descriptors/seven executors are explicitly distinguished.

B11: the old fixture had a 10-second wait but a default 60-second command timeout. Synchronous disposal cancelled/disposed tokens without waiting for the execution's Finished barrier; Directory.Delete in finally could replace the primary exception. IAsyncDisposable now drains those existing execution tasks before releasing tokens; fixture owners await it before deletion, retain the primary error and preserve evidence on cleanup failure. The command-success case has an explicit 20-second tool/30-second harness budget. A forced 50ms timeout while an owned PowerShell process runs must remain TimeoutException, stop that process and allow deletion only after drain. Slow startup is a hypothesis for the historical masked failure, not a proven root cause. No new job/queue engine or full restart-recovery claim is introduced.

## Acceptance pending

Focused AR-001 CI runs `dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-001` three times; the unchanged full Avalonia CI runs every existing suite and publish/helper smoke separately. The proposed main-workflow edits were not applied because the CI token cannot write workflow files. E1/E2 use scripted transport, fixture backend and disposable local command. Invalid native count preflight does not access COM. E3/E4 Office/model/UI are NOT_RUN. AR-083 remains DEFERRED_BY_USER. Record actual CI before claiming completion.


## First actual CI repair — not yet accepted

Focused run 35691748431 tested source 88cc6241d9488e26e0652751a7c14c26df4aecb3: build succeeds; 9 RC-02 cases pass and 4 fail. The two oversized production-runtime cases already reject correctly but their tests incorrectly parse the evidence-footer projection as raw JSON; the assertions now explicitly require the footer and parse the preceding JSON body. The forced-timeout and held-transport-disposal tests expose UI synchronization-context capture in ToolExecutionScheduler awaits and implicit await-using disposal. Engine continuations now use ConfigureAwait(false), preserving gate/cancellation semantics while no longer depending on the caller pumping UI during shutdown. Both deterministic regression cases remain mandatory; no pass is claimed until rerun. Original primary errors and failed-run evidence remain preserved. This does not prove the exact cause of the earlier AR-000 sporadic cleanup failure.


## Full CI uncovered another pre-existing exact-inventory mismatch

Source c26b3521ce4b7d60e69f3942dda123c23fd53cf3 passed the AR-001 corpus 13/13 in all three focused repetitions (run 35692162013; artifact digest b26040954154ed315355978edcad3e09e8267c85be2855a7cb863f8735cf79f8). Full run 35692347728 passed H2 603/603, architecture 35/35 and all preceding suites, then Phase-11 case 1108 failed: its exact WebResearchHost list omitted the already-implemented web.read_feed. Phase-11 was 14 passed / 1 failed; downstream MB suites and publish were NOT_RUN. The corrected test retains exact ordering and selected-only schema checks, adds read-only/scope assertions, and executes the existing feed backend/parser/observation path using a synthetic feed. No actual search/browser connection is installed or claimed. Native/fixture Excel null preflight is made explicit to remove the two new nullable warnings without changing its rejection contract. This repair remains AR-001 CI restoration; AR-060 live Web acceptance stays open.


## Test-only compile correction

Run 35692802913 on 82af106655358fa4f27d60f3da2a53e35341f1cb failed compilation (CS8852) because the added Phase-11 test assigned the init-only FeedObserved property after construction. The fixture now binds that callback in an object initializer, preserving the provider contract and all feed assertions. The failed build is not counted as a test pass. Detailed failed/passing attempts are retained in ci-attempts.json. Full CI still requires a clean run on the corrected source.
