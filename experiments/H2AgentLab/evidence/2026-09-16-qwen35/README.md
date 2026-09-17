# Qwen3.5 local evaluation evidence

Date: 2026-09-16. All document contents are synthetic. No real user document was edited.

See [the evaluation report](../../QWEN35_EVALUATION.md) for modes, timing caveats and the acceptance decision.

## Preserved logs

- `read-before.txt`: real model, original tool-loop implementation, default thinking; failure.
- `read-after.txt`: real model, corrected thinking replay, default thinking; empty completed response.
- `word-edit.txt`: real model, explicit thinking off; incomplete at the eight-minute evaluation limit.
- `excel.txt`: real model, explicit thinking off; draft produced but incorrect, incomplete at eight minutes.
- `regression-tests.txt`: 18 deterministic checks passed.
- `recovery-tests.txt`: 14 deterministic checks passed.
- `skills-tests.txt`: 14 deterministic checks passed.

The minimal native API probes were diagnostic calls with evaluator-supplied tool results, not end-to-end document tests. Their timings are summarized in the report; no raw probe log was retained. Numeric thinking counts are recorded, not private reasoning text.

## Independently observed artifacts

Word draft: `.artifacts/qwen35-20260916-word-nothink-01/state/runs/07d7effc51a2434fb50036ddeabc6f30/work/output/DNTT-test.docx` relative to the Lab directory. Draft and source SHA-256 both equal `20ED3A8CAEC0E77C6CD4A7561D816856A891CA93D232CD4CB1B1AC40E5642DB0`; the requested text was not added.

Excel draft: `.artifacts/qwen35-20260916-excel-nothink-01/state/runs/4fc67abbe1c042368e32e67d1317597c/work/output/result.xlsx`. Draft SHA-256 is `2B18C5B1D08422BB7D87F07AC033773DAB54E01E7F775B3678512E2898FC8431`. Independent read-only checks failed italic preservation for `Tasks!A2`, `Tasks!A3`, `Tasks!A4`. Source SHA-256 remained `0F53F30B271DD15FCAD9FDC4B6F8E7658D2C2118BF8AE0443724E7CDFE242BD4`. No published `workspace/result.xlsx` existed.

Neither draft was repaired by the evaluator. Neither case qualifies as an end-to-end pass. No production UI layout change or visual-parity certification is part of this test.
