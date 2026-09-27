# H2 Agent — Critical User-Reported Production Issues

**Date:** 2026-09-23  
**Status:** **BLOCKING REPAIR INPUT — USER-CONFIRMED FROM REAL APP USAGE**  
**Repository:** `HoangHung997/NotePad`  
**Implementation branch at capture:** `feature/h2-agent-reliability-ar-000`  
**Observed branch HEAD before this document:** `730203eb05c65c7b66fc7088068e6d8c934f979f`

> This file records four serious defects confirmed by the user while operating the real H2 application.
>
> These are not speculative roadmap ideas and must not be hidden by existing fixture/unit passes.
> They must be repaired and accepted through the production path before H2 Agent reliability can be considered ready.
>
> **Do not interrupt or rewrite the currently active AR-064 changes.** Finish/save the current AR-064 checkpoint first. Then execute the critical repair tasks referenced at the end of this document before starting the next ordinary reliability task.
>
> Physical two-PC/NAS acceptance remains **DEFERRED_BY_USER** and does not block these repairs.

---

## 0. Product decisions that remain unchanged

These defects do **not** authorize a redesign of H2.

Keep:

- H2 main screen = project/Command Center first.
- One Agent runtime for Global Assistant and Project Agent.
- Global mode uses whole-computer implicit grounding.
- Project mode uses project-anchored implicit grounding; explicit external targets may extend scope exactly as requested.
- Model decides the next work step; host owns permissions, state, execution, evidence and verification.
- Project data, Agent execution state and machine-local UI state remain separate sources of truth.
- Do not create a second Agent engine, AI Inbox database, ProjectState database, evidence store, or separate Work Assistant runtime.
- Do not replace the current engine with Docker/Open Interpreter/Cua/etc. merely because of these defects.
- Do not silently fall back from a live application resource to an on-disk file when that changes task meaning.

---

# ISSUE 1 — OpenAI GPT-5.6 Luna text chat works, Agent tool task returns HTTP 400

**Severity:** CRITICAL  
**User-visible symptom:** normal chat with GPT-5.6 Luna succeeds, but asking H2 Agent to execute a task such as creating an Excel file fails with HTTP 400.

Observed UI message resembles:

```text
HTTP 400: Máy chủ từ chối yêu cầu.
Kiểm tra giao thức, tên model và dữ liệu gửi...
```

## 1.1. What is already known

The same profile/model can answer ordinary text chat. Failure appears when the Agent path adds tool definitions/tool continuation.

Therefore the defect must be treated first as an **Agent ↔ OpenAI tool-calling wire compatibility problem**, not as proof that GPT-5.6 Luna cannot use tools.

Current OpenAI Agent path uses:

```text
H2 AgentRuntime
→ OpenAiResponsesTransport
→ Responses API
→ tools/function calls
→ H2 executes tool
→ function_call_output
→ continuation
```

The current transport also loses useful provider diagnostics on non-success responses: the production path throws a generic status-derived error instead of preserving a bounded/sanitized provider error body.

## 1.2. Primary hypotheses to verify, not blindly assume

### Hypothesis A — provider wire tool names

H2 internal callable names include names such as:

```text
excel.read_range
word.replace_range
autocad.inspect_file
```

The OpenAI transport currently forwards the internal name directly into the Responses function `name`.

The repair must verify whether GPT-5.6/OpenAI Responses rejects one or more of these provider wire names. If confirmed, keep internal H2 names unchanged and introduce a reversible provider-safe wire-name mapping, e.g.:

```text
internal: excel.read_range
wire:     excel_read_range
```

The mapping must be bijective within a request and continuation, collision-checked, deterministic, bounded, and tested when tools are dynamically loaded after `tool_search`.

Do **not** rename the entire H2 registry just to satisfy one provider.

### Hypothesis B — schema/tool continuation compatibility

Also inspect:

- tool JSON schema accepted by the exact model/API;
- dynamically loaded tool schemas on continuation;
- function call IDs / function_call_output matching;
- reasoning/stateless replay items;
- parallel_tool_calls behavior;
- unsupported model-specific request fields.

The exact provider error is required before choosing the final repair.

### Hypothesis C — GPT-5.6 capability metadata is stale in H2

H2's model capability table must be checked against the exact GPT-5.6 models the user selects. This may not be the direct HTTP 400 cause, but stale model metadata must not cause unsupported reasoning/tool request fields or hide supported settings.

## 1.3. Required repair behavior

1. Preserve a bounded, sanitized provider error:
   - HTTP status;
   - provider error code/type;
   - safe message;
   - failing parameter if provided;
   - request phase;
   - **never** API key/auth headers/full sensitive payload.
