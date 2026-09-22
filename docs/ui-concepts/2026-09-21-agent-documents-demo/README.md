# H2 Notes — Agent và tài liệu

Bộ 20 ảnh đề xuất ngày **21.09.2026**, phát triển từ hướng “Agent và tài liệu” người dùng đã chọn để xem thêm. Mở [thư viện ảnh](index.html) bằng trình duyệt để lọc theo nhóm và xem từng ảnh lớn. Trang chạy trực tiếp từ thư mục, không cần mạng hoặc máy chủ.

**Đề xuất giao diện · Dữ liệu minh họa · Chưa nghiệm thu.** Các kích thước dưới đây là mục tiêu bố cục tính bằng DIP, không khẳng định kích thước pixel của ảnh xuất ra. Ảnh ImageGen không phải ảnh chụp ứng dụng Avalonia đang chạy và không thay thế việc kiểm tra giao diện ở đúng kích thước/DPI. Bộ ảnh này không chỉnh sửa hoặc thay thế các PNG và `BASELINE.json` của [baseline responsive hybrid đã duyệt](../2026-09-15-responsive-hybrid/README.md).

Các ảnh dùng chung hướng ivory / terracotta, Agent làm vùng trao đổi chính, tài liệu và bằng chứng nằm trong vùng xem chi tiết. Nội dung dự án, kết quả Agent, trạng thái kết nối và các giá trị hiển thị đều là dữ liệu minh họa.

## Cách xem

- Mở [index.html](index.html), chọn **Tất cả**, **Kích thước**, **Chức năng**, **AI Assistant** hoặc **Thao tác**.
- Bấm ảnh để mở lớn. Dùng nút trước/sau hoặc phím **← / →**; nhấn **Esc** hoặc **×** để đóng.
- **Mở ảnh gốc** mở PNG riêng trong trình duyệt để phóng to theo nhu cầu. Bộ đếm trong khung xem theo nhóm đang chọn.
- Đây là thư viện ảnh để thảo luận thiết kế. Các nút được vẽ bên trong ảnh chưa phải chức năng của ứng dụng.

## Kích thước và bố cục

| # | Ảnh | Mục tiêu DIP | Ý đồ bố cục |
|---|---|---|---|
| 01 | [Agent và tài liệu · rộng](01-agent-wide.png) | 1440 × 860 | Ba vùng: dự án/hội thoại, Agent, tài liệu; duyệt thay đổi ngay trong hội thoại. |
| 02 | [Agent và tài liệu · vừa](02-agent-medium.png) | 1040 × 760 | Thu danh sách dự án thành bộ chọn để giữ chỗ cho Agent và tài liệu. |
| 03 | [Agent · cửa sổ hẹp](03-agent-narrow.png) | 560 × 820 | Một cột hội thoại, tài liệu mở bằng nút riêng; composer luôn ở đáy. |
| 04 | [Agent · kích thước tối thiểu](04-agent-minimum.png) | 560 × 600 | Header gọn, kết quả cuộn riêng, một vùng nội dung tại một thời điểm. |
| 05 | [Đổi dự án trên cửa sổ hẹp](05-project-picker.png) | 560 × 820 | Drawer tìm/chọn dự án và hội thoại, có nút đóng rõ ràng. |
| 06 | [Xem tài liệu trên cửa sổ hẹp](06-document-narrow.png) | 560 × 820 | Tài liệu thành trang riêng; quay lại hội thoại bằng một nút. |

## Chức năng ứng dụng

