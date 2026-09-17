# H2 Notes Project Sheet

**Bản hiện hành:** [responsive hybrid, kho từng dự án và AI](D:/VSstudio/Nodepad/docs/RESPONSIVE_IMPLEMENTATION.md). Phần dưới lưu lịch sử bản bảng xanh, không mô tả UI hiện tại.

> Bản thử được mô tả dưới đây có trước hướng responsive hybrid đã chốt ngày
> 15/09/2026. Từ nay triển khai theo [đặc tả hiện hành](D:/VSstudio/Nodepad/docs/APPROVED_PRODUCT_SPEC.md)
> và [bảng nghiệm thu UI](D:/VSstudio/Nodepad/docs/UI_ACCEPTANCE.md).
> Các kết quả build/test cũ không chứng minh đã có UI mới, kho từng dự án hoặc AI.

## Quyết định và tiến độ

Theo chỉ đạo mới nhất: Avalonia/.NET 10, **tận dụng chọn lọc NeraSpreadSheet**,
không nhúng nguyên SDK. UI DataGrid lai bảng tính theo ảnh đã chốt, không Ribbon,
công thức, workbook engine, biểu đồ hoặc OpenXML/PDF. Quyết định này thay thế
phương án tích hợp nguyên `NeraSpreadSheet.Avalonia` trong tài liệu thiết kế cũ.

Nhánh `codex/project-sheet`. Bản WPF trên `main`, mốc `9eb4e0b`, được giữ nguyên;
source WPF và `_ver2` không bị sửa. Không push lên GitHub.

| Bước | Nội dung | Kết quả |
| --- | --- | --- |
| 1 | Giao diện Avalonia theo ảnh, cột, nhóm dự án/checklist, khung Notes | Đã triển khai |
| 2 | Dữ liệu, sửa trực tiếp, rich text, ưu tiên, kéo thả, tự lưu | Đã triển khai và có regression tests |
| 3 | Build/test, mở Windows app, đối chiếu giao diện và dữ liệu | Đã thực hiện |
| 4 | Dùng dài, IME/DPI nhiều màn hình, mọi tình huống tray | Chưa nghiệm thu đầy đủ |

Đây là các bước triển khai, không phải một tác vụ chạy định kỳ. Không tự bật
khởi động cùng Windows hoặc thay thiết lập startup của bản WPF.

## Tái sử dụng thực tế

Nguồn: https://github.com/HoangHung997/NeraSpreadSheet

SHA `e169248ae7adf832745b75e6dae73883cf38e7e3` (`main`). Import nguyên logic
`ContinuousScrollController`, `ScrollContracts`, `GridColumnDefinition` và giữ
contract `GridSelection` cùng nhóm DataGrid để truy nguồn. `GridSelection` chưa
được UI dùng vì hiện chọn một hàng, không hỗ trợ chọn nhiều hàng.

Bốn file ở `src/H2Notes.Core/Vendor/Nera`, có `SOURCE.md` ghi nguồn/SHA.
Tái sử dụng theo yêu cầu chính chủ repo, không tự tuyên bố giấy phép upstream.
Không reference vào `.artifacts` hoặc checkout khác trên máy, không sửa repo Nera.

Bảng riêng của H2 Notes áp dụng kiến trúc viewport/editor dùng lại đã đọc ở host
Avalonia Nera. Không gọi đây là full `NeraSpreadsheetControl`. Avalonia 12.1.2 và
AvaloniaEdit 12.0.0 được khóa version; AvaloniaEdit vẽ style từ rich runs thực,
không dùng syntax coloring thay dữ liệu. Có `THIRD-PARTY-NOTICES.md` trong output.

## Đã có trong bản thử

- Bảng xanh nhạt, header cố định; 4 cột STT, tên, tiến độ, ghi chú; kéo chỉnh cột.
  Dòng dự án giữ STT; dòng checklist đặt ô tích ở cột STT, không còn cột Xong riêng.
