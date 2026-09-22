# Bong bóng Assistant thu gọn — 22/09/2026

Theo yêu cầu mới của người dùng, Assistant rảnh hoặc đã kết thúc dùng hình tròn nhỏ. Khi Agent đang làm và chat bị ẩn, bong bóng mở ngang với một dòng hoạt động. Mở chat sẽ thu bong bóng về hình tròn; ẩn chat giữa tác vụ sẽ hiện lại thanh hoạt động. Chiều cao không tăng.

## Hành vi đã triển khai

- Hình tròn 54×54 DIP; thanh hoạt động 320×54 DIP. Windows có thể làm tròn kích thước theo pixel vật lý.
- Hiện hoạt động công khai mới nhất của Agent: suy nghĩ, đọc/sửa Word, cập nhật Excel, tra cứu mạng, đọc tài liệu, kiểm tra kết quả. Nội dung dài cuộn ngang trên một dòng và có tooltip. Không phát lại toàn bộ nội dung tài liệu, đầu ra công cụ hay suy luận nội bộ.
- Chỉ mở thanh khi tác vụ còn hoạt động và cửa sổ chat bị ẩn hoặc thu nhỏ. Chờ xác nhận vẫn có dấu hiệu để người dùng mở chat; hoàn thành, lỗi cuối cùng và hủy đều thu tròn.
- Giữ vị trí tâm biểu tượng khi mở/thu. Sát mép phải thì thanh mở sang trái. Bấm mở chat, kéo di chuyển, khung chat bám theo; chuột phải mở menu khi đang là hình tròn.
- Vị trí/cài đặt vẫn thuộc máy hiện tại, không đưa vào dữ liệu dự án dùng chung.

## Kiểm tra thực tế

Chạy bản Release Windows x64 trong chế độ demo với dữ liệu/cài đặt riêng. Hoạt động mẫu không gọi model và không sửa tài liệu người dùng. Ảnh lấy từ cửa sổ Avalonia thật bằng RenderTargetBitmap ở DPI thực tế 125%:

- [Rảnh](ui-verification/2026-09-22-bubble-compact/01-idle-circle.png), [đang làm khi ẩn chat](ui-verification/2026-09-22-bubble-compact/02-working-hidden-chat.png).
- [Hoạt động dài](ui-verification/2026-09-22-bubble-compact/03-long-activity.png), [sau khi cuộn ngang](ui-verification/2026-09-22-bubble-compact/04-long-activity-scrolled.png).
- [Mở chat](ui-verification/2026-09-22-bubble-compact/05-working-open-chat.png), [ẩn lại](ui-verification/2026-09-22-bubble-compact/06-working-chat-hidden-again.png), [hoàn thành](ui-verification/2026-09-22-bubble-compact/07-completed-circle.png).
- [Sát mép phải](ui-verification/2026-09-22-bubble-compact/08-working-right-edge.png), [mở chat tại mép phải](ui-verification/2026-09-22-bubble-compact/09-right-edge-chat-open.png), [ẩn chat tại mép phải](ui-verification/2026-09-22-bubble-compact/10-right-edge-chat-hidden.png), [thu lại tại mép phải](ui-verification/2026-09-22-bubble-compact/11-right-edge-completed.png).
- [Kích thước, DPI, vị trí và trạng thái](ui-verification/2026-09-22-bubble-compact/states.json). Chiều cao thực tế 53,6 DIP do làm tròn ở 125%; vị trí neo sát mép phải giữ nguyên khi mở/ẩn và kết thúc.

Đã kiểm bằng chuột Windows qua Computer Use: bắt đầu tác vụ mẫu → thanh ngang; bấm H2 → chat mở/bong bóng tròn; kéo H2 từ (700,650) sang (741,691) pixel → chat dịch theo chiều ngang và giữ giới hạn màn hình; ẩn chat → thanh hiện lại; hoàn thành → hình tròn. Thao tác chọn bằng accessibility đôi lúc trả sai tọa độ/cache; lấy ảnh mới và bấm theo ảnh đã hoạt động. Không dùng kết quả lỗi của công cụ này làm bằng chứng ứng dụng lỗi.

Đã xem ảnh đối chiếu với `2026-09-21-agent-documents-demo/14-assistant-bubble.png`: giữ nền kem, màu đất nung, logo H2 và thanh thấp bo tròn. Hình tròn lúc rảnh, chiều rộng thanh và việc ẩn thanh lúc mở chat là thay đổi được người dùng yêu cầu, không phải khớp từng pixel với ảnh cũ. Không thay PNG/BASELINE.json gốc. Chưa nghiệm thu trực tiếp DPI 100%/150% hoặc màn hình vật lý thứ hai.

## Kiểm tra mã và gói

- Release self-contained publish đạt. Bộ kiểm tra đầy đủ: **590/590 đạt**, gồm 2 kiểm tra hồi quy mới cho bong bóng/tiến trình. Log: `.artifacts/bubble-compact-2026-09-22/tests-full-final.log`.
- Kiểm tra hồi quy bao phủ vòng đời thật của App/Agent adapter, hoạt động mới nhất, không tự mở chat, không rò đầu ra công cụ, một dòng cố định, neo vị trí, phân biệt bấm/kéo/hủy và vị trí đa màn hình.
- Lượt đầu toàn bộ đạt 589/590: một kiểm tra cấu trúc còn yêu cầu tên API đọc tóm tắt cũ. Đã cập nhật sang API quan sát tác vụ hiện có, API này trả cả tóm tắt và hoạt động; không tạo runtime hay kho tác vụ thứ hai.
- Đóng gói vào bản full portable hiện có, giữ các sửa Word/CV, Python, OfficeHost/DesktopHost và OCR. Xem [thông tin bản portable](H2_PORTABLE_BUILD_2026-09-22.md) để biết kết quả kiểm ZIP và cách chuyển máy.
