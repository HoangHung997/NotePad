# H2 Agent — Đặc tả triển khai độ tin cậy và công việc dài

**Mã:** H2-AR-SPEC · **Phiên bản:** 1.0 · **Ngày:** 22/09/2026  
**Repository:** `HoangHung997/NotePad` · **Nhánh chuẩn tại thời điểm soạn:** `main`  
**Mốc tài liệu đã đọc:** `ad8c1082e722546a997b4e78b687980d4766622f`. Báo cáo nguồn đối chiếu code tại `9265c75c4be60e4019d655ef049f66ffc6f779bc`.  
**Trạng thái:** đặc tả triển khai theo yêu cầu người dùng; không phải biên bản đã thực hiện, không tự đóng bất kỳ test/task nào.

Đọc cùng [tracker triển khai AR](H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md). Hai file này là đặc tả và tracker chi tiết **cho đợt hoàn thiện này**, không phải hai sản phẩm hay hai engine mới.

## 0. Thẩm quyền và cách sử dụng

Giữ [Agent Master](H2_AGENT_MASTER_SPEC.md), [Product Master](H2_PRODUCT_MASTER_SPEC.md), [Chat Surface](H2_AGENT_CHAT_SURFACE_SPEC.md) làm nền. Tài liệu này cụ thể hóa các khoảng trống trong [báo cáo hợp nhất](H2_AGENT_CONSOLIDATED_REVIEW_AND_SOLUTION_2026-09-22.md), đặc biệt G01–G13 và MB-124–MB-127. Không triển khai lại các MB cũ đã có bằng chứng tương đương.

Trong phạm vi độ tin cậy, trí nhớ, context, công cụ và phục hồi, yêu cầu chi tiết ở đây bổ sung các yêu cầu cấp cao. Không dùng một mô tả lịch sử để bỏ qua yêu cầu mới. Khi có xung đột **thực chất** về sản phẩm/quyền/phạm vi, ghi vào tracker và xin quyết định; không tự đổi kiến trúc. Những sai khác tên file, namespace hoặc refactor tương đương có thể tự xử lý bằng bảng đối chiếu.

Khi bắt đầu phải đọc HEAD mới nhất và trạng thái nhánh đang làm. SHA ở trên chỉ là mốc nghiên cứu, **không phải yêu cầu checkout/reset về code cũ**. Mọi nhận định baseline dưới đây phải được xác minh lại trước khi sửa.

## 1. Mục tiêu và giới hạn của đợt triển khai

### 1.1. Kết quả cần đạt

H2 phải thực hiện được công việc qua **app thật → adapter production → runtime → công cụ thật → đọc lại → kết quả có bằng chứng**, giữ đúng yêu cầu sau nhiều vòng làm việc và sau gián đoạn. Agent phải biết đã làm gì, chưa làm gì, đang chờ gì, kết quả nào chưa chắc chắn và có thể truy nguồn khi context không chứa đủ dữ liệu.

Mức thành công không được đo bằng số class, số tool có tên, số checkbox đã đánh hoặc phần trăm giống một sản phẩm khác. Đo bằng corpus được khai báo, request thực, kết quả công việc, tác động ngoài phạm vi, chất lượng nhớ và khả năng phục hồi.

### 1.2. Những quyết định giữ nguyên

- Giao diện chính vẫn là **quản lý dự án/Command Center**. Không chuyển cả app thành chat-first.
- Global Assistant/bong bóng và Project Agent dùng một runtime logic, một hệ tool, một hệ quyền và cùng renderer chat theo Chat Surface.
- Model chọn bước làm tiếp theo dựa trên mục tiêu/quan sát. Host quản trạng thái, quyền, thực thi, giới hạn và kiểm chứng. Không hard-code workflow Word → Excel → Web vào core.
- ProjectRoot là phạm vi chọn đích **ngầm định**, không tự động đồng nghĩa sandbox bảo mật. Đích ngoài dự án cần chỉ định rõ; quyền thực thi vẫn được kiểm riêng.
- Dữ liệu dự án, Agent execution state và state giao diện máy phải có chủ sở hữu riêng, không sao chép thành nhiều nguồn sự thật.
- Không bắt người dùng đổi hostname để dùng hai PC. Không đồng nhất DeviceId với tên máy.
- Test **hai PC/NAS thật được hoãn theo người dùng**: không chặn việc triển khai phần độc lập, nhưng luôn ghi nợ nghiệm thu và không tuyên bố multi-PC đã được chứng nhận.

### 1.3. Không thuộc yêu cầu bắt buộc

Không viết lại toàn bộ Agent; không bắt cài Docker Desktop; không tách mọi tool thành service; không bắt gắn Open Interpreter/Cua/UFO; không vector database/knowledge graph/marketplace lớn; không tự nâng quyền Windows; không thay đổi Coordinator/NAS protocol trong đợt này nếu chưa có task và quyết định riêng.

Python/OpenXML và các thư viện tạo tài liệu vẫn là đường hợp lệ. Không ép mọi thao tác phải dùng COM, cũng không dùng bản file đã lưu thay cho tài liệu live chưa lưu mà không nói rõ.

## 2. Baseline: giữ, sửa, thu gọn và hoãn

### 2.1. Thành phần hiện hữu cần tái sử dụng

| Nhóm | Đường dẫn/điểm nối tại mốc nghiên cứu | Quyết định |
|---|---|---|
| H2 production | `src/H2Notes.Avalonia/App.axaml.cs`, `experiments/H2AgentLab/Integration/H2ProductionAgentAdapter*.cs` | Giữ cầu nối thật; thống nhất chuẩn bị context và state; không quay lại adapter giả |
| Runtime | `Runtime/AgentRuntime.cs`, `Runtime/AgentRuntimeFactory.cs`, `Tasking/AgentOrchestrator.cs` | Giữ vòng model/tool; bổ sung điểm kiểm soát request, nghĩa vụ nhiệm vụ và phục hồi |
| Context | `Context/AgentContextManager.cs`, `Session/RuntimeCompactionCoordinator.cs`, `Session/CompactionManager.cs`, `Prompting/` | Giữ nền; sửa nén theo nội dung có nguồn và kiểm soát từng request thật |
| Transport | `Transport/IAgentTransport.cs`, `OllamaTransport.cs`, `ChatCompletionsTransport.cs`, các Responses transport | Giữ wire protocol; hỗ trợ đo/rebase continuation an toàn, không tạo HTTP client khác trong UI |
| Tool | `Tools/ToolRegistry.cs`, `NormalRuntimeToolRegistry.cs`, `DeferredToolDiscovery.cs`, `ToolExecutionScheduler.cs` | Giữ registry; hợp đồng hóa kết quả, tránh danh sách tool quảng bá nhiều hơn khả năng thực thi |
| Skill | `Tools/SkillRuntimeTools.cs`, `Skills/` | Giữ nạp theo nhu cầu; thống nhất tên callable với prompt |
| Production providers | `Integration/H2ProductionToolSession*.cs`, `H2OfficeRuntimeTools.cs`, `H2LocalCommandTool.cs` | Giữ composition; bổ sung lỗi, tài nguyên, job và quyền thống nhất |
| Office/desktop | `H2AgentLab.OfficeHost/`, `OfficeProtocol/`, `DesktopHost/`, `DesktopProtocol/` | Giữ helper cô lập; sửa discovery, đọc theo phần, ghi an toàn và chẩn đoán |
| Evidence | `Session/ArtifactStore.cs`, archive/thread của production, `Verification/` | Mở rộng tại chủ sở hữu Agent; không thêm kho evidence riêng trong H2 UI |
| Project/sync | `src/H2Notes.Core/`, `src/H2Notes.Coordinator/` | Giữ thẩm quyền hiện hữu; nối event/correlation qua API, không ghi tắt shared JSON |

