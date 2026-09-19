# H2 Notes: bảng nghiệm thu UI và yêu cầu mới

> **HISTORICAL UI ACCEPTANCE / MIGRATION EVIDENCE**
>
> Bảng này tiếp tục được giữ để bảo toàn các cổng UX/native/DPI/IME và bộ ảnh cũ. Future product authority là `docs/H2_PRODUCT_MASTER_SPEC.md` + `docs/H2_PRODUCT_MASTER_TASKS.md`. Khi redesign, dùng bảng này làm bằng chứng migration/so sánh, không xem nó là kiến trúc tương lai.

## Thanh soạn một hàng · 15/09/2026

Theo ảnh bổ sung của người dùng: +/quyền/model/mic/gửi cùng hàng; quyền chỉ còn khiên khi thanh nút hẹp dưới 460 DIP. Model và mức suy luận ở chung thẻ; slider chỉ dùng mức hỗ trợ và có đặt lại mặc định. Mốc riêng tư và xem dữ liệu gửi chỉ nằm trong Ngữ cảnh của +/@. Sửa Ctrl+Enter ở pha xử lý trước TextBox để không bị mất phím; chọn @ vẫn không tự gửi. Kiểm thử, ảnh thực tế và phần chưa kiểm native ghi tại [báo cáo](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-composer-single-row/REPORT.md).

## Thanh soạn và PDF · 15/09/2026

Bổ sung kiểm thử OCR: đã cài cả GOT-OCR 2.0, MinerU và Docling trên CPU; ba phép thử qua chính `AiPdfProcessor` đều nhận Markdown, giữ nguồn và không gửi PDF gốc trong chế độ OCR. Đây là kiểm tra tích hợp, không xác nhận độ chính xác scan tiếng Việt hay thử PDF với API có phí. Bằng chứng trong báo cáo bên dưới.

Đã bổ sung thanh soạn theo ảnh Codex người dùng gửi: ba quyền, model đã lưu, mức suy luận có kiểm tra hỗ trợ, nút mic Windows, gửi/dừng tròn. 304 kiểm thử đạt, Release không lỗi/cảnh báo. Có đường gửi PDF gốc và tiền xử lý Markdown với chọn engine/cài đặt cục bộ; mức sẵn sàng OCR phải dựa trên thử thực tế, không suy từ việc có menu. Render bản Release tại UI-04/UI-07 và cửa sổ AI 340×420/900×620; đã sửa footer chiếm hết vùng đọc ở kích thước nhỏ. Cả 10 hash chuẩn không đổi. Kiểm thử native mic/IME/clipboard và DPI khác vẫn chưa xác nhận. [Ảnh, kiểm tra và giới hạn](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-composer-pdf/REPORT.md).

## Cập nhật chat, suy nghĩ và LAN · 15/09/2026

Đã thêm tiến trình mở/thu gọn, tự xóa khi có câu trả lời; địa chỉ Ollama LAN; gửi trực tiếp không hỏi lại; nút +, dán ảnh, thả tệp và menu @. Release build không lỗi/cảnh báo; 255 kiểm thử đạt. Đã sửa lỗi bố cục khi tách/ghép AI được phát hiện lúc chạy app thật; lần chạy cuối thoát bình thường. LAN gửi/nhận thành công với Gemma4, nhưng câu thử không trả phần suy nghĩ, không nhận là đã kiểm chứng suy nghĩ live. Render app thật theo UI-04/UI-07 và cửa sổ nhỏ; native clipboard/chuột/DPI chưa kiểm trực tiếp. Xem [bằng chứng và giới hạn](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-chat-input/REPORT.md).

## Cập nhật AI tài liệu · 15/09/2026

Đã thêm ngữ cảnh dự án đầy đủ, đính kèm ảnh/Word/Excel/văn bản và bản nháp tệp để người dùng lưu. Release build không lỗi/cảnh báo, 131 kiểm thử đạt. Đã đối chiếu render của app thật với UI-04/UI-07, chỉnh bố cục cửa sổ AI thấp. Chưa chứng nhận native Windows/DPI/file dialog hoặc chất lượng model thật. Xem [bằng chứng và giới hạn](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-ai-documents/REPORT.md) và [hướng dẫn](D:/VSstudio/Nodepad/docs/AI_DOCUMENTS.md).

Chuẩn ngày 15/09/2026 là baseline lịch sử của UI hiện tại. Đọc `docs/H2_PRODUCT_MASTER_SPEC.md` trước khi thay information architecture; dùng `docs/APPROVED_PRODUCT_SPEC.md` và bộ 10 ảnh cũ để kiểm migration/không mất hành vi quan trọng.

