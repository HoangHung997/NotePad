# AR-012 — scoped resource binding

Status: IMPLEMENTED / E2_PASS / DONE on `ed46ab820c5b084c64a11c5c171d2ca0dbee5adf`.

The prior 19-case draft has been transferred to the existing branch, not a new engine/store. New integration calls the existing deterministic binding policy from Office dispatch/readback, keeps per-session pins, rejects ambiguous model-selected targets, protects frozen active/selection context, and checks selected desktop identity before action. Existing progress/archive and AgentTurnView now project Project/Global/External target chips; permission gates stay separate. Fifteen new registered cases exercise the production wrapper with a controlled no-network Office transport, real temporary file effects, headless UI tree and a dedicated Windows junction.

Native Office multi-instance PID/view discovery, content-version separation, atomic handle-based file I/O and NAS alias certification are NOT claimed. Current native catalog session/path and helper-connection identity are checked as available; incomplete or stale explicit capture fails closed. Arbitrary FullAccess PowerShell/Python is not an OS sandbox. No new model credential/endpoint, personal document, outgoing user message or second Agent store. E3/E4 NOT_RUN; AR-083 DEFERRED_BY_USER.

## Resumed review — exact Word revision preflight

The inherited candidate `3ee09035e06bb8a02a483d684e141419769b5160` passed its 39-case Windows E2 corpus (three repetitions), retained AR-011/010/001 and 74 Agent suites in run `35720746799`; archive SHA256 `f300e623a1bf876490c01cdbee2d8ddee453a7fa94119a4870a42e5a70a71dbd` was downloaded and verified. This is prior evidence, not acceptance of the following repair.

Review found that Word's locally observed stale StateToken returned a legacy failure without no-effect proof BEFORE calling PatchWordAsync. The outcome bridge consequently classified a rejected input as Unknown, unlike the equivalent Excel guard. The Word check now throws the existing ToolPreflightException with stale_resource. Five registered concrete production tests distinguish three stale operation rejections (replace/format/insert), a valid read-back verified single-line Word fixture edit preserving a guard paragraph, and an actual disposable-file write followed by lost response which must remain Unknown and not repeat. Native Word layout/paging is not simulated as accepted.

The review above describes the pre-acceptance work. Its final exact-source results are recorded below; native E3/E4 and the AR-083 deferral remain unchanged.

## Accepted execution and traceability

Full CI **35724401082**, job `106734498448`, tested `ed46ab820c5b084c64a11c5c171d2ca0dbee5adf`: **683 H2 tests / 0 failures**, all required Agent/provider/helper suites, Windows publish and packaged Office/Desktop helper IPC passed. Focused review **35724340135** reproduced **2 passes / 3 expected failures** on the old Word wrapper, restored a clean repaired checkout and passed **44/44 AR-012 x3**, **25/25 AR-011**, **11/11 AR-010**, **13/13 AR-001** and **74/74 Agent suites**. Downloaded evidence SHA256/CRC and identity/results were read back; these counts are not inferred from source.

| Requirement | Implemented owner | Executed evidence |
|---|---|---|
| Exact explicit/linked file scope and prefix collision | `src/H2Notes.Core/H2AgentTargetScope.cs`, `src/H2Notes.Core/H2AgentResourceBinding.cs`, `experiments/H2AgentLab/Integration/H2ProductionToolSession.cs` | AR-012 RC-04 + concrete external file/read-only/FullAccess negative tests |
| Global captured-active vs Project generic-open; no model-selected ambiguity | `src/H2Notes.Core/H2AgentResourceBinding.cs`, `experiments/H2AgentLab/Integration/H2OfficeRuntimeTools.cs` | RC-03/04/05/24 production Office cases |
| Provider/session/path checks before dispatch and after readback | `experiments/H2AgentLab/Integration/H2OfficeRuntimeTools.cs` | connection/session/Save As/unsaved/language evidence/unknown readback cases |
| Immutable execution identity separate from history correlation | `experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs` | queued Global task attached to a project keeps original execution identity |
| Separate content/UI binding metadata and selected desktop identity | `src/H2Notes.Core/H2AgentResourceBinding.cs`, `experiments/H2AgentLab/Desktop/SelectedDesktopWindowController.cs` | RC-07/24 deterministic identity and actual fixture helper IPC |
| External target chip and shared Global/Project projection | `src/H2Notes.Avalonia/Controls/AgentTurnView.cs`, `src/H2Notes.Core/H2AgentThread.cs` | real headless turn renderer, ticker and activity replay |
| Unsafe path/reparse fail closed | `src/H2Notes.Core/H2AgentTargetScope.cs` | Windows junction escape, reserved/device/ADS/trailing/prefix cases |
| Stale Word revision proven before dispatch vs lost response after effect | `experiments/H2AgentLab/Integration/H2OfficeRuntimeTools.cs` | 5 Word revision cases, 3 expected old-code failures, valid readback and post-write unknown controls |

Exact CI steps, artifact hashes, commands, negative-control identity and limitations are in [acceptance.json](acceptance.json). No native document, full visual UI, alias/OS-race or multi-PC certification is implied. The old local draft is superseded by the remote checkpoint. Next: **AR-020 NOT_STARTED**, same branch/PR; no merge authorized.