Tên class mới bên dưới là **tên logic đề nghị**, không phải lệnh tạo đủ từng class/thư mục. Tái sử dụng/refactor tương đương được chấp nhận nếu giữ hợp đồng và test.

### 2.2. Bất nhất phải xác minh và sửa sớm

1. Prompt nhắc `search_skills` nhưng callable chuẩn ở mốc nghiên cứu là `list_skills`.
2. Schema/adapter Excel chấp nhận 200 cells trong khi OfficeHost chặn trên 128.
3. Architecture guard danh sách callable chưa phản ánh `read_tool_output` đã được thêm.
4. Nhiều tool đọc Excel vẫn gọi snapshot toàn workbook, giới hạn tổng UsedRange 5.000 cells; đây chưa phải đọc vùng có phân trang.
5. Token snapshot Excel có cả selection/active sheet; cần tách ngữ cảnh UI khỏi phiên bản nội dung.
6. Kiểm tra từng ô trong vòng ghi Excel có thể để lại thay đổi một phần nếu lệnh sau lỗi.
7. Word discovery/context capture 3 giây khác timeout thực thi Office 60 giây; không nuốt mọi lỗi thành `null` mà mất chẩn đoán.
8. Nén trong Lab, dựng context production, lịch sử UI và history nội bộ transport chưa chứng minh cùng pipeline.
9. Archive có thể xem lại nhưng đánh Failed khi process cũ kết thúc; chưa đủ bằng chứng resume.
10. Fetch/feed đang có backend thật; search/browser tổng quát chưa được tính là đã có chỉ vì lớp giao diện tồn tại.

Không sửa theo danh sách này một cách mù quáng. Nếu HEAD mới đã sửa, chạy đúng regression, dẫn commit và đánh `ALREADY_SATISFIED` có bằng chứng trong tracker.

### 2.3. Chỉ bỏ sau parity

Giảm dần prompt tự ghép, duplicate context builders, executor switch hoặc wrapper thực sự trùng. Không xóa code chỉ vì tên có `Legacy`, `Lab`, `AgentTools`. Chứng minh call path không dùng, thay thế có test, CI qua và migration lịch sử an toàn trước khi bỏ. Các nguồn WPF/WinForms lịch sử không phải mục tiêu dọn của đợt này.

## 3. Bản đồ trách nhiệm và một đường chạy chung

```text
H2 Project UI / Global Assistant
  → invocation có scope + nguồn yêu cầu
  → H2 production adapter
  → Agent-owned work state + context preparation
  → AgentRuntime
       model quyết định bước
       → tool discovery
       → validation / target binding / permission / scheduling
       → provider hoặc job
       → observation + evidence
       → verification / work-state update
       → budget kiểm tra trước request tiếp theo
  → completion hoặc waiting/blocked/interrupted
  → UI chiếu lại typed events và kết quả
```

Lab và H2 có thể có composition root khác để thử fixture, nhưng phải dùng chung các primitive chuẩn bị context, áp ngân sách, completion và recovery. Test phải chứng minh đường production gọi những primitive đó; không yêu cầu mọi cửa sổ phải đi qua một class UI của Lab.

**Model không cấp quyền; UI không quyết định verifier pass; tool success không tự đóng task; một bản tóm tắt không thay nhật ký nguồn.**

## 4. Danh tính phiên và dữ liệu bền vững

### 4.1. Phân biệt các ID

| ID | Ý nghĩa |
|---|---|
| WorkspaceId / ProjectId | Dự án/workspace H2; không phải đường dẫn máy |
| ThreadId | Hội thoại chứa nhiều yêu cầu |
| TaskId | Đơn vị mục tiêu người dùng đang theo dõi |
| GoalRevisionId | Phiên bản yêu cầu hiện hành trong task |
| TurnId / ProviderContinuationId | Lượt/protocol model; không dùng làm TaskId |
| ToolCallId | Định danh của protocol model cho một đề nghị tool |
| InvocationId | Một lần host tiếp nhận thao tác; giữ qua mất phản hồi khi backend hỗ trợ |
| LogicalOperationId | Liên kết các lần thử cùng một kết quả cần đạt; không đồng nghĩa được phép ghi lặp |
| JobId | Công việc dài thực thi ở worker/process |
| ResourceId / ResourceVersion | Đích và phiên bản nội dung đã quan sát |
| EvidenceId / CheckpointId | Bằng chứng và mốc trạng thái có thể truy hồi |

Không tạo một ID mỗi lần poll job rồi coi đó là công việc mới. Không dùng PID trần làm danh tính bền vững: PID có thể được tái sử dụng, cần instance/start identity.

### 4.2. Chủ sở hữu lưu trữ

**H2 project store:** công việc người dùng, notes, links, project metadata.  
**Agent store:** task, revision, operation/job journal, verifier, evidence, context checkpoint, thread execution.  
**Local UI store:** geometry, monitor, bubble, draft, active capture và lựa chọn máy.  
**Coordinator:** giữ thẩm quyền event/queue dự án khi đường Coordinator được cấu hình, theo spec riêng.

Nâng cấp archive Agent hiện hữu hoặc thêm journal bên trong cùng Agent store; không viết `ProjectState` thứ hai. Lưu summary cache để UI chạy nhanh được phép nếu có thể rebuild từ nguồn.

### 4.3. Journal tối thiểu

Mỗi event có schema version, EventId, TaskId, sequence cục bộ có thứ tự, UTC, kind, payload/hash hoặc reference, provenance và actor class. Thứ tự logic dựa sequence/revision, không dựa riêng giờ máy. Thời gian là metadata, không phải bằng chứng thứ tự toàn hệ thống.

