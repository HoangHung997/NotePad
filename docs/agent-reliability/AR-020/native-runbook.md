# AR-020 native Office acceptance — not yet executed

**E3: NOT_RUN / AWAITING_ENVIRONMENT.** CI registration checks and injected probes are not installed Office evidence. Do not close AR-020, AR-021, MB-127 or AR-083 from a green fixture run.

## Safe setup

Use only dedicated small test documents. No model, API credential, remote endpoint or personal project file is required. The native commands attach to existing Word/Excel windows; they do not open applications, close documents, save, patch, enable macros, submit/upload or change focus. The metadata catalog includes local names/paths; keep it local and inspect/redact before sharing. Marker reports retain only hashes, native identities and match booleans, not document bodies.

Prepare DOC-A and DOC-B as exact cell values / whole Word paragraphs in separate test documents (e.g. H2_AR020_A.xlsx and H2_AR020_B.xlsx). Put UNSAVED-ONLY in a new unsaved test document. Keep fixtures below the existing whole-snapshot limits (5000 Excel cells, 2000 Word paragraphs). Those limits/paging still belong to later AR tasks.

## Observe native windows with the built/published helper

Use `tools/agent-reliability/Invoke-Ar020NativeProbe.ps1`, or its copy from the handoff package. It enforces a finite deadline and kills only its own diagnostic helper if blocked in COM. Both output paths must be new and their parent directory must exist.

```powershell
.\Invoke-Ar020NativeProbe.ps1 -OfficeHostPath 'C:\H2-test\H2AgentLab.OfficeHost.exe' -OutputPath 'C:\AR020Evidence\catalog-01.json' -AllowNativeOffice
.\Invoke-Ar020NativeProbe.ps1 -OfficeHostPath 'C:\H2-test\H2AgentLab.OfficeHost.exe' -ManifestPath 'C:\AR020Evidence\cases.json' -OutputPath 'C:\AR020Evidence\markers-01.json' -AllowNativeOffice
```

Select exact PID, start ticks, root/view handles and full path or unsaved name from the fresh catalog. Do not infer any handle, select a lookalike by name, or edit IDs until a test appears green. `cases.json` is an array of 1–8 entries:

```json
[{"CaseId":"EXCEL-A-VIEW1","Application":"excel","ProcessId":1234,"ProcessStartUtcTicks":123456789,"RootWindowHandle":123456,"ViewWindowHandle":123456,"FullName":"C:\\AR020Fixtures\\H2_AR020_A.xlsx","Marker":"DOC-A"}]
```

The numeric values above are placeholders, NOT executable fixture identities. Marker values are restricted to DOC-A, DOC-B and UNSAVED-ONLY. The probe re-discovers and validates the selected native identity before reading, compares returned identity afterwards, and never prints/exports a raw snapshot. Its exit 0 means only the selected marker observations matched; it does not certify the entire gate.

## Required E3 matrix before acceptance

Record the exact application/test source SHA, Windows and Office version/bitness, individual commands, timing, observed native identities and case-specific failures. Run separately for Word and Excel: two actual process instances; two views of one document with distinct selections; near-identical names; unsaved marker; Save As invalidating the prior binding; close/reopen; busy/modal failure; capture original view then change foreground; ordinary H2 capture/readback through the configured app. Every boundary needs repeated observations, counterpart markers and no unrelated document modification. Test native behavior, not just this marker helper. An unsupported Office build/view is a support gap, not an instruction to fall back to `GetActiveObject` or kill Office.

A timeout, missing OBJID_NATIVEOM pane, old session, incomplete catalog or mismatch is a failed/not-run case. Preserve all outputs and leave later steps NOT_RUN. PID/window checks are conservative preflight/readback observations, not race-free handle transactions. Full semantic content/UI revision separation and native range/layout support remain later work. E4 model/UI and E5 physical multi-PC gates are separate; AR-083 stays DEFERRED_BY_USER.
