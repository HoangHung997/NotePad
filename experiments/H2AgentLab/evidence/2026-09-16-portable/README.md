# Deployment regression evidence

- `portability-tests.txt`: optional arguments, actual skill discovery, missing bundle diagnostics, runtime location selection and required write arguments (8 checks).
- `regression-tests.txt`: general regression (18 checks).
- `recovery-tests.txt`: recovery behavior with simulated model turns and real tools (14 checks).
- `skills-tests.txt`: runtime/skill behavior (14 checks).
- `package-installation.txt`: actual self-contained release using its bundled Python to create/read DOCX, XLSX, PDF and render PDF inside AppContainer.
- `relocated-installation.txt`: repeat from the extracted ZIP at another path containing spaces. The log records which Python was selected.
- `relocated-skills.txt`: all 14 skill/runtime checks repeated from that extracted ZIP.

All file fixtures are synthetic. No real model, user documents or third-party API was used in this pass. Counts do not measure AI task quality. Native UI observations and outstanding DPI/second-machine checks are recorded in [the report](../../PORTABILITY_EVALUATION.md).
