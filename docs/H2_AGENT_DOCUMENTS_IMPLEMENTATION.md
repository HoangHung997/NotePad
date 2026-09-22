# Triển khai giao diện Agent và tài liệu

Người dùng yêu cầu triển khai toàn bộ bộ `ui-concepts/2026-09-21-agent-documents-demo` ngày 21/09/2026. Yêu cầu này duyệt hướng giao diện mới để triển khai; các dòng `PROPOSED_NOT_ACCEPTED` trong tài liệu tạo ảnh là trạng thái lịch sử, không phải yêu cầu xin duyệt lại. Giữ nguyên toàn bộ ảnh tham chiếu, manifest và baseline 15/09.

## Quy tắc triển khai

- Ivory `#FCFAF7`, thanh bên `#F3EEE7`, terracotta `#A4573D`, chữ charcoal; dùng Segoe UI, lưới khoảng cách 8 DIP.
- Cửa sổ rộng có thanh ứng dụng, dự án/trao đổi, nội dung, tài liệu. 1040 DIP ẩn danh sách dự án; 560 DIP dùng drawer và trang tài liệu riêng. Kích thước nhỏ nhất 560 × 600; Assistant 380 × 610.
- Agent là tab mặc định. Công việc và ghi chú có trang riêng. Chỉ một composer cho mỗi hội thoại; lịch sử cuộn độc lập, composer ở đáy. Trang tài liệu hẹp ẩn composer, có nút quay lại.
- Các ảnh có chỗ bố trí khác nhau giữa các màn hình. Thống nhất cấu trúc dùng chung theo README/PROMPTS và các hiệu chỉnh, không sao chép số liệu, tên tệp hay trạng thái minh họa vào dữ liệu thật.
- Quyền và ngữ cảnh độc lập. Không tự chọn toàn quyền. Trạng thái Agent, xác minh và đồng bộ được lấy từ nguồn thật, không suy diễn thành công.
- Tệp liên kết có xem trước, mở ứng dụng, liên kết lại và dùng trong trao đổi. Quyền sửa không mở rộng chỉ vì liên kết tệp.

## Ma trận kiểm tra

| Ảnh | Luồng triển khai / kiểm tra |
|---|---|
| 01–04 | Agent 1440×860, 1040×760, 560×820, 560×600; điều hướng, composer, inspector |
| 05–06 | Tìm dự án/trao đổi trong drawer; tài liệu riêng và quay lại |
| 07 | Thẻ dự án, bộ lọc, tiến độ checklist; trạng thái Agent và đồng bộ riêng |
| 08–09 | Sửa việc, hoàn thành, ghi chú định dạng, chọn đoạn hỏi Agent |
| 10 | Liên kết tệp/thư mục, tìm, xem trước, liên kết lại, dùng trong trao đổi |
| 11 | Lịch sử và bằng chứng; giữ cả xác minh chưa đạt |
| 12 | Ollama/API riêng, lấy model, nạp model, thử kết nối, lưu cục bộ |
| 13,19 | Xung đột/đổi kho với lựa chọn rõ ràng, chưa chọn thì chưa áp dụng |
| 14–17 | Bong bóng trạng thái, kéo theo panel, Assistant 640/380/full |
| 18 | Đính kèm, @, quyền, model; một hàng điều khiển |
| 20 | PDF theo trang/zoom và nguồn đối chiếu |

## Kết quả triển khai ngày 22/09/2026

Đã thay bề mặt ứng dụng Avalonia theo hướng Agent và tài liệu. Giữ nguyên nguồn WPF, `_ver2`, 20 PNG đề xuất, manifest và 10 PNG/BASELINE.json của ngày 15/09.

