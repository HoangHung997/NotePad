# AR-051 — source-backed work compaction and cancellation boundary

Status: **IMPLEMENTED / AWAITING_ENVIRONMENT**. E1/E2 passed; **not DONE** because the required actual H2 UI/model/tools E4 subset is NOT_RUN. Native AR-020/033 and deferred AR-083 remain open.

## Exact code and observed evidence

Resumed existing AR-051 implementation at `ceaf0238c01d84389af9595a5325cf47c2e75671`; its fourteen prior commits were not overwritten or claimed as newly authored here. Runtime cancellation repair `c6432a216326890b30cb6aba14196e6aedbf0b59`, exact validated checkout `28e34454c7f44deebaa867121e661739a79470b5`. Focused 35835063919 and full 35835069955 are recorded in [acceptance.json](acceptance.json). Full PR test-merge checkout is verified equal for application/tests/tools, not a merge into main.

37/37 AR-051 in three repetitions; all retained AR corpora and 74/74 independent Agent suites pass. Full H2 tests: 1002/1002; publish and helper startup/IPC pass. Warnings are recorded, not claimed fixed.

## Existing production path retained

RuntimeCompactionCoordinator/CompactionManager, ArtifactStore and the existing Agent journal own sources and activation. Every safe boundary records complete public tool-call/result batches, exact source handles and host mandatory work state: latest goal revision, obligations, preservation constraints, observed evidence, pending operations and unknown effects. No hidden reasoning or parallel engine/database/transcript is introduced.

A candidate contains deterministic exact source excerpts, not a counts-only summary. All current host anchors and the latest two complete batches accompany bounded extracts. User revisions keep their user role; retrieved data remains Assistant data and cannot grant permission, fulfill an outcome, waive a requirement or authorize replay. Generic read_tool_output and scoped history retrieval resolve exact source bytes, including earlier checkpoint ancestors. Missing/corrupt/rehashed sources fail closed without silently rewriting them.

The same selected Ollama, Chat, Responses HTTP/stateless/stored or WebSocket transport previews the candidate, then checks its exact serialized fingerprint again before send. Request budget sequence and usage corrections survive; server continuation identity is cleared at a same-provider context rebase. This is not automatic model/endpoint switching. The journal activation is atomic locally, not an atomic transaction with the remote provider; send failure still stops safely with sources and evidence retained.

## Repaired cancellation defect

The previous coordinator checked cancellation before source admission but invoked the summarizer immediately after the durable callback returned. Cancellation arriving during admission could therefore still run summary work; a throwing/invalid summary could replace the original cancellation with a compaction error.

The repair checks the original token immediately after source admission. The successful source receipt remains intact, while summarization/checkpoint activation/provider send do not start. Six cases cover normal/throwing/invalid summarizers, with and without a previously activated checkpoint. The unchanged new tests reproduce six failures with only the old coordinator restored; exact current bytes are restored and rebuilt before positive tests. Current runs preserve the original cancellation token, exact formula/source identity, previous checkpoint and read-only archive reopening. No preemption guarantee is made once a synchronous summarizer has already begun.

## E2 scope and limits

Five transport modes each complete ten compaction cycles in each of three repetitions. Their 41 serialized fixture requests preserve roles/call pairs/current corrections and source formula `=SUM($A$1:$A$3)+0.125`. Pressure tests reduce estimated effective input from over 60,000 to under 10,000 while retaining the 60,031-character original output. For stored/WebSocket modes a rebase body may be larger than the tiny delta even while provider-held effective context shrinks; bytes and estimated token counts are not interchangeable.

Global and Project production cases use the actual registry, disposable file and journal: one real write survives ten cycles, the missing PDF remains a pending obligation, final state remains Blocked, and archive reopening does not rerun tools. Injected summary failure preserves its source and prior evidence without replay. The model transport and failure summarizers are fixtures, not live model or E4 acceptance.

Default compaction remains bounded (interval 12, minimum 4, pressure fraction 0.65, maximum 16 cycles). Summaries are deterministic source extracts plus structured state; no claim of unlimited memory, generative summarizer quality, multi-hour native recall, or restart replay is made. E4 must still exercise the configured authorized model, actual H2 UI and tools, source-grounded recall and failure handling with a finite budget.

## Handoff

Park AR-051 as E2 passed / E4 AWAITING_ENVIRONMENT, not DONE. Continue only independent AR-064 under tracker section 2.2 (dependencies AR-010/011/031 accepted): concrete production plugin/provider lifecycle, real trusted fixture package and RC-28. No AR-064 code is included here. Do not bypass native AR-020/033 or AR-041 dependencies. AR-083 remains DEFERRED_BY_USER; no physical two-PC certification or MB-124–127 closure.
