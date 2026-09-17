# H2 Notes responsive: bản triển khai đầu tiên

Ngày: 15/09/2026. Nhánh: `codex/project-sheet`.

Đây là bản chạy thử có chức năng, **chưa phải bản nghiệm thu toàn bộ đặc tả**.
Giữ nguyên WPF, `_ver2`, bộ ảnh chuẩn và file dữ liệu v1. Không push/commit.

## Đã triển khai

- Shell màu kem/terracotta: danh sách dự án bên trái, bộ chọn/drawer khi hẹp, tab khi thấp; task ở trên và ghi chú ở dưới, splitter. Kích thước tối thiểu 560 x 600 DIP.
- Giữ bản nháp, vùng chọn và undo khi resize; lưu dự án đang chọn, bố cục riêng, trạng thái thu gọn. Checkbox nằm ở đầu dòng công việc; Next chỉ đổi khi thả hoặc thay trạng thái.
- AI ẩn mặc định, mở tạm khi hẹp; nổi, ghim phải hoặc dưới khi rộng. Khung nổi có tay nắm, bóng kéo, vùng ghim xem trước, đổi kích thước; fullscreen tự ghim phải nếu người dùng chưa chủ động ẩn.
- Cài đặt kho: chọn/mở/trở về mặc định, preview, hợp nhất/ghi đè/dùng dữ liệu đích/hủy; lựa chọn riêng cho xung đột nội dung hoặc thứ tự bảng. Ghi đè có xác nhận riêng. Hợp nhất không tự xóa dữ liệu chỉ có ở một bên.
- Kho schema 2: `projects/<ten>--<id>.h2project.json`, `notes/<ten>--<id>.h2note.json`, chỉ mục `workspace.h2index.json`. Giữ rich text, checklist, chat, liên kết và dữ liệu mở rộng ở model.
- File lock, kiểm thay đổi ngoài app, nhật ký ghi nhiều tệp, backup và rollback. Đổi tên giữ ID; file không thuộc app không bị ghi đè. App dùng danh sách dự án bẩn để không serialize lại mọi dự án khi lưu.
- Thiết lập AI riêng: Ollama native, OpenAI Responses, Chat Completions tương thích, Gemini native; preset OpenAI/Gemini/DeepSeek và URL/model nhập tay. Có danh sách model, Ollama đang nạp RAM, nạp/giải phóng model.
- Chat theo dự án và nhiều cuộc trao đổi, bản nháp, streaming/dừng, giữ phản hồi lỗi/dở dang, xóa lịch sử có xác nhận, tải thêm tin cũ. Không tự gọi AI khi mở app; không tự tải model, thử nhiều nhà cung cấp hoặc retry yêu cầu có thể tính phí.
- Ngữ cảnh là tùy chọn, được xem trước khi gửi. Kết quả chỉ thêm vào ghi chú hoặc công việc sau thao tác của người dùng. Khóa API nằm trong kho DPAPI theo tài khoản Windows, không nằm trong project/index/backup dự án. Không chuyển tiếp khóa qua HTTP redirect; đổi endpoint phải nhập lại hoặc xóa khóa cũ.

## Bằng chứng và giới hạn

- Build Release: 0 cảnh báo, 0 lỗi. Console tests: **77 passed, 0 failed**.
- Bao gồm regression rich text, lưu/mở, checkbox, drag/cancel, clipboard/import, session, phần hình học responsive; thêm kho theo dự án, ghi tăng dần, rollback, merge và 4 giao thức AI bằng HTTP giả lập.
- HTTP test không gọi API thật và không tiêu hạn mức. Việc người dùng tự thử trên app không được tính thành kết quả kiểm thử của agent.
- Đã mở executable Release. Lần mở kho mới tạo 18 tệp dự án, 2 note thường và 1 backup nguồn; đối chiếu đủ 18 ID dự án, 40 công việc, không có file sai hash trong index. Backup nguồn khớp byte với v1. SHA-256 file v1 trước/sau đều là `9FC9A7A4F682264B3DD5D954314DAF3228940EBE6D46391A1A7E49E576D4E95E`.
- SHA-256 và kích thước cả 10 ảnh baseline đều khớp manifest.
- Ảnh từ control thật của app: [báo cáo render](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-responsive-final/REPORT.md). Dùng RenderTargetBitmap 96 DPI; scale của cửa sổ Windows báo 1.25. **Không phải screenshot desktop**, không chứng minh chữ/DPI/IME hoặc thao tác tray trên Windows. Công cụ native không liệt kê cửa sổ ẩn taskbar; không đổi yêu cầu taskbar chỉ để làm test.
- Giữ các lượt render stage1/stage2/stage3 để truy vết chỉnh bố cục. Chỉ thư mục `responsive-final` là lượt cuối của bản này.

