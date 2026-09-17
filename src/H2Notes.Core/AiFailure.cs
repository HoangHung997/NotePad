using System.Net;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

public sealed class AiServiceException(string code, string message, HttpStatusCode? status = null)
    : HttpRequestException(message, null, status)
{
    public string Code { get; } = code;
}

public static class AiFailure
{
    public static string Describe(Exception error) => error switch
    {
        AiServiceException service => service.Message,
        OperationCanceledException => "Đã dừng hoặc quá thời gian chờ. Phần trả lời đã nhận được giữ lại.",
        HttpRequestException { StatusCode: { } status } => FromStatus(status, "").Message,
        HttpRequestException => "Không kết nối được máy chủ AI. Kiểm tra địa chỉ, mạng và Ollama có đang chạy không. Không tự gửi lại.",
        InvalidOperationException => "Cấu hình AI chưa hợp lệ. Kiểm tra địa chỉ, giao thức và model trong Thiết lập AI.",
        _ => "Phản hồi chưa hoàn tất hoặc không đúng giao thức. Phần đã nhận được giữ lại; không tự gửi lại."
    };

    public static AiServiceException FromStatus(HttpStatusCode status, string providerError)
    {
        // Interpret known error categories, never display/retain the server body:
        // custom endpoints may echo a prompt, URL credentials, or API key.
        var extraUsage = providerError.Contains("extra usage", StringComparison.OrdinalIgnoreCase);
        var restrictedModel = providerError.Contains("no longer available to new users", StringComparison.OrdinalIgnoreCase);
        var (code, message) = (int)status switch
        {
            400 => ("invalid_request", "Máy chủ từ chối yêu cầu. Kiểm tra giao thức, tên model và dữ liệu gửi. Nếu có ảnh, model phải hỗ trợ đọc ảnh; app không tự đổi model hay bỏ ảnh."),
            401 => ("authentication", "Khóa API hoặc phiên đăng nhập không hợp lệ. Với Ollama Cloud, kiểm tra đăng nhập Ollama; với API online, kiểm tra khóa đã lưu."),
            402 => ("payment", extraUsage ? "Model cloud yêu cầu Extra Usage; số dư sử dụng bổ sung hiện không đủ. Đây không phải model chạy trên máy. Chọn model khác hoặc tự kiểm tra tài khoản nhà cung cấp."
                : "Máy chủ yêu cầu thanh toán hoặc số dư sử dụng cho model này. Kiểm tra hạn mức/tài khoản hoặc chọn model khác."),
            403 => ("permission", "Tài khoản/khóa hiện chưa được phép gọi model này. Kiểm tra quyền truy cập hoặc gói sử dụng."),
            404 => ("model_unavailable", restrictedModel ? "Model này không còn nhận người dùng mới qua API. Hãy chọn model mới hơn rồi thử lại; lấy được danh sách model không có nghĩa tài khoản gọi được model đó."
                : "Model hoặc đường dẫn API không khả dụng với kết nối này. Kiểm tra tên model/địa chỉ; model có trong danh sách vẫn có thể bị giới hạn quyền truy cập."),
            429 => ("quota", "Đã chạm hạn mức hoặc gửi quá nhanh. Kiểm tra quota và thời gian đặt lại của nhà cung cấp; app không tự gửi lại."),
            >= 500 => ("server", "Máy chủ AI đang lỗi hoặc quá tải. Phần đã nhận được giữ lại; hãy thử lại sau."),
            _ => ("http", "Máy chủ từ chối yêu cầu. Kiểm tra địa chỉ, giao thức và cấu hình AI.")
        };
        return new(code, $"HTTP {(int)status}: {message}", status);
    }

    internal static async Task CheckResponse(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string providerError = "";
        try
        {
            var bytes = new byte[16384]; var used = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            while (used < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(used), token);
                if (read == 0) break; used += read;
            }
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 0, used));
            if (json.RootElement.TryGetProperty("error", out var error))
                providerError = error.ValueKind == JsonValueKind.String ? error.GetString() ?? ""
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        throw FromStatus(response.StatusCode, providerError);
    }
}
