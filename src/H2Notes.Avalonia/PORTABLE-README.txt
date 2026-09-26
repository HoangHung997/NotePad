H2 NOTES AVALONIA - PORTABLE WINDOWS x64

1. Giải nén / chép NGUYÊN thư mục publish, không chỉ H2Notes.Avalonia.exe.
   Bản CI Portable là self-contained: máy đích không cần cài .NET 10.
   Có thể đặt thư mục ở đường dẫn có dấu Unicode và khoảng trắng.

2. Kiểm tra gói trước khi dùng:
   - Mở PowerShell trong thư mục ứng dụng.
   - Chạy: .\VERIFY-PORTABLE.ps1
   - Script kiểm tra runtime tối thiểu trước khi mở app, sau đó H2 xác minh
     portable-manifest.json, SHA-256 toàn bộ file, Python đi kèm và IPC helper.
   - portable-manifest.json ghi exact source Git SHA, runtime win-x64, file inventory
     và SHA-256 từng file. Không chỉnh file trong gói nếu muốn giữ manifest hợp lệ.

3. Dependency preflight:
   - Word/Excel, AutoCAD, Ollama/model, Brave Search và browser CDP là capability
     riêng. Thiếu một capability phải hiện Ready/NeedsConfiguration/Unavailable;
     thiếu chúng KHÔNG được giả là app core bị hỏng.
   - Preflight offline không mở tài liệu, không gọi model/search/browser và không
     in API key.
   - API key vẫn nằm trong Windows DPAPI vault của từng Windows profile, KHÔNG
     nằm trong thư mục portable. PC/profile mới phải nhập lại key.

4. OCR PDF/ảnh:
   - ZIP CI tiêu chuẩn CỐ Ý KHÔNG chứa các model OCR nhiều GiB.
   - Có thể cài/đóng gói OCR riêng vào ocr-runtime sau khi app đã chạy ổn.
   - Việc thiếu OCR model chỉ làm capability OCR NeedsConfiguration, không làm
     portable app core thất bại.

5. Dữ liệu:
   - local-config, credential vault, Agent journal, draft và workspace local không
     được đóng gói trong ZIP.
   - Workspace/NAS là dữ liệu riêng; AR-083 hai PC/NAS vẫn cần nghiệm thu vật lý.
   - Project link dùng đường dẫn tuyệt đối không tự được sao chép sang máy khác.

6. Khi chuyển máy:
   - Giữ bản nguồn cho đến khi VERIFY-PORTABLE.ps1 và lần mở app trên Windows
     profile mới đều đạt.
   - Cấu hình lại model/endpoint/credential và các ứng dụng native cần dùng.
