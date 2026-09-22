# AR-032 — scoped source-backed history retrieval

Status: ACTIVE / NOT_RUN. Source must compile and pass E2 and full CI on its actual commit before acceptance.

The existing AgentIntegrationTaskArchive owns all journal source data. An in-memory sequence/hash locator index is rebuilt by the existing Apply/replay path; it stores no duplicate payloads and changes no journal schema. The production adapter registers search_history/read_history through its existing ToolSession/ToolRegistry. Both Global and Project use that same path.

Global defaults to the exact host thread; Project may use the same project or, when IncludeProjectContent is false, only its thread. Model-supplied TaskId/ThreadId filters narrow this authority, never widen it. Cross-thread Global browsing is not added: open the intended thread through the existing UI. Historical sources are data, not a new instruction, grant or proof of current completion. Correlation changes are rechecked on every read.

Search pages return at most three results and inspect at most 32 source payloads. A page with no match may still carry nextCursor. Query fence and filters are pinned; locators are invocation-local and bounded. Exact source sequence plus TaskId permits rediscovery after restart. Every source read checks on-disk bytes against the append/replay receipt, rejecting missing or rehashed edits. Current metadata is labelled separately from the historical version.

Read returns exact bounded text chunks with journal/version/content hashes. Existing h2a1_ ArtifactStore text is readable only when the selected source snapshot cites matching proof; no arbitrary LocalPath/URI is opened. Binary/public file artifacts remain metadata. Search/read are read-only and cannot provide current-task verification. Three unfinished source locators precede long context; raw prior goal/output/drafts are not placed in host-priority instructions. The original six Completed convenience window is not the sole continuation route.

Planned tests: RC-15 old unfinished work beyond six/200, literal Unicode formula/path retrieval, stable paging and invalid/foreign tokens, task/thread/project isolation, reclassified Global history, late source corruption, exact archived text and proof mismatch, ten real component checkpoint/reopen cycles, and concrete Global/Project registry runs with a scripted transport including historical-injection denial. The checkpoint test is not a semantic model summarizer or E4. Until logs are inspected, these are definitions, not PASS.

Remaining limits: local startup/index scans scale with retained bounded history; lexical search is not semantic search; model quality/native Office is not tested. Retrieval never resumes tool actions or uncertain writes (AR-041), and cannot close obligations from historical evidence. No engine/store/ProjectRecord ownership or UI layout redesign. AR-020 E3 pending, E4 NOT_RUN, AR-083 DEFERRED_BY_USER.
