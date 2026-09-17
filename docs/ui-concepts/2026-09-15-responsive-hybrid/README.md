# H2 Notes: bố cục tự thích ứng theo kích thước

Bộ 10 ảnh mô phỏng, ngày 15/09/2026. Phát triển hướng 2.4 (AI khi cần) và 2.5 (sắp xếp linh hoạt) theo yêu cầu mới: toàn màn hình ghim AI bên phải.

**Người dùng đã chốt bộ 10 ảnh này làm chuẩn giao diện ngày 15/09/2026. Chưa triển khai UI mới, chưa build, chưa tích hợp AI trong đợt chốt này.** Các hội thoại, thông tin lưu, ngày tháng và nút trong ảnh là dữ liệu minh họa, không phải trạng thái ứng dụng thật. Ảnh được tạo bằng công cụ tạo ảnh tích hợp; không dùng CLI/API riêng để tạo ảnh.

Mỗi chặng triển khai phải đối chiếu app thật với ảnh tương ứng theo [bảng nghiệm thu](D:/VSstudio/Nodepad/docs/UI_ACCEPTANCE.md). Yêu cầu bổ sung về thư mục lưu, từng tệp dự án và AI nằm trong [đặc tả đã chốt](D:/VSstudio/Nodepad/docs/APPROVED_PRODUCT_SPEC.md). [BASELINE.json](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/BASELINE.json) giữ dấu kiểm SHA-256 của mười ảnh gốc; không ghi đè ảnh chuẩn để hợp thức hóa sai khác khi làm app.

## Xem nhanh

Ưu tiên xem ảnh 10, 02, 05, 07 để đánh giá luồng: nhỏ nhất -> đổi dự án -> kéo rộng -> toàn màn hình.

| Ảnh | Trạng thái |
| --- | --- |
| 01 | [Cửa sổ hẹp: một dự án](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/01-hep-cong-viec-va-ghi-chu.png) |
| 02 | [Đổi dự án khi danh sách ẩn](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/02-hep-doi-du-an.png) |
| 03 | [Cửa sổ hẹp: tập trung soạn ghi chú](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/03-hep-soan-ghi-chu.png) |
| 04 | [Cửa sổ hẹp: gọi AI khi cần](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/04-hep-mo-ai.png) |
| 05 | [Cửa sổ vừa: hiện danh sách dự án](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/05-vua-danh-sach-chi-tiet.png) |
| 06 | [Cửa sổ vừa: AI dạng nổi](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/06-vua-ai-noi.png) |
| 07 | [Toàn màn hình: AI ghim bên phải](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/07-toan-man-hinh-ai-ben-phai.png) |
| 08 | [Kéo đổi bố cục: xem trước vị trí ghim](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/08-keo-ghim-ai-xuong-duoi.png) |
| 09 | [Ghi chú thường: cửa sổ nhỏ và menu định dạng](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/09-note-thuong-nho-gon.png) |
| 10 | [Cả chiều rộng và chiều cao tối thiểu](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/10-kich-thuoc-toi-thieu.png) |

## Kích thước đề xuất

Các số dưới đây là **đơn vị giao diện độc lập DPI (DIP)**, không phải kích thước tệp PNG hay số pixel đo chính xác từ Zalo. Ảnh người dùng gửi cho thấy cửa sổ Zalo rộng khoảng 686 pixel hiển thị; chưa xác định mức Scale Windows từ ảnh đó.

| Trường hợp | Rộng/cao đề xuất | Cách bố trí |
| --- | --- | --- |
| Nhỏ nhất | 560 x 600 | Thanh biểu tượng + một vùng nội dung; Công việc/Ghi chú chuyển bằng tab. |
| Hẹp, đủ cao | Rộng 560-899; ví dụ 560 x 820 | Ẩn danh sách chi tiết; có thể chia công việc và ghi chú trên/dưới khi còn đủ chỗ. |
| Vừa | Rộng từ 900; ví dụ 1040 x 760 | Danh sách cố định khoảng 240-260; vùng làm việc còn lại. AI chỉ mở khi gọi. |
| Toàn màn hình đủ rộng | Ví dụ 1440 x 860; từ khoảng 1360 dễ bố trí đủ ba vùng | Danh sách + nội dung + AI bên phải rộng khoảng 320-360. |