2. Reproduce a minimal real sequence with the authorized GPT-5.6 Luna profile:
   - text-only request;
   - request with only `tool_search`;
   - `tool_search` result;
   - dynamically load one simple tool;
   - continuation with that tool;
   - execute tool;
   - send function output;
   - final continuation.
3. Repair provider-wire compatibility without changing Agent semantics.
4. Add provider contract tests that fail if internal callable names or schemas cannot be mapped safely.
5. A normal text-chat test alone is not acceptance.

## 1.4. Acceptance

**E2:** recorded exact serialized Responses request fixture demonstrates safe wire mapping/continuation and bounded error diagnostics.

**E4 required for this issue:** through the real H2 UI with the user's authorized OpenAI profile, GPT-5.6 Luna must successfully execute at least:
- one read-only Agent tool task;
- one harmless file-creation task in a dedicated test folder;
- at least one deferred tool load after `tool_search`.

No destructive/personal-document test is needed.

If OpenAI still rejects the request, retain the exact sanitized provider reason and leave this issue open.

---

# ISSUE 2 — Live application target semantics are not reliable enough

**Severity:** CRITICAL  
**User-visible symptom:** user asks H2 to modify the Word file currently open on screen; Agent reports:

```text
word.replace_range (native_object_unavailable)
word.get_active_document (native_object_unavailable)
word.find_text (native_object_unavailable)
read_file (file_busy)
```

## 2.1. Important correction

H2 is **not** designed to always edit the disk file. The current production design already attempts a structured live Office path:

```text
H2 Agent
→ H2OfficeRuntimeTools
→ OfficeHostClient
→ OfficeHost
→ window/PID/view discovery
→ AccessibleObjectFromWindow / OBJID_NATIVEOM
→ live Word/Excel native object
```

The real failure is that the native live object could not be attached/reacquired, after which the Agent attempted a disk-file fallback that did not preserve the meaning of the user's request.

## 2.2. Required semantic rule

When user language explicitly refers to live state:

```text
"file Word đang mở"
"Excel đang active"
"ô đang chọn"
"bản vẽ đang chọn"
"trang web đang mở"
"trên màn hình"
```

the task target is a **LiveResource**, not merely a filesystem path.

Live and disk resources are distinct:

```text
WordLiveDocument != DocxDiskFile
ExcelLiveWorkbook != XlsxDiskFile
AutoCadLiveDrawing != DwgDiskFile
BrowserLiveTab != HttpUrlFetch
```

Disk fallback is allowed only when:
- the original request permits it; or
- user explicitly approves the semantic change.

Never silently replace unsaved/live state with a saved disk snapshot.

## 2.3. Required LiveResource behavior

A bound live resource must retain stable identity/provenance such as:

- provider;
- resource/session ID;
- process ID + process start identity;
- root/view HWND where applicable;
- document/workbook/drawing/tab identity;
- canonical path if saved;
- dirty/unsaved state;
- selected sheet/range/entity/tab when relevant;
- observed content/state version.

Read/find/mutate/verify operations use the same bound resource, not a fresh ambiguous “active document” lookup every step.

## 2.4. Recovery if live provider fails

For `native_object_unavailable`, `provider_busy`, `stale_resource`, etc., the host/Agent may:

1. confirm window/process still exists;
2. re-enumerate the same application;
3. reacquire the same resource;
4. check modal/busy state;
5. retry only after state changes;
6. use a same-target structured/provider fallback;
7. use UIA/accessibility on the **same window/resource** where semantically sufficient;
8. use visual/computer-use fallback only with correct target identity and readback.

Do not use a disk file as if it were the same live document.

If only partial UI content is observable, report partial provenance and do not claim full-document access.

## 2.5. Application-specific expectations

### Word / Excel

Structured native Office provider first. Live unsaved state must be supported or the limitation must be explicit.

### AutoCAD

Current file/Core Console capabilities do not prove live current drawing + SelectionSet semantics. A task about “block/entity đang chọn” requires a live native/plugin/provider path or must be reported unsupported/blocked. Do not substitute the saved DWG silently.

### Web

`web.fetch(URL)` is not the same as the currently open browser tab. A request about “trang đang mở” may require live browser session/tab identity, DOM/current logged-in state, or accessibility/browser provider. Do not claim HTTP fetch is the same resource when it is not.

### Generic desktop

Use structured app integration when available, then accessibility/UIA, then visual fallback while preserving exact target identity.

## 2.6. Acceptance

A dedicated real-app corpus must cover:
- Word live unsaved document;
- Excel live workbook and selected range;
- multiple same/similar documents/windows;
- changing focus must not redirect a pinned mutation;
- native reconnect after a transient failure;
- forbidden live→disk substitution;
- AutoCAD/browser capability must report truthful readiness if live provider is not implemented.

AR-020 E3 remains open until native Office evidence exists; this issue is not closed by fixture discovery tests.

---

# ISSUE 3 — Every meaningful failure must return to the Agent for recovery or a specific explanation

