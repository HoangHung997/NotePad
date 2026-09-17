# H2 Notes project guidance

## Approved product and UI baseline

Before changing UI, storage, or AI integration, read:
- `docs/APPROVED_PRODUCT_SPEC.md`
- `docs/UI_ACCEPTANCE.md`
- `docs/ui-concepts/2026-09-15-responsive-hybrid/README.md`

The user approved the ten responsive hybrid mockups on 2026-09-15.
Treat them as the visual baseline, not optional inspiration. Keep the original
PNGs and `BASELINE.json` unchanged unless the user explicitly approves a new
baseline. Do not replace the warm hybrid design with the earlier green table
or another concept. Earlier implementation documents describe the existing
prototype; they do not prove that the new baseline has been implemented.

For every implemented UI stage, run the actual app, capture the relevant state
at the recorded window size/DPI, compare it with the corresponding baseline,
and record evidence and interaction checks in the acceptance log. A successful
build or headless test does not establish visual parity. If direct visual
testing is unavailable, say so and leave that check unverified.

The new requirements include configurable storage folders with explicit
merge/overwrite/use-existing choices, one data file per project including chat,
and separate Ollama/multi-provider AI settings. Follow the safety and migration
rules in the approved spec. Never test overwrite against the user's live data.
Never store API secrets in project files or move them with shared project data.

Keep the WPF source and `_ver2` intact while working on the Avalonia branch.
Do not start implementation merely to acknowledge or record a design discussion.
After successful implementation builds, open the correct new app for the user,
without two processes writing the same data store.

