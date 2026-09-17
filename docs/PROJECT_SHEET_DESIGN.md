# H2 Notes: bảng dự án nhẹ từ NeraSpreadSheet

> **Thiết kế lịch sử, đã được thay thế.** Yêu cầu mới nhất là tận dụng chọn lọc
> Nera, không nhúng nguyên SDK. Bản Avalonia đã được triển khai theo hướng đó.
> Xem [trạng thái triển khai, cách chạy và giới hạn](PROJECT_SHEET_IMPLEMENTATION.md).
> Các mục “chưa viết code” và “tích hợp trực tiếp nguyên SDK” dưới đây chỉ là lịch sử.

## Trạng thái

- Nhánh thử nghiệm local: `codex/project-sheet`.
- Mốc giữ nguyên ứng dụng trước thử nghiệm: `main`, commit `9eb4e0b`.
- Đã tải và đọc nguồn NeraSpreadSheet; chưa thay UI, model hoặc dữ liệu H2 Notes.
- Chưa triển khai bảng. Giữ yêu cầu của chủ app: thảo luận, chốt rồi mới viết code.
- Không push hoặc chỉnh repository NeraSpreadSheet của chủ app.
- Chỉ đạo bổ sung của chủ app: dùng SDK UI Avalonia hiện hành của NeraSpreadSheet.
  Phương án giữ WPF/.NET 8 và lấy chọn lọc source đã được thay thế bằng hướng dưới đây.

## Quyết định nền tảng

H2 Notes mới dùng **Avalonia/.NET 10**, tích hợp trực tiếp
**NeraSpreadSheet.Avalonia**. Không dùng WPF host của Nera, không nhúng UI WPF vào
Avalonia và không tự dựng một bảng khác để giả giao diện Nera.

Giữ bản WPF trên `main` để đối chiếu và quay lại. Bản thử Avalonia được triển khai
riêng trên `codex/project-sheet`, ưu tiên Windows trước. Chưa chuyển source app
cho đến khi chủ app xác nhận viết code.

## Nguồn đối chiếu

Repository: https://github.com/HoangHung997/NeraSpreadSheet

Nhánh `main`, SHA đã tải: `e169248ae7adf832745b75e6dae73883cf38e7e3`.
Bản tham khảo riêng ở `.artifacts/reference/NeraSpreadSheet`, được bỏ qua bởi Git
của H2 Notes. Bản này độc lập với các thư mục NeraSpreadSheet đang làm việc khác.

Những nhận xét dưới đây áp dụng cho SHA này, không phải cam kết về mọi phiên bản:

| Thành phần nguồn | Đã thấy trong code | Cách dùng đề xuất |
| --- | --- | --- |
| `src/NeraSpreadSheet.Avalonia/NeraSpreadSheet.Avalonia.csproj` | Host Avalonia target .NET 10, tham chiếu engine/UI modules chung | Dependency UI chính cho app mới, dùng phiên bản nguồn/package xác định |
| `src/NeraSpreadSheet.Avalonia/NeraSpreadsheetControl.cs` | Control bảng thật, có editor dùng lại và lifecycle riêng | Nhúng trực tiếp vào cửa sổ quản lý dự án Avalonia |
| `src/NeraSpreadSheet.Avalonia/NeraSpreadsheetControl.Editor.cs` | Begin/Commit/Cancel đi qua session chung; editor là TextBox, commit chuỗi; style áp dụng theo ô | Dùng luồng sửa ô thật; không suy rằng đã có sửa định dạng từng đoạn chữ |
| `src/NeraSpreadSheet.Scrolling/` | Gom input, cuộn theo frame, giữ offset lẻ, giới hạn biên | Dùng qua SDK, không sao chép thành bộ cuộn thứ hai |
| `src/NeraSpreadSheet.DataGrid.Core/` | Bốn file contract, chưa có UI bảng | Không dùng để dựng control thay thế SDK Avalonia |
| `src/NeraSpreadSheet.Iconography/` | Catalog, tài nguyên icon và giấy phép icon bên thứ ba | Dùng tài nguyên SDK phù hợp, giữ thương hiệu H2 Notes |
| `ARCHITECTURE.md` | Chỉ layout/render vùng nhìn thấy; không tạo control/editor cho mọi ô | Giữ kiến trúc/đường input của SDK khi tích hợp |

H2 Notes hiện target .NET 8 và dùng WPF; đây là chuyển nền tảng UI thật, không phải
chỉ đổi tên file XAML hoặc thêm một reference. Giữ logic nghiệp vụ C# phù hợp,
tách các chỗ phụ thuộc WPF rồi dựng shell/cửa sổ/menu theo Avalonia.

