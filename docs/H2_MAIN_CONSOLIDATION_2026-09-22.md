# Hợp nhất bản hiện tại vào main — 22/09/2026

Người dùng yêu cầu lấy bản H2 Notes cuối cùng đã kiểm tra làm `main`, đồng bộ lên GitHub và dọn các bản/nhánh cũ.

## Mã nguồn và lịch sử

- `dbb7da8`: ghi nhận toàn bộ phần Agent, tài liệu, giao diện, sửa Word/CV và bong bóng thu gọn đã làm, kèm kiểm thử và bằng chứng. Đây là cây mã ứng dụng đã đạt **590/590**.
- Hợp nhất bốn commit đặc tả mới nhất của `feature/nas-multi-device-sync`, đến `fd5bfff`.
- Hợp nhất lịch sử `main` cũ tại `8a45794`. Các tài liệu cùng được thêm ở hai nhánh có xung đột: giữ đặc tả/tiến độ mới hơn, đồng thời giữ thông báo tài liệu lịch sử đã được master spec thay thế.
- Hợp nhất lịch sử độc lập `full-source-import-20260917` tại `b000513`. Đã so toàn bộ danh sách: mã nguồn chung đã nằm nguyên trong cây hiện tại; khác biệt chung chỉ ở README và ignore, tệp chỉ có ở bản nhập cũ là cache Python `tools/ocr/__pycache__/convert.cpython-312.pyc`. Vì vậy ghi nhận quan hệ merge, giữ cây mã hiện tại, không nhập lại cache.
- Cả ba đầu nhánh cũ được giữ trong lịch sử tổ tiên của `main`; không force-push hoặc viết lại lịch sử. Nhánh phụ chỉ được xóa sau khi kiểm tra remote `main` chứa các commit đó.
- Mã ứng dụng, kiểm thử, Agent, WPF và `_ver2` sau hợp nhất không đổi so với `dbb7da8`. Không dùng việc hợp nhất để đánh dấu các giới hạn NAS hai máy hay các chức năng chưa nghiệm thu thành đã xong.

## Dọn dẹp

- Chuyển **21 thư mục build/bản thử** và **10 tệp metadata/checksum cũ** vào Thùng rác, tổng kích thước logic **12.405.630.375 byte**. Thùng rác cho phép khôi phục; đây không phải số dung lượng đã giải phóng vật lý.
- Chỉ dọn các bản build thử trong `.artifacts`, hai bản sao kiểm relocation trong Downloads và metadata ZIP đã hết dùng. Giữ log kiểm thử, bằng chứng, đặc tả, ảnh baseline, mã WPF/WinForms, dữ liệu người dùng, thư mục portable đang chạy và ZIP đầy đủ mới nhất.
- Xóa `bin/LATEST.txt` đã trỏ bản cũ; bỏ ngoại lệ đưa root `bin` vào Git. Gói nhị phân được phân phối riêng khỏi mã nguồn.
- README nay mô tả bản Avalonia/Agent đang dùng và dẫn tới biên bản hiện tại, thay nội dung đầu trang còn ghi 77 kiểm tra và Agent chưa tích hợp.
- Ba workflow nhận thay đổi trên `main`. CI vẫn build/test/publish artifact; bỏ bước tự commit ZIP/metadata vào nhánh để không tiếp tục tạo các commit build cũ. Quyền workflow CI giảm về đọc nội dung.
- GitHub chưa có Release tại thời điểm rà soát; không có release cũ cần xóa. Gói full nhiều GB bàn giao trên máy, không đưa vào lịch sử Git.

## Bằng chứng

- [590 kiểm tra ứng dụng](audit-evidence/2026-09-22-main/regression.txt).
- [Kiểm tra 36.317 tệp của ZIP đầy đủ](audit-evidence/2026-09-22-main/archive-verification.json).
- [Các đầu nhánh trước khi hợp nhất](audit-evidence/2026-09-22-main/refs-before.txt).
- [Bong bóng và kiểm chuột native](H2_BUBBLE_COMPACT_2026-09-22.md), [bản portable và các giới hạn](H2_PORTABLE_BUILD_2026-09-22.md).

Việc xóa vĩnh viễn hàng loạt bị bộ duyệt tự động chặn; đã hoàn tất phương án có thể khôi phục bằng Thùng rác, không yêu cầu người dùng cấp quyền lại.
