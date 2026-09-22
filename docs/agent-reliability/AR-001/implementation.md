# AR-001 — contract repair and CI restoration

Source baseline: `e573394963ea44398217721ed5f200f899234d31`. Existing branch `feature/h2-agent-reliability-ar-000`, PR #3. Runtime baseline is unchanged from AR-000 main before this patch.

B01: production prompt uses canonical skill name constants. B02: ExcelPatchLimits owns 1..128 for schema, adapter, IPC client/server and both backends; oversize batches are rejected before writing or native discovery, never truncated. The optional enlarged fixture tests 128 distinct cells and unrelated-cell preservation. B03: exact-set guards include the already-shipped read_tool_output; historical MB-120 measurements remain intact and current 20 descriptors/seven executors are explicitly distinguished.

B11: the old fixture had a 10-second wait but a default 60-second command timeout. Synchronous disposal cancelled/disposed tokens without waiting for the execution's Finished barrier; Directory.Delete in finally could replace the primary exception. IAsyncDisposable now drains those existing execution tasks before releasing tokens; fixture owners await it before deletion, retain the primary error and preserve evidence on cleanup failure. The command-success case has an explicit 20-second tool/30-second harness budget. A forced 50ms timeout while an owned PowerShell process runs must remain TimeoutException, stop that process and allow deletion only after drain. Slow startup is a hypothesis for the historical masked failure, not a proven root cause. No new job/queue engine or full restart-recovery claim is introduced.

## Acceptance pending

Focused AR-001 CI runs `dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-001` three times; the unchanged full Avalonia CI runs every existing suite and publish/helper smoke separately. The proposed main-workflow edits were not applied because the CI token cannot write workflow files. E1/E2 use scripted transport, fixture backend and disposable local command. Invalid native count preflight does not access COM. E3/E4 Office/model/UI are NOT_RUN. AR-083 remains DEFERRED_BY_USER. Record actual CI before claiming completion.