## Chưa hoàn tất / cần nghiệm thu tiếp

- Chưa khớp hoàn toàn bộ ảnh: icon còn giản lược; thanh định dạng chưa có đầy đủ style đoạn/danh sách/link như ảnh; trình bày tên dự án ở header/sidebar chưa phản ánh đủ mọi rich span. Cài đặt chưa có mockup riêng được duyệt.
- Chưa kiểm native drag/docking/capture-lost, tray single/double/right, tiếng Việt gõ nhanh, DPI 100/125/150/200%, nhiều màn hình và startup thực. UI-08 mới có render trạng thái đã ghim, chưa có screenshot đúng lúc kéo.
- Splitter AI phải/dưới chưa ghi nhớ kích thước chỉnh tay đầy đủ. Một số trạng thái thu gọn/ghim dưới có vùng đọc thấp, cần rà lại mật độ và khả năng co giãn.
- Lưu vẫn dùng I/O đồng bộ lúc debounce/flush, còn kiểm hash các file kho; chưa có pipeline ghi nền theo revision hoặc benchmark lưu kho/chat rất lớn. Khi thư mục mất kết nối, bản nháp còn trong RAM và có lỗi lưu, **chưa có kho phục hồi bản nháp cục bộ bền vững**.
- Merge chọn nguyên phiên bản khi xung đột, chưa có merge ba chiều/revision/tombstone. Chưa phục hồi chỉ mục hỏng bằng quét dự án. Chưa remap các ID nhúng trong chuỗi liên kết tùy ý/trường lạ. Metadata wrapper mới chưa round-trip mọi trường lạ ngoài model.
- Vị trí cửa sổ/cấu hình session còn trong chỉ mục, chưa tách hoàn toàn theo máy. Khi dùng kho trên máy khác, chỉ mới có kiểm vị trí còn tiếp cận được trên màn hình.
- Chưa có giao diện quản lý/liên kết lại toàn bộ `ProjectLink`, undo riêng thao tác AI tạo công việc, hoặc kiểm quyền ghi/đầy đĩa/mất điện bằng môi trường lỗi thật. Undo việc chèn văn bản vào Notes dùng undo hiện có của editor.
- Chưa smoke test model/API thật. Không cam kết mọi API chỉ cần đổi URL: giao thức khác cần adapter mới. Chưa có UI timeout/tham số nâng cao hoặc tải model về máy.

## Nơi sửa UI và chạy

- Shell: `src/H2Notes.Avalonia/MainWindow.axaml`.
- Quy tắc responsive, drawer, kéo/ghim: `MainWindow.Responsive.cs`.
- Bảng công việc: `Controls/ProjectGrid.cs`; rich editor/menu/toolbar: `Controls/RichEditor.cs`.
- Chat: `Controls/AiChatPanel.cs`; cấu hình: `AiSettingsWindow.cs`, `SettingsWindow.cs`.
- Note thường: `NoteWindow.cs`. Kho dữ liệu/giao thức AI nằm trong `src/H2Notes.Core`.

```powershell
dotnet build H2Notes.Avalonia.slnx -c Release
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release
& .\src\H2Notes.Avalonia\bin\Release\net10.0\H2Notes.Avalonia.exe --show
```

`--show` là mở rõ bảng theo yêu cầu; chạy không có cờ vẫn theo phiên đã lưu.
Kho mặc định: `%LOCALAPPDATA%\H2Notes\workspace-v2`.
`--demo --data <file-rieng>` chỉ dùng dữ liệu thử v1 riêng; không dùng cờ demo để kiểm việc ghi mỗi dự án một file.
`--ui-evidence <thu-muc>` chỉ hoạt động cùng cả `--demo` và `--data`, không dùng dữ liệu người dùng để tạo hình minh họa.
