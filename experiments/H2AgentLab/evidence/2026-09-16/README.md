# Nhật ký kiểm UI và bằng chứng

Ngày 16/09/2026. Đây là bằng chứng cho app riêng H2 Agent Lab, không phải nghiệm thu thay đổi UI H2 Notes. Không thay hoặc gắn trạng thái hoàn thành cho 10 ảnh baseline của H2 Notes.

## Giao diện từ app thật

- `lab-1180x800-render96.png`: app Release chạy với `--evidence`, cửa sổ yêu cầu 1180×800 DIP, RenderTargetBitmap 96 DPI. Đã xem toàn ảnh: vùng chat, thanh bên và ô soạn/nút gửi rõ; thanh bên có cuộn độc lập.
- `lab-840x620-render96.png`: app thu về 840×620 DIP, render 96 DPI. Đã xem: footer không bị vùng chat đẩy ra ngoài; chat và thanh bên cuộn độc lập. Nội dung dài phải cuộn, không hứa hiển thị hết cùng lúc.
- `capture.txt` ghi hệ số native lúc chụp là 1,25. Hai ảnh render không phải screenshot native, không dùng để tuyên bố đạt DPI pixel parity.
- Nội dung hội thoại trên các ảnh là dữ liệu dựng cho kiểm UI, có nhãn MODEL MẪU và Không gửi AI; không phải kết quả suy luận thật.

## Quan sát native

Đã mở bản Release bình thường từ thư mục `bin/Release/net10.0-windows`, kho riêng `%LOCALAPPDATA%/H2AgentLab`. Kiểm qua Windows Computer Use: cửa sổ title `H2 Agent Lab · Bản thử độc lập`; trạng thái cuối PID 23872 còn chạy và Responding=true. Screenshot lần đầu có H2 Notes che một phần. Sau khi activate riêng Lab, screenshot toàn cửa sổ hiển thị rõ khung soạn, Chỉ đọc, Dừng và Gửi yêu cầu. Không thay cài đặt nổi trên cùng hoặc dữ liệu của H2 Notes.

Đã xác minh mở vùng thử điều khiển từ UI ở lần kiểm trước. Bộ kiểm UI nhận form/control nhưng chưa xác nhận được focus khi thử ô nhập; không gửi phím khi focus chưa rõ. Chưa thực hiện test model → công cụ UIA của Lab → duyệt → thao tác → kiểm lại. Không được coi điều khiển máy tính đã nghiệm thu chỉ vì có accessibility tree.

Các dialog kết nối/phê duyệt chưa được kiểm đầy đủ bằng nhập liệu trên desktop; không tự nhập key hay tự bấm cấp quyền. Chưa có screenshot native được lưu thành tệp tại đây; ảnh native được quan sát qua công cụ trong phiên làm việc.

## Word

`gemma-word-page-1.png` là render bằng LibreOffice của Word thật do Gemma4 tạo qua công cụ, không phải ảnh mô phỏng. Đã xem toàn trang: chữ tiếng Việt rõ, các dòng không chồng/cắt, đủ nội dung fixture H2-7429. Không dùng ảnh này để chấm tác vụ AI hoàn tất vì server lỗi ở lượt trả lời cuối.

## Nguồn dữ liệu

Tất cả log benchmark và trang Word tại đây chỉ có nội dung giả Cầu Bình Minh, H2-7429. Không có API key, nội dung tài liệu thật hoặc kho H2 Notes. Các thư mục `.artifacts` chứa fixture/chạy thử không được đưa vào git.
