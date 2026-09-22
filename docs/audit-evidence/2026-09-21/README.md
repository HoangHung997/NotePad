# Bằng chứng rà soát 21/09/2026

Source HEAD: `9660a0f3000ea9c24d5e071160d22196baf4836a` trên `feature/nas-multi-device-sync`, cùng các sửa Coordinator/test H2M-133F local đã có trước audit. Không sửa production Agent/UI trong đợt thu thập này.

## Agent

`agent-offline-probe.json` ghi kết quả `H2ProductionAgentAdapter` với `AgentRuntimeFactory` mặc định. Chỉ transport được thay bằng câu trả lời cố định `Xin chào!`. Probe không gọi model/network/vault và dùng workspace tổng hợp trong thư mục tạm.

- Một lần với `readOnly=false`, một lần với `readOnly=true`.
- Một prompt nhiều dòng để kiểm tra validation tại đầu vào.
- Snapshot tên tool của registry mặc định và trạng thái desktop controller.

Nó chứng minh lỗi completion gate/validation và cấu hình registry; không chứng minh khả năng của provider thật hoặc toàn bộ hành vi tool.

## UI

PNG được tạo từ `H2Notes.Avalonia.App`, `MainWindow` và style thật, dùng Avalonia.Headless 12.1.2 + Skia, `UseHeadlessDrawing=false`, `RenderTargetBitmap` 96 DPI. App được thiết lập không có desktop lifetime production; dữ liệu lấy từ `SheetStorage.Demo()` với tên dự án tổng hợp. Không nạp/sửa project của người dùng.

| File | Kích thước DIP / PNG | Trạng thái |
| --- | --- | --- |
| command-center-1040.png | 1040×760 | Command Center |
| project-agent-1440.png | 1440×860 | Workspace Agent |
| project-agent-1040.png | 1040×760 | Workspace Agent |
| project-agent-560.png | 560×820 | Workspace Agent hẹp |
| project-agent-long-title-560.png | 560×600 | Workspace Agent với tên dự án dài |

`layout-measurements.json` ghi vị trí/kích thước control và hit-test tại tâm sau khi bố trí. Một số hit-test trong frame đầu hoặc sau đổi size có thể chưa ổn định; báo cáo chỉ dùng hit-test 1040 khớp ảnh và vị trí hình học đã kiểm tra. Không suy ra mọi nút có `CenterHitInside=false` đều lỗi.

**Đây là render ngoài màn hình, không phải native screenshot.** Chúng tái hiện được chồng lấn/cắt nội dung; không thay nghiệm thu Windows/DPI/IME, chuột/phím thật hoặc so sánh parity đầy đủ với mockup.

## Tái chạy harness local

Harness dùng cho audit nằm ở `.artifacts/audit-2026-09-21/` (được Git ignore), gồm `AuditProbe.csproj`, `Program.cs`, `AgentAuditProbe.cs`, `UIAuditProbe.cs`. Sau khi đã có output Release của H2 Notes, lệnh được dùng là:

```powershell
dotnet run --project .artifacts/audit-2026-09-21/AuditProbe.csproj -c Release -p:BuildProjectReferences=false
```

`BuildProjectReferences=false` tránh ghi đè DLL đang bị app mở giữ. Đây không phải cách thay thế build sạch khi sửa sản phẩm. Các kết quả JSON/PNG trong thư mục này được lưu lại để báo cáo vẫn đọc được dù thư mục tạm bị xóa; harness chưa trở thành bài regression trong CI.

`baseline-hash-check.json` kiểm tra 10 ảnh thiết kế gốc với SHA-256 trong `docs/ui-concepts/2026-09-15-responsive-hybrid/BASELINE.json`: tất cả khớp.