- Agent trung tâm; danh sách dự án chứa các trao đổi thật. Cửa sổ 1040 DIP thu thanh bên, cửa sổ 560 DIP dùng drawer. Tab Công việc/Ghi chú/Tệp/Lịch sử chuyển đúng nội dung; khung soạn được chuyển cùng hội thoại thay vì tạo bản sao.
- Tổng quan sáu thẻ 3×2 ở 1440 DIP, tìm/lọc dự án, tiến độ checklist, trạng thái Agent và trạng thái lưu/đồng bộ riêng. Công việc có số thứ tự, checkbox, ghi chú, trạng thái và việc tiếp theo. Ghi chú có định dạng và gửi đoạn được chọn sang Agent.
- Liên kết tệp/thư mục, tìm, báo mất liên kết, liên kết lại và dùng tệp trong trao đổi. Word có nội dung/bảng/định dạng và zoom; Excel có sheet, địa chỉ ô, giá trị lưu, công thức, phân trang và sao chép; PDF render trang thật, đổi trang, zoom, văn bản và nguồn đối chiếu.
- Bằng chứng tách Xem trước/Thay đổi/Bằng chứng. Chỉ hiển thị diff hoặc trạng thái xác minh khi đã được ghi nhận; không tự tạo dấu “đạt” hoặc tô vùng PDF giả.
- Assistant 640×610, 380×610 và cửa sổ đầy đủ: lịch sử trên, composer dưới, cùng thread; đính kèm tệp/ảnh và clipboard, xem/gỡ tệp nháp, lưu nháp riêng theo trao đổi. Model hiện tại là `gemma4:cloud`; không gán mức suy luận không được model công bố.
- Bong bóng 270×54 có bốn trạng thái, mở bằng bấm, kéo theo khung chat. Quyền và ngữ cảnh tách biệt; đính kèm không tự cấp quyền sửa. Các giới hạn quyền và cấp quyền một lần vẫn thực thi qua Agent runtime.
- Cài đặt tách Ollama/API; đổi nhóm rỗng xóa các trường còn sót, lưu đúng profile. Chuyển kho phải chọn hợp nhất/ghi đè/dùng kho hiện có, xem trước rồi áp dụng. Đối chiếu ghi chú có hai phiên bản, ba lựa chọn, kiểm tra nội dung đã thay đổi và giữ audit.

## Kiểm chứng

- Build Release self-contained Windows x64 thành công. Bộ kiểm tra: **574 đạt, 0 lỗi**; gồm quyền, thread/queue, chuyển hội thoại, preview, xung đột và bản nháp đính kèm. Thay đổi cuối về zoom Word được kiểm bằng bản chạy thật và ảnh bổ sung.
- [Thư viện đối chiếu 20 nhóm màn hình](ui-verification/2026-09-22-agent-documents/index.html), [kích thước/DPI](ui-verification/2026-09-22-agent-documents/sizes.txt), [log kiểm tra](ui-verification/2026-09-22-agent-documents/tests.log).
- Chạy ứng dụng native Windows ở **125%**. Ảnh bề mặt xuất 96 DPI theo DIP; Assistant 610 DIP làm tròn thành 610,4 DIP, bong bóng thành 269,6×53,6 DIP trên màn hình này. Ghi chú có ảnh bề mặt 1040×760 và [ảnh native rộng 1440×860](ui-verification/2026-09-22-agent-documents/native-notes-wide.jpg); chưa xác nhận mọi DPI.
- Chuột thật mở Công việc, tích và bỏ tích việc: tiến độ 3/5→4/5→3/5, việc tiếp theo cập nhật. Mở menu model thấy Gemma 4 Cloud. Bấm bong bóng mở Assistant; kéo bong bóng **(-50,-25) pixel**, panel cùng delta. [Tọa độ ghi nhận](ui-verification/2026-09-22-agent-documents/native-bubble-follow.json).
- Chuột thật chọn đoạn ghi chú và bấm Hỏi Agent: đoạn chọn nằm trong bản nháp, chưa gửi model. Kéo resize từ 1440×860 xuống 560×600 giữ nguyên bản nháp; mở tài liệu hẹp ẩn composer, quay lại còn nội dung cũ. Zoom Word lên 166% phóng to trang thật và có cuộn ngang.
- Tất cả dữ liệu kiểm tra là bộ mẫu riêng. Không thử ghi đè kho dữ liệu người dùng. Kiểm tra SHA-256 xác nhận **10/10 ảnh baseline gốc giữ nguyên**.
- Bản portable đã kiểm offline: Python Agent tạo/đọc tài liệu và render, DesktopHost/OfficeHost, đủ thành phần OCR. Máy thứ hai chưa được kiểm trực tiếp; việc nhận diện OCR không thay thế thử chất lượng trên tài liệu thật.
- [Gói đầy đủ ngày 22/09](H2_PORTABLE_BUILD_2026-09-22.md): ZIP 4,31 GB; 36.300/36.300 tệp khớp SHA-256 và CRC khi đọc lại. Bản mới đã được khởi động từ thư mục portable trên máy; kết nối được chọn vẫn là Gemma 4 Cloud.

## Giới hạn được giữ rõ trong giao diện

Đã triển khai các luồng chính của cả 20 nhóm, nhưng **chưa chứng nhận giống 100% từng pixel** với ảnh ImageGen. Nội dung, mật độ chữ, font Windows và trạng thái thực tế khác dữ liệu vẽ mẫu. Word preview chưa phải máy dàn trang của Word; mở tệp gốc để xem phân trang in. Tô vùng trích dẫn PDF cần tọa độ nguồn thật; không suy đoán từ câu trả lời. Tab Thay đổi cần diff đã ghi nhận. Nhập giọng nói dùng Windows, chưa phải hội thoại âm thanh hai chiều. Khả năng tác vụ phụ thuộc model, quyền đã chọn và ứng dụng có trên máy; không khẳng định đầy đủ mọi chức năng Codex.