**Severity:** CRITICAL  
**User-visible symptom:** H2 often terminates with a generic line such as:

```text
Chưa hoàn thành: công cụ vẫn còn lỗi...
```

This is not an acceptable final Agent behavior when the model is still available.

## 3.1. Core rule

Every meaningful non-terminal failure must become a structured observation available to the Agent:

```text
failure
→ normalized typed outcome
→ Agent sees what failed and what changed
→ Agent reasons about the goal
→ safe recovery / alternate capability / clarification
→ verification
→ continue
```

Only when no goal-preserving allowed recovery remains may the task become Blocked/PartiallyCompleted.

## 3.2. Host vs Agent responsibility

The host may automatically handle deterministic infrastructure recovery such as:
- one bounded reconnect;
- refreshing a stale cache;
- retrying a known read-only transient operation;
- normal provider health refresh.

The Agent handles:
- deciding whether the failed approach is essential to the goal;
- finding an alternate tool/provider/skill;
- choosing a semantically equivalent method;
- deciding what can still be completed;
- explaining unresolved blockers.

The host must not invent the business explanation when the model is operational.

## 3.3. Failure observation requirements

Agent-visible failure should include, when known:

- stage;
- tool/provider;
- error code/type;
- target/resource identity;
- retry class;
- whether state changed since last attempt;
- mutation effect: None / Applied / PartiallyApplied / Unknown;
- what was attempted already;
- safe recovery candidates;
- forbidden semantic fallbacks;
- unresolved obligation IDs;
- evidence/failure references.

Do not expose secrets or huge raw exception dumps.

## 3.4. No blind retry

Same tool + same arguments + same resource version + same failure cannot repeat indefinitely.

A retry must be justified by changed evidence, for example:
- refreshed resource handle;
- provider health changed;
- modal closed;
- new permission;
- corrected arguments;
- different semantically valid tool;
- verified partial state.

Unknown mutation effects require read/reconcile before any replay.

## 3.5. Goal-centric recovery

One tool failing does not mean the whole task fails.

Example:

```text
Goal: create an XLSX file
Excel live provider unavailable
→ Agent may discover a safe OpenXML/openpyxl creation path
→ verify generated workbook
→ complete
```

But:

```text
Goal: modify unsaved Word document currently open
native Word provider unavailable
→ disk python-docx is NOT semantically equivalent
→ must recover same live target or explain blocker
```

## 3.6. Final blocked answer

If recovery is exhausted, runtime state may be Blocked, but the user-facing final response should normally be generated by the Agent from authoritative state and explain:

1. what was successfully completed;
2. what remains incomplete;
3. exact known cause;
4. recovery approaches attempted;
5. why unsafe/non-equivalent fallbacks were not used;
6. what user/app/configuration change is required to continue.

Do not hard-code one generic paragraph for every provider.

Example quality target:

```text
Tôi đã hoàn thành phần X và Y. Phần sửa tài liệu Word chưa thực hiện được vì H2
không lấy được native document object từ phiên Word đang mở (native_object_unavailable).
Tôi đã thử nhận diện lại đúng cửa sổ và kết nối lại nhưng vẫn chưa có session hợp lệ.
Tôi không chuyển sang sửa file DOCX trên đĩa vì bản đang mở có thể chứa thay đổi chưa lưu.
Để tiếp tục đúng yêu cầu, H2 cần khắc phục kết nối OfficeHost ↔ Word native object,
hoặc bạn phải cho phép dùng bản đã lưu trên đĩa.
```

Exact wording remains model-generated; the host supplies facts/state.

## 3.7. Model/API unavailable exception

If the model transport itself is unavailable (for example HTTP 400 before the Agent can receive the failure), the host may show a technical failure card built from safe structured diagnostics. It must still say:
- which phase failed;
- whether any side effect occurred;
- what remains;
- what configuration/repair is needed.

Once the model is available again, normal Agent-generated explanation resumes.

## 3.8. Acceptance

Test failures at:
- grounding;
- tool discovery;
- provider attach;
- permissions;
- execution;
- verification;
- artifact publish;
- retrieval/compaction;
- model continuation.

For each recoverable case, prove the Agent either repairs or changes method without a new user prompt.

For each non-recoverable case, prove the final answer is specific and evidence-backed rather than only “Chưa hoàn thành”.

This issue strongly intersects AR-041/AR-070/AR-080/AR-081 and must be included in those acceptance corpora.

---

# ISSUE 4 — Command Center “Cần bạn xử lý” cannot collapse and has no acknowledgement lifecycle

**Severity:** HIGH / PRODUCT-BLOCKING UX  
**User-visible symptom:** many attention items consume most of the Command Center and push project cards off screen. Opening an item does not remove it, so handled issues keep returning.

