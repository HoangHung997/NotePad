# H2 Notes — Agent và tài liệu — bản portable 22/09/2026

Gói: `C:\Users\hoang\Downloads\H2Notes-Agent-Documents-Full-2026-09-22-win-x64.zip`.

- ZIP cập nhật sau sửa Word/CV và bong bóng thu gọn: **4.331.090.901 byte** (~4,33 GB).
- **36.317 tệp**, 7.895.949.160 byte sau giải nén (chưa tính manifest).
- SHA-256 mới: `b91f0277bcfd1dd6d3849af6966b06c3f69d862a820b40c157d3bc8517cc2c1c`.
- Windows x64, .NET self-contained, Python Agent, DesktopHost/OfficeHost, GOT-OCR 2.0, Docling và MinerU đi kèm.
- Bản chạy trên máy được cập nhật tại thư mục `H2Notes-Full-2026-09-21-win-x64` để giữ lối tắt đang có. `build-info.json` ghi bản mới ngày 22/09.

## Cách dùng trên máy khác

Giải nén **toàn bộ** vào thư mục ngắn có quyền ghi, ví dụ `C:\H2Notes`, rồi mở `MO H2 NOTES.cmd` hoặc `H2Notes.Avalonia.exe`. Không chạy từ bên trong ZIP, không chỉ chép riêng EXE.

Cấu hình lại kết nối AI/Ollama trên máy đích. API key, tài liệu, lịch sử và bản nháp cá nhân không nằm trong gói. Ollama/model chat, Office và AutoCAD là các ứng dụng bên ngoài; cần cài hoặc kết nối máy chủ tương ứng để sử dụng.

## Kiểm tra

- Release publish thành công; bộ kiểm tra ứng dụng **590/590**. [Bong bóng thu gọn](H2_BUBBLE_COMPACT_2026-09-22.md): 11 ảnh native 125% và kiểm bấm/kéo/mở/ẩn/hoàn thành. [Sửa lỗi Word/CV](H2_WORD_CV_REPAIR_2026-09-22.md) đã kiểm ở lượt trước: 3 lượt phục hồi trên Word thật, 3 lượt Gemma 4 Cloud và 54 điều kiện DOCX độc lập đạt.
- [Kiểm tra portable mới](../.artifacts/bubble-compact-2026-09-22/portable-final/portable-check.json): Python tài liệu và hai helper chạy được, đủ thành phần OCR.
- [Kiểm tra ZIP mới](../.artifacts/bubble-compact-2026-09-22/archive-verification.json): đọc lại cả 36.317 tệp qua ZIP, khớp SHA-256/CRC/kích thước và danh sách manifest, không có tên tệp trùng. Không giải nén thêm một bản đầy đủ vì dung lượng trống không đủ. Các payload nén không đổi được giữ nguyên; phần cuối ZIP cũ có bản sao phục hồi riêng.
- Bản portable mới đã được khởi động trên máy hiện tại, chỉ một tiến trình H2 Notes dùng cấu hình người dùng.
- [Biên bản UI và các giới hạn](H2_AGENT_DOCUMENTS_IMPLEMENTATION.md), [thư viện đối chiếu](ui-verification/2026-09-22-agent-documents/index.html).
- [Kiểm thử Agent bằng Gemma 4 Cloud](H2_AGENT_CAPABILITY_ACCEPTANCE_2026-09-22.md): tạo/sửa/xóa Word/Excel/PDF, Word đang mở, DWG/DXF qua AutoCAD, ba OCR và luồng OCR→Agent, đọc/lọc/lưu tin thật. Excel đang mở còn bị hộp thoại chặn; chưa xác nhận lại điều khiển desktop native trong phiên Windows từ chối input.

Chưa thử trên máy vật lý thứ hai. Kiểm tra thành phần OCR không phải chứng nhận chất lượng mọi tài liệu hoặc mọi khả năng của model.
