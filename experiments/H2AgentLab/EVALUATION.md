# Đánh giá H2 Agent Lab

**Bản hiện tại: skill-driven có phục hồi. Xem [đánh giá phục hồi](RECOVERY_EVALUATION.md) và [nền kỹ năng](SKILL_AGENT_EVALUATION.md). Phần dưới là bằng chứng lịch sử của v0.1, không mô tả công cụ/giới hạn hiện tại.** Các công cụ hẹp và build .NET ngoài sandbox đã được gỡ; không cộng kết quả cũ vào benchmark model mới.

Ngày 16/09/2026. **Kết luận: CHƯA ĐẠT để tích hợp vào H2 Notes.** Có bản thử độc lập chạy được, nhưng chưa có bằng chứng đạt toàn bộ năng lực người dùng yêu cầu hoặc tương đương Codex.

## Phạm vi bản đã làm

Ứng dụng C# / Avalonia riêng, không gọi Codex và không đọc kho dữ liệu, khóa hoặc lịch sử H2 Notes. Hiện tham chiếu thư viện `H2Notes.Core` để tái sử dụng xử lý tài liệu/kết nối, không phải một sản phẩm đã đóng gói hoàn toàn độc lập khỏi mã nguồn H2. Khi build, thư viện cần thiết được đặt cạnh executable của Lab.

Đã có vòng gọi công cụ thực: đọc/tìm tệp, đề xuất sửa mã, xác nhận thao tác, kiểm phiên bản tệp bằng hash, sao lưu trước khi thay, tạo/sửa Word đơn giản, kiểm cấu trúc, build/test .NET, mở tài liệu, lưu nhật ký có thời gian. Backend điều khiển Windows UI Automation đã viết nhưng chưa được nghiệm thu end-to-end qua model.

Ollama local/LAN và API tương thích OpenAI Chat Completions là hai giao thức hiện có. Không đồng nghĩa hỗ trợ native mọi hãng. Khóa API chỉ trong RAM; không lấy khóa đã lưu của app khác để thử ngầm.

## Kết quả đã đo

| Bài kiểm | Kết quả | Giới hạn của kết luận |
| --- | --- | --- |
| Release build | 0 lỗi, 0 cảnh báo | Build không chứng minh tác vụ AI đúng |
| 16 bài tự kiểm | 16 đạt, 0 lỗi | Kiểm lớp điều phối/công cụ, không thay đánh giá model/UI thật |
| Gemma4 local đọc `brief.md`, lần đầu | Không đạt: công cụ đọc đúng nhưng model diễn giải sai việc cần làm | Phát hiện kết quả công cụ bị mã hóa thành chuỗi escape Unicode khó đọc |
| Gemma4 đọc lại sau sửa Unicode | Đạt một bài: dùng `read_file`, nêu đúng H2-7429 và việc còn lại | Chỉ một mẫu sau sửa; chưa đủ tỷ lệ chất lượng |
| Gemma4 tạo và kiểm Word | Đạt một phần: `read_file` → `create_word` → `check_word` → `word_paragraphs`; sau đó máy chủ báo lỗi ở lượt 5 | Không tính là tác vụ end-to-end đạt, không tự retry |
| Đọc và render Word thực do Gemma tạo | Tệp có đúng dự án, mã, hai việc chưa xong và cảnh báo chưa có ngày xác nhận; một trang, không thấy tràn/đè chữ | Tài liệu rất đơn giản, không chứng minh giữ nguyên bố cục PDF/ảnh hoặc Word phức tạp |
| API tương thích | Mock streaming có tách tham số tool và trả đúng call ID đạt | Chưa gọi dịch vụ online thật, chưa chấm chất lượng/chi phí |
| Build qua công cụ | Chạy build một dự án .NET mẫu, exit 0, có DLL thật | Chưa đánh giá AI tự sửa lỗi có test hành vi; không có sandbox OS |
| Khởi động UI thật | Cửa sổ Lab mở, phản hồi, đưa ra trước được; thấy đủ ô soạn/nút gửi | Chưa đánh giá đầy đủ mọi DPI, mọi màn hình và mọi dialog |
| Vùng thử điều khiển | Mở được form thử từ nút của app, nhận được các control | Thao tác nhập qua công cụ kiểm UI chưa xác minh được focus; không tính backend điều khiển máy tính của Lab đã đạt |

