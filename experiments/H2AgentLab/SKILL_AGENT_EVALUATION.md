# Đánh giá hướng skill-driven

Ngày 16/09/2026. Chỉ làm trong H2 Agent Lab, chưa tích hợp H2 Notes.

## Đã thay kiến trúc

Model nhận metadata kỹ năng, tự đọc SKILL.md và tham chiếu, viết Python theo yêu cầu, chạy trên bản sao trong Windows AppContainer, đọc lỗi/kết quả, sửa tiếp và đề xuất xuất tệp. Có 5 skill; 3 skill tài liệu chuyển thể từ bộ Codex thực đã cài, giữ bản gốc và [nguồn](skill-sources/NOTICE.md). Không sao chép lõi Codex hay tài khoản của người dùng.

Công cụ không mã hóa từng thao tác nghiệp vụ. Hai bài kiểm khác nhau trên cùng workbook dùng cùng một `run_python`; mã xử lý khác nhau do phía gọi cung cấp. Trong tự kiểm mã này do bộ test viết; không gọi đó là thành tích suy luận của model.

## Kiểm thử

| Kiểm tra | Kết quả đã xác minh | Giới hạn |
| --- | --- | --- |
| Release build | 0 lỗi, 0 cảnh báo | Không chứng minh chất lượng model |
| Regression | 16/16 đạt | Mock stream, quyền, đường dẫn, backup, lịch sử |
| Skill/runtime | 14/14 đạt | Chạy Python thật; các bước model trong hai bài giao thức là giả lập |
| Hai tiến trình dùng runtime chung | 16/16 lượt chạy đạt sau sửa khóa liên tiến trình | 8 lần mỗi tiến trình, không phải 16 yêu cầu nghiệp vụ khác nhau |
| Cách ly | Chặn đọc/ghi tệp gốc không stage, kết nối loopback đang hoạt động, tiến trình con; không thừa hưởng marker bí mật | Bài thử có kiểm soát, không thay kiểm toán bảo mật độc lập |
| Excel | Font/fill theo điều kiện, giữ nghiêng/công thức/sheet khác; yêu cầu thứ hai thêm tổng hợp từ đầu ra trước | Không có engine tính lại công thức hoặc render Excel nguyên bản |
| Word | Tạo bảng, đoạn chữ có định dạng riêng; mở lại kiểm | Chưa render Word trong Lab, không chứng nhận dàn trang |
| PDF | Tạo PDF, kiểm text, render trang thành PNG thật | Chưa OCR, chưa đánh giá tài liệu phức tạp |
| Hai giao thức AI | Skill → mã thật → inspect → gửi byte PNG thật → publish chạy qua Ollama/API giả lập | Chưa gọi online thật, không kết luận mọi model đều vision/tool-call đúng |
| Khôi phục/bảo vệ | Đọc lại run sau restart; chặn artifact bị sửa, từ chối, hash cũ và xuất khi Chỉ đọc; hủy script | Không tự hoàn tác thao tác đã làm ngoài sandbox |

Lần chạy đầu của bài sandbox tìm ra thiếu biến môi trường Windows khi khởi tạo Python; đã sửa và kiểm lại. Kiểm mạng dùng máy chủ thử thực sự đang lắng nghe: host kết nối được, sandbox bị chặn, không coi mọi lỗi kết nối bất kỳ là bằng chứng cách ly.

## Model thật

Gemma4 local, bài sửa workbook tổng hợp lần 1: **chưa đạt**. Model gọi sai tên tài nguyên skill, sau đó sinh mã dùng sai đường dẫn input, không tạo artifact. Máy chủ trả `error reading llama-server response: context canceled` ở vòng tiếp theo. Không xuất tệp hay làm thay đổi nguồn. Không gọi exit code 0 của đoạn mã bắt lỗi là hoàn thành yêu cầu.

Đã làm rõ đường dẫn `input/` và `output/` ngay trong mô tả công cụ, thêm gợi ý tài nguyên có thật khi đọc skill sai. Oracle so sánh style qua bản sao đối tượng thay vì so proxy của openpyxl. Lần kiểm lại được ghi riêng, không sửa bằng chứng lần đầu.

Lần 2 cũng **chưa hoàn tất trong giới hạn 8 phút của benchmark**: model vẫn chọn sai đường dẫn skill, có lập kế hoạch và sinh mã, nhưng chưa xuất workbook. Lần này còn phát hiện Python khởi tạo lỗi khi bài regression ở một tiến trình khác chạy đồng thời. Không quy toàn bộ lỗi này cho model.

Nguyên nhân xung đột runtime: cập nhật ACL dùng chung là thao tác đọc-sửa-ghi, khóa chỉ trong một tiến trình chưa đủ. Đã bổ sung khóa tệp độc quyền liên tiến trình, được hệ điều hành nhả nếu host thoát. Hai tiến trình chạy đồng thời 8 lần mỗi bên: 16/16 đạt. Chạy lại bản Release cuối: regression 16/16, skill/runtime 14/14. Lưu cả bằng chứng lỗi trước sửa. **Chưa chạy lại benchmark Gemma4 sau sửa khóa này**; không suy từ test harness rằng model đã hoàn thành tác vụ.

## Ranh giới hiện tại

Bằng chứng lưu ở [thư mục kiểm thử](evidence/2026-09-16-skills/README.md). Đã chạy app thật, mở danh sách 5 skill và xem nội dung spreadsheets; có ảnh native dialog tại DPI 125% và hai ảnh render giao diện minh họa. Không dùng ảnh minh họa làm bằng chứng tác vụ AI thành công. 10 ảnh baseline H2 Notes kiểm hash vẫn nguyên vẹn.

Sandbox chỉ áp dụng cho Python, không bao trùm UI Automation hay ứng dụng được mở. AppContainer thường không phải LPAC; có thể đọc tài nguyên được hệ thống cấp cho mọi AppContainer. Kiểm tăng trưởng output không phải quota ổ cứng. Cần kiểm toán thêm trước khi chạy mã không tin cậy trên dữ liệu quan trọng.

Chưa tích hợp OCR, renderer Word, engine tính Excel, sandbox build C#/Node, browser agent, cửa hàng skill/MCP, tìm kiếm mọi phiên cũ, hay kiểm điều khiển desktop bằng model. Dữ liệu chat/run lưu local, không mã hóa; người dùng cùng tài khoản Windows có thể đọc. API key không nằm trong kho dự án hoặc môi trường Python.

**Kết luận: kiến trúc kỹ năng đã triển khai và các bài runtime/giao thức đạt; chưa đủ bằng chứng để gọi tương đương Codex hoặc tích hợp vào H2 Notes.** Chất lượng end-to-end phải chấm riêng theo model, không thể bù bằng việc thêm hướng dẫn.

Nguồn thiết kế công khai: [Codex skills](https://learn.chatgpt.com/docs/build-skills), [Windows AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer). Hướng dẫn và khai báo native không được dùng thay cho kết quả chạy thật ở trên.