- Settings có tự giãn hàng riêng cho tên, tiến độ và ghi chú. Mặc định tên/tiến độ
  bật, ghi chú tắt. Hàng lấy chiều cao lớn nhất của các cột bật, tối thiểu 40 DIP
  hoặc đủ một dòng với font lớn. Dùng TextLayout Wrap để cả chuỗi không có dấu cách
  cũng xuống dòng; phần hiển thị rút gọn giữ nguyên dữ liệu và thêm … kể cả khi xuống đoạn.
  Cột tắt dùng chiều cao còn lại và dấu …; tooltip theo ô hiện toàn bộ nội dung.
  Ghi chú trong bảng không còn bị cắt mất các đoạn sau dòng đầu ở tầng dữ liệu.
- Nhóm dự án mở/thu gọn; chọn dòng để soạn Notes phía dưới, thu gọn/đổi chiều cao pane.
- Tìm trong dự án, checklist và ghi chú; giữ nhóm cha và STT gốc khi lọc.
- Thêm dự án/task qua dialog OK/Hủy; xóa có xác nhận.
- Double-click/F2 sửa trực tiếp, Enter xác nhận, Esc hủy; hỗ trợ xuống dòng.
- Tick và Next từ checklist chưa xong; Next dài xuống dòng.
- Kéo dự án cùng cả nhóm, kéo task trong cùng dự án; bóng kéo/vạch thả. Preview
  không đổi collection/Next, drop mới cập nhật. Esc/capture lost hủy. Không kéo khi lọc.
- Chuột phải dự án có “Ưu tiên thực hiện trước”, chuyển cả nhóm lên đầu một lần.
- Rich text cho tên/task/Notes và ghi chú task: font, cỡ, màu/highlight, B/I/U/strike, undo/redo;
  menu khi soạn áp dụng phần chữ chọn, swatch cơ bản và RGB có preview.
- Menu và toolbar đọc định dạng từ vùng chọn/con trỏ. Định dạng trộn hiện trạng thái
  riêng (font/cỡ để trống, nút ba trạng thái); bật đậm trên vùng trộn áp dụng cho cả
  vùng thay vì đảo từng đoạn. Di chuyển vùng chọn không ghi dữ liệu hoặc thêm undo.
- Tự lưu sau 900 ms ngừng gõ; flush draft khi đổi context/thoát.
- Ghi chú thường mở trong cửa sổ riêng, vẫn soạn có định dạng.
- Tray: mở bảng, note mới, đưa cửa sổ đang mở lên trước, hiện tất cả, settings/thoát.
  Một click raise, hai click mở bảng; mất focus không tự hide.
- Settings: opacity không active 1–100%, khi active 100%, mở lại note đã mở,
  bám viền, đăng ký chạy cùng Windows khi người dùng tự bật.

## Cửa sổ và khôi phục phiên

- Bảng dự án, ghi chú thường và hộp thoại không hiện trên taskbar. Bảng/note dùng
  thanh tiêu đề riêng, không có nút hoặc thao tác nhấp đúp phóng to/thu nhỏ; vẫn
  kéo thanh tiêu đề để di chuyển và kéo mép để đổi kích thước.
- X/Alt+F4 trên bảng hoặc note chỉ ẩn cửa sổ, lưu nội dung và bỏ cửa sổ đó khỏi
  danh sách cần khôi phục. Mở lại từ tray/menu ghi chú. Chỉ lệnh Thoát mới tắt app.
- `DesktopSession` lưu bảng đang xem và ID các cửa sổ đang hiện, theo thứ tự
  sau-trước. Cửa sổ đã ẩn không tự mở; phiên ẩn toàn bộ chỉ chạy tray khi mở lại.
  File đời trước chưa có session dùng các cờ `IsVisibleOnDesktop` làm mặc định.
- Khi bật **Khôi phục cửa sổ và bố cục khi mở lại app**, mở bằng executable hoặc
  `--startup` đều khôi phục cùng phiên. Chạy cùng Windows vẫn cần người dùng bật
  riêng trong Cài đặt. Không tự đăng ký startup trong quá trình kiểm thử.
