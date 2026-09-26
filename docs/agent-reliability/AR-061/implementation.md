# AR-061 — Desktop application lifecycle implementation

Validated source: `779a92bc0027be8c6194edf1fd00c1c132ae8d2e`  
Branch: `feature/h2-agent-reliability-ar-000`  
PR: #3  
Implementation: **IMPLEMENTED**  
Completed evidence: **E2**  
Native acceptance: **E3/E4 AWAITING_ENVIRONMENT**

## Problem repaired

The production runtime could open a specific file in its associated application, but it did not expose a concrete normal-runtime executor for opening or activating a desktop application such as File Explorer, a blank Word/Excel window, or AutoCAD. First-party extension metadata contained app lifecycle concepts, but product execution did not have a complete DesktopHost-backed callable path.

## Implemented path

- Normal V2 ToolRegistry callables: `launch_app`, `list_running_apps`, `wait_for_app_window`, `activate_app`.
- DesktopProtocol/DesktopHost typed RPC for list/launch/wait/activate.
- Safe Windows resolver:
  - explicit aliases for Explorer, Word, Excel, AutoCAD and selected common apps;
  - exact registered App Paths executable/product/file-description matching;
  - no arbitrary executable path, shell syntax or command arguments;
  - blocked sensitive process policy remains authoritative.
- Exact observed result identity includes session/window/process evidence. Multiple matching windows fail closed instead of choosing by title/order.
- A single already-open safe target may be verified/reused rather than returning a false launch failure.
- Bounded startup wait supports slower applications without unbounded blocking.
- H2 permission presets remain authoritative and no file/document grant silently becomes machine app-launch authority.
- Application mutation failures with uncertain effect require same-app/session observation before retry.
- Tool-search descriptions/ranking cover normal user phrasing such as opening Word, Excel, File Explorer or AutoCAD.

## E2 validation

Dedicated AR-061 run `36208050660`, job `108308672263`: **SUCCESS**.

- AR-061 focused: **6/6**
- DesktopHost: **10/10**
- MB-112 Desktop/computer-use: **16/16**
- Full H2: **1292/1292**
- All required Agent suites: PASS
- Windows x64 self-contained publish: PASS
- Packaged DesktopHost/OfficeHost startup and IPC verification: PASS

Full Avalonia CI `36208050707`, job `108308917465`: **SUCCESS**. All H2/Agent/MB acceptance, Office/Desktop/Web/CAD/MCP/plugin and transport gates passed; win-x64 publish and packaged-helper IPC passed.

Cross-build `36208050639`: **SUCCESS**, compile/publish evidence only.

All **26/26** workflows on the exact code SHA completed SUCCESS; 0 failed.

## Portable

Final full-CI portable artifact: `10894338034`, 110,358,547 bytes, SHA256 `12d0997324ebbf1fadc206a5874bae861ec4172a5869f4cfdafa7547d1426109`.

The archive was independently downloaded in the ChatGPT execution environment: SHA256 matched, ZIP integrity test passed for 482 entries, and the package contains the main H2 executable plus DesktopHost and OfficeHost helper executables.

## Not claimed

No real user-machine E3/E4 pass is claimed. File Explorer, Word, Excel and AutoCAD launch/activation from the user's actual H2 configuration must still be tested. Fixture/DesktopHost acceptance and CI do not substitute for that native workflow.
