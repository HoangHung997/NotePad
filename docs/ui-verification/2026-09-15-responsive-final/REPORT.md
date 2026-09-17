# Đối chiếu bản responsive đầu tiên

15/09/2026, Release .NET 10 / Avalonia 12.1.2. Baseline: `h2-notes-responsive-hybrid-v1`.

10/10 SHA-256 và kích thước baseline đã kiểm, không thay ảnh chuẩn. Dữ liệu render là demo riêng, dự án số 3, hai việc xong trên ba việc; không sửa dữ liệu thật để chụp. AI không có tin nhắn mẫu giả làm phản hồi thật.

## Phương pháp

App thật chạy trên Windows, render chính control của cửa sổ bằng `RenderTargetBitmap`, 96 DPI theo kích thước DIP trong tên file. Scale cửa sổ báo 1.25. Không capture desktop vì công cụ native không trả cửa sổ ẩn taskbar. Các hình này chứng minh bố cục control, **không thay thế nghiệm thu pixel/IME/input/DPI native**.

Đã xem ảnh baseline UI-01/UI-05 và các render qua ba lượt chỉnh. Thay bố cục xanh trước đó bằng màu kem/terracotta, drawer hẹp, task dạng xếp hai dòng khi hẹp, bảng hai cột khi rộng; sửa nút accent còn xanh, vùng đọc công việc thiếu chỗ và khung AI dưới chiếm hết vùng nhập.

| ID | Render | Kết luận |
| --- | --- | --- |
| UI-01 | `UI-01-560x820-render96.png` | Có bộ chọn, tiêu đề xuống dòng, đủ 3 việc mẫu, Notes dưới; icon/toolbar chưa khớp hoàn toàn. |
| UI-02 | `UI-02-560x820-render96.png` | Drawer, tìm kiếm, highlight đã có; đổi dự án được test headless. |
| UI-03 | `UI-03-560x820-render96.png` | Checklist tóm tắt, Notes mở rộng; chưa kiểm thao tác Windows. |
| UI-04 | `UI-04-560x820-render96.png` | Trang AI tạm, quay lại, chọn kết nối, ngữ cảnh, composer; chưa có API smoke test. |
| UI-05 | `UI-05-1040x760-render96.png` | Sidebar, bảng việc, Next và Notes; đúng hướng bố cục, chưa nghiệm thu toàn bộ chi tiết. |
| UI-06 | `UI-06-1040x760-render96.png` | Khung AI nổi không ép editor; có tay nắm và resize, chưa kiểm native. |
| UI-07 | `UI-07-1440x860-render96.png` | Ba vùng với AI phải. Đây là kích thước tham chiếu, không phải bằng chứng thao tác maximize. |
| UI-08 | `UI-08-docked-1160x820-render96.png` | Chỉ trạng thái sau ghim dưới; thiếu screenshot drag preview/cancel, vùng đọc còn thấp. |
| UI-09 | `UI-09-560x600-render96.png` | Note riêng không sidebar; toolbar chưa đủ chức năng trong baseline. |
| UI-10 | `UI-10-560x600-render96.png` | Dùng tab ở chiều cao tối thiểu; draft/selection/undo được test khi resize. |

**Tổng kết: đang làm, chưa nghiệm thu UI.** Không đánh dấu Đạt chỉ từ build và ảnh render. Test suite của lượt cuối: 77/77; chi tiết phạm vi và các chức năng còn thiếu trong [báo cáo triển khai](D:/VSstudio/Nodepad/docs/RESPONSIVE_IMPLEMENTATION.md).