- Di chuyển/resize cả note thường và bảng đều lên lịch lưu, kể cả khi tắt bám
  viền. Lưu ngay trước khi ẩn/thoát/tắt máy. Quá trình shutdown không bị nhầm với
  người dùng bấm X và không ghi đè phiên thành toàn bộ note ẩn.
- Lưu vị trí pixel, kích thước DIP và trạng thái ghim; không ép note lớn về 1000
  DIP khi mở lại. Không snap trong lúc khôi phục. Nếu màn hình chứa vị trí cũ đã
  tháo, đưa thanh tiêu đề về màn hình đang có để người dùng kéo lại được.
- Không gán bảng vào lifetime.MainWindow: lifetime sẽ tự Show cửa sổ đó khi
  khởi động, bất kể trạng thái ẩn trước đó. App quản lý các cửa sổ với
  `OnExplicitShutdown` và khôi phục theo session riêng.

Đợt này: Release build 0 warnings/0 errors, 55/55 tests đạt. Probe chạy lifetime
Avalonia thật trên nền headless trong process riêng đã qua mở thường, `--startup`,
phiên chỉ có note (bảng ẩn), phiên nhiều cửa sổ và `TryShutdown` giữ nguyên session.
Chưa thử tắt/khởi động lại Windows thật. App Release đã mở lại trên máy; công cụ
điều khiển Windows không liệt kê cửa sổ không có taskbar nên kiểm tra trực quan
bản mới qua công cụ này bị giới hạn, không thay đổi ShowInTaskbar để lách kiểm tra.

## Bảo vệ dữ liệu

Cài đặt có nút **Nhập từ app cũ…**: chọn JSON, xem số bảng/dự án/công việc/ghi chú,
xác nhận để nhập thêm bản sao. Không thay settings hoặc ghi đè ghi chú hiện tại.
ID của bản sao được cấp mới; trùng tên thêm hậu tố. Hỗ trợ project-hub, ghi chú
thường và project-note đời cũ (đưa Content/checklist vào nhóm dự án).
Mọi đoạn chữ được chuyển sang rich runs trước khi lưu, vẫn giữ trường raw.
File nguồn chỉ đọc (tối đa 32 MB); JSON sai cấu trúc bị từ chối. Cùng snapshot đã
nhập được nhận biết bằng SHA-256, không nhân đôi. File đã cập nhật có thể nhập lại
thành bản sao mới. Trạng thái lưu trữ được giữ; không mở đồng loạt các note đã nhập.

Mỗi lần nhập có thư mục `import-backups/<thời gian>-<id>/` bên cạnh state mới,
chứa `source.json` (đúng byte đã xem trước) và `before-import.json` (kể cả draft
vừa flush). Chỉ bổ sung vào state trong bộ nhớ sau khi atomic save thành công.
Đóng Cài đặt sẽ mở bảng/ghi chú không lưu trữ đầu tiên vừa nhập.
43 tests đạt sau khi thêm import, gồm rollback khi lỗi lưu, preview không ghi,
snapshot nguồn, các ID trùng, malformed/schema mới và kiểm nút trong Settings.

Lần đầu đọc `%LOCALAPPDATA%\Nodepad\state.json`, lưu RIÊNG thành
`%LOCALAPPDATA%\H2Notes\project-sheet-v1.json`. `.legacy-backup` giữ nguyên byte
nguồn; `.bak` giữ lần lưu trước. Save dùng file tạm/flush/atomic replace. File lock
chặn hai process ghi cùng state; JSON lỗi/schema mới bị từ chối, không reset rỗng.

ID, thứ tự, trường lạ và raw XAML gốc được giữ. Chỉnh sửa mới vào `NameRich`,
`NotesRich`, `TextRich`, `CommentRich`, `ContentRich`; ưu tiên đọc các trường này. Hai bản không
đồng bộ hai chiều; không dùng file WPF gốc làm `--data` của app mới.

