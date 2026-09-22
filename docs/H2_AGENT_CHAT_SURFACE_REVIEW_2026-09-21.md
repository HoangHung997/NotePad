# Rà soát đặc tả chat H2 và đối chiếu ChatGPT/Codex trên máy

Ngày: 21/09/2026. Đề xuất này đã được người dùng chấp thuận ở lượt tiếp theo.
Xem [bản triển khai và giới hạn nghiệm thu](H2_AGENT_CHAT_SURFACE_IMPLEMENTATION_2026-09-21.md).
Nội dung rà soát gốc được giữ để truy nguyên quyết định, không thay đặc tả chuẩn.

## Nguồn đã kiểm tra

- Đã fetch GitHub và lấy riêng `docs/H2_AGENT_CHAT_SURFACE_SPEC.md` từ
  `origin/feature/nas-multi-device-sync` (remote HEAD `fd5bfffb919047931d7051ddda1f482feffd626f`).
- Commit gần nhất thay đổi file: `65c657ff41dc686aa6a13b8d63ba89466aae9157`,
  “Add canonical H2 Agent chat surface specification”.
- Git blob của bản tải về và bản trên remote cùng là
  `48ea24c644e0004b91113a032db4bb7f6b7c6192`. Đã đọc 109 mục; giữ nguyên nội dung.
- Đã quan sát trực tiếp cửa sổ **ChatGPT** của ứng dụng Codex trên máy, tại tác vụ
  “Đồng bộ dự án NotePad”. Chế độ hiển thị đang là ChatGPT; không đổi sang chế độ
  sản phẩm khác. Thấy lượt đang chạy, câu trả lời, dòng hoạt động và thanh soạn.
- Không thử gửi thêm tin, đổi quyền/model, bấm Dừng, cấp quyền hay tác động dữ liệu
  qua ứng dụng tham chiếu. Chưa kiểm chứng thao tác với mọi loại preview/approval.
- Đã đối chiếu mã H2 hiện tại và các tài liệu baseline được AGENTS.md yêu cầu.

