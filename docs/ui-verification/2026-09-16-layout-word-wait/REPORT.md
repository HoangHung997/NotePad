# Word giữ bố cục và chờ AI trên máy yếu

Ngày kiểm: 16/09/2026. Nhánh `codex/project-sheet`; worktree có sẵn nhiều thay đổi chưa commit. Không sửa WPF, `_ver2`, PNG chuẩn hoặc `BASELINE.json`.

## Phạm vi triển khai

- +/@ có thao tác Word giữ bố cục từ PDF/ảnh. Bản nháp AI cũng có thể tham chiếu ID nguồn, nhưng không được tự ghi tệp hoặc lấy đường dẫn do model đề xuất để đọc.
- Dùng MinerU CPU cục bộ để lấy bố cục; dùng EasyOCR vi/en trong bộ Docling đã cài và kiểm model nếu có. Không tải model khi chạy, không gửi bản nguồn đến LLM trong luồng này.
- Word có chữ chỉnh sửa được trong các khung đoạn văn tại tọa độ trang. Dấu/chữ ký, bảng và vùng chưa nhận chữ giữ ảnh. Cho sửa OCR, font/cỡ và định dạng đậm/nghiêng trước khi chọn nơi lưu. Quyền Chỉ đọc không tạo tệp; vẫn xác nhận ghi đè, kiểm lại phạm vi trước khi ghi.
- Mặc định chờ chat đến tín hiệu hoàn tất hoặc Dừng/lỗi mạng, không đặt giới hạn tổng thời gian. Cấu hình cũ tự nhận mặc định này. Nếu tắt, giới hạn im lặng đặt lại theo từng dòng reasoning/answer/heartbeat. Nhận done thì kết thúc, không đợi EOF.

## Kiểm thử

| Kiểm tra | Kết quả và bằng chứng |
| --- | --- |
| Release build | 0 cảnh báo, 0 lỗi; `build.txt`. |
| Bộ kiểm thử ứng dụng | 322 đạt, 0 lỗi; `tests.txt`. Bao gồm 4 giao thức stream, chờ ban đầu và giữa các phần lâu hơn timeout cũ, đặt lại idle timer, hủy, không retry, server không đóng stream sau done. |
| Bridge Python | 13 đạt; `bridge-tests.txt`. Bao gồm giới hạn trang, từ chối output chỉ có ảnh, giữ hình nền/nguồn nguyên vẹn. |
| PDF thực tế qua C# | 1 trang, 19 dòng sửa được, hash nguồn không đổi, không LLM. |
| Ảnh của trang mẫu qua C# | 1 trang, 19 dòng sửa được, hash nguồn không đổi, không LLM. |
| DOCX mẫu đã rà | OpenXML hợp lệ, 19 dòng native, 1 trang; đã render bằng LibreOffice và xem toàn trang, không thấy tràn/ngắt dòng thừa. Đã sửa OCR và font/đậm/nghiêng bằng tay cho mẫu này, không phải kết quả tự động nguyên trạng. |
| DOCX hai trang thử | Hai trang A4, 38 dòng sửa được, OpenXML hợp lệ; render đúng 2 trang, xem trang 2 xác nhận chữ/đồ họa không lặp sai trang hay tràn. Trang 2 là dữ liệu kiểm thử nhân từ mẫu, không phải tài liệu người dùng. |
| Baseline | Cả 10 SHA-256 đúng manifest; `baseline-hashes.txt`. |

Nguồn PDF được xử lý cục bộ, SHA-256 `0C98774B26D6FAC9AE93945F3EFD932F497478E4497A4DCCB0FCAB6AA730F846`. Không đưa nội dung tài chính, ảnh trang hay bản Word của người dùng vào kho mã. Bản mẫu riêng lưu tại `D:/Downloads/H2Notes-Word/2026.09.14_So1409_De-nghi-tam-ung.docx`, SHA-256 `A7EE6D33688A1D20C5F7A552EF6EE259AE299B3B4D75F55C0F8953BA5E179B23`.

