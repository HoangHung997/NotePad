H2 NOTES AVALONIA - PORTABLE WINDOWS x64

1. Chép NGUYÊN thư mục publish, không chỉ H2Notes.Avalonia.exe.
   Bản CI Portable là self-contained: máy đích không cần cài .NET 10.

2. OCR PDF/ảnh:
   - Artifact CI tiêu chuẩn có code bridge nhưng KHÔNG chứa các model OCR nhiều GiB.
   - Trên máy đang có đủ GOT-OCR 2.0 + Docling + MinerU, mở:
     Thiết lập AI -> Tài liệu PDF và OCR ảnh -> Đóng gói đủ 3 OCR vào app.
   - Chờ app báo ĐÃ PORTABLE, sau đó chép lại NGUYÊN thư mục ứng dụng.
   - Thư mục ocr-runtime cạnh H2Notes.Avalonia.exe chứa Python + dependencies + models.
     Khi sang máy/đường dẫn mới app tự sửa launcher venv từ Python đi kèm.

3. Kết nối AI:
   - API key được Windows DPAPI bảo vệ và CỐ Ý không nằm trong thư mục portable.
     Trên PC mới hãy nhập lại API key.
   - Ollama localhost là dịch vụ ngoài H2 Notes. PC mới phải có Ollama + model,
     hoặc cấu hình H2 Notes trỏ tới một Ollama server trong LAN.

4. Dữ liệu:
   - Workspace NAS có thể dùng chung giữa nhiều PC.
   - Draft chưa gửi, vị trí cửa sổ và một số thiết lập máy được giữ local để tránh xung đột.
   - Các project link dùng đường dẫn tuyệt đối (C:\..., D:\...) không tự được sao chép sang máy mới.

5. Kiểm tra trước khi chép:
   Thiết lập AI -> Tài liệu PDF và OCR ảnh -> Kiểm tra copy sang máy khác.

Không xóa kho dữ liệu nguồn khi đổi máy. Luôn thử bản portable trên PC mới trước khi bỏ bản cũ.
