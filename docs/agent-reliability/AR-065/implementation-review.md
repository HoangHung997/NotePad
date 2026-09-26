# AR-065 — reconciled wire repair and acceptance boundary

**2026-09-23 · Branch `feature/h2-agent-reliability-ar-000` · PR #3 draft/open/unmerged**

**Status:** implementation and E1/E2 validation are recorded in `evidence.json`; **AR-065 is NOT DONE until real H2/OpenAI E4 is accepted**. This document is not a claim that the original user's HTTP400 has been reproduced against the actual provider. No real OpenAI request, personal-document mutation, new credential/endpoint, Docker/engine replacement or main merge was performed.

## Reconciliation and ownership

The resumed repository already contained the AR-065 runtime foundation at `6d6fd8427498b00ba50a2273ded1af8d9467f5e5`, newer than the previous local ZIP/checkpoint. The old damaged `.b64` transfer and its failed integrity job were not executed again. Their replacement by direct source had already been delivered by another worker and was retained, not claimed as newly authored. User-PC working tree remained NOT_ACCESSIBLE; processing used verified committed-source snapshots and isolated clean Windows CI checkouts.

Current validation source is **`26d79367392a1f7f02d6acaef116e420ac9125dc`**. The application/test/tools trees of the full-CI test-merge checkout are compared to that exact source; the merge checkout is recorded in `evidence.json` and is **not a merge into main**. Later checkpoint changes are documentation/workflow-only and do not acquire an unexecuted full-CI PASS.

## Runtime foundation retained

`OpenAiResponsesWireContract` supplies deterministic collision-checked aliases at the Responses HTTP/WebSocket boundary. Internal registry names, call IDs, roles, arguments, resource scopes and journals retain their meaning. Deferred tool admission is atomic, unchanged re-admission is idempotent, and incompatible same-name replacement requires rebasing instead of silently changing an active contract. Unknown/unadvertised wire names fail before dispatch.

Function schemas are copied without optional-to-required/null rewriting, with `strict: false` explicit. Stateless output/reasoning replay and stored/WebSocket continuation identities are preserved. Request budget accounting remains on the actual serializer path. The exact Luna capability entry was checked against official model metadata; arbitrary suffixes and unrelated providers do not inherit it.

HTTP and streamed errors use bounded, allow-listed status/code/type/parameter/phase diagnostics. The HTTP error reader is capped at 16 KiB and two seconds, honors caller cancellation, and does not preserve raw provider-echoed messages or credentials. The existing production adapter's error/archive path is reused. No second diagnostics/evidence engine or automatic replay/fallback was added.

The four old-source negative-control cases test dotted-name projection and absence of an explicit non-strict field. Their legacy log phrase about “implicit strict normalization” is a fixture assertion label, **not an observation that a real server normalized or rejected the original request**. The exact original provider reason remains an E4 investigation requirement.

## Work completed in this continuation

Two retained AR-050 fixtures scripted `lookup` without advertising it. They now declare that tool before testing retained-context overflow; the existing rejection/budget/no-send assertions were kept. Host rejection of an unadvertised tool was not relaxed.

Ten new Global/Project cases exercise the real production adapter, AgentRuntime, concrete Responses HTTP serializer, disposable file IO and durable archive: initial HTTP400; HTTP400 after a read; HTTP400 after a real write; successful read/final; and an unadvertised tool. They verify exact call IDs, actual observations, unchanged control/source bytes, one creation, mutation/evidence retention and archive reopening without provider reallocation or replay. The HTTP handler is scripted, so these remain **E2, not real-model/UI E4**.

The new corpus initially failed to compile because it omitted the existing UI permission mapper namespace; that was corrected. Four read cases then exposed a missing required `offset` in the fixture request. The fixture now sends `offset="0"` and parses the leading domain JSON before the host evidence suffix, checking exact content, hash, offset and truncation rather than guessing JSON escaping. No production guard was weakened to accommodate the fixture.

A retained AR-040 Project roundtrip failed its demand for at least two polls despite reaching terminal completion. Its two-second child sleep was timing-dependent on a loaded runner. The disposable child now waits for two observed polls of the same job via a test-owned release marker and an independent twenty-second deadline. Existing one-start/one-effect, job identity, output, terminal ownership, progress and archive assertions remain. There was no AR-040 runtime redesign.

One-use delivery workflows used exact clean-HEAD/preimage checks and normal fast-forward pushes; completed writers were removed. No reset, force-push, branch deletion or main merge occurred.

## Executed validation

Commands are kept in the existing read-only validator:

```powershell
dotnet restore H2Notes.Avalonia.slnx
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore
python tools/agent-reliability/test_ar065_wire_control.py
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-065
# Three independent repetitions, followed by the full H2Notes test command.
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build
./tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar065/agent-suites
```

Exact results, run/job/attempt IDs, hashes and case inventories are persisted in `evidence.json`, not inferred from a green upload step. The focused artifact is downloaded and independently checked for ZIP CRC/SHA256, source file SHA256/Git blob equivalence, clean old-control restoration/rebuild, all PASS/RESULT counts, production receipts and the 74 independently invoked Agent suite exits. Full CI is separately checked through every mandatory step, full H2 log and publish/helper IPC.

The independent artifact checker initially compared committed LF source bytes directly to the Windows checkout restoration hash. Both restored transport hashes matched the exact LF-to-CRLF conversion, and the control had restored the original checkout bytes exactly and returned to a clean Git status before rebuilding. This is recorded explicitly in `restoration_eol_reconciliation`; it is not described as raw-byte equality between Git LF blobs and Windows CRLF files. No source/checksum was changed to bypass that check.

Historical failures remain retained: damaged-transfer run `35874131889`; two budget-fixture failures in `35875731324`; new-corpus compile failure in `35878985021`; four missing-offset fixture failures plus one retained job timing failure in focused/full run `35879547779`. These runs are not relabelled as passes. The last of those artifacts was independently verified as SHA256 `a061fe29e18e602f1ec6cdedf47dacb0131605d2c6e1801731a64b692aa23c2d`.

## Remaining acceptance and next action

Follow `real-h2-acceptance.md` using an authorized isolated Windows/H2 installation, an already configured exact Luna Responses profile and a finite operator-approved model budget. Execute read-only, harmless creation/readback and deferred-tool-load cases through the real H2 UI. Preserve sanitized diagnostics if OpenAI still refuses the request. Before repeating anything with uncertain effects, inspect the existing journal and postconditions.

E3/real Office and E4/model/UI are AWAITING_ENVIRONMENT. The hosted runner's packaged helper ping is not native document/model acceptance. AR-064 stays PARTIAL, AR-020/033 native gates and AR-051 real-model gate remain open, MB-124–127 are not closed, and AR-083 is DEFERRED_BY_USER. AR-066 is the next critical candidate only after the current checkpoint and appropriate dependency/gate decision; it was not implemented in this continuation.

Official contract references checked on 2026-09-23: https://developers.openai.com/api/docs/guides/function-calling ; https://developers.openai.com/api/docs/models/gpt-5.6-luna ; https://developers.openai.com/api/docs/guides/deployment-checklist . These guide the compatibility repair but do not substitute for the user's actual-provider evidence.