Tính nhẹ đến từ phạm vi giao diện và cách cập nhật: chỉ hiện các lệnh cần cho note,
không bắt buộc mở Ribbon đầy đủ, thanh công thức, biểu đồ, in/PDF hoặc nhập XLSX.
Ẩn UI không đồng nghĩa loại được tất cả dependency; không tự cắt module SDK.
Mức RAM, thời gian mở và độ trễ phải đo trên bản tích hợp, chưa có kết luận ở bước này.

## Giao diện đề xuất

Một bảng trong cửa sổ quản lý Avalonia, không biến mỗi dự án thành một workbook.
Note thường được chuyển UI nhưng phải giữ hành vi. Giai đoạn đầu giữ bản WPF
độc lập để đối chiếu, chưa hứa thêm chế độ Thẻ Avalonia vì không thể dùng lại trực
tiếp template WPF. Thu gọn nhóm, checkbox và kéo cả nhóm là yêu cầu của H2 Notes;
phải đối chiếu extension points SDK, không coi tất cả đã có sẵn.

| STT | Dự án / Công việc | Xong | Tiến độ / Việc tiếp theo |
| --- | --- | --- | --- |
| 1. | Đường Gom CT_TA_171 (có mũi tên thu gọn) | | 1/3; Bóc khối lượng đoạn Km8-Km16 |
| | Công việc đã hoàn thành (thụt vào) | Đã tick | |
| | Bóc khối lượng đoạn Km8-Km16 (thụt vào) | Chưa tick | Việc tiếp theo |
| | Lên dự toán (thụt vào) | Chưa tick | |
| 2. | Điện Hạt Nhân I - Ninh Thuận (đang thu gọn) | | 2/2; Đã xong |

- Thanh trên chỉ có tìm kiếm, thêm dự án và thêm công việc cho dự án đang chọn.
- Tên dự án đậm, công việc thụt vào; STT chỉ đánh cho dự án, tự cập nhật theo thứ tự.
- Cột cuối xuống dòng khi dài; không giấu Next trong dấu ba chấm.
- Khung Notes phía dưới thuộc dự án đang chọn, có thể kéo đổi chiều cao/thu gọn.
- Không lặp toàn bộ Notes trong từng hàng: tránh hàng quá cao và soạn chậm.
- Khi cửa sổ hẹp, ưu tiên tên + checkbox, thông tin tiến độ chuyển thành dòng phụ;
  không ép cửa sổ note phải rộng như Excel.
- Tên, checklist và Notes phải giữ định dạng từng đoạn chữ và các chức năng menu
  hiện có. Đây là điều kiện nghiệm thu cần triển khai/kiểm riêng, không phải tính
  năng đã được chứng minh sẵn trong editor ô của SDK.

## Tác vụ và bảo toàn dữ liệu

1. Click chọn hàng; double-click hoặc F2 sửa tên/công việc. Enter xác nhận,
   Esc hủy draft; ưu tiên Alt+Enter xuống dòng theo editor SDK hiện tại, không
   ghi đè phím SDK khi chưa có lý do. Notes dùng Enter xuống dòng.
2. Checkbox đổi trạng thái thật. Việc tiếp theo luôn là checklist chưa xong đầu tiên
   có nội dung, không có ô nhập Next độc lập.
3. Chuột phải tại mũi tên dự án có “Ưu tiên thực hiện trước”: đưa cả nhóm lên đầu,
   giữ thứ tự checklist. Đây là di chuyển một lần, không phải ghim vĩnh viễn.
4. Kéo dự án đi cùng toàn bộ checklist. Kéo checklist chỉ đổi thứ tự trong dự án
   của nó ở phiên bản đầu. Có bóng kéo và vạch vị trí thả; chỉ sửa collection và
   tính lại STT/Next sau khi thả, Esc không để lại thay đổi.
5. Nếu đang tìm kiếm/lọc, tạm không cho kéo thứ tự để tránh vị trí bị hiểu nhầm;
   lệnh ưu tiên vẫn xác định dự án bằng ID, không bằng chỉ số hàng đang nhìn thấy.
6. Trước khi đổi dự án đang soạn hoặc chuyển màn hình, commit draft đang dùng.
   Không dựng lại bảng trong khi bộ gõ tiếng Việt đang nhập tổ hợp ký tự.
7. Giữ ID dự án, ID checklist, thứ tự và nội dung JSON. Bản thử dùng bản sao dữ
   liệu ở vùng lưu riêng, không cho hai app đồng thời ghi file state đang dùng.
   Rich text XAML WPF cần bộ chuyển đổi có phiên bản, sao lưu và kiểm tra round-trip;
   không nạp bằng loader WPF trong app Avalonia hoặc biến tất cả thành text thuần.
8. Văn bản thuần dùng để tìm kiếm/preview không bao giờ ghi đè dữ liệu có định dạng.
   Session/workbook của SDK là biểu diễn bảng gắn với model qua ID, không phải
   kho dữ liệu thứ hai độc lập. Chọn một đường commit/Undo thống nhất, có chặn
   cập nhật vòng lặp và kiểm tra rollback; không duy trì hai lịch sử mâu thuẫn.