Ghi intent trước khi gửi mutation; sau đó ghi accepted/started/result/verification/reconciled theo quan sát thật. Checkpoint ghi temp, validate, activate bằng primitive hiện hữu phù hợp. Phát hiện file hỏng phải quarantine + báo lỗi + giữ nguồn để phục hồi, không âm thầm biến lịch sử thành rỗng thành công. Quy định fsync/flush và atomic replacement được kiểm theo nền tảng, không hứa nguyên tử cho mọi NAS.

Trimming index không được xóa nguồn còn được task/checkpoint/artifact tham chiếu. Retention phải có thể cấu hình, tránh lưu credential, cookie, raw auth headers hoặc toàn tài liệu nhạy cảm vào log debug.

## 5. Invocation và chọn đích: Global / Project

### 5.1. Context tối thiểu

`Mode`, `ProjectId?`, `ProjectRoot?`, linked resources đã được người dùng gắn, explicit targets với nguồn chỉ định, active work capture có thời điểm và identity, permission scope riêng, model profile và thread/task/revision.

Global có toàn bộ capability đã được bật, nhưng “toàn máy” không mặc nhiên nghĩa quét tất cả ổ đĩa. Lời nói chỉ trỏ như “file này/đang active” dùng tài nguyên gắn với context active đã bắt đúng lúc mở trợ lý, rồi được tái xác nhận khi cần.

Project có cùng capability. Khi không nêu đường dẫn, chỉ dùng project root/linked resource và tài liệu đang mở thuộc scope đó. Ngoài project chỉ trở thành target qua chỉ định rõ của người dùng hoặc resource đã gắn rõ trước đó; văn bản trong file/Web không tự cấp scope.

### 5.2. Tránh hiểu sai “active”

Phân biệt “file Excel đang mở” với “file Excel đang active/ô này”. Trong project, nếu active ngoài project nhưng có đúng một workbook project đang mở, có thể đề xuất/chọn workbook project cho câu nói chung “đang mở” theo Chat Surface và hiển thị đích rõ. Nếu người dùng nói rõ **đang active/selection này**, không tự thay bằng tài liệu project không active: báo không có active target hợp lệ và yêu cầu chọn hoặc chỉ đường dẫn.

Nhiều candidate tương đương phải hỏi; không chọn theo tên gần giống hoặc thứ tự danh sách. Không tự mở một file đóng khi yêu cầu đặc biệt nói bản đang mở chưa lưu.

### 5.3. Kiểm scope và permission riêng

Resolve và canonicalize file/UNC/mapped alias theo identity hiện có. Không so prefix chuỗi đơn giản (`Project` không bao gồm `ProjectOther`). Xử lý symlink/junction/reparse point theo chính sách thật; fail closed khi không xác định được đích mutation.

Path explicit chỉ mở đúng file hoặc folder được chọn. “Đọc” không tự cấp quyền “xóa/ghi”. FullAccess do người dùng chọn có thể cho thao tác rộng không hỏi lại, nhưng không bỏ target grounding, bảo vệ bí mật, logging và verification. Không tự nhắc quyền sau khi bị từ chối bằng cách đổi sang Python/shell.

Đổi project/thread trong UI không đổi scope tác vụ đang chạy. Gắn task Global vào project sau đó chỉ đổi correlation lịch sử; không tự mở rộng quyền hay đổi file tác vụ đang xử lý.

## 6. Hợp đồng công cụ thống nhất nhưng nhỏ

### 6.1. Descriptor

Tái sử dụng ToolDescriptor: tên chuẩn, namespace, description, input/output schema version, access/effect class, supported operations, dependencies, readiness, resource semantics, concurrency key và verifier capability. Tên callable/hướng dẫn phải sinh từ nguồn thống nhất.

**Registered ≠ ready ≠ exposed ≠ verified.** Provider unavailable có thể xuất hiện trong kết quả discovery dưới dạng capability chưa sẵn sàng kèm lý do; không được quảng cáo như tool đang chạy tốt hoặc biến mất không giải thích.

Readiness tối thiểu: Ready, Degraded, NeedsConfiguration, Busy, Unavailable, Unsupported. Tool của endpoint chưa cấu hình phải hướng dẫn cấu hình rõ; không giả search bằng URL hoặc giả thao tác browser bằng chuỗi văn bản.

### 6.2. Kết quả chung

Tên field dưới đây là contract logic; adapter tương thích dần được phép:

```json
{
  "schemaVersion": 1,
  "invocationId": "...",
  "logicalOperationId": "...",
  "status": "Succeeded|Running|Rejected|Failed|Cancelled|PartiallyApplied|OutcomeUnknown",
  "effect": "None|Applied|PartiallyApplied|Unknown",
  "resource": {"id": "...", "observedVersion": "..."},
  "data": {},
  "artifactRefs": [],
  "evidenceRefs": [],
  "completeness": {"complete": false, "nextCursor": "...", "reason": "paged"},
  "job": null,
  "error": null,
  "verification": {"status": "NotRun", "reportRefs": []}
}
```

Payload Word/Excel/CAD giữ type chuyên biệt. Không serialize toàn bộ document vào envelope. Error tối thiểu: code, phase, safe message, retry class, recovery candidates, mutation effect và provider/version. Recovery gợi ý là dữ liệu, không tự cho phép host/model vượt scope.

Phân loại lỗi cần có: invalid_arguments, unknown_tool, unsupported_operation, needs_configuration, resource_not_found, ambiguous_target, stale_resource, provider_busy, modal_blocked, permission_denied, connection_lost, deadline_exceeded, rate_limited, partial_result, verification_failed, partially_applied, outcome_unknown.

Mất phản hồi sau lúc có thể ghi khác lỗi validate trước khi ghi. Không trả mọi trường hợp thành `tool_failed → retry`.

### 6.3. Sửa hợp đồng mà không gỡ bảo vệ

Dùng một nguồn khai báo giới hạn batch, byte và pagination. Bài test so schema, adapter, protocol và backend. Không tự cắt bớt 200 ô xuống 128 rồi báo xong; phải chia batch có identity/kiểm chứng hoặc từ chối trước ghi.

Provider mới cài không được ép mọi tác vụ hiện hành fail vì registry version đổi. Pin implementation/schema thực sự đã dùng, refresh an toàn khi không có call phụ thuộc in-flight. Giao thức không tương thích cần tái bind, không bê nguyên live handle sang provider mới.

## 7. Tài nguyên: file, live document, cửa sổ và browser

Resource binding tối thiểu: provider instance/version, ResourceId, kind, local machine/session, process ID/start identity khi có, window/view identity, document identity, canonical path nếu đã lưu, content version, UI state token nếu cần, dirty state và bound-at.

