# AR-082 — Clean-profile portable and dependency preflight

Validated code: `2f3a0c995307ee4615ceb647ff66322a795fc90f`.

## Implemented package behavior

- self-contained Windows x64 package; target machine does not require external .NET;
- exact-source `portable-manifest.json` with SHA256/size for every packaged file;
- bundled Agent Python runtime for the supported document/Python environment;
- `VERIFY-PORTABLE.ps1` bootstrap plus in-app offline package verification;
- Unicode/space-path portable verification;
- clean-profile and configured-profile dependency matrix;
- packaged DesktopHost/OfficeHost IPC verification;
- privacy gate excluding H2 local configuration, credential vault, Agent journal/task records and workspace state;
- standard ZIP deliberately excludes multi-GB OCR models and reports OCR as optional configuration.

## Evidence boundary

AR-082 E3 is limited to a fresh GitHub-hosted Windows runner profile. The gate deliberately removes external .NET from PATH/DOTNET_ROOT and runs the copied package outside the repository path. It does not contact live model/search/browser providers or attach to native Office/AutoCAD documents.

The clean runner reports native/optional dependencies independently: missing Word/Excel, AutoCAD, model/search/browser or OCR does not become a false app-core failure.

The dedicated AR-082 package and full-CI user-facing package are separate package builds from the same source SHA. No bit-for-bit reproducible-build claim is made. Each package is bound to and verified by its own embedded manifest.

## Validation

- focused AR-082: 5/5;
- retained AR-081 6/6, AR-080 4/4, AR-042 6/6, AR-041 6/6, AR-062 6/6, AR-060 6/6;
- full H2: 1363/1363;
- required Agent suites: 75/75;
- dedicated run 36273538823 / job 108491843442: SUCCESS;
- full Avalonia CI 36273538843 / job 108491844035: SUCCESS;
- all 25 exact-SHA PR workflows: SUCCESS.

Final user-facing portable artifact 10917395115: 175,482,994 bytes; artifact SHA256 `7cdbbaf684f49aeb57288cfe75e1553f3795eadf5cd07c7047cace7e246521b6`. Independent ZIP verification passed. Embedded manifest inventories 2,969 files / 415,193,548 bytes; all listed hashes/sizes match; canonical manifest content SHA256 is `47ebd73523c72f7c9eb05f3b69977598f415df405b43c9f93bd840387ff8b460`.

E4 physical/native/provider acceptance remains deferred. E5 two-PC/NAS remains deferred.
