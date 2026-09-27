# H2 Agent Reliability — Final Handoff

Status: **STALE_AFTER_OPTIONAL_TASK_ACTIVATION** — AR-071 is active.

> The user explicitly selected optional AR-071 after the AR-090 audit. This report remains exact provenance for the validated AR-090 SHA below, but it is no longer the current code handoff. Re-run AR-090 after AR-071 before issuing a new final handoff.

At its validated SHA this report carried **IMPLEMENTATION_READY_FOR_USER_TEST**. It does not replace the AR specification/tracker and it does not claim that deferred native/model/two-PC acceptance has passed.

- AR-090 validated code SHA: `9ce4fd8834ca0790bb95c091bf9f4f2019c7cf8b`
- AR-090 final portable artifact: `10917512842`
- Final portable SHA256: `7e0cd414d7d94aae6743344c21a31b702e3bf5efb8883deab36921c49754d00c`
- Final portable ZIP entries: `2970`
- Final manifest inventory: `2969 files / 415193548 bytes`
- Final manifest content SHA256: `acadbc434f7c168c409db976a4ca3a1fc9c207d586c852b3a368156eec5d6348`
- Pre-AR-090 validated code SHA: `2f3a0c995307ee4615ceb647ff66322a795fc90f`
- Pre-AR-090 full-CI portable artifact: `10917395115`
- Pre-AR-090 portable SHA256: `7cdbbaf684f49aeb57288cfe75e1553f3795eadf5cd07c7047cace7e246521b6`

## 1. Handoff state

**Implementation state:** all mandatory implementation tasks through AR-082 have an implementation/evidence checkpoint. AR-090 is the final audit/handoff gate.

**Acceptance state:** not all native/live acceptance is complete. AR-083 physical two-PC/NAS is **DEFERRED_BY_USER** and **E5 NOT PASSED**. Native Office/CAD/desktop/model/search/browser and long-duration interactive E4/E3 cases remain deferred/awaiting environment where their tracker rows say so.

This report therefore says **IMPLEMENTATION_READY_FOR_USER_TEST**, not “project complete”, “all acceptance passed”, “multi-PC complete”, or “production certified everywhere”.

## 2. Task status — two axes

The canonical per-task wording remains the table in `docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md`.

| Group | Implementation | Acceptance |
| --- | --- | --- |
| AR-000/001/010/011/012/030/031/032/040/050 | Done | Required deterministic/CI evidence passed |
| AR-020–024 | Implemented | Native Office E3/E4 debt remains as labelled |
| AR-033/041/042 | Implemented | Native/restart/concurrency E3 debt remains as labelled |
| AR-051/052 | Implemented | Real-model/provider E4 debt remains |
| AR-060–070 | Implemented/core/integration gates complete | Live provider/native E3/E4 debt remains |
| AR-071 | Optional; selected after this audit and now ACTIVE | Re-run final audit after AR-071 |
| AR-072 | Optional; NOT_SELECTED | Not part of current scope |
| AR-080/081 | Implemented | Native/live-model/accessibility E4 debt remains |
| AR-082 | Implemented | E3 PASS only for clean GitHub-hosted Windows runner profile; physical/native E4 deferred |
| AR-083 | Harness/work can wait | **DEFERRED_BY_USER; E5 NOT PASSED** |
| AR-090 | Audit/handoff gate PASS | **IMPLEMENTATION_READY_FOR_USER_TEST**; deferred acceptance below remains open |

## 3. Capability matrix

| Capability | Implemented evidence | Remaining acceptance / limitation |
| --- | --- | --- |
| Global + Project Agent production bridge | E2 production bridge and UI gates | Live provider combinations still depend on configured environment |
| Tool outcome, scope, completion, durable journal | E1/E2 green | No generic exactly-once claim |
| Excel bounded read/write/recalc | E1/E2 green | Native Office E3 deferred |
| Word bounded read/structure-preserving mutation | E1/E2 green | Native Word/layout E3 deferred |
| Long process/job lifecycle | E2 + real child processes | External app/provider-specific long jobs not universal |
| Restart reconcile | E1/E2 green | Native crash/restart E3 deferred; no blind replay |
| Steering/cancel/same-resource concurrency | E1/E2 green | Local host serialization is not a distributed/NAS lock |
| Context budget/compaction/resume/rebase | E1/E2 green | Real-model long-duration E4 deferred |
| Web search/fetch/browser | Production backend contracts E1/E2 green | Live search/browser providers need configuration and E3/E4 |
| Desktop launch/capture/act | E1/E2 green | Native DPI/IME/app-specific E3/E4 deferred |
| Artifact generation/publish | DOCX/XLSX/PDF closed-path E1/E2 green | PDF content/layout and native recalculation/rendering not universally certified |
| AutoCAD closed/live bridge | E1/E2 green | Real installed AutoCAD selection/undo/session E3/E4 deferred |
| Plugin/provider lifecycle | E2 green | Trusted external/native provider E3 deferred |
| OpenAI/Luna tool-call path | E2 wire/runtime green | Real credentialed E4 deferred |
| UI reliability/performance | E1/E2 green | Native DPI/IME/screen-reader E4 deferred |
| Portable package/preflight | E3 clean Windows runner profile | Physical clean machine/native providers not certified |
| Two-PC/NAS | Not accepted in this pass | **AR-083 DEFERRED_BY_USER; E5 NOT PASSED** |

