# Work Assistant — sửa theo phản hồi ngày 21/09/2026

## Kết quả

Khung chat bám theo bong bóng; lịch sử cuộn ở trên, ô soạn luôn ở dưới. Gửi không thu cửa sổ. Enter gửi, Shift+Enter xuống dòng. Có lựa chọn model, Desktop/thư mục, quyền theo lượt và yêu cầu xác nhận ngay trong lịch sử. Câu tiếp nối nhận các lượt trước từ kho tác vụ Agent hiện có; không tạo thêm cơ sở dữ liệu hoặc bộ chạy Agent trong H2.

Thiết kế này áp dụng yêu cầu và hai ảnh mới của người dùng ngày 21/09. Màu nền ấm, đường viền và biểu tượng dùng hệ thống H2 hiện có. Mười PNG baseline 15/09 và BASELINE.json được giữ nguyên; kiểm tra SHA256 đạt 10/10. Không coi ảnh Work Assistant này là nghiệm thu thay cho mọi màn hình khác.

## Nguyên nhân lỗi Agent

Tác vụ người dùng `fccedc7b-7683-45c1-bbd8-3df196de1315` yêu cầu tạo TXT chứa “xin chào” ở Desktop. Bản cũ lưu Completed dù lời cuối nói không thể thực hiện vì `unknown_tool`.

Tái hiện bằng đúng Ollama `gemma4:latest` cho thấy model gọi `files:write`, sau đó `files:write_text`. `write_text` mới là tên đăng ký thực tế. Lookup cũ ném ngoại lệ với dấu `:`, còn tên không tồn tại nhưng hợp lệ nhận lỗi rồi vẫn có thể kết thúc Completed. Ngoài ra, quyền cho workbook/session không cấp quyền ghi file ngoài Desktop; workspace của Agent trước đó luôn cố định trong thư mục dữ liệu riêng.

Các sửa đổi:

- Lookup trả về không tìm thấy đối với tên không hợp lệ. Runtime nhận dạng `namespace:tên_chính_xác` chỉ khi namespace và callable cùng khớp metadata đăng ký; không thực thi tên suy đoán.
- Với tên chưa biết, tìm và tải schema thực tế, trả lỗi có hướng dẫn sửa rồi cho model thử lại. Trạng thái lỗi được gửi đúng sang transport. Kết quả JSON `ok:false`/`success:false` cũng được tính là thất bại.
- Không cho Completed khi còn lời gọi lỗi chưa được phục hồi. Tìm công cụ hoặc cập nhật kế hoạch không xóa lỗi; thao tác thành công ở đường dẫn khác cũng không xóa lỗi ban đầu.
- Bổ sung phạm vi thư mục rõ ràng. Chọn Desktop/thư mục mặc định hỏi trước khi sửa. Dùng lại SafeWorkspace, kiểm tra đường dẫn, liên kết, hash trước ghi và xác minh đọc lại. Quyền thư mục không cấp quyền Office, điều khiển desktop hay chạy shell tùy ý.
- Ghi tên công cụ và trạng thái vào `tool-outcomes.jsonl` của tác vụ để lần lỗi sau có dấu vết; không ghi arguments hoặc nội dung tài liệu vào nhật ký này.

## Kiểm chứng