Tài liệu chính thức bổ trợ:
[ChatGPT Work và Codex trong desktop](https://learn.chatgpt.com/docs/use-chatgpt)
phân biệt cách trình bày cho người dùng thông thường và chi tiết dành cho lập trình.
[Codex App Server](https://learn.chatgpt.com/docs/app-server) mô tả Thread → Turn →
Item và cập nhật một lượt đang chạy qua `turn/steer`. Đây là tham khảo hành vi,
không yêu cầu H2 phụ thuộc Codex hoặc dùng giao thức riêng không công khai.

## Nhận xét chính

Đặc tả đi đúng hướng ở một runtime, một bề mặt chat dùng chung, nội dung có kiểu,
Markdown an toàn, tiến trình lấy từ runtime và bằng chứng có danh tính. Tuy nhiên,
109 mục hiện thiên về danh sách thành phần. Cần bổ sung quy tắc **gom theo lượt,
mức độ nổi bật và trạng thái hiển thị** để các thành phần đó tạo ra một hội thoại
gọn, dễ đọc như giao diện tham chiếu.

Trong cửa sổ vừa quan sát:

- Tin người dùng là khối gọn, bo góc, căn phải; câu trả lời là văn bản trên nền
  hội thoại, không nằm trong một bong bóng lớn có viền.
- Hoạt động hiện bằng dòng nhỏ, màu nhẹ như “Đã chạy lệnh” hoặc tên công việc;
  lời cập nhật của trợ lý có khoảng cách rõ, không thành hàng loạt bảng kỹ thuật.
- Có thời gian xử lý của lượt; thanh soạn vẫn nằm ở đáy. Nút Dừng thay vị trí nút
  hành động khi đang chạy.
- Thanh soạn có + và quyền ở trái; model/mức suy luận, micro và nút tròn ở phải.
  Không có các nút @/Context riêng chiếm chỗ thường trực.
- Thanh bên và tiêu đề giúp nhận biết dự án/cuộc trò chuyện; vùng nội dung là
  nơi đọc chính. Các thông tin vận hành không lấn át kết quả.

H2 nên học thứ bậc thông tin và hành vi này, đồng thời giữ màu ivory/terracotta
và vai trò quản lý dự án đã chốt. Không thay mười PNG/BASELINE.json.

## Các bổ sung nên chốt vào đặc tả

### 1. Một lượt có phần hoạt động và phần kết quả riêng

Đề xuất một nhóm hiển thị theo TurnId:

1. Yêu cầu người dùng và tệp/ngữ cảnh đính kèm.
2. Phần hoạt động: cập nhật công khai, tiến trình, công cụ, xác minh và sửa lỗi.
3. Kết quả cuối, tệp đầu ra và hành động mở/xem thay đổi.

Khi làm việc, phần hoạt động hiện bước mới nhất cùng thời gian. Khi xong, mặc
định thu gọn thành một dòng có thể mở, ví dụ “Đã xử lý trong 2 phút · 6 thao tác”.
Kết quả và tệp đầu ra vẫn hiện. Thu gọn chỉ thay cách trình bày, không xóa sự kiện.

Chờ phê duyệt, câu hỏi chưa trả lời và lỗi chưa giải quyết luôn phải dễ thấy;
không giấu chúng trong phần đã thu gọn. Các lần lỗi đã khắc phục có thể nằm trong
chi tiết, vẫn giữ được lịch sử thất bại → sửa → xác minh lại.

### 2. Công cụ thường là một dòng; chỉ việc cần chú ý mới thành thẻ lớn

Giữ typed ToolCallBlock theo mục 29–31 nhưng biểu diễn mặc định gọn:

`✓ Đã đọc DuToan.xlsx · 12 sheet    Chi tiết`

Không tạo một thẻ có viền/padding lớn cho mỗi lần đọc tệp hoặc mỗi thông báo.
Phê duyệt, yêu cầu người dùng quyết định, lỗi đang chặn và tệp đầu ra mới cần thẻ
nổi bật. JSON, log dài, tham số nội bộ nằm trong chi tiết/inspector.

Mục 27 đã cấm 50 tin tiến trình; nên nói rõ cả heartbeat cũng cập nhật cùng một
khối, không tạo thêm lời chat định kỳ. Lời cập nhật công khai và trạng thái thật
của công cụ là hai loại dữ liệu khác nhau, không dùng lời model để đổi trạng thái.

### 3. Giữ thanh soạn đúng yêu cầu trực tiếp đã chốt

Sửa sơ đồ mục 54 cho khớp yêu cầu trước đây của người dùng:

`+   Khiên/quyền                         Model · Mức suy luận   Mic   Gửi/Dừng`

Ô nhập ở trên, toolbar một hàng ở dưới, chips ngay phía trên ô soạn. @ hoạt động
trong nội dung nhập; ngữ cảnh và đính kèm truy cập qua +. Khi hẹp: rút gọn tên
model/quyền, giữ tooltip/tên truy cập, không ép chữ nhỏ hoặc thêm hàng toolbar.

Mục 76 gợi ý Enter xuống dòng/Ctrl+Enter gửi, trong khi Work Assistant hiện dùng
Enter gửi và Shift+Enter xuống dòng. Giữ hành vi đã chốt; nếu muốn cho chọn kiểu
phím thì phải là cài đặt rõ ràng, không đổi âm thầm khi nâng cấp.

Phân biệt micro nhập lời nói và hội thoại âm thanh hai chiều. Không hiển thị
tính năng giọng nói/model/mức suy luận mà provider và runtime chưa hỗ trợ.

### 4. Bổ sung rõ “Toàn quyền tiếp cận” và quyền thực tế

Mục 58 chưa liệt kê FullAccess đã được người dùng yêu cầu và hiện có trong H2.
Cần chốt nó trong bảng quyền, thay vì để renderer mới vô tình bỏ tính năng.

- Ngữ cảnh “Máy này” chỉ nói nơi tìm đối tượng mặc định; không đồng nghĩa đã cấp
  toàn quyền hoặc được tự quét/gửi toàn bộ dữ liệu máy.
- FullAccess phải giữ grant theo tác vụ/tài khoản máy, thời hạn và giới hạn quyền
  Windows đang có; không nâng quyền quản trị hoặc lấy grant từ dữ liệu NAS.
- “Dùng chính sách dự án” phải hiện quyền hiệu lực, không chỉ tên preset mơ hồ.
- Xóa chip phải cho biết ảnh hưởng tới lượt gửi tiếp theo. Không khiến người dùng
  tưởng đã thu hồi một quyền của tác vụ đang chạy khi backend vẫn giữ grant đó.
- Khi mở bong bóng làm H2 giành foreground, phải giữ/kiểm tra lại tài liệu ngoài H2
  đã được chọn trước đó, không biến cửa sổ H2 thành “tài liệu đang làm việc”.

### 5. Thread và Turn cần có ngay từ nền tảng

Mục 69 để nhiều task trong một cuộc trò chuyện cho giai đoạn sau là quá muộn nếu
muốn trải nghiệm tiếp tục như Codex. Nên chốt ngay:

`ThreadId → TurnId → ItemId`, với `TaskId/ToolCallId/ArtifactId` là liên kết tới
nguồn dữ liệu Agent có thẩm quyền. Không tạo thêm bản sao cơ sở dữ liệu Agent.

“Sửa tiếp”, “làm lại phần này”, mở từ bong bóng, mở trong dự án và mở sau restart
phải quay lại đúng cuộc trò chuyện. Không suy nhóm hội thoại bằng thời gian hoặc
gom mọi task không có ProjectId vào cùng một lịch sử. Gắn task vào dự án không
viết lại phạm vi, quyền và lịch sử đã thực thi trước đó.

Nên đưa persistence/replay tối thiểu lên cùng giai đoạn typed events, trước khi
làm nhiều kiểu thẻ và viewer, thay vì đợi đến bước 9 của mục 106.

### 6. Nhập bổ sung khi Agent đang làm

Đặc tả cần quy định người dùng có thể soạn tiếp khi đang chạy. Khi gửi, phân biệt
“Bổ sung cho tác vụ này” với “Xếp hàng lượt tiếp theo”; hiển thị đã nhận để tránh
người dùng bấm lại. Hủy một tin trong hàng đợi không có nghĩa hủy tác vụ đang chạy.

Đây là đề xuất hành vi H2; `turn/steer` trong tài liệu OpenAI là một ví dụ công
khai cho bổ sung đầu vào vào lượt hiện tại. Không cần sao chép API đó.

Thay model/quyền giữa lượt phải rõ hiệu lực. Không âm thầm nâng quyền của tác vụ
đang chạy. Trước lúc thực thi tin đã xếp hàng, kiểm tra lại tài liệu/phiên đã chọn;
không tự đổi sang tài liệu foreground khác chỉ vì người dùng đã chuyển cửa sổ.

### 7. Định nghĩa dừng, mất kết nối và khôi phục

Thêm trạng thái “Đang dừng” cho tới khi runtime xác nhận đã dừng. Không đánh dấu
Cancelled trong lúc công cụ vẫn đang sửa tệp. Nêu rõ phần đã thực hiện; Dừng
không tự đồng nghĩa Hoàn tác.

Restart/reconnect phải phục hồi item bằng ID/sequence, không sinh bản sao thẻ,
không chạy lại thao tác ghi hoặc approval cũ. Approval hết hạn/đối tượng đã đổi
phải hiển thị không còn hiệu lực. “Tiếp tục” phải phân biệt khôi phục quan sát với
thực thi lại hành động.

### 8. Kết quả và viewer theo mức độ cần thiết

Giữ pane phải là tùy chọn như mục 43, mở khi bấm tệp/bằng chứng. Màn hẹp dùng
trang/ngăn riêng có nút quay lại đúng vị trí đọc. Tệp ngoài dự án có nhãn rõ.

Bảng và code rộng cuộn ngang trong chính khối đó; không kéo giãn cả hội thoại.
Bảng lớn có bản xem trước và mở đầy đủ. Chọn/copy văn bản phải được kiểm tra
thực tế, gồm chọn qua nhiều đoạn; không chỉ có nút sao chép toàn câu trả lời.

Mẫu “Completed / Changes / Verification / Remaining” ở mục 39 chỉ là mẫu cho
tác vụ có thay đổi, không bắt mọi câu trả lời theo mẫu đó. Hỏi đáp đơn giản hoặc
lời chào không cần tạo kế hoạch/công cụ/thẻ xác minh giả. Hoàn thành trả lời và
xác minh một thay đổi là hai việc khác nhau.

### 9. Chốt vai trò cửa sổ nổi

Mục 84 cho phép compact chỉ hiện ít nội dung, nhưng yêu cầu trước của người dùng
là chat đi cùng bong bóng và có lịch sử/ô soạn đầy đủ. Cần phân biệt rõ:

- Bong bóng: điểm mở và thông báo trạng thái.
- Chat nổi thông thường: dùng cùng renderer, lịch sử và composer với project chat;
  kéo bong bóng thì khung đi theo, có giới hạn trong vùng màn hình.
- Chế độ rút gọn: lựa chọn rõ ràng, có “Mở hội thoại đầy đủ” vào đúng ThreadId.

Không coi mọi cửa sổ ngoài dự án là một bản chat bị cắt giảm. Tách/ghim/mở rộng
giữ nguyên bản nháp, vị trí đọc, task đang chạy và quyền đã chụp cho lượt đó.

## Khoảng cách trong mã hiện tại

| Vị trí | Đang có | Việc cần làm theo đặc tả |
|---|---|---|
| `Controls/MarkdownMessageView.cs` | Markdig → AST → Avalonia; có bảng/code | Tái sử dụng, thêm giới hạn bảng lớn, cuộn ngang, copy phù hợp và cập nhật từng phần; hiện SetMarkdown xóa/dựng lại toàn bộ children |
| `Controls/ChatMessageView.cs` | Cả user/assistant đều có Border dạng bong bóng; progress nằm ở vùng thinking và biến mất khi có kết quả | Agent prose thành vùng tài liệu; tách hoạt động công khai và typed runtime events; giữ milestones có thể mở lại |
| `Controls/AiChatPanel.Agent.cs` | Có observation theo sequence nhưng nối progress.Message thành chuỗi và đưa vào SetThinking | Giữ Kind/Code/Sequence cùng danh tính công cụ để dựng item đúng loại |
| `WorkAssistantCompactWindow.Conversation.cs` | Plain SelectableTextBlock; 30 task gần nhất, cắt phần hiển thị sau 16.000 ký tự; dựng lại danh sách | Dùng cùng surface và định danh thread; phân trang lịch sử; xem đầy đủ thay vì cắt im lặng |
| `App.WorkAssistantConversation.cs` | Nhóm task global theo ProjectId=null và mốc thời gian trong bộ nhớ | ThreadId bền vững, tách cuộc trò chuyện đúng sau restart |
| `AgentTaskWindow.cs` | Goal/kết quả/bằng chứng nối vào TextBlock | Mở canonical full thread của task, inspector theo resource ID |

Vì vậy, việc sửa riêng ô soạn chưa thể làm toàn bộ chat giống giao diện tham chiếu.
Bộ Markdown đã có là phần nên tận dụng, không cần viết lại parser từ đầu.

## Thứ tự triển khai đề xuất

1. Chốt ba điểm dễ lệch yêu cầu: toolbar, phím gửi, FullAccess; phân biệt chat nổi
   đầy đủ với compact. Không tự thay baseline.
2. Thread/Turn/items + scope resolver + lưu/đọc lại tối thiểu; một nguồn task và
   một renderer dùng chung. Tạo một luồng thật từ prompt đến kết quả và restart.
3. Đổi cách hiển thị assistant, nhóm hoạt động theo lượt, giữ composer/scroll;
   tái sử dụng Markdown hiện có; approval/error/verification có trạng thái đúng.
4. Thẻ tệp và inspector dùng chung, sau đó mở rộng bảng tính, Word/PDF/ảnh.
5. Gửi bổ sung/hàng đợi và khôi phục đầy đủ; đo hiệu năng, bàn phím, DPI/IME.

Mỗi lát triển khai phải có bằng chứng app thật và production adapter như mục 104.
Bộ nghiệm thu tối thiểu nên có: đang chạy; đã hoàn tất và thu gọn; cần phê duyệt;
lỗi rồi tự sửa; bảng/code rộng; tệp trong inspector; cửa sổ nổi hẹp; cuộn lên khi
streaming; bổ sung giữa lượt; mở lại sau restart không trùng hành động. Chạy thử
thêm dự án có Excel ngoài phạm vi để kiểm tra luật chọn đối tượng.

Lượt rà soát này chỉ tải đặc tả và ghi đề xuất; không sửa mã ứng dụng, không build,
không thay mười ảnh chuẩn, không tuyên bố đã nghiệm thu bề mặt chat mới.
