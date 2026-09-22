# H2 Notes

`main` là nhánh chính thức của bản H2 Notes hiện tại, đã hợp nhất mã nguồn, đặc tả và lịch sử các nhánh cũ ngày 22/09/2026.

H2 Notes là ứng dụng Windows quản lý dự án, công việc, ghi chú, tài liệu và hội thoại AI. Bản hiện tại sử dụng Avalonia, tích hợp H2 Agent Lab với các công cụ tài liệu, Office/desktop, OCR và web.

## Bản hiện tại

- Giao diện dự án và Agent theo thiết kế `2026-09-21-agent-documents-demo`, có lịch sử hội thoại, ô soạn phía dưới, quyền theo tác vụ và xem tài liệu.
- Assistant nổi: hình tròn khi rảnh hoặc chat đang mở; một dòng hoạt động cuộn ngang khi đang làm và chat bị ẩn. Khung chat đi theo bong bóng.
- Bổ sung và sửa các luồng Word/Excel/PDF, AutoCAD, OCR, đọc tin; báo kết quả theo bằng chứng kiểm tra.
- Kiểm tra ứng dụng gần nhất: **590/590 đạt**. Xem [kiểm thử chức năng và giới hạn](docs/H2_AGENT_CAPABILITY_ACCEPTANCE_2026-09-22.md), [sửa Word/CV](docs/H2_WORD_CV_REPAIR_2026-09-22.md) và [kiểm chứng bong bóng](docs/H2_BUBBLE_COMPACT_2026-09-22.md).

Kết quả này không chứng nhận mọi khả năng của model, độ chính xác OCR trên mọi tài liệu hay đồng bộ trên hai máy thật. Các vấn đề còn mở được ghi trong biên bản nghiệm thu.

## Chạy và kiểm tra mã nguồn

Cần Windows và .NET 10 SDK:

```powershell
dotnet restore .\H2Notes.Avalonia.slnx
dotnet build .\H2Notes.Avalonia.slnx -c Release --no-restore
dotnet run --project .\tests\H2Notes.Tests\H2Notes.Tests.csproj -c Release --no-build
dotnet run --project .\src\H2Notes.Avalonia\H2Notes.Avalonia.csproj -c Release
```

## Bản portable

[GitHub Actions](https://github.com/HoangHung997/NotePad/actions/workflows/avalonia-ci.yml) tạo bản ứng dụng Windows x64 self-contained theo commit trên `main`. Chọn lượt chạy thành công mới nhất và tải artifact `H2Notes-Avalonia-Portable-win-x64`. Gói ứng dụng này khác gói đầy đủ nhiều GB có Python/model OCR.

Thông tin gói đầy đủ đã kiểm tra, thành phần đi kèm và cách chuyển sang máy khác nằm trong [biên bản portable 22/09](docs/H2_PORTABLE_BUILD_2026-09-22.md). Gói đầy đủ hiện được bàn giao trên máy cục bộ; không lưu ZIP nhiều GB trong Git. [Workflow OCR](https://github.com/HoangHung997/NotePad/actions/workflows/avalonia-portable-ocr.yml) phục vụ đóng gói runtime OCR riêng.

Giải nén toàn bộ gói trước khi chạy. Cấu hình AI và API key thuộc máy/tài khoản của người dùng. Microsoft Office, AutoCAD, dịch vụ Ollama hoặc nhà cung cấp AI cần được cài/kết nối riêng theo chức năng sử dụng.

## Mã nguồn và tài liệu

- `src/H2Notes.Avalonia/`: ứng dụng hiện tại.
- `src/H2Notes.Core/`, `src/H2Notes.Coordinator/`: dữ liệu và đồng bộ.
- `experiments/H2AgentLab/`: Agent core và phần tích hợp đang được ứng dụng sử dụng.
- `experiments/H2AgentLab.OfficeHost/`, `experiments/H2AgentLab.DesktopHost/`: bộ kết nối ứng dụng Windows.
- `tests/`, `tools/`: kiểm tra, công cụ đóng gói, OCR và kiểm thử NAS.
- [Đặc tả sản phẩm](docs/H2_PRODUCT_MASTER_SPEC.md), [đặc tả Agent](docs/H2_AGENT_MASTER_SPEC.md), [đặc tả chat](docs/H2_AGENT_CHAT_SURFACE_SPEC.md).
- [Thiết kế được duyệt](docs/ui-concepts/2026-09-21-agent-documents-demo/README.md), [nghiệm thu giao diện](docs/UI_ACCEPTANCE.md).
- [Hợp nhất và dọn các bản cũ](docs/H2_MAIN_CONSOLIDATION_2026-09-22.md).

`src/Nodepad.Desktop/`, `Nodepad.slnx` và `_ver2/` giữ mã WPF/WinForms lịch sử để đối chiếu. Đây không phải bản ứng dụng hiện tại; không xóa dữ liệu hoặc sửa ảnh baseline gốc khi dọn bản build.