**Cập nhật triển khai 15/09:** đã có bản chạy thử, 77/77 tests và render các control thật, nhưng chưa đủ cổng nghiệm thu native. [Báo cáo hiện hành](D:/VSstudio/Nodepad/docs/RESPONSIVE_IMPLEMENTATION.md) và [đối chiếu render](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-responsive-final/REPORT.md) thay phần tiến độ "Chưa làm" trong bảng lịch sử dưới đây. Các ô đó không được xem là Đạt; giữ để tiếp tục nghiệm thu từng tình huống.

## Quy trình mỗi chặng

Kiểm tra AI thật 15/09: đã mở bản Release mới sau khi người dùng thoát app. 113/113 tests; đã xác nhận lỗi 402 của model Ollama Cloud cũ và 404 của Gemini 2.5, thử thành công gemma3:4b local và Gemini 3.1 Flash-Lite online bằng câu thử ngắn. Thêm lỗi rõ ràng trong lịch sử, nút thử AI và sửa cập nhật cấu hình trong chat đang mở. [Báo cáo](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-ai-connections/REPORT.md). Chưa đổi model mặc định, chưa nghiệm thu thao tác chuột Windows trực tiếp.

Đính chính chat 15/09: cửa sổ AI tách ra nhưng vẫn chung hội thoại, bản nháp và ngữ cảnh dự án đang chọn, có bộ đổi dự án trong cửa sổ. 100/100 tests; [báo cáo và ảnh](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-project-ai-window/REPORT.md). Chưa nghiệm thu thao tác Windows trực tiếp; bản đang chạy cần thoát bình thường trước khi thay executable chính.

Lịch sử triển khai chat 15/09: nút gửi cùng hàng, bong bóng hai phía, giờ/mốc ngày. Bản 94/94 tests đã hiểu sai “độc lập” thành lịch sử riêng; phần đó được thay thế bởi đính chính trên. [Báo cáo cũ](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-chat-history/REPORT.md) chỉ lưu dấu lần triển khai, không phải yêu cầu hiện hành. Không ghi đè bộ ảnh gốc.

Cập nhật icon/kéo giãn 15/09: đã thay bằng bộ vector và kiểm tra 83/83 tests; xem [báo cáo icon và con trỏ](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-icons-resize/REPORT.md). Đã render lại control thật theo các kích thước chuẩn. Chưa xác nhận con trỏ/DPI bằng thao tác chuột Windows trực tiếp, chưa đánh dấu toàn bộ UI là Đạt.

1. Chọn ID ảnh và yêu cầu liên quan; kiểm SHA-256 theo BASELINE.json. Không sửa baseline để làm test qua.
2. Dựng đúng dữ liệu mẫu và trạng thái: dự án số 3, hai việc xong trong ba việc, vùng chọn/khung mở như ảnh. Chỉ dùng dữ liệu thử, không sửa ghi chú thật để chụp.
3. Chạy bản build thật ở kích thước DIP và DPI đã ghi. Chụp cả cửa sổ, không chỉ vùng đã làm tốt.
4. Lưu ảnh thực tế và báo cáo đối chiếu dưới `docs/ui-verification/<ngay>-<chang>/`; tên gồm state ID, kích thước và DPI. Không lưu bằng chứng vào thư mục ảnh chuẩn.
5. So sánh cùng tỷ lệ vùng cửa sổ, loại phần caption ngoài ảnh mẫu. Kiểm bố cục, màu, cỡ chữ tương đối, mật độ, icon, khoảng cách, vùng cuộn, trạng thái nút và clipping. Cho phép khác biệt rasterization của font, không cho phép tự thiết kế lại.
6. Test thao tác tương ứng, IME và lưu/mở lại. Ảnh tĩnh không chứng minh tương tác đúng; unit/headless test không chứng minh UI Windows hiển thị đúng.
7. Ghi kết quả thực tế: Chưa làm / Đang làm / Đạt / Chưa đạt / Chưa kiểm trực tiếp. Chỉ Đạt khi có ảnh thực và kết quả hành vi; nếu công cụ không kiểm được thì ghi giới hạn, không giả đã đối chiếu.
8. Sau build thành công mở app mới cho người dùng kiểm. Không dùng dữ liệu thật để test destructive merge/overwrite.

## Ma trận ảnh chuẩn

