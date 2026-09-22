# Sửa lỗi Word khi viết CV — 22/09/2026

## Nguyên nhân đã xác nhận

Lượt trong ảnh (`feed4ff826a94832ac2368f8ddc9b4a0`) có hai lần gọi `word.replace_range`: lần đầu bị chặn bởi giới hạn thay một đoạn; lần sau sửa thành công và native readback đạt. Runtime vẫn giữ lỗi đầu nên hiển thị “Chưa hoàn thành”. Đây không phải bằng chứng rằng quyền Full Access bị từ chối.

Khi thử CV dài bằng Gemma 4 Cloud, phát hiện thêm việc WordHost đọc định dạng từng ký tự và vượt giới hạn chờ 10 giây. Các lần thử lỗi vẫn được lưu trong `gemma-run-1`, không được tính là đạt.

## Thay đổi

- Văn bản xuống dòng tạo các đoạn Word thật. Mọi chỉ số đầu vào tham chiếu snapshot trước khi sửa; áp dụng từ cuối lên để không làm lệch đoạn khác.
- Kiểm tra toàn bộ lệnh trước khi ghi: chỉ số tồn tại, không trùng, giới hạn kích thước, không chứa ký tự phá cấu trúc. Giữ chặn thay chữ làm mất định dạng hỗn hợp; bảo vệ bảng, field, đối tượng nhúng, content control và ngắt section.
- Đọc lại và đối chiếu chữ/định dạng của các đoạn được sửa; kiểm tra các đoạn ngoài vùng sửa dù vị trí bị dời, bảng, lề trang, header/footer.
- Lưu mã lần bị từ chối trước khi ghi. Chỉ gỡ lỗi khi sửa lại đã được kiểm chứng trên đúng tài liệu, trạng thái và tập đoạn gốc. Thành công trên tài liệu/đoạn khác không xóa lỗi. Cho phép bước chuẩn hóa định dạng đã kiểm chứng trên chính vùng đó trước khi sửa chữ.
- Dùng ranh giới run trong OOXML do Word trả về, đối chiếu nguyên văn rồi đọc định dạng qua native range. Trường hợp không ánh xạ chính xác quay về đọc từng ký tự. Giới hạn chờ Office của production là 60 giây.
- Giữ mã lỗi Office cụ thể và thông báo phạm vi/offset cho thao tác chèn chữ.

## Kiểm tra

- Release publish: thành công; vẫn có các cảnh báo nullable/obsolete có sẵn trong dự án.
- **588/588 kiểm tra ứng dụng đạt**, gồm 4 bài hồi quy Word CV.
- **3/3 bài Word thật qua production adapter với chuỗi lỗi → sửa lại đạt**. Có đoạn trống, dòng trống, in nghiêng, nhiều đoạn và nhiều vùng sửa.
- **3/3 yêu cầu viết CV bằng `gemma4:cloud` đạt**, dùng công cụ Word thật và lưu bản sao DOCX.
- **54/54 điều kiện đọc lại DOCX độc lập đạt** trên 6 bản kết quả: số đoạn, tiêu đề đậm, phần KEEP và chữ nghiêng, bảng, header/footer, thiết lập trang và tiếng Việt.
- Các file đầu vào là tài liệu tổng hợp trong thư mục test riêng. Không sửa tài liệu cá nhân để thử lỗi. Không sửa trạng thái lịch sử của lượt lỗi cũ thành thành công.

## Bằng chứng

Thư mục: `.artifacts/word-cv-repair-2026-09-22/`.

- `final-tests.log`, `final-build.log`, `publish.log`.
- `run-3/script-result-1.json` đến `script-result-3.json`: snapshot trước/sau, lỗi bị chặn, lần sửa lại, kết quả verifier và bản sao.
- `gemma-run-2/cv-gemma-1/result.json` đến `cv-gemma-3/result.json`: model thật, tiến trình, thời gian, evidence.
- `run-3/independent-readback.json`: kết quả kiểm tra nội dung DOCX độc lập.
- `run-3/cv-gemma-result-1.docx` đến `cv-gemma-result-3.docx`: CV do model tạo.
- `archive-verification.json`: kiểm tra bản ZIP sau cập nhật.

Phạm vi chứng minh là sửa các đoạn văn bản Word và bảo toàn phần còn lại theo snapshot. Không dùng kết quả này để tuyên bố mọi cấu trúc Word nâng cao hoặc giao diện giống thiết kế 100%.
