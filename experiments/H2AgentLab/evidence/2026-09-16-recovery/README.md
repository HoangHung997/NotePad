# Recovery stage evidence

2026-09-16. H2 Agent Lab only. Final Release build: zero errors/warnings.

- `recovery-tests.txt`: 14 passing tests, actual filesystem/Word/Python operations;
  model steps are intentionally simulated for deterministic fault injection.
- `regression-tests.txt`: 16 passing regression tests.
- `skills-tests.txt`: 14 passing skill/runtime tests.
- `gemma-recovery.txt`: real local gemma4:latest, recovered from a deliberately
  seeded missing-file result and read actual promt.docx, returned H2-REC-731 and
  the pending quantity-review task. Original bytes unchanged. One sample only.
- `before-test-assertion-fix.txt`: retained first test run; its search assertion
  wrongly assumed that an exact match must exclude fuzzy matches. Corrected to
  verify the exact candidate exists and all 215 supported files were scanned.

No real user documents were used in these tests. No online API was called. The
real model received the exact observed candidate as part of the failure result;
this is assisted recovery, not evidence it independently invented a search plan.
The final build added Windows access-denied classification and one more Word
validation test after that live run; the same recovery behavior was retained.

The existing Lab window was inspected on the native desktop before replacement:
task finished, Stop disabled, composer empty. Closed gracefully and replaced with
the final Release executable. No permission checkbox, workspace or model setting
was changed. No screenshot of the user's chat was saved. This stage adds progress
messages to existing controls, not a layout change; the interactive recovery
message layout itself has not had a separate native visual acceptance run.

All ten approved H2 Notes PNG hashes still match BASELINE.json. No changes to the
approved mockups, WPF, _ver2 or H2 Notes source were made in this stage. Existing
unrelated changes in the worktree were retained. No integration into H2 Notes.
