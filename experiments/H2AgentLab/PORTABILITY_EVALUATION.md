# Portable deployment repair

Date: 2026-09-16. Scope: independent H2 Agent Lab only, not H2 Notes/WPF/_ver2.

## Diagnosis from the supplied screenshots

- `Private Python runtime not prepared` was a deployment defect: Python lived only in the original Windows user's LocalAppData, outside the copied program folder.
- `list_skills` required `query` even though its description called it optional. This was an application contract defect, not evidence of an incapable model.
- An empty skill search could mean either missing `skills/` files or an unmatched filter. The screenshots alone did not distinguish these. The new responses distinguish the cases and provide actual available names.
- `discover_available_skills` is not a Lab tool. No arbitrary aliases were added: unknown names remain rejected, now with the actual tool list for recovery.
- An empty `list_files` result is not an installation error by itself. The workspace on the second PC must contain the intended supported documents.
- General launch of a blank Microsoft Word application is not an existing capability. Opening an existing supported document uses `open_file` and explicit approval; its associated application must be installed.

## Changes

- Self-contained Windows x64 release with .NET Desktop Runtime, all five skills, worker/runtime guidance, provenance/license files and curated CPython 3.12 Office/PDF libraries.
- Portable Python is resolved relative to the executable. Development builds retain the per-user runtime. A damaged portable Python directory does not silently fall back to the developer machine's installed runtime.
- Runtime readiness checks actual required files as well as the marker. Missing components produce repair instructions in the environment dialog and agent input.
- Optional skill query accepts `{}`; non-string arguments still fail. No-match filters return the real catalog. Missing bundles return a non-recoverable installation diagnostic rather than an empty list.
- Existing AppContainer, staged copies, approvals, publication hashes and backups remain. No unrestricted Python fallback, auto-download, API change or model change.

## Verification

- Final development Release build: 0 warnings, 0 errors. The first attempt was blocked by the previously open Lab exe; the successful build followed the user's explicit close confirmation.
- 54 distinct deterministic checks passed: 8 portability, 18 regression, 14 recovery, 14 skill/runtime.
- Package verification created and read back DOCX/XLSX/PDF and rendered a PDF preview using the packaged Python inside AppContainer. No LLM or private documents were used.
- The distributed ZIP was extracted to a different path containing spaces. All 2,962 manifest file hashes matched. The moved executable again selected its own `python/`, passed the document/render check and all 14 skill/runtime checks, including isolation. This is relocation testing on this PC, not a second physical PC or clean-VM certification.
- Native UI opened from the portable release. The existing model selection (Gemma4), workspace and history remained. The environment dialog displayed five skill buttons and the Python path beside this executable. Opening/closing the dialog and returning to the ready composer were observed through native screenshots/accessibility. One normal Lab process remained (PID 24192 at verification).
- Main window requested size 1180 x 800 DIP; environment dialog 760 x 640 DIP. Native screenshots are recorded in the task transcript. Display DPI was not independently measured in this pass; multi-DPI/pixel parity is not certified. No H2 Notes baseline UI was implemented or changed.

This repair establishes package/runtime behavior. It does not change the previous model-quality acceptance failures into passes. No real-model task was re-benchmarked in this pass.

## Package

`D:/VSstudio/Nodepad/releases/H2-Agent-Lab-Portable-20260916-r1.zip`

Size: 145,501,184 bytes. SHA-256:

`904C558D995F12F59A03A4480649895ED38956AD4D16061842D39242BE0C7C3E`

Extract all contents to a writable local NTFS directory on Windows 10/11 x64. Do not run from the ZIP or copy only the executable. Models/Ollama, AI credentials and a Word-compatible desktop application are separate prerequisites. The archive does not contain user chat history, project documents, connection profiles or API keys. Local application data stays in the current Windows user's LocalAppData.

See [portable instructions](PORTABLE.md). Evidence logs are in [the evidence folder](evidence/2026-09-16-portable/README.md).