**Đường dẫn là thuộc tính, không phải luôn là danh tính của tài liệu live.** Save As/rename có thể thay đường dẫn; provider phải chứng minh continuity hoặc báo cần rebind. Sau restart, không tái dùng COM object/HWND/PID cũ mà không kiểm chứng.

File trên disk và live document là hai resource liên quan. Kết quả phải cho biết provenance `DiskSnapshot`, `LiveDocument`, `UIAVisibleContent`, `ScreenOCR` hoặc tương đương. Agent không được gọi UIA/OCR giới hạn là “đã đọc toàn văn”.

Mọi read page cần resource version/cursor; nếu nội dung đổi, invalid cursor hoặc read view mới có nhãn rõ. Không ghép trang 1 phiên bản A với trang 2 phiên bản B thành một tài liệu hoàn chỉnh giả.

## 8. Office: định danh, đọc và ghi an toàn

### 8.1. Discovery

Liệt kê instance/window/view có thể gắn được; không lấy một `GetActiveObject` rồi mặc định là cửa sổ người dùng đang nhìn. Giữ discovery metadata rẻ, không đọc mọi workbook/document để tìm một đích.

Có thể thử API native theo cửa sổ như AccessibleObjectFromWindow/OBJID_NATIVEOM khi phù hợp, nhưng phải probe và nghiệm thu đúng Office/build. Không coi tên API là bảo đảm tương thích. Không thay bằng fuzzy-name mutation.

Phân biệt discovery deadline, execution deadline và ứng dụng busy/modal. Nếu enrichment thất bại, giữ thông tin cửa sổ đã biết và trả typed reason, không âm thầm làm mất context. Cache binding có invalidation để không start/stop helper vô ích theo từng UI tick.

### 8.2. Excel đọc theo vùng thật

Tool đọc phải nhận workbook resource, sheet identity, range/query và trường cần đọc. Metadata sheet/used extent không kéo toàn bộ cells. Ưu tiên đọc value/formula theo khối; định dạng chỉ vùng cần. Các vùng lớn trả page/cursor và completeness.

Thay ngưỡng full-workbook 5.000 cells bằng giới hạn **mỗi page/kết quả** được đo, không đơn giản tăng ngưỡng tổng. Corpus ít nhất có workbook vượt 5.000 cells, nhiều sheet, sparse UsedRange và yêu cầu chỉ vài ô. Đo số cell/byte đọc thực.

Content version không phụ thuộc riêng vào selection, active sheet hoặc vị trí cửa sổ. Với thao tác trên selection, bind selection ban đầu thành target cụ thể; đổi focus không được redirect mutation. Dữ liệu mục tiêu thay đổi thật phải bị phát hiện.

### 8.3. Excel ghi theo batch

Preflight toàn batch trước lần ghi đầu: resource, sheet, địa chỉ, duplicate/overlap, formula/value conflicts, merge/protection, kích thước, quyền và phiên bản. Không coi preflight là đủ để hứa transaction COM nguyên tử.

Ghi intent + trạng thái trước của vùng cần; serialize cùng tài nguyên ở phạm vi host, không chỉ lock một task. Nếu hỗ trợ rollback/undo thì kiểm chứng nó; nếu không, báo PartiallyApplied/OutcomeUnknown và đọc lại, không che lỗi.

Mỗi chunk có invocation/revision/target riêng và thuộc cùng logical operation. Sau ghi, đọc lại target và guard region/structure cần bảo toàn. Công thức phải kiểm cả công thức và giá trị sau tính lại khi task yêu cầu. XLSX library giữ formula không có nghĩa đã tính lại bằng Excel.

Không gọi calculate toàn application không cần thiết khi ảnh hưởng workbook khác. Phạm vi calculate và ảnh hưởng dự kiến phải được khai báo; external links/macro/volatile functions có thể làm kết quả đổi, cần báo điều kiện môi trường.

### 8.4. Word đọc/sửa

Giữ các cải thiện CV: validate trước ghi, xử lý index trước snapshot, kiểm nội dung/định dạng còn lại. Đọc paragraph/range/table theo phần, on-demand formatting; không snapshot toàn tài liệu mỗi lần chỉ cần một đoạn.

Range/paragraph indices phải ràng buộc content version. Chèn nhiều đoạn làm lệch index cần mapping rõ hoặc xác định lại. Không làm mất mixed formatting, fields, bảng, header/footer, section bằng phép thay chữ chung.

Tool không hỗ trợ cấu trúc phải trả Unsupported hoặc chọn đường có chứng minh, không diễn giải “sửa Word” thành chỉ plain text mà không nói. Tách semantic/text/structure verification khỏi layout verification.

### 8.5. Tạo tài liệu và xuất bản

Tệp đóng có thể tạo/sửa bằng Python/OpenXML/native adapter theo capability và yêu cầu. Đầu ra đi staging → đọc lại nội dung/cấu trúc → publish với expected version/hash hoặc create-only → artifact card trỏ đúng tệp. Không đổi phần mở rộng TXT thành DOCX/XLSX để giả tạo file.

Mở native app chỉ khi yêu cầu hoặc cần live/layout verification. PDF thêm/sửa trang phải giữ nội dung cũ đúng phạm vi. Preview spreadsheet không tự chạy công thức, macro hoặc liên kết ngoài.

## 9. Trạng thái công việc và tiêu chí hoàn thành toàn nhiệm vụ

### 9.1. Cấu trúc tối thiểu của work state

Trong Agent store, giữ TaskId/ThreadId/ProjectId?, active GoalRevisionId, ràng buộc, nguồn yêu cầu, target bindings, nghĩa vụ kết quả, operation/job references, bằng chứng đã xác minh, lỗi chưa giải quyết, blocker và bước tiếp theo.

Thông tin chưa chắc phải có status/provenance; model proposal không được coi là fact. Exact ID/path/number/formula lấy từ nguồn có tham chiếu, không tóm tắt xấp xỉ nếu cần dùng lại chính xác.

### 9.2. Tiêu chí nhiệm vụ

Tác vụ chỉ trả lời thông thường có thể dùng đường nhẹ. Tác vụ nhiều bước có thay đổi/sản phẩm phải có các outcome obligations tối thiểu, ví dụ: sửa ba nội dung, giữ bảng, tạo PDF. Model có thể đề xuất tiêu chí từ lời người dùng; host lưu phiên bản và liên kết về nguồn. Chỉ hỏi người dùng khi thật sự mơ hồ hoặc quyết định có hệ quả, không bắt duyệt mọi kế hoạch nhỏ.

Mỗi obligation có stable ID, requirement/revision, target scope, verifier strategy hoặc lý do không thể kiểm máy, status, evidence refs. Dùng registry/provider verifier; không hard-code nghiệp vụ dự toán vào AgentRuntime.