9. Tray, opacity, startup, vị trí cửa sổ, ghim trên cùng, bám viền và note thường
   đều cần được chuyển sang host Avalonia và kiểm tra lại trên Windows.

## Điểm cần kiểm chứng trước khi chuyển toàn app

`RichTextDocumentSerializer` hiện dùng `System.Windows.Documents.FlowDocument`
và `TextRange` để lưu XAML. Trong nguồn SDK Avalonia đã đọc, editor ô là TextBox
và commit `_editor.Text` theo chuỗi, style editor áp dụng cho cả ô. Chưa có bằng
chứng đường này hỗ trợ menu định dạng từng phần chữ giống H2 Notes.

Cần thử nghiệm editor rich text tương thích Avalonia cho Notes và phần text trong
ô, xác minh khả năng tích hợp đúng với SDK. Không tự chọn thư viện mới, giả đầy đủ
chức năng hoặc âm thầm hạ xuống định dạng cả ô. Nếu SDK thiếu extension point,
ghi rõ thay đổi tối thiểu cần có và xin chốt trước khi sửa repository SDK.
Nội dung cũ chưa chuyển được đầy đủ phải giữ bản gốc, báo giới hạn và không ghi đè.

## Phân lớp khi triển khai

```text
H2 Notes shell/cửa sổ/menu: Avalonia / .NET 10
                  |
                  v
ProjectSheetAdapter: model dự án <-> ID hàng/ô + commit/Undo thống nhất
                  |
                  v
NeraSpreadSheet.Avalonia: control/session/editor/layout/render/cuộn của SDK
                  |
                  +-- Các lệnh dự án: ưu tiên, checklist, Next, thu gọn nhóm
                  +-- Khung rich Notes Avalonia (cần kiểm chứng editor)

Model lưu trữ H2 Notes -> bộ chuyển đổi rich text có phiên bản -> JSON riêng
```

Không tính lại preview rich text hoặc serialize cả note mỗi lần gõ/cuộn.
Giữ debounce hiện tại 900 ms cho autosave khi ngừng gõ, đồng thời commit khi rời
editor/đóng app. Cache theo ID và phiên bản nội dung; chỉ vô hiệu phần đã đổi.
Chỉ giữ frame loop khi thực sự có chuyển động hoặc cần vẽ lại.

Dùng SDK Avalonia với version/SHA rõ ràng và phụ thuộc được khai báo chính thức;
không reference vào `.artifacts` hoặc checkout riêng trên máy. Không sao chép
renderer/editor thành một nhánh engine khác. Chọn PackageReference hoặc nguồn
vendored có thể khôi phục sau khi xác minh cách phân phối SDK thực tế.

## Bằng chứng và cổng kiểm tra

Đã chạy trên bản tham khảo: test project `NeraSpreadSheet.Scrolling.Tests`,
Release, .NET 10: **3 passed, 0 failed, 0 skipped**. Các case kiểm offset lẻ,
cuộn tiến dần tới đích và giới hạn biên. Chưa phải đo hiệu năng hoặc nghiệm thu
tích hợp H2 Notes/Avalonia. Chưa build lại H2 Notes vì không sửa source app.

Trước khi thay UI mặc định cần kiểm:

- Đọc/lưu lại bản sao dữ liệu thử: ID, thứ tự, rich text, checkbox giữ nguyên.
- Nhập bản sao dữ liệu WPF, đóng/mở app Avalonia, đổi hàng đang sửa không mất
  draft hoặc định dạng; quay lại bản WPF không bị dữ liệu thử ghi đè.
- Gõ tiếng Việt nhanh, mở menu định dạng, đổi font/màu không mất selection.
- Ưu tiên/kéo dự án giữ cả nhóm; kéo checklist không kích hoạt kéo dự án.
- Kéo preview không thay Next; drop mới thay, cancel không thay.
- Next dài và Notes nhiều dòng không bị cắt; DPI 100/125/150/200% không lệch hit-test.
- Đo bản thẻ và bản bảng trên cùng máy/dữ liệu tổng hợp: thời gian mở, cuộn,
  độ trễ gõ và bộ nhớ ở 100 dự án/1.000 việc và 1.000 dự án/10.000 việc.
- Đối chiếu note thường, tray single/double/right click, opacity và startup.

Bước kế tiếp sau khi chủ app chốt cho viết code: tạo host Avalonia/.NET 10 riêng,
nhúng control SDK thật với dữ liệu tổng hợp và xác minh rich text/editor trước.
Sau đó nối bản sao dữ liệu dự án, checklist, Notes; chuyển tray/cửa sổ và đo/kiểm
rồi mới thay bản dùng hằng ngày. Không tuyên bố app hoàn thiện chỉ vì build đạt.