Current behavior observed in code:

```text
NeedsAttentionProjection
→ CommandCenterAttentionList
→ section visible whenever count > 0
→ clicking item only opens project/task details
→ periodic refresh rebuilds the same item
```

There is no collapse state and no acknowledged/handled metadata.

## 4.1. Required UI behavior

The “Cần bạn xử lý” section must support collapse/expand.

Recommended default:

```text
⚠ Cần bạn xử lý · 7   ▼
```

When expanded, show a bounded preview (for example 3–5 newest/highest-priority items) and “Xem tất cả” when longer.

The user's expanded/collapsed preference is machine-local UI state and must not be stored in shared project/NAS data.

The dashboard must remain project-management-first, not become an error log.

## 4.2. Acknowledgement semantics

Opening/handling an attention item must not mutate the authoritative Agent task from Failed/Blocked to Completed.

Keep:

```text
AgentTask.Status = Failed / Blocked / Waiting...
```

separate from:

```text
Attention acknowledgement = user has handled/accepted this notification
```

The attention list remains a projection from authoritative Agent/sync state plus a small acknowledgement overlay.

Do not create an AI Inbox database that copies errors/tasks/evidence.

## 4.3. Interaction requested by user

For the Command Center notification list, selecting an item should:

1. acknowledge that specific attention event;
2. remove it from the active “Cần bạn xử lý” list/count;
3. open the relevant Agent task/project/problem details;
4. retain the original task/error in history.

This matches the user's explicit request that after choosing an item to handle, it should no longer keep covering the dashboard.

If implementation wants a safer two-step design, that is **not** the currently approved behavior. The approved behavior is click/select → acknowledge + open.

## 4.4. Stable attention identity

Do not acknowledge only by TaskId, otherwise all future errors from the task would disappear.

Use a stable event/source identity incorporating the authoritative source revision/event, for example:

```text
source kind
+ source ID / AgentTaskId
+ error/attention code
+ source event/revision/sequence
```

Acknowledging one event hides only that event.

If the same task later produces a new failure/revision, a new attention item must appear.

Condition-based items such as WaitingForApproval or workspace sync warning disappear automatically when the source condition resolves.

## 4.5. Storage

Acknowledgement metadata is small, local/user-oriented state such as:

```text
AttentionId
AcknowledgedUtc
```

Do not copy:
- error body;
- project state;
- task state;
- evidence;
- full Agent history.

History remains queryable from authoritative sources.

## 4.6. Relationship to Issue 3

After Issue 3 is repaired, attention titles should prefer useful Agent-derived summaries such as:

```text
Không kết nối được với tài liệu Word đang mở
```

instead of dumping:

```text
word.replace_range(native_object_unavailable); ...
```

Technical detail stays in the Agent task/evidence view.

## 4.7. Acceptance

- collapse/expand works and is remembered locally;
- 20+ attention items do not hide project cards by default;
- clicking one item removes exactly that event from active attention immediately;
- reopening Command Center does not resurrect acknowledged event;
- original Failed/Blocked Agent task remains in history;
- a genuinely new error on same task appears as a new item;
- condition-resolved items disappear without manual acknowledgement;
- acknowledgement does not sync monitor/layout state through NAS;
- no second AI Inbox truth store is created.

---

# 5. Required execution order relative to current AR work

At the time this document was created, the worker had already progressed beyond AR-051 and was actively working on **AR-064 — Plugin/provider lifecycle**, with PR #3 still open/draft.

Do not throw away current AR-064 work.

Required sequence:

```text
Finish/save current AR-064 checkpoint
        ↓
CRITICAL REPAIR 1 — OpenAI/Luna Agent HTTP 400
        ↓
CRITICAL REPAIR 2 — LiveResource/native app semantics
        ↓
CRITICAL REPAIR 3 — Error-to-Agent recovery + specific blocked response
        ↓
CRITICAL REPAIR 4 — Command Center attention collapse/acknowledgement
        ↓
Critical integration acceptance
        ↓
Resume ordinary AR roadmap
```

The tracker assigns concrete AR IDs for these repairs.

---

# 6. Do not falsely close these issues

These are user-confirmed production defects.

The following are insufficient by themselves:

- source inspection;
- fixture-only provider;
- FakeAdapter;
- model-free scripted transport;
- existing “full CI” that did not reproduce the user's path;
- an old Word/Office acceptance fixture;
- normal GPT-5.6 text chat;
- local Gemma success;
- merely hiding errors from UI;
- marking attention item handled by changing Agent task to Completed.

Each repair must retain a regression that would have caught the user's observed failure.

---

# 7. Physical two-PC/NAS status

Unchanged:

```text
AR-083 = DEFERRED_BY_USER
```

These four critical defects are one-PC/product-path issues and must proceed independently.

Do not use the NAS deferment to postpone them.