Parser XML chặn DTD/external entity, không chạy XAML loader. Đã hỗ trợ run/span,
paragraph/newline và font/size/color/highlight/B/I/U/strike. Chưa đầy đủ FlowDocument:
hình, bảng, list nâng cao, hyperlink, paragraph alignment, typography đặc biệt chưa
được bảo đảm tương đương. Raw gốc vẫn giữ; preservation không có nghĩa hỗ trợ soạn
mọi định dạng đó. Clipboard hiện dán text, chưa round-trip rich clipboard giữa app.

## Kiểm tra và giới hạn

35 tests core + Avalonia headless đạt: rich spans/ranges/undo, import giữ raw/ID/
trường lạ, file hỏng/schema mới, thứ tự/Next, kéo preview/cancel và chống xung đột,
filter, hủy edit sau autosave, constructor MainWindow, viewport; thêm checkbox STT,
auto-height/ellipsis/tooltip, lưu thiết lập và định dạng comment, đồng bộ menu/toolbar
với vùng chọn chung/trộn, định dạng gõ tiếp và không phát sinh save khi chỉ chọn chữ.

Case 1.000 dự án/10.000 task: 27 visual descendants; lần đo cuối khoảng 1,15 giây
dựng layout đầu trong headless. Không phải native FPS/RAM comparison. Render chỉ
vẽ hàng nhìn thấy, nhưng projection rebuild vẫn đo mọi hàng, còn có thể tối ưu.

Build Release trước đợt chỉnh cột: 0 warnings, 0 errors; 26 tests đạt. Đã mở app Windows thật,
xem bố cục, kiểm double-click checklist/menu định dạng, mở bản sao state thật.
Checksum file WPF không đổi tại checkpoint import/mở đầu tiên. Sau đó file WPF
có cập nhật trong khi process WPF vẫn chạy; không nhập lại hoặc ghi đè thay đổi
này. Hai app lưu riêng và không đồng bộ hai chiều.
Chưa nghiệm thu dài với IME, DPI 100/125/150/200% nhiều màn hình, mọi app foreground,
restart Windows thật và so sánh hiệu năng WPF. Chưa tự bật startup khi kiểm thử.

Đợt chỉnh cột/định dạng: build Release 0 warnings/0 errors, 35/35 tests đạt.
Đã mở lại app với dữ liệu riêng hiện tại, kiểm tra bảng 4 cột, cài đặt tự giãn,
menu và toolbar đọc cùng Times New Roman/12 trên vùng chữ chọn, và dấu … cho
ghi chú nhiều đoạn. Nội dung người dùng không bị sửa trong kiểm tra UI.

Không có công thức, XLSX, multi-cell clipboard/selection: ngoài phạm vi bảng nhẹ.
Menu chưa parity mọi lệnh Simple Sticky Notes. Accessibility các ô custom-drawn
cần bổ sung trước khi công bố hỗ trợ screen reader đầy đủ. Chưa thay bản WPF mặc định.

## Chạy và sửa

Solution mới: `H2Notes.Avalonia.slnx`; solution WPF `Nodepad.slnx` giữ nguyên.

```powershell
dotnet build H2Notes.Avalonia.slnx -c Release
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release
dotnet run --project src/H2Notes.Avalonia/H2Notes.Avalonia.csproj -c Release
```

Thêm `-- --demo` cho mẫu riêng, `-- --data <path>` cho file kiểm thử riêng.

- `MainWindow.axaml`: khung UI, toolbar, pane Notes và khoảng cách/màu.
- `Controls/ProjectGrid.cs`: cột/chiều cao hàng, vẽ, kéo và editor ô.
- `Controls/RichEditor.cs`: menu định dạng và editor.
- `H2Notes.Core`: model, rich document, thao tác, persistence.

Bước sau: nhận phản hồi bố cục/thao tác thực tế, ưu tiên lỗi nhận được và
nghiệm thu IME/tray/DPI trước khi thay ứng dụng WPF dùng hằng ngày.
