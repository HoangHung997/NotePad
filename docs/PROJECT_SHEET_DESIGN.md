# H2 Notes: bảng dự án nhẹ từ NeraSpreadSheet

## Trạng thái

- Nhánh thử nghiệm local: `codex/project-sheet`.
- Mốc giữ nguyên ứng dụng trước thử nghiệm: `main`, commit `9eb4e0b`.
- Đã tải và đọc nguồn NeraSpreadSheet; chưa thay UI, model hoặc dữ liệu H2 Notes.
- Chưa triển khai bảng. Giữ yêu cầu của chủ app: thảo luận, chốt rồi mới viết code.
- Không push hoặc chỉnh repository NeraSpreadSheet của chủ app.

## Nguồn đối chiếu

Repository: https://github.com/HoangHung997/NeraSpreadSheet

Nhánh `main`, SHA đã tải: `e169248ae7adf832745b75e6dae73883cf38e7e3`.
Bản tham khảo riêng ở `.artifacts/reference/NeraSpreadSheet`, được bỏ qua bởi Git
của H2 Notes. Bản này độc lập với các thư mục NeraSpreadSheet đang làm việc khác.

Những nhận xét dưới đây áp dụng cho SHA này, không phải cam kết về mọi phiên bản:

| Thành phần nguồn | Đã thấy trong code | Cách dùng đề xuất |
| --- | --- | --- |
| `src/NeraSpreadSheet.Scrolling/ContinuousScrollController.cs` và `ScrollContracts.cs` | Gom input, cuộn theo frame, giữ offset lẻ, giới hạn biên; module không có ProjectReference/PackageReference | Ứng viên tái sử dụng mã nguồn cho cuộn bảng; kiểm lại khi chuyển sang .NET 8 |
| `src/NeraSpreadSheet.Wpf/NeraSpreadsheetControl.cs` | Một TextBox editor tái sử dụng; layout/viewport riêng; xử lý cuộn qua frame loop | Tận dụng cách thiết kế, không chép nguyên control |
| `src/NeraSpreadSheet.Wpf/NeraSpreadsheetControl.EditorDraft.cs` | Draft đang gõ tách khỏi dữ liệu đã commit; giữ selection/caret | Áp dụng luồng draft/commit cho rich editor của H2 Notes |
| `src/NeraSpreadSheet.DataGrid.Core/` | Bốn file contract về nguồn dữ liệu, cột, chọn hàng, sắp xếp; chưa có UI bảng | Tham khảo mô hình cột và dữ liệu theo record, không coi là control hoàn thiện |
| `src/NeraSpreadSheet.Iconography/` | Catalog, tài nguyên icon và giấy phép icon bên thứ ba | Có thể chọn vài icon phù hợp sau khi chốt UI; giữ attribution khi sao chép |
| `ARCHITECTURE.md` | Chỉ layout/render vùng nhìn thấy; không tạo control/editor cho mọi ô | Áp dụng nguyên tắc cho bảng H2 Notes |

Không lấy nguyên host bảng tính: WPF host hiện target
`net10.0-windows10.0.19041.0`, có 16 project trong cây phụ thuộc tính cả host,
và có backend/package đồ họa. H2 Notes đang là `net8.0-windows`.
Không thể thêm trực tiếp reference đó vào app hiện tại mà bỏ qua khác biệt target.

Nera hiện ưu tiên Avalonia cho app mới; WPF vẫn được giữ cho consumer cũ.
Điều đó không bắt buộc đổi nền tảng H2 Notes. Đề xuất giữ WPF/.NET 8 ở bước này,
không đưa Ribbon, công thức, biểu đồ, in/PDF hoặc XLSX vào đường chạy của note.
Số module không tự chứng minh app nặng: mức RAM, thời gian mở và độ trễ thực tế
vẫn phải đo sau khi có bản tích hợp.

## Giao diện đề xuất

Một bảng trong cửa sổ quản lý hiện tại, không biến mỗi dự án thành một workbook.
Note thường vẫn giữ nguyên. Trong giai đoạn thử nghiệm có chuyển đổi Thẻ / Bảng,
cùng đọc một bộ dữ liệu, không nhân đôi dự án.

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
- Tên, checklist và Notes tiếp tục giữ định dạng từng đoạn chữ cùng menu hiện có.

## Tác vụ và bảo toàn dữ liệu

1. Click chọn hàng; double-click hoặc F2 sửa tên/công việc. Enter xác nhận,
   Esc hủy draft, Shift+Enter xuống dòng khi sửa. Notes vẫn dùng Enter xuống dòng.
2. Checkbox đổi trạng thái thật. Việc tiếp theo luôn là checklist chưa xong đầu tiên
   có nội dung, không có ô nhập Next độc lập.
