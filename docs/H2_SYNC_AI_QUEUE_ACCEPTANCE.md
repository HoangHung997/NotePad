# H2M-133F — Project AI queue verification

Date: 2026-09-21  
Branch: `feature/nas-multi-device-sync`  
Imported source: `9660a0f3000ea9c24d5e071160d22196baf4836a`

## Scope

This continuation verifies and hardens the in-process Coordinator protocol for
project AI turns. It does not certify production client cutover or physical
two-PC operation. The authority remains
`H2_SYNC_COORDINATOR_EVENT_ARCHITECTURE.md` and the Product Master tracker.

The imported source built successfully, but its regression run reported
**520 passed, 1 failed**: the running-lease-expiry test enqueued PC2 without
registering that device. The fixture now registers PC2 explicitly; production
device validation remains enforced.

## Changes

- A lost lease-grant response can be retried by the same device while that exact
  lease is still waiting for synchronization. A running lease is not granted a
  second time.
- Repeating barrier confirmation acknowledges the same live running lease
  without extending its heartbeat. Both stale and impossible future watermarks
  are rejected.
- Completion retries compare the durable receipt, including request, lease,
  terminal state, sequence and message identity. Exact retries succeed after
  restart and after the next turn starts; incompatible retries fail without
  changing the next owner.
- New AI-correlated conflict resolutions use the same live-owner/lease checks
  as other project mutations. Already accepted resolutions remain retryable.
- Enqueue retries require identical request content and preserve the original
  server acceptance time.
- Removed the public legacy lease/completion shortcuts. Persistence tests now
  exercise FIFO grant, barrier confirmation and committed assistant messages
  through the production store operations.

## Verification

Final local results on Windows / .NET SDK 10.0.401:

- Release solution build: **PASS, 0 errors**. The incremental build reported one
  existing nullable warning in `H2ProjectNotesDetailTests.cs:70`. The initial
  clean build reported 20 existing warnings across the solution; this work does
  not claim a warning-free clean build.
- Full H2 Notes regression suite: **527 passed, 0 failed**.
- Legacy NAS harness local self-test: **PASS** on both logical nodes.
- Latest Release desktop executable launched with `--demo --data` targeting
  `.artifacts/h2m-133f/demo/project-sheet.json`; its process remained responsive
  and persisted the isolated demo file. No visual/native interaction acceptance
  is claimed, and no UI layout was changed in this continuation.
- Final independent source review found no remaining blocker in this diff.

Reproduction commands from the repository root:

```powershell
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build
dotnet run --project tools/H2Notes.NasAcceptance/H2Notes.NasAcceptance.csproj -c Release --no-build -- --self-test .artifacts/h2m-133f/nas-self-test
```

Local evidence is under `.artifacts/h2m-133f/` (ignored build/test output).
The NAS harness self-test is a local regression check for the retained legacy
probe, not evidence that mixed WebDAV/SMB locks are safe.

## Remaining gates

- Fresh remote CI evidence for this continuation has not been produced.
- H2M-133G owns authenticated transport and device credentials.
- H2M-133H owns production client/Agent wiring and legacy cutover. The current
  desktop application does not yet route its project AI execution through this
  Coordinator queue; these tests exercise the real store/service and client
  synchronization boundary with synthetic messages.
- H2M-133I/J own Coordinator UX and the complete fault/integration package.
- H2M-133K must run on the user's two physical PCs through the new Coordinator.
  The three real failures of the old NAS-lock protocol remain valid evidence.

H2M-133 and final product acceptance remain open.