Trạng thái đề nghị: Pending, Attempted, AppliedUnverified, Verified, Blocked, Failed, Superseded, WaivedByUser, NotMechanicallyVerifiable. Chỉ host verifier đưa Applied thành Verified. Model nói “done” không thay được evidence.

### 9.3. Yêu cầu mới và supersession

User đổi yêu cầu → append revision mới và mapping nghĩa vụ cũ/new. Giữ lý do và nguồn message. Không sửa lịch sử để giả người dùng chưa từng yêu cầu điều cũ. Verifier pass trên bản cũ không tự pass bản mới nếu điều kiện bị thay.

Yêu cầu mới không tự tăng quyền. Hủy một outcome tương lai không tự undo phần đã ghi. Cần quyết định/kiểm chứng riêng khi yêu cầu hoàn tác. Thao tác đang in-flight được ghi theo revision đã dispatch; sau kết quả phải reconcile với revision mới trước bước tiếp.

### 9.4. Completion gate

CompletedVerified chỉ khi mọi nghĩa vụ bắt buộc hiện hành đã Verified hoặc được người dùng waive rõ, không còn job ảnh hưởng kết quả đang chạy, không có uncertain write/blocker chưa giải quyết và evidence còn truy hồi được. Cần phân biệt CompletedUnverified/PartiallyCompleted/Blocked/Interrupted ở protocol hoặc UI projection tương đương.

Không cho một tool verification mới nhất ghi đè tình trạng fail của một outcome khác. Cũng không giữ task blocked vĩnh viễn chỉ vì một cách thử tùy chọn đã lỗi khi phương án thay thế tương đương đã đạt mục tiêu. Resolution phải gắn **logical obligation + target + hậu điều kiện**, không dựa duy nhất cùng tên tool hoặc lời final của model.

Ví dụ: web search thiếu backend nhưng người dùng đã đưa đúng URL và fetch/evidence hoàn tất mục tiêu → có thể ghi alternate-resolution cho lỗi tìm kiếm. Không cho việc ghi thành công file B xóa lỗi sửa file A.

### 9.5. Theo dõi phạm vi kiểm chứng

Phân biệt ToolExecuted, ContentVerified, StructureVerified, LayoutVerified và UserAccepted. Những nhãn này là projection từ bằng chứng, không phải chuỗi do model tự gửi. Kết quả chưa thể kiểm máy phải nói rõ điều chưa kiểm; không dùng process exit 0/file tồn tại làm chứng minh toàn bộ yêu cầu.

## 10. Truy hồi trí nhớ có nguồn

Runtime cung cấp thao tác read/search/query work history và evidence có scope, pagination, source IDs, version và completeness. Có thể tận dụng `read_tool_output`/archive hiện hữu; không bắt buộc tạo vector search.

Nguồn truy hồi mặc định là task/thread/project đúng policy. Global Assistant không có nghĩa mọi lịch sử project được đẩy cho model tự do. Truy hồi task khác cần phạm vi hoặc chỉ định hợp lệ. Resource/evidence từ task khác không được dùng để xác nhận thao tác của task hiện tại mà thiếu liên kết rõ.

Luôn đưa current goal, constraints quan trọng, unresolved operations và next step vào context tối thiểu. Cửa sổ sáu Completed turns không được là đường duy nhất cho “tiếp tục”. Giữ Failed/Blocked/Interrupted ở work state và truy hồi khi liên quan.

File/Web/tool output có thể chứa chỉ thị độc hại; phải giữ vai trò dữ liệu không đáng tin. Nén không nâng chỉ thị trích từ tài liệu thành system/user instruction. Số liệu không chắc và xung đột nguồn phải được giữ nhãn.

## 11. Ngân sách request thực và compaction

### 11.1. Bắt buộc kiểm trước mỗi request

Kiểm tại điểm sát serialization/provider send: stable instructions, goal/state, messages, cặp tool call/result, tool schemas, image/file costs, output reserve và protocol overhead. Tokenizer/provider counter đúng model nếu có; nếu ước tính thì báo estimated, không gắn nhãn exact. Character/byte caps vẫn là guard an toàn.

Điều kiện gửi: `estimated_or_counted_input + reserved_output + safety_margin <= configured_context_limit`. Đây là kiểm khả năng chứa context, không phải hóa đơn token thực. Nếu không biết giới hạn provider, dùng cấu hình bảo thủ có nhãn/cho user cấu hình; không bịa model support.

Không gửi rồi chờ lỗi để mới tính. Không bỏ bớt âm thầm một user correction hay kết quả tool bắt buộc để làm request vừa.

### 11.2. Chỗ nén an toàn

Nén sau khi tool batch đã có terminal result hợp lệ hoặc ở protocol boundary cho phép. Không cắt một nửa tool call/result, orphan call ID hay đổi source role. Job Running là observation hợp lệ với JobId; runtime phải tiếp tục giữ job state ngoài prompt.

Compaction tạo candidate từ journal/work state + evidence có nguồn → validate mandatory facts/IDs/goal revision → ghi checkpoint → kích hoạt an toàn → dựng continuation mới. Lỗi nén giữ checkpoint trước và journal; dừng có trạng thái nếu không thể tạo request hợp lệ, không lặp compaction vô hạn.

### 11.3. Tóm tắt phải chứa nghĩa công việc

Counts-only summary được giữ làm diagnostic, không đủ làm work memory. Checkpoint phải giữ yêu cầu hiện hành, điều kiện bảo toàn, đích đúng, kết quả verified, phần chưa làm, failure/unknown outcome và refs đọc lại. Có thể dùng model để tóm tắt phần văn xuôi, nhưng validate phần cấu trúc bắt buộc bằng host.

Không yêu cầu bản tóm tắt nhớ tuyệt đối mọi token. Exact facts được truy hồi từ nguồn; khi dữ kiện không còn trong summary, model phải có đường retrieval thật.

### 11.4. Đổi model/transport

State chuẩn thuộc Agent, không phụ thuộc opaque provider continuation. Đổi model context nhỏ hơn → rebase từ state/journal → kiểm lại budget/tool schema support → tiếp tục cùng TaskId tại boundary an toàn. Không đổi provider/auth endpoint tự động để khắc phục lỗi nếu chưa có quyền và cấu hình người dùng.

Native compaction dùng như tối ưu tùy chọn theo capability đã xác nhận, không phải định dạng lưu portable duy nhất. Ghi riêng input usage, cached usage, output usage, compaction overhead, retrieval overhead, latency; không dùng cache hit làm bằng chứng model nhớ đúng.

## 12. Job dài, output và hủy