Tất cả trạng thái dưới đây **chưa nghiệm thu trên app mới**. Đây không phải kết quả kiểm thử của bản bảng xanh cũ.

| ID | Kích thước tham chiếu DIP | Bắt buộc đối chiếu | Trạng thái |
| --- | --- | --- | --- |
| UI-01 | 560 x 820 | Thanh biểu tượng, bộ chọn dự án, công việc trên/ghi chú dưới, Next, không sidebar chi tiết/AI | Chưa làm |
| UI-02 | 560 x 820 | Drawer tìm/chọn dự án, highlight, tên đầy đủ, chọn/đóng/giữ bản nháp | Chưa làm |
| UI-03 | 560 x 820 | Notes chiếm vùng chính, checklist tóm tắt, toolbar theo lựa chọn | Chưa làm |
| UI-04 | 560 x 820 | AI trang tạm, Quay lại dự án, ngữ cảnh, chọn local/online, composer | Chưa làm |
| UI-05 | 1040 x 760 | Sidebar chi tiết, bảng công việc, note dưới, splitter, AI mặc định ẩn | Chưa làm |
| UI-06 | 1040 x 760 | AI nổi, tay nắm, close/ghim, dữ liệu gửi, không bóp toàn bộ editor | Chưa làm |
| UI-07 | 1440 x 860 | Danh sách trái/nội dung giữa/AI phải, resize, ẩn/hiện theo lựa chọn | Chưa làm |
| UI-08 | 1160 x 820 | Bóng kéo và drop preview, commit lúc thả, Esc/capture lost hủy | Chưa làm |
| UI-09 | 560 x 600 | Note thường riêng, rich toolbar/menu đồng bộ, không sidebar dự án | Chưa làm |
| UI-10 | 560 x 600 | Nhỏ nhất dùng tab, đủ vùng nhập, không tràn chữ/nút ngoài cửa sổ | Chưa làm |
| UI-11 | Chốt mockup trước triển khai | Cài đặt dữ liệu, preview chuyển kho, xung đột/ghi đè, trạng thái lỗi | Chưa có ảnh riêng |
| UI-12 | Chốt mockup trước triển khai | Cài đặt AI: Ollama/model, provider online/tùy chỉnh, key che, kiểm kết nối | Chưa có ảnh riêng |

Kiểm ngưỡng quanh 900 và 1360 DIP, đủ/thấp chiều cao, DPI 100/125/150/200%, kéo qua hai màn hình, task dài không khoảng trắng và Notes nhiều đoạn. Các mốc kích thước là điểm xuất phát; thay đổi cấu trúc để khắc phục không khớp ảnh cần nêu rõ và xin chốt, không âm thầm sửa chuẩn.

## Ma trận dữ liệu và AI

| ID | Tình huống nghiệm thu | Trạng thái |
| --- | --- | --- |
| DATA-01 | Kho rỗng: đồng bộ sang/không đồng bộ/hủy, kho nguồn nguyên vẹn | Chưa làm |
| DATA-02 | Kho có sẵn: Đồng bộ/Ghi đè/Không làm gì/Hủy đúng nghĩa và preview | Chưa làm |
| DATA-03 | Trùng tên khác ID, trùng ID giống/khác nội dung, xung đột thứ tự/xóa/rich text/chat | Chưa làm |
| DATA-04 | Giữ cả hai ánh xạ ID, merge lặp không nhân đôi, không tự chọn theo mtime | Chưa làm |
| DATA-05 | Backup và rollback giữa chừng, process lock, hết dung lượng/quyền ghi, file lỗi/schema mới | Chưa làm |
| DATA-06 | Đích bằng/lồng nguồn, liên kết thư mục, file không thuộc app không bị tác động | Chưa làm |
| DATA-07 | Mỗi dự án một tệp đầy đủ; rename/trùng tên/tên Windows đặc biệt/di chuyển kho | Chưa làm |
| DATA-08 | Migrate v1/WPF giữ ID/thứ tự/định dạng/raw/trường lạ/note thường/phiên | Chưa làm |
| DATA-09 | Mất kết nối thư mục, khôi phục bản nháp, không báo lưu giả hoặc tự dùng kho trống | Chưa làm |
| DATA-10 | Gõ nhanh/chat stream chỉ ghi mục đổi, không nghẽn UI, không cắt lịch sử dài | Chưa làm |
| AI-01 | Ollama không chạy/không model, refresh model, phân biệt đã cài/đang nạp | Chưa làm |
| AI-02 | Nạp/giải phóng/hủy/lỗi bộ nhớ, tải model chỉ sau xác nhận | Chưa làm |
| AI-03 | Preset OpenAI/Gemini/DeepSeek và hồ sơ tùy chỉnh đúng giao thức | Chưa làm |
| AI-04 | Model list không hỗ trợ thì nhập tay, kiểm kết nối không dùng dữ liệu dự án | Chưa làm |
| AI-05 | Streaming/dừng/lỗi/retry có kiểm soát; không gọi nhầm dự án/kho sau chuyển | Chưa làm |
| AI-06 | Chat theo dự án, mở lại/đổi provider giữ lịch sử và model nguồn của từng tin | Chưa làm |
| AI-07 | Key không lọt project/backup/log; đổi host/redirect không rò key | Chưa làm |
| AI-08 | Xem ngữ cảnh, gửi đúng phạm vi, không tự fallback cloud, AI sửa phải áp dụng/undo | Chưa làm |
| REG-01 | STT/ưu tiên/drag project/task/Next chỉ đổi sau drop, tooltip/auto-height | Cần kiểm hồi quy |
| REG-02 | Rich text/IME/selection/undo/menu/toolbar đồng bộ, gõ tiếng Việt không lag | Cần kiểm hồi quy |
| REG-03 | Tray single/double/right, X hide/no taskbar, opacity, snap, startup/restore | Cần kiểm hồi quy |

