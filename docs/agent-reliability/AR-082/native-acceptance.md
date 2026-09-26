# AR-082 physical clean-machine acceptance — deferred

Use the final portable artifact **10917395115**, source SHA `2f3a0c995307ee4615ceb647ff66322a795fc90f`.

The CI clean-profile gate is already E3 for a fresh GitHub-hosted Windows runner profile. This file describes the remaining optional/physical acceptance; it must not be used to retroactively call the CI runner a separate physical machine or E4 native-provider test.

1. Copy/extract the complete ZIP to a new folder on an authorized Windows x64 profile/machine, preferably under a Unicode + spaces path.
2. Do not install .NET for the test. Run `VERIFY-PORTABLE.ps1`.
3. Confirm `portable-check.json` reports manifest PASS, bundled Python Ready, helper IPC Ready and document/Python environment Ready.
4. Confirm missing optional/native dependencies are listed independently as NeedsConfiguration/Unavailable rather than crashing the core app.
5. Configure only the model/provider/native applications the user intends to use. Re-run preflight; configured items may be detected but live provider/native E4 requires the corresponding task-specific acceptance.
6. Confirm no API key/token/local-config/Agent journal/workspace is present inside the extracted portable folder. Credentials remain machine/profile-local.
7. Open H2 Notes and inspect normal UI startup. Native Office/CAD/model/search/browser behavior is tested under their own deferred E4 gates, not inferred from portable startup.
8. OCR is optional and intentionally absent from the standard ZIP; install/package a separate OCR runtime only if needed.
9. Keep the source machine/package until this physical test completes.

AR-083 two-PC/NAS remains separately DEFERRED_BY_USER and is not satisfied by this clean-machine test.