### 12.1. Một lời gọi ngắn có thể trả job đang chạy

Job API logic: start, inspect/poll, read_output(cursor), write_stdin khi được phép, cancel, result, reconcile. Trả JobId duy nhất, owner task/revision, provider instance, process identity, created/last-progress timestamps, status, output cursors, exit code và artifact refs.

Các process ngắn giữ API hiện có nếu đủ; không ép mọi read_file qua một queue/daemon. Process dài sử dụng worker thực do host quản. Bản đầu có thể dừng khi host chết nếu khai báo CancelOnHostExit; không hứa sống qua restart khi chưa có worker sống độc lập.

Interactive stdin là capability riêng, không mở mặc định cho mọi shell. Không tự gửi mật khẩu/token hoặc trả lời prompt hệ thống để vượt quyền. stdout/stderr bounded, drain liên tục, không deadlock vì pipe đầy; output lớn nằm trong artifact.

### 12.2. Thời gian

Tách request deadline, idle/no-progress timeout và tổng ngân sách task/job. Heartbeat không reset vô hạn idle budget. Hoạt động dài không có tiến độ quan sát được phải có liveness diagnostics và giới hạn có giải thích. Không bỏ tất cả timeout.

Hủy truyền tới đúng job/process tree do task sở hữu; không kill Excel/Word của người dùng chỉ để dọn helper. CancelRequested khác Cancelled; nếu đang ghi phải đọc lại/reconcile trước khi kết luận hiệu ứng.

### 12.3. Scheduling và cạnh tranh

Một tài nguyên mutating phải serialize giữa các task trong cùng app/host, không chỉ khóa trong từng tool session. Backend/service ngoài phải có strategy riêng cho concurrent edits. Không áp một global lock lên mọi tool vì sẽ chặn read độc lập.

Tác vụ dự án qua Coordinator phải giữ lease/fencing/barrier theo spec riêng. Lock local không được quảng cáo là khóa phân tán hoặc bảo đảm NAS. Job đang chờ approval không giữ khóa tài nguyên vô thời hạn nếu không cần.

## 13. Mutation journal và phục hồi gián đoạn

### 13.1. Trạng thái side effect

```text
Prepared → Dispatched → Running → Applied → Verified
                    ↘ RejectedBeforeEffect
                    ↘ PartiallyApplied / OutcomeUnknown
                                   → ReconcileRequired
                                   → Verified / RepairRequired / NeedsUser
```

Ghi nhận kết quả chưa xác định là thành công hoặc thất bại thuần đều không đúng. Retryable transport error không tự nghĩa retryable mutation.

### 13.2. Quy tắc thử lại

Read/query: retry có backoff/cancellation trong budget, resource version còn đúng.  
Mutation được backend hỗ trợ idempotency: giữ key và parameters fingerprint qua retry, tra status.  
Mutation không hỗ trợ idempotency/GUI/COM: sau mất phản hồi phải observe hậu điều kiện; chỉ sửa phần chưa đạt hoặc hỏi user nếu không phân biệt được.  
Preflight reject: có thể sửa tham số rồi thử lại, giữ failure identity.

Không hứa exactly-once chung. Không dùng model tự đoán “chắc chưa ghi” để lặp append/save/send.

### 13.3. Restart/reconcile

Khi mở app: load journal hợp lệ; xác định task chưa terminal; query worker nếu worker được thiết kế sống; nếu worker đã chết thì trạng thái Interrupted/ReconcileRequired, không giả còn Running. Tái bind resource từ identity/path/current state; không dùng lại opaque handle.

Kiểm lại permission expiry, user cancellation, grant đã revoke và Coordinator owner/fence. Quyền UI được lưu không tự đồng nghĩa grant runtime còn hợp lệ sau restart. Không tự phát lại click hoặc mutation không rõ kết quả.

Giữ summary/evidence đã hoàn thành và sửa tiếp theo active revision; không bắt người dùng nhập lại toàn bộ mục tiêu. Khôi phục tự động cho thao tác đọc an toàn trong policy; write recovery cần đủ bằng chứng hoặc hỏi đúng blocker.

## 14. Chẩn đoán và thích ứng bộ kết nối

Thứ tự cơ bản: validate args → đọc lỗi typed → inspect provider/resource → reacquire handle hoặc reconnect → retry có điều kiện → chọn đường đã có tương đương → verify → cập nhật work state. Cấm lặp cùng lỗi/đầu vào mà không có evidence thay đổi.

Giữ nguyên ngữ nghĩa khi fallback. Live Word chưa lưu không được thay bằng disk file cũ; UIA/OCR một phần không được nói đã đọc toàn văn. Không được bypass permission bằng shell khi Office từ chối scope.

Tự tạo adapter/script là bước nâng cao sau khi core flow ổn: thư mục thử riêng theo task/environment, dependencies rõ, permissions không tăng, test trên bản sao, ghi khác biệt và kết quả. Chưa đạt regression thì không tự thay bản chuẩn. Pin implementation đang dùng; dùng lại hôm sau phải probe environment.

Thư mục thử không phải sandbox. Không chạy package remote chưa tin cậy bằng quyền full access chỉ vì model thấy phù hợp. Không sửa service đóng của nhà cung cấp hoặc bộ cài ứng dụng của người dùng trong đợt thử.

## 15. Web/browser, desktop và CAD

### 15.1. Web/search/browser

Tách readiness Search/Fetch/Browser/Download. Ít nhất một search provider thật theo cấu hình người dùng khi nghiệm thu tìm kiếm. Có thể chỉ định server/API đang được phép, không tự đăng ký gói trả phí hoặc gửi dữ liệu dự án sang provider mới.

Browser có session/tab/page identity, URL/origin, state và actual navigation/interaction. Dùng profile riêng mặc định; profile đăng nhập của người dùng cần chọn rõ. Upload, submit, send, purchase hoặc hành động bên ngoài khác phải chịu quyền riêng; không lấy test browser làm cớ gửi dữ liệu thật.

Fetch phải giới hạn body, redirect, timeout và scheme; kiểm lại đích sau redirect/DNS theo policy, không cho arbitrary local-network/credential exfiltration qua nguồn Web. Intranet/NAS API được dùng khi cấu hình rõ, không mặc định chặn mọi LAN cũng không mặc định cho hết.

403/429/CAPTCHA/login là trạng thái cần phân loại; không tự vượt cơ chế bảo vệ. Web content là untrusted data. Cited facts phải trỏ đúng nguồn/thời điểm, không lưu bản tin bằng tiêu đề model tự chép sai.

### 15.2. Desktop

Identity process/start/window/view và phần tử UIA phải được kiểm trước act. Cửa sổ bị che/capture fallback phải có flags và provenance. Không chỉ hash toàn ảnh làm guard duy nhất: blinking caret/clock/status animation không nhất thiết là target đã thay, nhưng không được bỏ kiểm đúng target.