Ví dụ Scale 125% thì chiều rộng 560 DIP hiển thị khoảng 700 pixel, gần tỷ lệ cửa sổ Zalo trong ảnh. Đây là mốc thử thiết kế, chưa đo và kiểm chứng trực tiếp bằng Avalonia trên máy.

Ảnh 01-04 dùng cửa sổ hẹp nhưng cao để đọc được nhiều nội dung; ảnh 10 mới là thử nghiệm đồng thời chiều rộng và chiều cao tối thiểu. Không thu nhỏ chữ khi giảm cửa sổ: đổi bố cục, xuống dòng và cuộn phần nội dung.

## Quy tắc tương tác

- Khi danh sách ẩn, bấm tên dự án có mũi tên, nút Dự án / số hoặc biểu tượng Dự án trên thanh bên để mở cùng một bộ chọn. Đề xuất Ctrl+K cho tìm/chuyển nhanh.
- Bộ chọn hiện ngay dự án đang chọn, cho tìm theo dự án/công việc, tên dài xuống dòng. Chọn một dòng sẽ đóng bộ chọn và mở dự án đó. Bấm ngoài, X hoặc Esc chỉ đóng bộ chọn.
- Khi chiều rộng vượt mốc vừa, danh sách chi tiết tự hiện; người dùng vẫn có quyền thu lại. Chừa một khoảng đệm quanh mốc chuyển để giao diện không bật/tắt liên tục khi kéo mép.
- Với cửa sổ thấp, Công việc và Ghi chú hiển thị luân phiên qua tab. Khi đủ cao, có thể bật bố cục trên/dưới; không để hai vùng đều quá thấp.
- AI mặc định ẩn ở cửa sổ hẹp/vừa. Bản hẹp chuyển sang trang AI có Quay lại dự án; bản vừa mở khung nổi.
- Khi vào toàn màn hình lần đầu theo bố cục tự động, ghim AI bên phải. Nếu màn hình không đủ rộng cho cả danh sách và AI, ưu tiên thu danh sách về bộ chọn để bảo vệ vùng soạn thảo; ở màn hình rất hẹp dùng AI dạng trang tạm.
- Nếu người dùng chủ động ẩn AI, không mở lại liên tục vì các thay đổi kích thước nhỏ. Ghi nhớ lựa chọn theo nhóm bố cục. Việc hiển thị AI không tự khởi động mô hình, tự gửi API hay sửa dữ liệu.
- Kéo các khung bằng tay nắm trên tiêu đề, không lấy thao tác chọn văn bản làm thao tác kéo. Khung nổi có bóng theo chuột, vị trí ghim có preview; chỉ đổi bố cục lúc thả.
- Giữ nguyên dự án đang chọn, bản nháp, undo, vùng bôi đen, vị trí cuộn và lịch sử chat khi đổi kích thước/chuyển kiểu khung. Đổi dự án phải cập nhật phạm vi ngữ cảnh AI rõ ràng, không trộn dữ liệu âm thầm.
- AI online chỉ gửi nội dung trong phạm vi đã chọn sau thao tác Gửi. Có Xem dữ liệu gửi; kết quả cần bấm Chèn/Thay/Thêm công việc để áp dụng. Lịch sử chat theo dự án.
- Lưu cách chia khung và trạng thái đóng/mở theo nhóm kích thước; có Bố cục -> Khôi phục mặc định.
- Giữ yêu cầu cửa sổ không nằm trên taskbar, X chỉ ẩn, không đưa lại nút thu nhỏ/phóng to. Có thể mở chế độ Lấp đầy màn hình từ menu cửa sổ, và quay lại kích thước trước đó từ cùng menu.
- Giữ các chức năng đã yêu cầu: số thứ tự dự án tự cập nhật, kéo sắp xếp dự án/checklist, ưu tiên dự án, checkbox chung vùng STT, tự xuống dòng theo cấu hình cột và tooltip đủ nội dung, định dạng đồng bộ, import JSON cũ. Các nút ít dùng nằm trong menu thay vì trải ra tất cả ở kích thước hẹp.

## Bộ ảnh

### 01. Cửa sổ hẹp: một dự án

Cửa sổ hẹp nhưng còn đủ cao: công việc ở trên, ghi chú ở dưới. Danh sách dự án đã ẩn; nút Dự án / 3 mở bộ chọn.