Trong quá trình này không có bài thử trên tài liệu thật của người dùng. Các yêu cầu local chỉ gửi dữ liệu giả trong thư mục fixture. Chưa có benchmark online thực tế.

## Bằng chứng

- [Bộ tự kiểm cuối](evidence/2026-09-16/tests.txt): đường dẫn/quyền, hash/backup, Word, phiên lịch sử, streaming, hủy, tìm tệp và build thực.
- [Gemma trước sửa](evidence/2026-09-16/gemma-read-before-fix.txt) và [sau sửa](evidence/2026-09-16/gemma-read-after-fix.txt).
- [Tác vụ Word chưa hoàn tất](evidence/2026-09-16/gemma-word-partial.txt). Tệp Word vẫn còn sau lỗi, không bị app xóa hoặc tạo lại.
- [Ghi nhận kiểm giao diện](evidence/2026-09-16/README.md). Ảnh 1180×800 và 840×620 là render từ control của app thật với dữ liệu mẫu, **không phải** ảnh chụp native và không phải cuộc chat AI thật.

Bản sau bài Word đã bổ sung thông báo lỗi stream có lý do giới hạn 400 ký tự và loại bỏ key hiện dùng, lưu nhật ký phiên theo cách ghi tạm rồi thay thế, chặn một số cửa sổ terminal/IDE/quyền/bảo mật và co cửa sổ vừa vùng làm việc. Bộ 16 test đã chạy lại sau các thay đổi; chưa chạy lại benchmark Word với model sau đó.

## Vì sao chưa kết luận OK

1. Tác vụ nhiều bước với model thật vẫn có lỗi kết thúc. Chưa kiểm độ ổn định, tốc độ và khả năng khôi phục qua nhiều lần chạy.
2. Backend desktop cần bài thử có quan sát trước, duyệt hành động và đọc lại kết quả trên app thật. Chưa có vision/canvas hoặc browser agent; không thể nói “điều khiển mọi phần mềm”.
3. Word mới đạt cấu trúc đơn giản. Chưa có renderer tích hợp, OCR/bố cục phức tạp, biểu mẫu, bảng, track changes và đánh giá nội dung tự động độc lập.
4. Lớp kiểm đường dẫn/phê duyệt không phải sandbox. Chạy build/test vẫn có thể thực thi mã tùy ý ngoài thư mục nếu người dùng duyệt dự án không tin cậy. Bộ chặn cửa sổ theo tên không nhận diện hết màn hình nhạy cảm.
5. Lịch sử gần nhất được nạp, phiên cũ được giữ, nhưng chưa có tìm kiếm/mở lại mọi phiên, index ngữ nghĩa, khôi phục backup trong UI và truy vết việc người dùng làm bên ngoài Lab.
6. Chưa có bài thử online thật và chưa kiểm các giao thức native ngoài Chat Completions. Chưa kiểm sửa Excel/tính lại công thức hoặc quy trình lập trình nhiều ngôn ngữ.

## Thứ tự để nghiệm thu tiếp

Ưu tiên ổn định vòng model/công cụ trước, không chỉ mở rộng thêm nút UI. Bước tiếp theo là lặp bài đọc → tạo Word → kiểm → kết luận trên local; ghi nguyên nhân lỗi từ máy chủ và thử cấu hình/model đã chọn. Sau đó bổ sung sandbox và bộ thử điều khiển desktop có bằng chứng đọc lại, rồi renderer/tác vụ Word nâng cao và benchmark online do người dùng cấu hình.

Áp dụng bộ nghiệm thu 30 tác vụ được đề xuất trong [RESEARCH.md](RESEARCH.md), công bố riêng từng nhóm đạt/chưa đạt. Chỉ khi người dùng duyệt kết quả mới đưa từng adapter đã đạt vào H2 Notes. **Hiện chưa sửa hoặc tích hợp chat H2 Notes; WPF, `_ver2` và 10 ảnh baseline được giữ nguyên.** Kiểm SHA-256 cả 10 ảnh: 0 khác biệt với manifest.