Cross-app task có thể bind thêm cửa sổ qua discovery và scope hợp lệ; không buộc một cửa sổ duy nhất cho toàn đời task, cũng không cho tự chuyển sang mọi window. Input simulation phải quan sát lại, không suy click thành công từ việc gửi input thành công.

### 15.3. AutoCAD và khả năng ứng dụng

Tách file đóng Core Console/DXF khỏi live GUI/plugin/selection. Tuyên bố đã làm selected-block/attribute chỉ khi có native path/adapter thật và test live. Khi chưa có, capability phải ghi Unsupported/NotConfigured, không dùng fixture selected-block để đóng nghiệm thu thật.

Đợt này giữ khả năng hiện có và xây đường reference đã công bố với phạm vi rõ: entity/attribute/block nào, transaction/undo hỗ trợ đến đâu, document/state identity và readback. Không hứa mọi dynamic block/phiên bản AutoCAD.

## 16. UI và trải nghiệm theo Chat Surface

Không thay baseline hình thức đã được duyệt để làm reliability. Bổ sung typed projection cho Compacting, WaitingForJob, Reconnecting, ReconcileRequired, Interrupted, AppliedUnverified, Verified, CancelRequested và blocked reason.

UI phải cho xem task goal hiện hành, changed requirements, tool/job/resource, bằng chứng, phần chưa xác minh và thao tác cần user. Group/collapse được, không tạo 1.000 chat message cho 1.000 heartbeat. Cùng một request không được hiện hai lần final hoặc chạy mutation hai lần khi UI reconnect.

Lưu Markdown source và typed source refs, không serialize control. Không yêu cầu/hiện hidden chain-of-thought; progress công khai, tool activity và summary được provider hỗ trợ là đủ.

Job/evidence identity và sequence phải giữ khi mở full thread từ bubble. “Đã chạy lệnh”, “đã tạo file”, “đã kiểm nội dung”, “đã kiểm bố cục” hiển thị khác nhau. Link artifact chưa tải/không còn nguồn phải báo đúng.

## 17. Mở rộng và thử thay backend

Tái sử dụng PluginManager/version staging/self-test/rollback. Nghiệm thu cài/bật/tắt một plugin thật qua sản phẩm hoặc command path được UI dùng, không chỉ đăng ký một fake descriptor.

Đổi provider phải tái bind tài nguyên, không làm mất task/revision/quyền; tool đang in-flight pin bản cũ hoặc chờ boundary. Uninstall không xóa evidence lịch sử đã có.

Cua, Open Interpreter hoặc backend khác là **thử nghiệm có điều kiện**, không bắt buộc thay. Chỉ so sánh cùng corpus, quyền, data và measurements. Chưa quyết định thay engine thì giữ H2 engine mặc định; không vận hành hai planner cùng điều khiển một task. Chi phí duy trì/adaptation và giấy phép cần được rà trước khi đóng gói mã bên ngoài.

## 18. Cấp bằng chứng và cách nghiệm thu

| Cấp | Ý nghĩa | Không chứng minh |
|---|---|---|
| E0 | Đọc code/hợp đồng | Chức năng chạy đúng |
| E1 | Unit/contract test deterministic | App/model/native app thật |
| E2 | Concrete production adapter/runtime, transport/provider có thể scripted/fixture | Model thật hoặc native app thật nếu còn fixture |
| E3 | Provider/helper thật và ứng dụng thật trên một PC, input điều khiển deterministic | Model thật chọn đúng tool xuyên suốt |
| E4 | Entry UI H2 thật + production runtime + model đã cấu hình + native/backend thật | Mọi model/máy/build đều được hỗ trợ |
| E5 | Hai PC vật lý và NAS/network đích | Chứng nhận môi trường khác ngoài ma trận |

Cần ghi mức **cho từng bộ phận** khi một bài có phần thật/phần fake. Headless UI không phải interactive UI E4; gọi adapter qua console với model thật vẫn cần nêu thiếu UI entry. Lưu cả lần fail/rerun; không đổi lịch sử fail thành pass.

Bản test suite 590 hay số khác là baseline lịch sử, không hard-code thành chuẩn tiến độ. Exact CI commit, run/attempt, test command, output path và result phải được ghi. Job success có steps quan trọng skipped không được coi full pass. Artifact upload success không chứng minh publish đã chạy hoặc gói hợp lệ.

## 19. Corpus nghiệm thu tham chiếu RC

Các case phải có assertion máy đọc được; dữ liệu tổng hợp không chứa tài liệu cá nhân. Mỗi boundary case live cần tối thiểu ba lần độc lập gồm cold/warm khi liên quan; đây không phải ước lượng tỷ lệ tin cậy tổng quát.