![01. Cửa sổ hẹp: một dự án](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/01-hep-cong-viec-va-ghi-chu.png)

### 02. Đổi dự án khi danh sách ẩn

Bộ chọn dự án mở đè trong cửa sổ: tìm kiếm, xem tên đầy đủ, tiến độ và việc tiếp theo. Chọn xong tự đóng.

![02. Đổi dự án khi danh sách ẩn](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/02-hep-doi-du-an.png)

### 03. Cửa sổ hẹp: tập trung soạn ghi chú

Tập trung vào ghi chú: checklist thu về một dòng tóm tắt; phần soạn thảo dùng toàn bộ chiều cao còn lại.

![03. Cửa sổ hẹp: tập trung soạn ghi chú](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/03-hep-soan-ghi-chu.png)

### 04. Cửa sổ hẹp: gọi AI khi cần

AI ở cửa sổ hẹp: thay tạm vùng nội dung bằng chat; Quay lại dự án khôi phục bản nháp, lựa chọn và vị trí đang đọc.

![04. Cửa sổ hẹp: gọi AI khi cần](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/04-hep-mo-ai.png)

### 05. Cửa sổ vừa: hiện danh sách dự án

Cửa sổ vừa: danh sách chi tiết hiện cố định, công việc và ghi chú chia trên/dưới. AI chưa chiếm diện tích.

![05. Cửa sổ vừa: hiện danh sách dự án](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/05-vua-danh-sach-chi-tiet.png)

### 06. Cửa sổ vừa: AI dạng nổi

Gọi AI ở cửa sổ vừa: cửa sổ chat nổi, có thể di chuyển, đổi cỡ, đóng hoặc ghim cạnh. Chọn phạm vi dữ liệu trước khi gửi API.

![06. Cửa sổ vừa: AI dạng nổi](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/06-vua-ai-noi.png)

### 07. Toàn màn hình: AI ghim bên phải

Toàn màn hình: danh sách bên trái, công việc/ghi chú ở giữa, AI ghim bên phải. Có thể ẩn hoặc tách AI ra.

![07. Toàn màn hình: AI ghim bên phải](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/07-toan-man-hinh-ai-ben-phai.png)

### 08. Kéo đổi bố cục: xem trước vị trí ghim

Kéo khung AI bằng tay nắm: bóng khung theo chuột, vùng nhận hiện trước. Chỉ áp dụng bố cục khi thả; Esc hủy.

![08. Kéo đổi bố cục: xem trước vị trí ghim](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/08-keo-ghim-ai-xuong-duoi.png)

### 09. Ghi chú thường: cửa sổ nhỏ và menu định dạng

Note thường là cửa sổ riêng, không có danh sách dự án. Thanh công cụ và menu chuột phải phản ánh cùng định dạng của vùng chọn.

![09. Ghi chú thường: cửa sổ nhỏ và menu định dạng](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/09-note-thuong-nho-gon.png)

### 10. Cả chiều rộng và chiều cao tối thiểu

Đề xuất mức tối thiểu 560 x 600 đơn vị giao diện: chỉ hiện một vùng Công việc hoặc Ghi chú theo tab, không ép hai vùng nhỏ xíu.

![10. Cả chiều rộng và chiều cao tối thiểu](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/10-kich-thuoc-toi-thieu.png)

## Giới hạn bản mô phỏng

Ảnh raster minh họa bố cục, không phải ảnh chụp chương trình đang chạy hay bản đặc tả pixel tuyệt đối. Có một số khác biệt nhỏ giữa ảnh về nhãn phụ, kiểu biểu tượng và khoảng cách chữ; khi triển khai sẽ thống nhất bằng một bộ thành phần giao diện. Ảnh menu ghi chú chỉ minh họa trạng thái định dạng, không thay thế danh sách chức năng đầy đủ đã yêu cầu.

Bố cục đã được chốt; dùng các kích thước trên làm mốc triển khai ban đầu và kiểm lại ở DPI thực. Các thay đổi lớn so với ảnh phải xin chốt lại. Việc ghi nhận yêu cầu mới chưa đồng nghĩa đã viết xong chức năng trong app.

[Prompt tạo ảnh và lần tinh chỉnh](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-responsive-hybrid/PROMPTS.md)