## Mẫu ghi một lần nghiệm thu

### Bổ sung 16/09/2026: Word giữ bố cục và chờ AI

- [Báo cáo](ui-verification/2026-09-16-layout-word-wait/REPORT.md): Release không lỗi/cảnh báo, 322 kiểm thử app và 13 kiểm thử bridge đạt. Chạy PDF/ảnh thật qua MinerU và C#, kiểm nguồn không đổi; xuất Word native, render bản một trang đã rà và bản hai trang tổng hợp. Không gửi tài liệu cho LLM/cloud.
- Bằng chứng cửa sổ đối chiếu OCR 1080x760/680x600, cài đặt chờ 620x840, chat 1440x860/560x820. Ảnh từ RenderTargetBitmap, native scale 125%, đã đối chiếu cấu trúc UI-04/UI-07; chưa kiểm chuột native/pixel parity vì công cụ không liệt kê cửa sổ app.
- Cả 10 ảnh chuẩn không đổi. Font scan vẫn ước lượng, bảng/dấu/chữ ký giữ ảnh, OCR cần rà. Mẫu Word đã sửa OCR thủ công không chứng minh độ chính xác tự động.
- Đã mở lại đúng Release sau khi người dùng thoát; kiểm chỉ một tiến trình dùng kho thật. Chưa nghiệm thu toàn bộ các hàng trong ma trận phía trên.

### Bổ sung 15/09/2026: OCR ảnh và cuộn suy nghĩ

- [Báo cáo](ui-verification/2026-09-15-image-ocr-thinking/REPORT.md): 313 kiểm thử ứng dụng và 11 kiểm thử bridge đạt. Chạy thực tế MinerU với ảnh người dùng và PDF scan mẫu; đối chiếu thêm Docling, thử chữ OCR với Ollama local. Không gọi API online.
- Khung suy nghĩ dài tự bám dòng mới; bằng chứng app Release ở 1440x860 và 560x820, scale native 125%, ảnh RenderTargetBitmap 96 DPI. Đã xem đối chiếu cấu trúc với UI-04/UI-07; không xác nhận pixel parity hay thao tác chuột native. Công cụ Computer Use không liệt kê cửa sổ H2 Notes.
- Giữ nguyên cả 10 PNG/BASELINE.json. Cài đặt có lựa chọn OCR ảnh rõ ràng, ảnh gốc không mất. OCR tiếng Việt vẫn cần đối chiếu, MinerU mất nhiều dấu ở ảnh được báo lỗi.
- App cũ đã thoát trước build; app Release mới được mở lại, chỉ một tiến trình dùng dữ liệu thật.

- Bản build/revision và chặng:
- ID yêu cầu, ID ảnh chuẩn và kết quả kiểm hash:
- Kích thước DIP, DPI, màn hình và dữ liệu thử:
- Đường dẫn ảnh thực tế / ảnh so sánh:
- Đã kiểm thao tác nào:
- Sai khác còn lại và quyết định xử lý:
- Test đã chạy/kết quả:
- Chưa kiểm được và lý do:
- Đã mở đúng app sau build:
- Kết luận Đạt / Chưa đạt / Chưa kiểm trực tiếp:
