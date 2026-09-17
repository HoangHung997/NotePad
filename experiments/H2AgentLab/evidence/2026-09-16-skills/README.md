# Skill agent verification, 2026-09-16

Separate H2 Agent Lab stage, not an implementation or replacement of the ten
approved H2 Notes mockups. Original baseline PNG hashes checked: 10 match,
0 changed. WPF, `_ver2` and H2 Notes source were not edited during this stage.

## Automated evidence

- `regression-tests.txt`: 16/16 pass, real file operations and simulated model streams.
- `skills-tests.txt`: 14/14 pass, real Windows AppContainer/Python executions,
  synthetic documents, mocked Ollama/API tool loops including actual image bytes.
- `gemma-skills-first.txt`: real local model, incomplete task. No spreadsheet
  result published; this is NOT a passing evaluation.
- `gemma-skills-second.txt`: incomplete after the eight-minute benchmark limit;
  one script startup overlapped a second process and exposed the runtime ACL race.
- `before-concurrency-fix.txt`: retained failing run, 13/14, no hidden failure.
- `concurrent-a.txt`, `concurrent-b.txt`: 8/8 runs per simultaneous process after
  adding an OS-released exclusive runtime lease. The regression/skill logs above
  were replaced with the final post-fix reruns, both passing. Real Gemma was not
  rerun after this final concurrency fix.

All automated fixtures use dedicated test directories, not live project data.
Script/task payloads in the test harness are intentionally different from a
model choosing and writing the payload. Both layers are assessed separately.

## UI evidence

Release executable actually run. `ui/lab-1180x800-render96.png` and
`ui/lab-840x620-render96.png` render actual controls at 96 DPI with synthetic
chat explicitly labelled as illustration, not a real successful AI task.
The computer's actual native scaling was 125%; see `ui/capture.txt`.

Also opened the regular Lab window on the desktop, observed it responding,
clicked **Ky nang & moi truong**, observed all five skill entries and private
runtime-ready status, opened **spreadsheets**, read its real guidance and closed
the viewer/dialog back to chat. Native captures:

- `ui/skills-native.png`: skills dialog, 760 x 640 logical content size at 125%.
- `ui/spreadsheet-skill-native.png`: skill text viewer, 850 x 650 logical content
  size at 125%.

No key, permission checkbox, workspace or AI connection changed during UI
inspection. No test prompt submitted through the user's existing chat and no
capture of the user's chat content was stored in this evidence directory.

The compact render shows the last sidebar actions below the fold; the sidebar
has a scroll viewer. Send/Stop/composer remain visible. No full DPI matrix,
keyboard-accessibility audit or model-driven desktop task has been certified.

The native inspection helper failed to map the skills dialog title-bar Close
button twice (missing geometry, then outside-window coordinates). A fresh
observation plus Alt+F4 closed it and returned to the main Lab window. This was
not counted as successful point-and-click coverage.
