using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace H2AgentLab;

public sealed record WindowTarget(int Handle, int Pid, long Started, string Title)
{
    public override string ToString() => Title + " · PID " + Pid;
}
public sealed record ControlInfo(string Token, int[] RuntimeId, string Name, string Type, string Value, bool CanInvoke, bool CanSetValue);
public sealed record ComputerRequest(string Action, WindowTarget? Window = null, ControlInfo? Control = null, string Text = "");

public sealed class ComputerTools(WindowTarget target)
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "OpenConsole", "conhost", "wsl", "bash",
        "Codex", "ChatGPT", "Code", "devenv", "consent", "CredentialUIBroker", "SystemSettings", "SecHealthUI",
        "LogonUI", "Taskmgr", "regedit", "mmc", "mstsc", "1Password", "Bitwarden", "KeePass", "KeePassXC", "NordPass", "LastPass"
    };
    public static bool IsWindowAllowed(string processName, string title) =>
        !BlockedProcesses.Contains(processName)
        && (!processName.Equals("H2AgentLab", StringComparison.OrdinalIgnoreCase) || title == "H2 Agent Lab · Vùng thử an toàn")
        && !new[] { "password", "credential", "security", "mật khẩu", "bảo mật", "quyền truy cập" }
            .Any(word => title.Contains(word, StringComparison.OrdinalIgnoreCase));
    private readonly Dictionary<string, ControlInfo> _observed = [];
    private DateTime _snapshotAt;
    public WindowTarget Target => target;
    public async Task<object> Inspect(Func<Approval, CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        if (!await approve(new("Đọc cửa sổ đã chọn", target + "\nNội dung giao diện có thể gửi tới model hiện chọn. Không chụp toàn màn hình, không đọc ô mật khẩu."), ct)) throw new AgentFaultException("denied", "Chưa được phép đọc cửa sổ.", false);
        var result = await Call(new("inspect", target), ct);
        var nodes = result.Deserialize<List<ControlInfo>>() ?? [];
        _observed.Clear(); foreach (var n in nodes) _observed[n.Token] = n; _snapshotAt = DateTime.UtcNow;
        return new { window = target.Title, controls = nodes, maxAgeSeconds = 60, truncatedAt = 150 };
    }
    public async Task<object> Act(string action, string token, string text, Func<Approval, CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        if (DateTime.UtcNow - _snapshotAt > TimeSpan.FromSeconds(60) || !_observed.TryGetValue(token, out var control)) throw new AgentFaultException("stale_state", "Cần đọc lại cửa sổ; mã điều khiển đã hết hạn.");
        if (text.Length > 10000) throw new IOException("Text quá dài.");
        if (action == "click_control" ? !control.CanInvoke : !control.CanSetValue) throw new IOException("Điều khiển không hỗ trợ thao tác này.");
        var preview = $"Cửa sổ: {target}\nĐiều khiển: {control.Name} ({control.Type})\n" + (action == "type_control" ? "Giá trị cũ: " + control.Value + "\nThay bằng: " + text : "Kích hoạt nút này. Có thể gửi, xóa hoặc thay đổi dữ liệu trong ứng dụng đó. Chỉ duyệt đúng tác vụ bạn yêu cầu.");
        if (!await approve(new(action == "type_control" ? "Thay nội dung ô nhập" : "Bấm điều khiển", preview), ct)) throw new AgentFaultException("denied", "Người dùng từ chối.", false);
        ct.ThrowIfCancellationRequested(); _observed.Clear();
        return await Call(new(action, target, control, text), ct);
    }
    public static async Task<List<WindowTarget>> List(CancellationToken ct) => (await Call(new("list"), ct)).Deserialize<List<WindowTarget>>() ?? [];
    private static async Task<JsonElement> Call(ComputerRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--computer-worker");
        using var process = Process.Start(start) ?? throw new IOException("Không mở được bộ điều khiển.");
        using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)); process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new IOException(await error);
        using var json = JsonDocument.Parse(await output); return json.RootElement.Clone();
    }

    // Runs in a disposable worker: a hung UIA provider cannot freeze the chat UI.
    public static int Worker()
    {
        try
        {
            var request = JsonSerializer.Deserialize<ComputerRequest>(Console.ReadLine() ?? "") ?? throw new IOException("Bad request");
            object result;
            if (request.Action == "list")
            {
                var windows = new List<WindowTarget>();
                foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    try
                    {
                        var info = e.Current;
                        if (info.NativeWindowHandle == 0 || info.IsOffscreen || string.IsNullOrWhiteSpace(info.Name)) continue;
                        using var p = Process.GetProcessById(info.ProcessId);
                        if (!IsWindowAllowed(p.ProcessName, info.Name)) continue;
                        windows.Add(new(info.NativeWindowHandle, info.ProcessId, p.StartTime.ToUniversalTime().Ticks, info.Name));
                        if (windows.Count == 80) break;
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                result = windows;
            }
            else
            {
                var target = request.Window ?? throw new IOException("No selected window");
                using var p = Process.GetProcessById(target.Pid);
                if (p.StartTime.ToUniversalTime().Ticks != target.Started) throw new IOException("Cửa sổ đã đóng hoặc tiến trình đã đổi.");
                if (!IsWindowAllowed(p.ProcessName, target.Title)) throw new IOException("Lab không điều khiển terminal, IDE, Codex, cửa sổ duyệt quyền hoặc ứng dụng bảo mật.");
                var root = AutomationElement.FromHandle(new IntPtr(target.Handle));
                if (root.Current.ProcessId != target.Pid || root.Current.Name != target.Title) throw new IOException("Cửa sổ đổi tên/phạm vi; hãy chọn lại.");
                var nodes = Walk(root).ToArray();
                if (request.Action == "inspect") result = nodes.Select(e => Describe(e)).Where(e => e is not null).Take(150).ToArray();
                else
                {
                    var old = request.Control ?? throw new IOException("Missing control");
                    var e = nodes.FirstOrDefault(e => e.GetRuntimeId().SequenceEqual(old.RuntimeId)) ?? throw new IOException("Điều khiển không còn tồn tại; đọc lại.");
                    var current = Describe(e) ?? throw new IOException("Không đọc/sửa ô mật khẩu.");
                    if (current.Name != old.Name || current.Type != old.Type || current.Value != old.Value || !e.Current.IsEnabled || e.Current.IsOffscreen)
                        throw new IOException("Điều khiển đã thay đổi sau khi xem; chưa thao tác.");
                    if (request.Action == "click_control" && current.CanInvoke)
                    {
                        ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        result = new { invoked = true, verifiedTaskOutcome = false, next = "Inspect the resulting UI; do not claim business success just from invoke." };
                    }
                    else if (request.Action == "type_control" && current.CanSetValue)
                    {
                        var value = (ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern); value.SetValue(request.Text);
                        result = new { valueSet = value.Current.Value == request.Text };
                    }
                    else throw new IOException("Thao tác chưa hỗ trợ, không tự đoán tọa độ hoặc gửi phím.");
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    private static IEnumerable<AutomationElement> Walk(AutomationElement root)
    {
        var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((root, 0)); var count = 0;
        while (pending.Count > 0 && count++ < 500)
        {
            var (element, depth) = pending.Dequeue();
            if (element.Current.IsPassword) continue;
            yield return element;
            if (depth >= 12) continue;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element); var siblings = 0;
            while (child is not null && siblings++ < 150)
            { pending.Enqueue((child, depth + 1)); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
        }
    }
    private static ControlInfo? Describe(AutomationElement e)
    {
        var state = e.Current; if (state.IsPassword) return null;
        var value = ""; var canSet = false;
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
        { var info = ((ValuePattern)v).Current; value = info.Value; canSet = !info.IsReadOnly; }
        else if (e.TryGetCurrentPattern(TextPattern.Pattern, out var text)) value = ((TextPattern)text).DocumentRange.GetText(1500);
        return new(Guid.NewGuid().ToString("N"), e.GetRuntimeId(), state.Name[..Math.Min(300, state.Name.Length)], state.ControlType.ProgrammaticName,
            value[..Math.Min(1500, value.Length)], e.TryGetCurrentPattern(InvokePattern.Pattern, out _), canSet && value.Length <= 1500);
    }
}
