# H2 Notes: Hướng 2 với trò chuyện AI

Năm mẫu bố cục, ngày 15/09/2026. Giữ phong cách hướng 2 và danh sách dự án bên trái kiểu ứng dụng nhắn tin; thay cách bố trí công việc, ghi chú và AI bên phải.

**Chỉ là ảnh mô phỏng, chưa tích hợp AI, chưa sửa code app, chưa kết nối mô hình hoặc API.** Nội dung chat và trạng thái kết nối trong ảnh là ví dụ minh họa. Tạo bằng công cụ image_gen tích hợp, dùng ảnh 02A cũ làm tham chiếu phong cách.

| Mẫu | Cách bố trí | Điểm đánh đổi |
| --- | --- | --- |
| [2.1. Ba vùng song song](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/21-ba-vung-song-song.png) | Danh sách trái, công việc/ghi chú ở giữa, AI bên phải. | Nhìn đồng thời mọi phần; cần cửa sổ tương đối rộng. |
| [2.2. Trò chuyện là trung tâm](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/22-chat-trung-tam.png) | Chat chiếm vùng chính, checklist và ghi chú ghim phía trên. | Gần Zalo nhất; khi soạn ghi chú dài cần mở vùng riêng. |
| [2.3. AI ở ngăn dưới](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/23-ai-ngan-duoi.png) | Công việc/ghi chú phía trên, chat bên dưới; kéo thay chiều cao. | Giữ chiều ngang cho bảng; chia sẻ chiều cao với chat. |
| [2.4. AI bật khi cần](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/24-ai-bat-khi-can.png) | Ưu tiên trang soạn, AI là ô chat nổi có thể di chuyển. | Thoáng khi viết; ô nổi có thể che nội dung nếu đặt không phù hợp. |
| [2.5. Không gian tự sắp xếp](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/25-khong-gian-tu-sap-xep.png) | Ba khung công việc, ghi chú, AI có thể đổi chỗ/đổi kích thước/thu gọn. | Linh hoạt nhất; cần giới hạn kích thước tối thiểu và cách ghép khung để không rối. |

## Điểm chung đề xuất

- Mỗi dự án có lịch sử chat AI riêng; không trộn hội thoại của các dự án.
- Chọn **Trên máy** hoặc **API online**, có lối mở cài đặt kết nối/mô hình.
- Hiển thị rõ ngữ cảnh AI sử dụng: dự án, checklist, ghi chú hoặc chỉ đoạn đang chọn.
- Có thể xem và bỏ bớt ngữ cảnh trước khi gửi. Bản API online cần nói rõ nội dung được chuyển ra ngoài máy.
- Đề xuất AI là bản nháp. Chỉ thêm công việc, lưu ghi chú hoặc thay đoạn chữ khi người dùng bấm xác nhận.
- Giữ các nút ghim, menu và X; không thêm nút thu nhỏ/phóng to cửa sổ app.
- Cỡ tiêu đề dự án nhỏ hơn bản 02A ban đầu, nhường diện tích cho nội dung.

## Gợi ý lựa chọn

**2.5** phù hợp nếu ưu tiên tùy biến bố cục. Có thể chọn cách bố trí mặc định giống **2.1**, rồi chuyển ngăn AI xuống dưới khi cửa sổ hẹp.
**2.2** phù hợp nếu muốn hội thoại là tác vụ chính, gần cảm giác Zalo nhất.
Chưa chốt runtime local, nhà cung cấp API hoặc mô hình cụ thể trong đợt mô phỏng này.

## Mô tả tạo ảnh

[Toàn bộ prompt](D:/VSstudio/Nodepad/docs/ui-concepts/2026-09-15-direction-2-ai/PROMPTS.md). Các ảnh PNG cùng thư mục là bản chốt của lượt mô phỏng này; bộ bốn cặp trước đó được giữ nguyên.