3. Chuột phải tại mũi tên dự án có “Ưu tiên thực hiện trước”: đưa cả nhóm lên đầu,
   giữ thứ tự checklist. Đây là di chuyển một lần, không phải ghim vĩnh viễn.
4. Kéo dự án đi cùng toàn bộ checklist. Kéo checklist chỉ đổi thứ tự trong dự án
   của nó ở phiên bản đầu. Có bóng kéo và vạch vị trí thả; chỉ sửa collection và
   tính lại STT/Next sau khi thả, Esc không để lại thay đổi.
5. Nếu đang tìm kiếm/lọc, tạm không cho kéo thứ tự để tránh vị trí bị hiểu nhầm;
   lệnh ưu tiên vẫn xác định dự án bằng ID, không bằng chỉ số hàng đang nhìn thấy.
6. Trước khi đổi dự án đang soạn hoặc đổi chế độ Thẻ/Bảng, commit draft đang dùng.
   Không dựng lại bảng trong khi bộ gõ tiếng Việt đang nhập tổ hợp ký tự.
7. JSON, ID dự án, ID checklist, thứ tự collection và rich text XAML hiện có vẫn
   là nguồn dữ liệu thật. Bảng chỉ là cách hiển thị; không chuyển dữ liệu thật sang XLSX.
8. Văn bản thuần dùng để tìm kiếm/preview không bao giờ ghi đè dữ liệu có định dạng.
   Undo và autosave tác động model H2 Notes, không tạo workbook thứ hai để đồng bộ.
9. Nút chuyển về Thẻ là cách quay lại UI cũ. Chưa thay tray, opacity, startup,
   vị trí cửa sổ, ghim trên cùng hoặc note thường trong phạm vi bảng này.

## Phân lớp khi triển khai

```text
NoteDocument / ProjectEntry / ChecklistItem hiện có
                  |
                  v
ProjectSheetAdapter: ánh xạ ID thành hàng đang hiển thị, cache nội dung/tiến độ
                  |
                  v
ProjectSheetView: header + vùng hàng nhìn thấy + selection + preview kéo
                  |
                  +-- Rich editor dùng lại cho ô đang sửa
                  +-- Khung rich Notes cho dự án đang chọn
                  +-- Bộ cuộn: chọn lọc từ NeraSpreadSheet.Scrolling
```

Không tính lại preview rich text hoặc serialize cả note mỗi lần gõ/cuộn.
Giữ debounce hiện tại 900 ms cho autosave khi ngừng gõ, đồng thời commit khi rời
editor/đóng app. Cache theo ID và phiên bản nội dung; chỉ vô hiệu phần đã đổi.
Chỉ giữ frame loop khi thực sự có chuyển động hoặc cần vẽ lại.

Nếu lấy source Nera vào app, dùng thư mục riêng cùng nguồn/SHA/ghi chú thay đổi;
không reference vào đường dẫn `.artifacts` hoặc bản checkout riêng của người dùng.
Module độc lập chỉ được chọn sau khi build và regression tests với target app.

## Bằng chứng và cổng kiểm tra

Đã chạy trên bản tham khảo: test project `NeraSpreadSheet.Scrolling.Tests`,
Release, .NET 10: **3 passed, 0 failed, 0 skipped**. Các case kiểm offset lẻ,
cuộn tiến dần tới đích và giới hạn biên. Chưa phải đo hiệu năng hoặc nghiệm thu
tích hợp H2 Notes/.NET 8. Chưa build lại H2 Notes vì không sửa source app.

Trước khi thay UI mặc định cần kiểm:

- Đọc/lưu lại bản sao dữ liệu thử: ID, thứ tự, rich text, checkbox giữ nguyên.
- Đổi Thẻ/Bảng, đóng/mở app, đổi hàng đang sửa không mất draft hoặc định dạng.
- Gõ tiếng Việt nhanh, mở menu định dạng, đổi font/màu không mất selection.
- Ưu tiên/kéo dự án giữ cả nhóm; kéo checklist không kích hoạt kéo dự án.
- Kéo preview không thay Next; drop mới thay, cancel không thay.
- Next dài và Notes nhiều dòng không bị cắt; DPI 100/125/150/200% không lệch hit-test.
- Đo bản thẻ và bản bảng trên cùng máy/dữ liệu tổng hợp: thời gian mở, cuộn,
  độ trễ gõ và bộ nhớ ở 100 dự án/1.000 việc và 1.000 dự án/10.000 việc.
- Đối chiếu note thường, tray single/double/right click, opacity và startup.

Bước kế tiếp: sau khi chủ app chốt cho viết code, làm lát cắt bảng có chọn dự án,
thu gọn, tick checklist, sửa rich text và khung Notes trước; đo/kiểm rồi mới mở
rộng kéo thả và thay UI mặc định. Không tuyên bố bảng hoàn thiện chỉ vì build đạt.