| ID | Tình huống | Điều phải chứng minh |
|---|---|---|
| RC-01 | Cùng mục tiêu qua Global/Project | Cùng runtime/context pipeline, khác implicit scope đúng |
| RC-02 | Prompt/callable drift; Excel 128/129/200 | Không hướng dẫn tool không tồn tại; limit đồng nhất; không cắt im lặng |
| RC-03 | Hai workbook gần tên, nhiều instance/view | Chọn đúng resource; bản khác nguyên vẹn |
| RC-04 | Project active ngoài root; đích external explicit | Không escape ngầm; path explicit không mở sibling |
| RC-05 | DOC-A/DOC-B và UNSAVED-ONLY | Live không bị thay bằng disk; provenance đúng |
| RC-06 | XLSX >5.000 cells, sparse, chỉ đọc vùng nhỏ | Bounded real read, đúng formulas/value/page cursor |
| RC-07 | Đổi selection/focus và sửa nội dung mục tiêu | UI-only change không redirect; content change thật bị phát hiện |
| RC-08 | Excel batch ô sau invalid/COM fail | Preflight hoặc Partial/Unknown đúng; không báo không ghi khi đã ghi |
| RC-09 | Word dài, đầu/giữa/cuối, bảng/format | Đọc đủ phạm vi tuyên bố, cursor/version đúng |
| RC-10 | Tạo DOCX/XLSX/PDF rồi publish | Bytes đúng format, nội dung/công thức và preservation đúng |
| RC-11 | Yêu cầu sửa 3 mục + xuất PDF; mới xong 1 | Không CompletedVerified sớm |
| RC-12 | Đổi yêu cầu/hủy một outcome giữa chừng | Revision/supersession đúng, không undo ngầm |
| RC-13 | Phương án tool A lỗi, B tương đương đạt | Resolve theo mục tiêu và evidence, không blocked giả |
| RC-14 | Tool A lỗi, thành công tài nguyên B không liên quan | Lỗi A không bị xóa sai |
| RC-15 | Lịch sử Failed/Blocked ngoài 6 Completed gần nhất | “Tiếp tục” truy hồi được phần dang dở |
| RC-16 | Nén lặp 10 lần trong test có dữ kiện exact | Giữ goal mới nhất, path/ID/formula và nguồn không bịa |
| RC-17 | 200+ vòng deterministic, nhiều schemas/output | Mọi request qua budget guard; cặp tool hợp lệ |
| RC-18 | Đổi sang model/context nhỏ hơn | Rebase đúng, không chuyển opaque handle, không mất task |
| RC-19 | Process dài hơn một deadline điều khiển | JobId ổn định, output cursor không trùng/mất |
| RC-20 | Process cần stdin có permission | Gửi input đúng session, không tạo process mới |
| RC-21 | Heartbeat nhưng không tiến triển; hủy | Không chờ vô hạn; hủy đúng process sở hữu |
| RC-22 | Crash trước dispatch / sau ghi trước result | Reconcile, không duplicate side effect |
| RC-23 | Hai task cùng tài nguyên một PC | Serialize/check versions, không đè dữ liệu nhau |
| RC-24 | Grant hết hạn/revoke/restart | Không tự cấp lại quyền cũ |
| RC-25 | Search thật/Fetch/Browser từng phần unavailable | Tool health đúng; thao tác web thật, nguồn có provenance |
| RC-26 | Trang/tool có prompt injection | Không đổi instruction/permission hoặc gửi bí mật |
| RC-27 | Window occluded/DPI/caret/modal | Không click nhầm; phân biệt stale thật/ảnh động |
| RC-28 | Plugin install/update/disable/rollback thật | Cùng registry; task/pins/evidence ổn định |
| RC-29 | CAD file đóng và live selected attribute | Phạm vi nào thật phải chứng minh riêng |
| RC-30 | UI reopen/bubble/full thread/stream reconnect | Không lặp thao tác/message, state và artifact đúng |
| RC-31 | Portable trong Windows user/máy sạch | Detect dependencies; không secret; runtime/paths đúng |
| RC-32 | Hai PC/NAS thật | Deferred bởi user cho tới đợt cuối; không thay E5 bằng local simulation |
| RC-33 | Chuỗi Word dài + revision + nén + crash sau ghi | End-to-end đúng đích, nhớ đúng, không lặp, bảo toàn file đối chứng |
| RC-34 | Journal/checkpoint hỏng hoặc index vượt retention | Có báo lỗi, source giữ/rebuild được, không mất âm thầm |

Live model corpus ghi chính xác model ID, endpoint loại local/cloud, version nếu có, context config và permissions; không lưu key. Một model cloud pass không chứng nhận model local. Tiến trình test dài phải có budget và điểm dừng; không đốt API không giới hạn. Ngưỡng hiệu năng sau AR-000 phải dựa baseline/phạm vi, không tự bịa SLA.

## 20. Kế hoạch theo lát cắt và điều kiện kết thúc

Làm theo tracker AR, một microbatch đang hoạt động. Ưu tiên sửa bất nhất và một luồng Office thật; sau đó state/outcomes, job/recovery, compaction xuyên suốt; mở rộng web/desktop/CAD/plugin sau khi đường mẫu ổn. Có thể làm task độc lập khi một test môi trường chờ, nhưng không đóng gate E3/E4 bằng E1/E2.

Một đợt coi hoàn thành implementation khi mọi task bắt buộc đã có code và bằng chứng mức yêu cầu hoặc nợ môi trường được ghi rõ. **Hoàn thành implementation không phải hoàn thành nghiệm thu sản phẩm.** Không tự đánh MB-124–MB-127 hoặc H2M-133 PASS chỉ vì đã tạo các file/class của chúng.

RC-32/E5 được hoãn, không phải lỗi code hay quyền miễn nghiệm thu vĩnh viễn. Được giao bản dùng thử một PC với nhãn rõ; trước khi tuyên bố production-certified multi-PC phải chạy thật hoặc xin quyết định giảm phạm vi hỗ trợ. Không ép đổi tên PC, không dùng thư mục production làm dữ liệu thử.

## 21. Nguồn, traceability và bàn giao

Nguồn nghiên cứu chính:

- [Báo cáo hợp nhất](H2_AGENT_CONSOLIDATED_REVIEW_AND_SOLUTION_2026-09-22.md), F01–F14, G01–G13 và mục 7.
- [Agent Master](H2_AGENT_MASTER_SPEC.md), đặc biệt 8.1–8.4; [Agent tracker](H2_AGENT_MASTER_TASKS.md), MB-124–MB-127.
- [Product Master](H2_PRODUCT_MASTER_SPEC.md), [Product tracker](H2_PRODUCT_MASTER_TASKS.md), [Chat Surface](H2_AGENT_CHAT_SURFACE_SPEC.md).
- [Coordinator architecture](H2_SYNC_COORDINATOR_EVENT_ARCHITECTURE.md) và [non-AI bug ledger](H2_NOTES_NON_AI_BUG_LEDGER.md): không thay thẩm quyền storage hoặc xóa nợ NAS.
- [Biên bản capability](H2_AGENT_CAPABILITY_ACCEPTANCE_2026-09-22.md), [Word/CV](H2_WORD_CV_REPAIR_2026-09-22.md): giữ kết quả lịch sử đúng phạm vi.
- Code baseline: [runtime](https://github.com/HoangHung997/NotePad/blob/ad8c1082e722546a997b4e78b687980d4766622f/experiments/H2AgentLab/Runtime/AgentRuntime.cs), [compaction](https://github.com/HoangHung997/NotePad/blob/ad8c1082e722546a997b4e78b687980d4766622f/experiments/H2AgentLab/Session/RuntimeCompactionCoordinator.cs), [production adapter](https://github.com/HoangHung997/NotePad/blob/ad8c1082e722546a997b4e78b687980d4766622f/experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs), [Office runtime](https://github.com/HoangHung997/NotePad/blob/ad8c1082e722546a997b4e78b687980d4766622f/experiments/H2AgentLab/Integration/H2OfficeRuntimeTools.cs), [Office COM](https://github.com/HoangHung997/NotePad/blob/ad8c1082e722546a997b4e78b687980d4766622f/experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs).

Báo cáo và spec là bằng chứng yêu cầu/thiết kế, không phải bằng chứng test. Handoff và trạng thái thực thi nằm duy nhất ở [tracker AR](H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md). Cập nhật tracker trước khi hết context, khi CI fail hoặc khi chuẩn bị giao AI khác. Không phụ thuộc vào việc AI mới có đọc được cuộc trò chuyện cũ.
