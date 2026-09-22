# Work Assistant — ô soạn và quyền theo ảnh ngày 21/09/2026

Yêu cầu trực tiếp của người dùng thay thế bố cục ô soạn Work Assistant trước đó: khung trắng bo tròn, vùng nhập phía trên, một hàng phía dưới gồm **+ → quyền → model/mức suy luận → micro → nút tròn**. Lịch sử tiếp tục cuộn riêng phía trên. Không sửa mười ảnh baseline hoặc nguồn WPF/`_ver2`.

## Đã sửa

- Bỏ hộp quyền ở hàng trên. Nút khiên mở menu có mô tả phạm vi thật; “Toàn quyền tiếp cận” hiện màu cam. Chữ dài được rút gọn ở cửa sổ hẹp, vẫn truy cập đầy đủ qua menu và tooltip.
- Model và mức suy luận dùng chung một nút. Chỉ hiện hồ sơ đã có tên model và chỉ đưa ra mức suy luận được backend hỗ trợ. Không giả nhãn GPT-6 cho model Ollama. Cập nhật kết nối AI cũng cập nhật danh sách Assistant.
- Nút micro và sóng âm khi ô nhập trống mở **nhập giọng nói Windows (Win+H)**; khi có chữ, nút tròn chuyển sang gửi. Đây chưa phải đàm thoại âm thanh hai chiều như Codex. Enter gửi, Shift+Enter xuống dòng. Không tự ghi âm hoặc gửi nội dung giọng nói.
- Chiều rộng mặc định 640 DIP; tối thiểu 380. Đã bỏ viền focus chữ nhật bên trong ô nhập, giữ khung bo tròn bên ngoài.

## Quyền có tác dụng ở backend

| Lựa chọn | Hành vi |
|---|---|
| Chỉ quan sát | Đọc và trả lời; chặn thay đổi, không cung cấp công cụ lệnh tự do. |
| Hỏi trước khi thay đổi | Xin duyệt nội dung thao tác cụ thể trong tài liệu/thư mục đã chọn. Từ chối không thực thi. |
| Cho phép thay đổi phạm vi đã chọn | Tự sửa đúng thư mục hoặc phiên tài liệu được cấp; đường dẫn thoát phạm vi, junction và thao tác khác phạm vi vẫn bị chặn. |
| Toàn quyền tiếp cận | Grant riêng cho tài khoản/máy hiện tại, tối đa một giờ mỗi tác vụ. Tệp được dùng đường dẫn tuyệt đối ngoài workspace; có `exec_command` chạy PowerShell và mạng theo quyền Windows hiện tại, không xin duyệt từng thao tác. Không tự nâng quyền quản trị. |

Grant được tạo từ lựa chọn của người dùng tại thời điểm gửi, không từ lời model, file, plugin hoặc lịch sử. Chụp lại ngữ cảnh mới đặt quyền về Chỉ quan sát. Trong lúc chạy khóa đổi quyền/model/thư mục; Hủy kết thúc tác vụ và tiến trình lệnh đang chạy. Full access không được cung cấp ở chế độ chỉ đọc, không được tái sử dụng khi hết hạn. Quyền không được lưu thành cấu hình cấp toàn máy vĩnh viễn.

Đối chiếu [tài liệu sandbox/approval chính thức của OpenAI](https://learn.chatgpt.com/docs/sandboxing): Full access bỏ giới hạn filesystem/network của lệnh và không dừng xin phép. H2 thực hiện ý nghĩa này cho công cụ tệp/lệnh đã nối. **Chế độ “Hỏi trước khi thay đổi” của H2 vẫn chặt hơn chế độ mặc định Codex**: hỏi từng thay đổi có phạm vi. H2 chưa có sandbox hệ điều hành cho shell ở chế độ hạn chế, nên không cung cấp shell tự do ở các chế độ đó. Không tuyên bố mọi tính năng/tool/plugin của Codex đã có trong H2.

Lệnh có thời hạn 1–300 giây, giới hạn phần output giữ lại và vẫn rút hết pipe để tránh treo. Hủy/timeout dừng cây tiến trình đang chạy. Mã thoát khác 0 hoặc lỗi công cụ chặn báo hoàn thành. Bằng chứng `local-command-exit` chỉ chứng minh tiến trình thoát thành công; model vẫn phải kiểm tra nội dung tệp/trạng thái ứng dụng để xác minh kết quả công việc. Hash đọc lại, kiểm tra phiên tài liệu và kiểm tra ghi đè của công cụ có cấu trúc vẫn áp dụng.

## Bằng chứng

- [554/554 kiểm thử Release](audit-evidence/2026-09-21/composer/tests-release.txt), [42/42 Work Assistant](audit-evidence/2026-09-21/composer/tests-workassistant.txt), [35/35 guard kiến trúc](audit-evidence/2026-09-21/composer/architecture-tests.txt).
- Kiểm thử mới: hàng công cụ ở 380/460/640/922 DIP; đổi sóng âm sang gửi; toàn quyền ghi/đọc lại đường dẫn ngoài workspace; scoped không nạp/chạy shell; quyền hết hạn/readonly không ghi; mã thoát lỗi và timeout chặn hoàn thành; Hủy thực sự kết thúc PID lệnh.
- Windows thật ở 125%, dữ liệu demo và settings riêng: [khung 640 DIP](audit-evidence/2026-09-21/composer/native-full-access.jpg), [nhập nội dung](audit-evidence/2026-09-21/composer/native-draft.jpg), [khung 380 DIP](audit-evidence/2026-09-21/composer/native-narrow.jpg), [menu quyền](audit-evidence/2026-09-21/composer/native-permissions-1.jpg), [kết quả Agent](audit-evidence/2026-09-21/composer/native-result.jpg).
- Đã bấm mở/chọn quyền, mở model picker, gõ vào ô soạn, gửi bằng Enter, thu hẹp cửa sổ thật. Kiểm tra bố cục và chữ không tràn; không khẳng định pixel parity với ảnh Codex 922 px hoặc nghiệm thu toàn bộ giao diện dự án.
- [Ollama thật, gemma4:latest](audit-evidence/2026-09-21/composer/live-full-access.json): tạo tệp mới ngoài workspace của Agent, đọc lại, Completed với bằng chứng. Model ban đầu gọi `files:write_file`, runtime nạp schema đúng rồi khôi phục thành `files:write_text` và `files:read_file`; [nhật ký kết quả](audit-evidence/2026-09-21/composer/live-tools.jsonl). Mẫu thực tế là `xin chào.` (có dấu chấm theo câu trong prompt), không dùng nó để chứng minh nội dung không có dấu chấm.
- Micro sử dụng dịch vụ Win+H đã có; chưa nghiệm thu nhận dạng âm thanh/micro thực tế trong lượt này.

Build được xuất riêng ở `.artifacts/repair-2026-09-21/composer-release`. Trước khi mở kho thật đã sao lưu config, bản nháp, lịch sử Agent, dữ liệu dự án và shortcut trong `.artifacts/repair-2026-09-21/before-composer-restart`; kết quả kiểm tra sau khởi động ở [restart-integrity.json](audit-evidence/2026-09-21/composer/restart-integrity.json).