## 4. Dependency matrix from final portable preflight

Ready in clean-runner package:
- DesktopHost helper
- OfficeHost helper
- bundled Python sandbox
- helper IPC
- packaged document/Python environment

Unavailable on the clean runner:
- native Word/Excel
- live AutoCAD drawing

Needs configuration / was not live-probed:
- AutoCAD closed-file external console
- Ollama/model endpoint
- online AI credentials
- web search provider
- browser CDP/live tab
- optional local OCR model pack

No secret, API key, personal document, project workspace, Agent journal/task record or old runtime grant is intentionally packaged.

## 5. Cleanup and architecture parity audit

The current production composition is:
`H2 Notes UI -> H2ProductionAgentAdapter -> AgentRuntime -> ToolRegistry/provider adapters -> evidence/completion`.

AR-090 does not delete compatibility code merely because it is old:
- `AgentRunner.cs` remains a frozen baseline/live-evaluation harness and is not on the normal production path.
- `AgentTools.cs` remains host plumbing/compatibility while `NormalRuntimeToolRegistry` owns the normal callable surface.
- historical WPF/WinForms source and old audit evidence remain retained for migration/baseline comparison.

No parity-safe runtime deletion was identified that could be removed without deleting an active compatibility/evidence surface. Cleanup in AR-090 therefore removes **stale current-status claims**, not evidence history.

The 2026-09-19 `H2_AGENT_FINAL_ARCHITECTURE_REPORT.md` is a historical MB-120 baseline. It must not be used as current AR status; this handoff supersedes it for current reliability state.

## 6. Known limitations and deferred acceptance

- AR-020/021/022/023/024: real/native Office environment acceptance remains deferred/awaiting environment as tracked.
- AR-033/041/042: native completion/restart/concurrency scenarios remain deferred where labelled.
- AR-051/052/060/061/062/063/064/065/066/067/068/069/070/080/081: live model/provider/native app E3/E4 combinations remain deferred where labelled.
- AR-082 proves a fresh GitHub-hosted Windows runner profile only, not a separately supplied physical clean PC.
- AR-083 physical two-PC/NAS acceptance remains **DEFERRED_BY_USER**.
- Optional AR-071/072 remain **NOT_SELECTED**.
- There is no claim of universal model quality, OCR accuracy, pixel-perfect Word/PDF layout, generic COM/GUI exactly-once execution, distributed resource locking, or all third-party provider compatibility.

## 7. Reproduce the current source gate

On Windows with .NET 10 SDK:

```powershell
dotnet restore .\H2Notes.Avalonia.slnx
dotnet build .\H2Notes.Avalonia.slnx -c Release --no-restore
dotnet run --project .\tests\H2Notes.Tests\H2Notes.Tests.csproj -c Release --no-build -- --filter AR-090
dotnet run --project .\tests\H2Notes.Tests\H2Notes.Tests.csproj -c Release --no-build
.\tools\agent-reliability\run_agent_suites.ps1 -OutputDirectory .\artifacts\ar090\all-agent-suites
.\tools\agent-reliability\validate_ar090.ps1
```

Final AR-090 dedicated validation and full Avalonia CI succeeded on the same code SHA. The exact final artifact/digest/manifest values above are now authoritative for this handoff.

## 8. Evidence retention

- Canonical tracker/handoff: `docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md`
- Canonical AR spec: `docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_SPEC.md`
- Per-task evidence: `docs/agent-reliability/AR-*/`
- Current final audit evidence: `docs/agent-reliability/AR-090/`
- Historical MB architecture report remains retained, explicitly labelled historical.
- GitHub Actions artifacts are time-limited; final handoff records artifact IDs, sizes, digests, source SHA and expiry.

## 9. Handoff result

AR-090 validation is green. The permitted handoff label is:

> **IMPLEMENTATION_READY_FOR_USER_TEST**

It means the mandatory implementation sequence is built, regression-gated and packaged for the user’s deferred real-environment testing.

It does **not** mean:
- PROJECT_COMPLETE,
- E5 passed,
- AR-083 passed,
- native Office/CAD/model/search/browser E4 passed,
- every deferred acceptance debt is closed.

## 10. Next actions after this handoff

1. User may test the final AR-090 portable build on the intended Windows machine.
2. Any observed regression reopens the owning AR task before new scope is added.
3. Native/model/provider acceptance may be executed when the user is ready.
4. AR-083 two-PC/NAS remains deferred until the user explicitly starts it.
5. Optional AR-071/072 remain unselected unless explicitly requested.