## Đối chiếu giao diện

Chạy app Release với `--demo --data <tệp thử riêng> --layout-evidence <thư mục bằng chứng>`. Dữ liệu tổng hợp, không gửi AI/OCR trong lượt chụp. Windows native scale 125%; ảnh dưới đây là RenderTargetBitmap 96 DPI từ control thật, **không phải ảnh chụp cửa sổ native**.

- UI-04: `files-compact-560x820-render96.png`, đối chiếu `04-hep-mo-ai.png`: giữ rail trái, trang AI tạm và quay lại dự án. Composer một hàng và model/menu đã thay theo các yêu cầu mới hơn, không giống từng pixel ảnh đầu.
- UI-07: `files-docked-1440x860-render96.png`, đối chiếu `07-toan-man-hinh-ai-ben-phai.png`: giữ danh sách trái, công việc/ghi chú giữa, AI phải và màu ivory/terracotta. Mật độ chữ, khoảng cách, một số điều khiển khác ảnh mẫu; chưa nghiệm thu pixel parity.
- `wait-setting-620x840.png`: tùy chọn chờ xuất hiện và bật mặc định. Mục mới không có mockup riêng.
- `layout-review-1080x760.png`, `layout-review-680x600.png`: xem trước trái, sửa OCR/font phải, hướng dẫn và Hủy/Lưu ở cuối. Đã khắc phục text preview bị lặp và ô cỡ chữ bị cắt. Ở 680 DIP, đậm/nghiêng tự xuống hàng, còn nút lưu. Màn hình xem trước là ước lượng, không phải renderer Word.
- `interaction-checks.txt`: sửa OCR cập nhật model, hủy/đóng không lưu. `thinking-scroll.txt`: 80/80 dòng, offset bằng cuối vùng cuộn ở cả 1440x860 và 560x820.

Công cụ Computer Use không liệt kê H2 Notes đang chạy (cửa sổ không nằm trên taskbar). Vì vậy chưa kiểm trực tiếp nhấp chuột native, file picker, Word tương tác, DPI đa màn hình và pixel parity; không dùng build/headless test để đánh dấu các mục đó đạt. Không thử API online thực trong đợt này; 4 giao thức timeout dùng luồng giả lập có độ trễ thật.

## Giới hạn và mở lại

Đây là bản thử giữ bố cục, tối đa 10 trang/8 MiB. Font scan ước lượng Times New Roman/Arial; tiếng Việt, số tiền, số tài khoản và ngày tháng cần rà lại. Bảng chưa phải bảng Word chỉnh sửa được. Đoạn thêm dài có thể cần dàn trang lại; toàn dòng sửa nội dung sẽ mất định dạng theo đoạn đã áp dụng cho dòng đó. Không có bảo đảm khôi phục đúng mọi font/bố cục từ PDF hoặc ảnh.

Sau khi người dùng xác nhận Thoát, không còn tiến trình dùng kho thật trước build. Đã mở đúng `src/H2Notes.Avalonia/bin/Release/net10.0/H2Notes.Avalonia.exe --show`, PID 26996, kiểm lại chỉ có một tiến trình. Các phiên thử trước đều dùng kho demo riêng và đã đóng. Exe SHA-256 `FFB7CB9DF70A9C537EDF310C2F9DD4BC020E64FA5BFACF94F6F61FC4D470AF40`; Avalonia DLL `5BA6001285D7A135FE49C623E4B55BFD3D9D872C0043353521C996C47292BA97`; Core DLL `89EE1A1186DA224F91FD30DE3E57E972E8AC511BD1219252D77900E47AC50E32`. Bridge layout trong bản phát hành đã kiểm trùng mã nguồn.

Kết luận: kiểm thử logic và bản Word đã rà đạt trong phạm vi trên; nghiệm thu UI native và độ chính xác tự động trên nhiều loại tài liệu còn mở.
