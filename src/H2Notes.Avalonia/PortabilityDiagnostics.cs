using System.Net;
using System.Text;
using H2Notes.Core;

namespace H2Notes.Avalonia;

internal static class PortabilityDiagnostics
{
    public static string Build(App app)
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        var bridge = AiSettingsWindow.PdfBridgePath;
        var install = Path.Combine(baseDir, "tools", "ocr", "install.py");
        var runtime = PortableOcrPackager.AppRuntimeRoot;
        var report = new StringBuilder();
        void Row(string name, bool ok, string detail) => report.Append(ok ? "✓ " : "⚠ ").Append(name).Append(": ").AppendLine(detail);

        report.AppendLine("H2 Notes · kiểm tra khả năng chép sang máy Windows khác");
        report.AppendLine("Thư mục ứng dụng: " + baseDir);
        report.AppendLine();

        var selfContained = File.Exists(Path.Combine(baseDir, "coreclr.dll")) || File.Exists(Path.Combine(baseDir, "hostfxr.dll"));
        Row(".NET", selfContained, selfContained
            ? "Bản self-contained; máy đích không cần cài .NET 10."
            : "Đây có vẻ là build framework-dependent; nên dùng artifact Portable win-x64 từ CI.");
        Row("OCR bridge", File.Exists(bridge), File.Exists(bridge) ? "convert.py nằm cạnh app." : "Thiếu tools/ocr/convert.py.");
        Row("OCR installer", File.Exists(install), File.Exists(install) ? "Có công cụ đóng gói Python portable." : "Thiếu tools/ocr/install.py.");

        if (Directory.Exists(runtime) && File.Exists(Path.Combine(runtime, "runtime.json")))
        {
            var scan = PortableOcrPackager.Inspect(runtime);
            Row("OCR portable", scan.Ready, scan.Message + $" Dung lượng: {scan.Bytes / 1024d / 1024d / 1024d:0.00} GiB.");
            foreach (var pair in scan.Engines) Row("OCR " + pair.Key, pair.Value, pair.Value ? "model/runtime có mặt." : "chưa sẵn sàng.");
        }
        else Row("OCR portable", false, "Chưa có thư mục ocr-runtime đầy đủ cạnh app. Bản CI tiêu chuẩn không tự nhúng model nhiều GiB; hãy dùng nút Đóng gói 3 OCR vào app trên máy đã cài OCR rồi chép cả thư mục ứng dụng.");

        report.AppendLine();
        report.AppendLine("Kết nối AI:");
        foreach (var profile in app.LocalSettings.Ai.Profiles)
        {
            if (profile.Protocol == AiProtocol.Ollama)
            {
                var local = Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint)
                    && (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
                if (local)
                    Row(profile.Name, false, "Ollama localhost là dịch vụ ngoài app; máy đích phải cài/chạy Ollama và có model " + (profile.Model.Length == 0 ? "đã chọn" : profile.Model) + ". Có thể đổi sang Ollama LAN để dùng chung một máy chủ.");
                else Row(profile.Name, true, profile.ProcessingLocation + ". Cần mạng tới máy chủ/model đã cấu hình.");
                continue;
            }
            var keyOk = false;
            try { keyOk = SecretVault.Read(profile.Id).Length > 0; } catch { }
            Row(profile.Name, keyOk, keyOk
                ? "Khóa API dùng được trên tài khoản Windows hiện tại. DPAPI cố ý không chuyển khóa sang PC khác."
                : "PC này chưa có/không giải mã được khóa API. Sau khi chép app sang PC khác cần nhập lại khóa; project/chat không chứa secret.");
        }

        var localLinks = app.State.Notes.SelectMany(n => n.Projects).SelectMany(p => p.Links)
            .Where(l => Path.IsPathFullyQualified(l.Target)).ToArray();
        Row("Liên kết tệp cục bộ", localLinks.Length == 0,
            localLinks.Length == 0 ? "Không có đường dẫn tuyệt đối trong project links."
                : $"Có {localLinks.Length} liên kết tuyệt đối. Chúng không được app tự sao chép; máy đích cần cùng ổ/đường dẫn hoặc người dùng chọn lại.");
        Row("Dữ liệu dự án", Directory.Exists(app.DataFolder), app.DataFolder + (app.UsesProjectFiles ? " · workspace nhiều máy/NAS." : " · file dữ liệu cục bộ."));

        report.AppendLine();
        report.AppendLine("Cố ý để theo từng máy: khóa API (Windows DPAPI), vị trí cửa sổ, draft chưa gửi, lựa chọn thư mục dữ liệu và dịch vụ Ollama localhost. Những mục này không nên đóng gói chung vì có secret hoặc trạng thái máy.");
        return report.ToString();
    }
}