- [Toàn bộ kiểm tra Release: 548/548](audit-evidence/2026-09-21/assistant/tests-release.txt).
- [Nhóm Work Assistant: 36/36](audit-evidence/2026-09-21/assistant/tests-workassistant.txt), gồm lời gọi sai, phục hồi tên có namespace, không báo hoàn thành khi thao tác khác thành công, giới hạn thư mục, Enter/Shift+Enter, vị trí nhiều DPI, bố cục dài và ngữ cảnh tiếp nối.
- [Guard kiến trúc: 35/35](audit-evidence/2026-09-21/assistant/architecture-tests.txt). Các bài runtime bổ sung ghi trong `runtime-checks.txt` cùng thư mục bằng chứng.
- Hai kỳ vọng cũ của MB-31/MB-40 được sửa để yêu cầu Blocked khi công cụ không tồn tại hoặc bị từ chối. Fixture H2M-111 trước đây đặt tệp ngoài Agent workspace và vẫn đậu dù đọc thất bại; nay đặt tệp đúng workspace để thực sự đọc được.
- Agent production với model thật tạo `H2-Agent-xin-chao.txt` tại Desktop thực của Windows (máy này chuyển Desktop qua OneDrive), nội dung UTF-8 chính xác `xin chào`. Có duyệt thay đổi trong probe, thực thi `write_text`, đọc lại bằng `read_file`, verifier PASS. SHA256: `8AAECD06D85C7F743D3545E7B865F3053C647591F95919C1C0B003046D4757AA`. [Kết quả thật](audit-evidence/2026-09-21/assistant/live-desktop-result.json).
- Windows thật, màn hình 1920×1080, vùng làm việc 1920×1020, DPI 125%: bubble từ (1090,867) đến (1121,886), panel từ (1195,197) đến (1226,216), cùng delta (31,19). [Số đo](audit-evidence/2026-09-21/assistant/native-drag.json), [giao diện](audit-evidence/2026-09-21/assistant/native-composer.jpg).
- [Lịch sử dài](audit-evidence/2026-09-21/assistant/native-history.jpg) cuộn riêng, composer không dịch khỏi đáy. Đóng/mở lại app review giữ lịch sử. Gửi tiếp bằng Enter câu hỏi về lời chào số 3 trả đúng “Hôm nay bạn thế nào?”. [Câu tiếp nối](audit-evidence/2026-09-21/assistant/native-followup.jpg).
- [Chọn Desktop qua menu thật](audit-evidence/2026-09-21/assistant/native-desktop-scope.jpg) hiển thị đúng thư mục và quyền hỏi trước thay đổi.

## Đối chiếu đặc tả và giới hạn

`H2_AGENT_MASTER_SPEC.md` là đặc tả chuẩn hiện tại; `experiments/H2AgentLab/AGENTLAB_V2_SPEC.md` là lịch sử nền. Đường thực thi production vẫn là H2 adapter → orchestrator → AgentRuntime → transport → registry/verifier, không quay lại runner v1.

Đặc tả thiết kế lõi độc lập ứng dụng và các extension tùy chọn. Việc có lớp/corpus cho filesystem, process/shell, Office, web, desktop, AutoCAD hay MCP không chứng minh tất cả được kết nối vào bản H2 chạy trên máy. Sửa này xử lý giao diện, tìm/gọi công cụ, trạng thái hoàn thành và truy cập thư mục được chọn. Không tuyên bố tương đương mọi chức năng Codex: AutoCAD native, tìm kiếm web production, thực thi shell tổng quát và tự cài/kết nối mọi extension vẫn cần công việc tích hợp/nghiệm thu riêng. Không hạ giới hạn quyền để che lấp phần chưa nối.

## Bản chạy

Publish local cuối: `.artifacts/repair-2026-09-21/assistant-release/H2Notes.Avalonia.exe`.

SHA256 H2Notes.Avalonia.dll: `9AEC9C9578BC2E372F1B34134EE5F9CA449EC41438A5F612DE4642C197BFA500`.

Các ảnh native dùng build assistant-review/assistant-final có cùng thay đổi sản phẩm; assistant-release bổ sung cập nhật kỳ vọng kiểm tra MB-31/MB-40. Đây là bản local, chưa phải bản phát hành GitHub. Những giới hạn về NAS/hai máy, native Office/AutoCAD và các màn hình khác trong báo cáo sửa trước vẫn còn hiệu lực.

Đã mở bản assistant-release trên kho thật lúc 17:02 ngày 21/09, PID 33284, với `--show-assistant`. Bản cũ và bản review đã dừng sau khi kiểm tra không còn tác vụ chạy. Snapshot trước khởi động lại ở `.artifacts/repair-2026-09-21/before-assistant-restart`. [Kiểm tra sau mở](audit-evidence/2026-09-21/assistant/restart-integrity.json): chỉ một H2Notes.Avalonia chạy; 33 tệp JSON dự án, cấu hình AI và bản nháp không đổi. [OfficeHost/DesktopHost đóng gói](audit-evidence/2026-09-21/assistant/packaged-helpers.json) ping thành công, không dùng fixture. Đã tạo lối mở `H2 Notes.lnk` trên Desktop trỏ vào đúng bản này; không thay lối mở H2.Desktop của dự án khác.
