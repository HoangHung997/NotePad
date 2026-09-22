# Kiểm tra bộ ảnh đề xuất — 21.09.2026

Trạng thái: **PROPOSED_NOT_ACCEPTED**. Đây là rà soát ảnh thiết kế và thư viện xem ảnh, không phải nghiệm thu giao diện ứng dụng.

## Đã kiểm tra

- Đủ 20 PNG theo manifest; tất cả mở được bằng bộ đọc ảnh.
- Đã xem trực quan cả 20 ảnh, gồm các ảnh sau hiệu chỉnh. Giữ hướng màu ivory/terracotta của ảnh tham chiếu.
- Sửa nhãn tiến độ đang chạy ở ảnh 03; tách phạm vi Assistant khỏi dự án ở ảnh 16 và 17.
- Sửa trạng thái Agent và đồng bộ riêng biệt ở ảnh 07; bỏ nhấn mạnh mặc định cho lựa chọn ghi đè ở ảnh 19; sửa số hạng mục chênh lệch ở ảnh 20.
- Manifest và danh mục nhúng trong HTML cùng 20 mục; JavaScript của thư viện đã kiểm tra cú pháp.
- Đã mở thư viện bằng trình duyệt trong Codex qua máy chủ cục bộ. Kiểm tra lọc AI Assistant trả về 4/20 ảnh, mở ảnh lớn, phím mũi tên chuyển 2/4 sang 3/4, Esc đóng ảnh và trả lại tiêu điểm, trở về nhóm 20/20 ảnh.
- Đã xem thư viện ở khung trình duyệt 1280 × 720 và khung hẹp 382 × 669. Ảnh giữ tỷ lệ và thư viện chuyển thành một cột ở khung hẹp.

## Giới hạn của bản demo

- Kích thước DIP ghi trong manifest là mục tiêu bố cục; ảnh xuất là bitmap có độ phân giải riêng, không phải ảnh chụp app đúng DPI.
- Ảnh 15 có canvas vuông 1254 × 1254 gồm khoảng trống quanh cửa sổ; nhãn 640 × 610 mô tả mục tiêu cửa sổ nổi.
- Nội dung, tên model, kết nối, số liệu và thời gian là minh họa. Tên model không xác nhận khả năng của một model đang được cài đặt thực tế.
- Khoảng cách, cỡ chữ, vị trí composer trong màn hình phụ và thanh điều hướng còn cần thống nhất khi chuyển từ ảnh sang bản thiết kế triển khai.
- Chưa triển khai, chạy hoặc nghiệm thu giao diện Avalonia trong công việc này. Không thay đổi mã nguồn ứng dụng, dữ liệu người dùng hoặc baseline đã duyệt.

Prompt gốc: [PROMPTS.md](PROMPTS.md). Hiệu chỉnh: [vòng 1](REVISIONS.md), [vòng 2](REVISIONS-2.md).
