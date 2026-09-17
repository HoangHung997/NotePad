# Skill provenance

Recorded 2026-09-16 for the user's explicitly requested local adaptation.

The three `SKILL.original.md` files are unchanged snapshots of OpenAI-authored
Codex skills installed on this computer. Their original plugin manifests are
preserved beside them, including author and `license: MIT` metadata. These
snapshots are references, NOT active Lab instructions. No Codex account,
credentials, conversation history, model weights or private service/runtime
implementation was copied. Manifest license metadata alone is not a substitute
for reviewing the full distribution terms before publishing a redistributed app.

Source root:
`C:/Users/hoang/.codex/plugins/cache/openai-primary-runtime/`

Each source is `<plugin>/26.904.11930/skills/<plugin>/SKILL.md`; each manifest is
`<plugin>/26.904.11930/.codex-plugin/plugin.json`.

| Plugin | Original SKILL.md SHA-256 |
| --- | --- |
| spreadsheets | B2477F39319682CF156C8FAA36EAC9F039AAEA03A5F09AE72111C76910AE4E75 |
| documents | 9FCC13C3CC34746B134D4ECF4A3C96C9F464D59944FCC99862B2E69AC953F19E |
| pdf | 9E429BFC5ADA20CCF25A531484E3DCC5DA59811D936A7CC5DFD23DBF2DFADD31 |

Active adaptations in `skills/` retain goal interpretation, inspecting inputs,
preserving unrelated content and formatting, composing a task-specific solution,
readback and evidence-based verification. They replace unavailable Codex-only
APIs, container paths, artifact SDKs and rendering commands with the Lab's actual
Python/AppContainer execution contract. They explicitly disclose missing Word
layout rendering, OCR and spreadsheet recalculation. `coding` and `computer-use`
are Lab-authored guidance for its existing capabilities, not copied Codex tools.

The private Python runtime is a local copy of an existing CPython environment
with selected libraries. Setup preserves CPython LICENSE.txt and package
dist-info metadata/licenses. It does not install into or change the Codex runtime.

Progressive loading follows the publicly described pattern: expose skill
name/description, read the selected SKILL.md, then load resources only as needed.
Reference: https://learn.chatgpt.com/docs/build-skills