| # | Ảnh | Mục tiêu DIP | Nội dung minh họa |
|---|---|---|---|
| 07 | [Tổng quan dự án](07-command-center.png) | 1440 × 860 | Việc cần xử lý, tiến độ theo checklist, trạng thái Agent và đồng bộ. |
| 08 | [Công việc dự án](08-project-tasks.png) | 1440 × 860 | Sửa công việc, tích hoàn thành, ghi chú và việc tiếp theo. |
| 09 | [Ghi chú dự án](09-project-notes.png) | 1440 × 860 | Soạn ghi chú có định dạng, chọn đoạn để trao đổi với Agent. |
| 10 | [Tệp và bản xem trước](10-project-files.png) | 1440 × 860 | Liên kết Word/Excel/PDF, xem trước tài liệu và báo tệp mất liên kết. |
| 11 | [Lịch sử, bằng chứng và xác minh](11-history-evidence.png) | 1440 × 860 | Theo dõi thay đổi và mở nguồn xác minh; giữ dấu lần kiểm tra chưa đạt. |
| 12 | [Kết nối AI và model](12-ai-settings.png) | 1040 × 760 | Ollama local/LAN/Docker, khu vực API riêng, chọn model và thử kết nối. |
| 13 | [Đồng bộ và xử lý xung đột](13-storage-conflict.png) | 1040 × 760 | Phân biệt lưu máy, đồng bộ, hàng đợi AI và xung đột cần chọn. |
| 19 | [Chuyển kho và nhập dữ liệu](19-storage-transfer.png) | 1040 × 760 | Xem nguồn/đích và chọn hợp nhất, ghi đè hoặc dùng dữ liệu hiện có. |
| 20 | [PDF và nguồn đối chiếu](20-pdf-evidence.png) | 1440 × 860 | Xem trang PDF, phần được dẫn nguồn và liên kết về kết quả Agent. |

## AI Assistant

| # | Ảnh | Mục tiêu DIP | Nội dung minh họa |
|---|---|---|---|
| 14 | [Bong bóng AI Assistant](14-assistant-bubble.png) | 1440 × 860 | Bốn trạng thái: sẵn sàng, đang làm, cần bạn xem, hoàn tất. |
| 15 | [Assistant nổi · 640 × 610](15-assistant-640.png) | 640 × 610 | Lịch sử ở trên, composer dưới; nhận ngữ cảnh tài liệu đang mở. |
| 16 | [Assistant nổi · tối thiểu 380 × 610](16-assistant-380.png) | 380 × 610 | Thu nhãn điều khiển, giữ chữ dễ đọc và nút Dừng. |
| 17 | [Assistant · cửa sổ đầy đủ](17-assistant-full.png) | 1040 × 760 | Mở trao đổi đầy đủ và tài liệu; có thể gắn tác vụ vào dự án. |

## Thao tác dùng chung

| # | Ảnh | Mục tiêu DIP | Nội dung minh họa |
|---|---|---|---|
| 18 | [Ô soạn, ngữ cảnh, quyền và model](18-composer-menus.png) | 1440 × 860 | Bốn cận cảnh thao tác dùng chung cho Agent dự án và AI Assistant. |

## Nguồn và phạm vi

- [Manifest bộ ảnh](manifest.json): tên tệp, nhóm, tiêu đề, mô tả và kích thước mục tiêu.
- [Ghi nhận kiểm tra](DESIGN_REVIEW.md): phạm vi đã rà soát và giới hạn của bộ ảnh đề xuất.
- [Prompt tạo ảnh](PROMPTS.md): yêu cầu tạo từng ảnh và cách bám ảnh tham chiếu.
- [Hiệu chỉnh vòng 1](REVISIONS.md), [vòng 2](REVISIONS-2.md): chỉnh trạng thái, phạm vi và dữ liệu minh họa sau khi xem ảnh.
- [Ảnh tham chiếu Agent và tài liệu](../2026-09-21-ui-rebuild-demos/02-agent-workspace.png).
- [Baseline đã duyệt](../2026-09-15-responsive-hybrid/README.md) vẫn giữ nguyên. Việc chọn một ảnh trong bộ demo này chưa tự động thay đổi baseline hay mã nguồn ứng dụng.

Thư viện nhúng sẵn metadata để mở được qua `file://`. Khi chỉnh danh mục sau này, cần cập nhật cả `manifest.json` và phần `demo-data` trong `index.html`.
