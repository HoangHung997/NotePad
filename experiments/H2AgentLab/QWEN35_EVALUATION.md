# Qwen3.5 in AI Lab: local acceptance evaluation

Date: 2026-09-16. Status: evaluation complete; this configuration is not approved for autonomous editing of real documents or integration into H2 Notes.

## Environment and scope

- User-selected `qwen3.5:latest`, digest `6488c96fa5faab64bb65cbd30d4289e20e6130ef535a93ef9a49f42eda893ea7`, Q4_K_M, approximately 6.6 GB on disk.
- Ollama 0.34.1 on loopback, Intel i5-13500H, 16 GB RAM; CPU inference. No cloud credentials or user documents used.
- Tests use fresh, isolated workspaces. They do not reuse the user's large conversation, modify the selected workspace, or change their saved thinking preference.
- The normal agent keeps its existing unlimited total wait until completion, cancellation or network failure. Only evaluation commands have six/eight-minute budgets.
- Recorded times are observations on this computer, not a controlled comparison with Gemma; some calls reload the model or benefit from prompt caching.

## Real-model results

| Case | Mode | Observed result |
| --- | --- | --- |
| Read `brief.md`, original Lab build | Default thinking and context | Failed. Model called `read_file`, then the next response contained no answer or tool call. First tool took about 163 seconds; failure after about 190 seconds. |
| Read `brief.md`, corrected tool-turn thinking replay | Default thinking and context | Failed. Completed response after 167.7 seconds had 291 thinking characters, no answer and no tool call. `done_reason=stop`, 83 generated tokens. |
| Minimal native two-turn tool protocol probe | Explicit thinking on, 4096 context | Tool call and follow-up answer received. This probe supplied a synthetic tool result directly; it is not an actual file-operation test. |
| Minimal native two-turn tool protocol probe | Explicit thinking off, 4096 context | Tool call and follow-up answer received. This does not establish that the full Lab agent works. |
| Append a paragraph to existing Word, preserve old content/formatting | Explicit thinking off, default context | Not complete within the 8-minute evaluation budget. No published result. The generated draft is byte-identical to the source and lacks the requested addition. |
| Excel formatting while preserving formulas/data | Explicit thinking off, default context | Not complete within the 8-minute evaluation budget. A draft changed the requested font and fills, but lost the required italic formatting in Tasks!A2:A4. Its verification code crashed, and no result was published. |
| Recovery from a naturally occurring wrong staged-file path | Observed within the Word case, thinking off | Partial: model corrected `DNTT.docx` to `input/DNTT.docx` after the real traceback and ran again. It still copied the source instead of saving its edited document. It then confused the private script output with a workspace path. No end-to-end success. |

The thinking-on minimal probe took about 39.6 + 34.2 seconds; the thinking-off probe about 5.7 + 5.4 seconds. The latter ran after the former and could benefit from warm model/cache, so these are not a fair speed ratio.

Word source and generated draft both have SHA-256 `20ED3A8CAEC0E77C6CD4A7561D816856A891CA93D232CD4CB1B1AC40E5642DB0`. The second script used `shutil.copy` after editing the in-memory Document, rather than saving that Document. This independently confirms the draft has no addition even though the script exited 0. The model was not supplied a corrected script by the evaluator. Its final verification/publication did not complete before cancellation; this does not prove it could never recover with more time.

The Excel script produced its draft at approximately 7 minutes 41 seconds, then failed with `AttributeError: 'PatternFill' object has no attribute 'fills'` in its own verification. A separate read-only openpyxl check found exactly the three italic-preservation failures above; checked values, formulas, sheet names, dimensions, target fonts/fills and non-target font/fill/number-format properties passed. Source SHA-256 remained `0F53F30B271DD15FCAD9FDC4B6F8E7658D2C2118BF8AE0443724E7CDFE242BD4`. No evaluator repair was applied to the draft. This partial result is not a successful agent task.

The long Excel code-generation turn generated 796 tokens in 186.5 seconds, approximately 4.27 tokens/second, excluding prompt processing. Repeated path mistakes and slow generation both contributed to elapsed time; fresh history alone did not solve this. The exact cause of the default-thinking empty responses remains unresolved.

## Program regression results

The final normal Release build completed with zero warnings/errors. Deterministic suites passed 46/46 checks: 18 general, 14 recovery and 14 skills checks. Model responses in these suites are simulated where applicable; real file and sandbox operations are exercised. These results validate program behavior, not Qwen's autonomous ability, UI-control ability or visual document fidelity.

The Word and Excel real-model runs deliberately disabled thinking only in their isolated evaluation profiles. The user's saved model, thinking preference, workspace and history were not changed. Test cancellation at eight minutes is not a newly introduced timeout in the normal app.

## Decision

Keep the Lab experimental. This combination of local model, CPU hardware, prompt and tool interface did not complete the tested document tasks reliably. It is not evidence that all Qwen deployments are worse than Gemma, and no controlled Gemma comparison was run here. Prioritize clearer workspace-versus-staged-artifact guidance, reliable output readback and diagnosis of empty native tool responses before expanding real-file access. Re-run independent acceptance cases after changes rather than accepting the model's success message as proof.

## Integration corrections

Ollama's documented streaming tool loop carries `thinking`, `content` and `tool_calls` back together. Lab previously dropped `thinking`. It now preserves that provider-supplied field only in memory within the active tool loop, not in saved history or a future user turn. Explicit `OllamaThinking` settings are now honored. No hidden thought is fabricated or converted into an executable tool call.

The runner reports per-call numeric metrics to evaluation listeners and rejects tools from an Ollama `length`/`unload` completion. Empty completed responses remain explicit failures; there is no silent switch of model, automatic thinking-disable fallback or retry of uncertain writes.

Reference: [Ollama streaming tool calling](https://docs.ollama.com/capabilities/tool-calling).

## Word test oracle

The fixture contains mixed run formatting, a table, section margins and a header. The request is to append exactly `đã test thành công` and publish a new `DNTT-test.docx`. An independent checker compares the original body XML semantically, section settings, styles and header/footer, verifies the one additional paragraph, checks OpenXML validity and compares source bytes. The model never receives the oracle code. The full post-publication checker was not reached in this run because no output was published; byte comparison independently established the unchanged draft. Rendering and opening Word through the GUI are separate, unverified capabilities in this case.

## Evidence

Logs are preserved in [the evaluation evidence bundle](evidence/2026-09-16-qwen35/README.md). Original isolated fixtures and draft artifacts remain under `.artifacts/qwen35-20260916-*`. An empty response is not proof the task is impossible, but it is a failed acceptance run and must not be called a pass.
