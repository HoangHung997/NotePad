# Word giữ bố cục và chờ AI

## Cách dùng

- Trong chat: **+ → Yêu cầu bản nháp → Word giữ bố cục từ PDF/ảnh**. Gõ **@bố cục** cũng tìm được thao tác này.
- Có thể yêu cầu AI chuyển tệp đã đính kèm thành Word giữ bố cục. Bản nháp cần `layoutSourceId` đúng ID tệp trong trao đổi; nút **Dựng Word giữ bố cục** chỉ chạy sau khi bấm, không tự lưu.
- Chọn PDF/PNG/JPEG/WebP gốc trên máy, tối đa 8 MiB và 10 trang. Đọc bằng MinerU đã cài; OCR vi/en dùng model EasyOCR trong bộ Docling nếu đã có và kiểm tra hợp lệ. Không tự tải hoặc gọi cloud.
- Đối chiếu bản xem trước, sửa chữ từng dòng, font/cỡ chữ. Đậm/nghiêng dưới dòng áp dụng cả dòng; bôi đen rồi chuột phải để định dạng riêng đoạn đó. Chỉnh nội dung một dòng sẽ bỏ định dạng đoạn riêng của dòng đó, cần áp dụng lại.
- Chọn nơi lưu mới. Nguồn không bị thay; nếu chọn tên trùng, vẫn cần xác nhận ghi đè. Không tạo Word dưới quyền Chỉ đọc.

## Phạm vi bản thử

Chữ nằm trong khung Word tại vị trí trang nguồn, không phải ảnh toàn trang giả làm chữ sửa được. Giữ hình nền sau khi tách phần chữ được nhận dạng, nên dấu/chữ ký, đường kẻ, bảng và phần chưa đọc được còn nguyên điểm ảnh. **Bảng chưa trở thành bảng Word sửa được.** Chữ giao với hình hoặc chữ màu có thể vẫn thuộc phần ảnh.

Phông/cỡ/kiểu chữ của scan là ước lượng, hiện giới hạn Times New Roman/Arial đã có trên máy. Cần rà dấu tiếng Việt, số tiền, tài khoản, ngày tháng. Không dùng LLM tự đoán nội dung để thay số. Màn hình xem trước là bố cục ước lượng của Avalonia, không phải bản render Word. Sửa dài thêm nhiều chữ có thể cần dàn trang lại trong Word. Kết quả mẫu có rà thủ công không chứng minh độ chính xác tự động trên tài liệu khác.

PDF đã chuyển thành Markdown ở bản cũ không còn tọa độ/ảnh PDF nguồn trong tin nhắn. Chọn lại nguồn, kiểm SHA-256 trước khi chạy; không tự đọc đường dẫn do AI đưa ra. Ghi kết quả lưu và thời điểm trong trao đổi. Bản gốc không được gửi đến LLM trong thao tác dựng bố cục này.

## Chờ AI

Thiết lập AI → **Chờ AI hoàn tất, không tự ngắt vì phản hồi chậm**. Mặc định bật, kể cả hồ sơ cũ chưa có trường này. Áp dụng local, LAN và các giao thức API của app.

- Không giới hạn tổng thời gian chat hoặc thời gian chờ model bắt đầu phát token; người dùng chủ động bấm Dừng nếu máy chủ bị treo.
- Nếu tắt: giới hạn im lặng theo `TimeoutSeconds` đã lưu (mặc định 180 giây), đặt lại khi nhận dòng phản hồi/suy nghĩ/SSE heartbeat. Không phải giới hạn tổng phiên.
- Kết thúc khi máy chủ gửi tín hiệu hoàn tất, không chờ kết nối HTTP đóng. Lỗi mạng và Dừng vẫn giữ phần câu trả lời nhận được, không tự gửi lại.
- Tiến trình suy nghĩ chỉ là dữ liệu tạm, không lưu vào lịch sử. Giới hạn của OCR Markdown, lấy danh sách model và nạp model chủ động vẫn tách riêng.

## Kiểm chứng

Xem `docs/ui-verification/2026-09-16-layout-word-wait/REPORT.md`. Không dùng thành công build hoặc bản xem trước để khẳng định tương đồng từng pixel.
