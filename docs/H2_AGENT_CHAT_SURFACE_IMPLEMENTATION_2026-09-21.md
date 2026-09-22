# H2 Agent chat surface — bản triển khai 21/09/2026

Người dùng chấp thuận hướng trong H2_AGENT_CHAT_SURFACE_REVIEW_2026-09-21.md,
sau đó chọn `gemma4:cloud`. Bản này triển khai các luồng dưới đây; không đánh dấu
cả 109 mục đặc tả hoặc mọi khả năng Codex là hoàn tất.

## Đã triển khai

- Project chat, Work Assistant và task detail dùng chung AgentChatSurface/AgentTurnView.
  Tin người dùng gọn bên phải; câu trả lời dạng tài liệu; composer ở đáy, giữ
  +/quyền/model/micro/gửi-dừng và Enter/Shift+Enter.
- Hoạt động gom theo lượt, có thời gian và thu gọn; kết quả, approval/lỗi tách riêng.
  JSON/mã/kế hoạch nằm trong chi tiết. Chỉ stream text công khai; không lưu reasoning riêng.
- ThreadId/TurnId/sequence bền vững, bản nháp và task reference riêng; mở lại không
  tự chạy thao tác hay phục hồi grant/approval. Task cũ quá 200 bản ghi vẫn truy xuất
  được; câu trả lời đầy đủ ở task record, index tương thích chỉ giữ tóm tắt.
- Lịch sử 30 lượt/lần, hoạt động 40 mục/lần; giữ vị trí khi đang đọc phía trên.
- Bổ sung giữa lượt có ID chống lặp và gửi bằng role user. Queue giữ model/scope/
  thời hạn quyền; hủy lượt chờ không hủy lượt đang chạy. Dừng chờ runtime xác nhận;
  bằng chứng/tệp đã tạo trước lỗi hoặc dừng vẫn được giữ.
- Workspace riêng theo dự án và đích liên kết/chỉ rõ; không lấy Office ngoài dự án
  làm đích mặc định. Một tệp không cấp thư mục cha/tệp bên cạnh. FullAccess là grant
  máy hiện tại còn hạn, không lấy từ NAS. Nhớ foreground ngoài H2 trước khi mở bubble.
- Markdown AST, bảng/code cuộn ngang, copy câu trả lời/code/bảng/ô/hàng. Bảng dài
  phân trang; sửa thiếu dòng trống trước bảng trong prose, giữ nguyên code/HTML.
- Tệp có nguồn/hash; inspector chung, màn hẹp có quay lại. XLSX/CSV 40 hàng/trang,
  giữ địa chỉ/công thức và không chạy công thức. Word xem nội dung trích; PDFium
  dựng ảnh trang PDF thật trong sandbox, kiểm hash nguồn, có trang/zoom/văn bản.

## Lỗi sửa qua kiểm thử

1. update_plan bị coi như sửa tài liệu, bị scope chặn hoặc đòi xác minh thay đổi.
   Đã phân loại đúng là nhật ký nội bộ, không nới quyền công cụ sửa tệp.
2. XLSX Skip+Read bỏ hàng ở trang sau: sửa và kiểm 101 hàng thưa, công thức, byte nguồn.
3. TabControl tùy biến thiếu theme làm inspector trắng: sửa và kiểm visual tree thật.
4. PDF/ảnh dùng lại bitmap/token đã hủy sau chuyển tab: sửa vòng đời tải/giải phóng.
5. Index cũ có thể cắt câu trả lời/không tìm task cũ: đọc durable record trước index.

## Bằng chứng

- `.artifacts/chat-surface-regression-final.log`: **566 đạt, 0 lỗi**.
- `.artifacts/chat-surface-new-tests.log`: **10/10** (thread/replay/draft, bổ sung,
  queue/cancel, scope, Markdown, XLSX/CSV và inspector).
- Agent guard **35/35**; Ollama **6/6**, Chat Completions **5/5**, Responses HTTP
  **7/7**, Responses WebSocket **6/6**, provider resilience **13/13**.
- Release self-contained Windows x64 publish thành công; còn cảnh báo nullable/
  obsolete sẵn có, không có lỗi build.
- Production adapter + **Gemma 4 Cloud**, không dùng transport giả:
  `.artifacts/chat-surface-2026-09-21/cloud05/result.json` hoàn tất DOCX/CSV/PDF,
  PDFium mở trang thật. `cloud06/result.json` lặp lại và nhận bổ sung giữa lượt,
  câu trả lời chứa `PHU-LUC-CHAT`. Chỉ dùng dữ liệu mẫu.
- Ollama LAN cấu hình cũ không phản hồi khi thử; DeepSeek cloud trước đó báo 402.
  Không quy lỗi dịch vụ thành hoàn tất hoặc tự đổi model trong lượt đang chạy.
- `Downloads/H2Notes-Package-Checks-2026-09-21/chat-surface/portable-check.json`:
  helper IPC, Python kèm app tạo/đọc/render Word/Excel/PDF và OCR inventory đạt.
  Không chạy lại ba engine OCR vì không đổi bridge/models đã kiểm trước.

## Giao diện và phạm vi nghiệm thu

Đã chạy Release thật, mở lại hội thoại Gemma Cloud trong dữ liệu riêng. Ảnh và
visual tree ở `audit-evidence/2026-09-21-chat-surface/native-assistant.png` và
`native-assistant-tree.txt`. Cửa sổ cấu hình 640×610 DIP; tool trả ảnh 640×610,
không trả phép đo DPI mới. Thấy composer ở đáy, bảng thành ô, ba tệp, copy và
bằng chứng. Đối chiếu màu ấm/chips/tin người dùng với UI-04/UI-07; assistant và
composer theo yêu cầu mới 21/09. **10/10 PNG giữ SHA-256, BASELINE.json không sửa.**

Công cụ Windows nhiều lần báo có input người dùng; các lần bấm/đổi tab/resize
không quan sát được kết quả được để **chưa nghiệm thu native**. Không lấy headless
thay bằng chứng chuột, không xác nhận pixel parity 560×820/1440×860 trong lượt này.

Chưa chứng nhận đủ 109 mục: Word preview chưa tái tạo bố cục trang; chưa có viewer
diff nhiều phiên bản/AutoCAD, chọn xuyên nhiều block hoặc hội thoại âm thanh hai
chiều. Global + chưa có toàn bộ đính kèm như project chat; queue nhận văn bản,
task detail là chế độ xem. NAS hai máy và toàn bộ DPI/IME/native approval matrix
còn cần nghiệm thu. WPF/_ver2 không bị thay đổi bởi chặng chat này.

Gói mới giữ Python/OCR của gói đã kiểm, không chứa dữ liệu dự án, khóa API, cấu hình
cá nhân, lịch sử, model chat Ollama hoặc bản cài Office. Máy đích cấu hình AI riêng.

Bổ sung kiểm tra: 4/4 wire tests cho lời nhắn bổ sung ở Ollama, Chat Completions, Responses stateless/stored; giữ role user và chặn TurnId khác. Mã sản phẩm không đổi sau bản Release đã kiểm.
Cập nhật máy: đã sao lưu cấu hình/draft/index; đưa 56 file hội thoại/tác vụ từ phiên review người dùng về runtime máy bằng ID, không ghi đè record khác; giữ đường dẫn tệp gốc; thêm/chọn hồ sơ Gemma 4 Cloud; không nhập dữ liệu demo vào dự án thật. Shortcut H2 Notes trỏ gói mới; H2.Desktop không thay đổi.


Kiểm tra bản sao: đã cập nhật bản giải nén cũ trong Downloads/H2 QA bằng các tệp chương trình Release mới; bốn tệp ứng dụng/lõi/Agent trùng SHA-256 với bản đang dùng. Portable diagnostic ở thư mục có dấu cách đạt helper, Python tài liệu và OCR inventory. Đây là thử di chuyển thư mục trên cùng máy, không phải thử máy Windows thứ hai.


Gói ZIP mới 4.309.010.025 byte: H2Notes-Chat-Full-2026-09-21-win-x64.zip. Đã kiểm CRC và SHA-256 từng tệp trong toàn bộ 36.299 tệp; xem H2_PORTABLE_BUILD_2026-09-21.md.
