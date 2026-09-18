using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using H2AgentLab.Metrics;
using H2AgentLab.Tasking;
using H2Notes.Core;

namespace H2AgentLab;

public sealed class LabWindow : Window
{
    private readonly string _stateRoot;
    private LabSession _session;
    private AiProfile _profile = new();
    private string _key = "";
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 200, PlaceholderText = "Yêu cầu một tác vụ… AI sẽ đọc, đề xuất thao tác và kiểm tra kết quả." };
    private readonly StackPanel _messages = new() { Spacing = 14, Margin = new Thickness(18) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = Label("Sẵn sàng · Chưa gửi dữ liệu", 12);
    private readonly TextBlock _workspaceLabel = Label("", 12);
    private readonly TextBlock _connectionLabel = Label("Chưa chọn model", 12);
    private readonly TextBlock _computerLabel = Label("Chưa cấp quyền cửa sổ", 12);
    private readonly CheckBox _readOnly = new() { Content = "Chỉ đọc", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _send = Button("Gửi yêu cầu", true);
    private readonly Button _stop = Button("Dừng");
    private readonly StackPanel _actions = new() { Spacing = 10 };
    private CancellationTokenSource? _running;
    private WindowTarget? _target;
    private Task? _runTask;
    private bool _historyHealthy = true;
    private AgentInspectionSnapshot? _lastInspection;
    public LabWindow()
    {
        Title = "H2 Agent Lab · Bản thử độc lập"; Width = 1180; Height = 800; MinWidth = 840; MinHeight = 620;
        var evidence = Arg("--evidence");
        _stateRoot = evidence is null ? Arg("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2AgentLab") : Path.Combine(evidence, "private-demo");
        Directory.CreateDirectory(_stateRoot);
        try { _session = LabSession.Load(_stateRoot); }
        catch (Exception ex) { _session = new(); _historyHealthy = false; _status.Text = "Không ghi đè lịch sử lỗi: " + ex.Message; _send.IsEnabled = false; }
        if (string.IsNullOrWhiteSpace(_session.Workspace))
        {
            _session.Workspace = Path.Combine(_stateRoot, "workspace"); Directory.CreateDirectory(_session.Workspace);
        }
        _workspaceLabel.Text = _session.Workspace;
        var connectionFile = Path.Combine(_stateRoot, "connection.json");
        if (File.Exists(connectionFile))
        {
            try { _profile = System.Text.Json.JsonSerializer.Deserialize<AiProfile>(File.ReadAllText(connectionFile)) ?? new(); _connectionLabel.Text = _profile.ProcessingLocation + " · " + _profile.Model + " (khóa API cần nhập lại)"; }
            catch (System.Text.Json.JsonException) { _status.Text = "Cấu hình AI lỗi; nhập lại kết nối. Không đọc hồ sơ H2 Notes."; }
        }
        var logo = new TextBlock { Text = "H2 / AGENT LAB", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brush("#A4573D"), Margin = new Thickness(0, 0, 0, 5) };
        var side = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        side.Children.Add(logo); side.Children.Add(Label("THỬ NGHIỆM ĐỘC LẬP", 11));
        side.Children.Add(Label("Không dùng kho H2 Notes.\nChưa đạt điều kiện tích hợp.", 13));
        side.Children.Add(new Separator()); side.Children.Add(Label("PHẠM VI LÀM VIỆC", 11)); side.Children.Add(_workspaceLabel);
        AddAction("Chọn thư mục…", PickWorkspace); AddAction("Kết nối AI…", Configure); AddAction("Chọn cửa sổ…", PickWindow); AddAction("Mở vùng thử điều khiển", OpenComputerFixture);
        AddAction("Nhật ký & bằng chứng", ShowJournal); AddAction("Tác vụ · tiêu chí · bằng chứng", ShowTaskInspection); AddAction("Tạo dữ liệu mẫu", Seed); AddAction("Cuộc trao đổi mới", NewSession);
        AddAction("Kỹ năng & môi trường", ShowSkills);
        side.Children.Add(_actions); side.Children.Add(_computerLabel); side.Children.Add(new Separator());
        side.Children.Add(Label("KỸ NĂNG HIỆN CÓ", 11));
        side.Children.Add(Label("Tự chọn và đọc skill\nTự viết Python / chạy / sửa lỗi\nExcel · Word · PDF · mã nguồn\nXem ảnh kết quả với model vision\nChạy trên bản sao, lưu có duyệt", 13));
        side.Children.Add(Label("CHƯA NGHIỆM THU", 11));
        side.Children.Add(Label("OCR và dàn trang Word chính xác\nĐiều khiển mọi phần mềm\nSandbox chạy C# / Node\nChất lượng ngang Codex", 12));
        var heading = new StackPanel { Spacing = 5, Margin = new Thickness(22, 15) };
        heading.Children.Add(new TextBlock { Text = "Làm việc cùng AI", FontFamily = new FontFamily("Cambria"), FontSize = 27, FontWeight = FontWeight.Bold });
        heading.Children.Add(Label("Chọn skill → tự viết cách xử lý → chạy thử → kiểm tra kết quả", 13)); heading.Children.Add(_connectionLabel);
        _scroll = new ScrollViewer { Content = _messages };
        var footer = new StackPanel { Spacing = 9, Margin = new Thickness(20, 8, 20, 18) };
        footer.Children.Add(_status); footer.Children.Add(_input);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        bar.Children.Add(_readOnly); Grid.SetColumn(_stop, 1); bar.Children.Add(_stop); Grid.SetColumn(_send, 2); bar.Children.Add(_send); footer.Children.Add(bar);
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        main.Children.Add(heading); Grid.SetRow(_scroll, 1); main.Children.Add(_scroll); Grid.SetRow(footer, 2); main.Children.Add(footer);
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        shell.Children.Add(new Border { Background = Brush("#F6F0E8"), BorderBrush = Brush("#E5DCCF"), BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = side } });
        Grid.SetColumn(main, 1); shell.Children.Add(main); Content = shell;
        _send.Click += async (_, _) => { _runTask = Send(); await _runTask; };
        _stop.IsEnabled = false; _stop.Click += (_, _) => _running?.Cancel();
        foreach (var item in _session.Events.Where(e => e.Kind is "user" or "assistant").TakeLast(20)) Bubble(item.Kind, item.Text, item.At);
        if (_messages.Children.Count == 0) Bubble("assistant", "AI chọn skill phù hợp và tự viết cách làm, không chỉ gọi vài thao tác có sẵn.\n\nVí dụ: sửa font/màu một vùng Excel nhưng giữ công thức; tạo Word có bảng; đọc và render PDF. Chọn thư mục và kết nối AI để bắt đầu.\n\nChỉ đọc: AI được chạy trên bản sao sau khi bạn duyệt, chưa được lưu vào tệp gốc. Python chạy trong Windows AppContainer không mạng. Các bước sửa và chạy lại không hỏi lặp trong cùng lượt.");
        Closing += (_, e) => { if (_running is not null) { e.Cancel = true; _running.Cancel(); _status.Text = "Đang dừng tác vụ; khi dừng xong bạn đóng lại cửa sổ."; } };
        if (evidence is not null) Opened += async (_, _) => await CaptureEvidence(evidence);
        else Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is not { } screen) return;
            var area = screen.WorkingArea;
            MinWidth = Math.Min(MinWidth, area.Width / RenderScaling);
            MinHeight = Math.Min(MinHeight, Math.Max(300, area.Height / RenderScaling - 40));
            Width = Math.Min(Width, area.Width / RenderScaling - 12);
            Height = Math.Min(Height, area.Height / RenderScaling - 48);
            Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)(Width * RenderScaling)) / 2), area.Y + 4);
        };
        if (!_historyHealthy) _actions.IsEnabled = false;
        else if (LabEnvironment.Problems(new SkillCatalog()).Length != 0)
            _status.Text = "Thiếu thành phần chạy · Mở Kỹ năng & môi trường để xem; cần giải nén gói Portable đầy đủ.";
    }
    private static string? Arg(string name) { var i = Array.IndexOf(Program.Arguments, name); return i >= 0 && i + 1 < Program.Arguments.Length ? Program.Arguments[i + 1] : null; }
    private void AddAction(string text, Func<Task> action)
    {
        var b = Button(text); b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Click += async (_, _) => { try { await action(); } catch (Exception ex) { await Message("Chưa thực hiện", ex.Message); } }; _actions.Children.Add(b);
    }
    private void Save()
    {
        if (!_historyHealthy) throw new IOException("Lịch sử cũ lỗi; chưa ghi hoặc thay thế dữ liệu. Dùng kho thử mới để tiếp tục.");
        _session.Save(_stateRoot);
    }
    private async Task Send()
    {
        if (_running is not null || string.IsNullOrWhiteSpace(_input.Text)) return;
        var prompt = _input.Text;
        if (string.IsNullOrWhiteSpace(_profile.Model)) { await Configure(); if (string.IsNullOrWhiteSpace(_profile.Model)) return; }
        if (_profile.ProcessingLocation != "Ollama · chạy trên máy" && !await Confirm(new("Gửi tới model đã chọn", $"{_profile.ProcessingLocation}\n{_profile.BaseUrl}\nModel: {_profile.Model}\n\nLượt này có thể gửi nội dung tệp trong thư mục được chọn, nhật ký gần đây và nội dung cửa sổ bạn duyệt đọc. Có thể phát sinh phí API. Không dùng dữ liệu thật nếu chưa tin endpoint."), CancellationToken.None)) return;
        using var cancel = new CancellationTokenSource(); _running = cancel;
        var telemetry = new AgentRunTelemetry();
        SetBusy(true); _input.Text = ""; Bubble("user", prompt);
        var streamed = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Text = "Đang chờ model…" };
        _messages.Children.Add(streamed); var answer = ""; var thought = "";
        try
        {
            var tools = new AgentTools(new SafeWorkspace(_session.Workspace), _stateRoot, Confirm, (kind, text) => { _session.Add(kind, text); Save(); }) { ReadOnly = _readOnly.IsChecked != false, Computer = _target is null ? null : new(_target) };
            var orchestrated = new AgentOrchestratedRun();
            _lastInspection = await orchestrated.RunAsync(
                _profile,
                _key,
                _session,
                tools,
                prompt,
                (kind, text) =>
                {
                    if (kind == "status") { _status.Text = text; answer = ""; }
                    else if (kind == "trace") { _status.Text = text; }
                    else if (kind == "recovery") { _status.Text = text; streamed.Text = text; answer = ""; }
                    else if (kind == "thinking") { thought += text; _status.Text = "Model đang suy nghĩ…"; streamed.Text = thought.Length > 6000 ? thought[^6000..] : thought; }
                    else if (kind == "thinking-clear") { thought = ""; streamed.Text = answer; }
                    else if (kind == "delta") { answer += text; streamed.Text = answer; }
                    else if (kind == "tool") { Bubble("tool", text.Length > 1600 ? text[..1600] + "\n… Xem đầy đủ trong nhật ký." : text); streamed.Text = "Đang đối chiếu kết quả công cụ…"; }
                    else if (kind == "final") { _messages.Children.Remove(streamed); Bubble("assistant", text); }
                    Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
                },
                Save,
                telemetry,
                _readOnly.IsChecked != false,
                cancel.Token);
            _status.Text = $"Lượt đã kết thúc · {_lastInspection.State} · context {_lastInspection.ActiveContextCharacters:N0} ký tự";
        }
        catch (OperationCanceledException) { streamed.Text = answer; _status.Text = "Đã dừng. Thao tác đã hoàn tất trước khi dừng vẫn giữ trong nhật ký."; _session.Add("cancelled", "User stopped this run."); Save(); }
        catch (Exception ex) { streamed.Text = answer; _status.Text = "Chưa hoàn tất: " + ex.Message; _session.Add("error", ex.Message); try { Save(); } catch (IOException) { } }
        finally
        {
            thought = "";
            try
            {
                var evidence = AgentTraceStore.Save(_stateRoot, telemetry.Trace, telemetry.Metrics);
                _session.Add("telemetry", "Turn metrics: " + Path.GetFileName(evidence));
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _status.Text += " · Không lưu được trace: " + ex.Message;
            }
            _running = null; SetBusy(false);
        }
    }
    private void SetBusy(bool busy) { _send.IsEnabled = !busy; _stop.IsEnabled = busy; _actions.IsEnabled = !busy; _readOnly.IsEnabled = !busy; }
    private async Task PickWorkspace()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Chọn một thư mục cho AI làm việc", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        _ = new SafeWorkspace(path); Save(); _session = new() { Workspace = path }; Save(); _workspaceLabel.Text = path; _messages.Children.Clear(); _target = null; _computerLabel.Text = "Chưa cấp quyền cửa sổ";
        Bubble("assistant", "Đã chuyển phạm vi. Không trộn ngữ cảnh thư mục cũ vào cuộc trao đổi mới.");
    }
    private Task NewSession() { Save(); _session = new() { Workspace = _session.Workspace }; Save(); _messages.Children.Clear(); return Task.CompletedTask; }
    private Task Seed()
    {
        var path = Path.Combine(_session.Workspace, "brief.md");
        if (File.Exists(path)) throw new IOException("brief.md đã tồn tại; không ghi đè.");
        File.WriteAllText(path, "# Dự án kiểm thử\nTên: Cầu Bình Minh\nĐã xong: Khảo sát hiện trường.\nChưa xong: Lập dự toán; gửi hồ sơ nghiệm thu.\nNgười phụ trách: Minh.\nKhông có ngày hoàn thành được xác nhận.\n");
        _session.Add("fixture", "Created brief.md, synthetic data only."); Save();
        _input.Text = "Đọc brief.md, tóm tắt việc chưa xong và tạo ke-hoach.docx rồi kiểm tra cấu trúc. Không tự bịa ngày hoàn thành.";
        _status.Text = "Đã tạo brief.md mẫu; bấm Gửi khi bạn muốn chạy AI."; return Task.CompletedTask;
    }
    private async Task Configure()
    {
        var protocols = new ComboBox { ItemsSource = new[] { "Ollama local / LAN", "API tương thích OpenAI Chat Completions" }, SelectedIndex = _profile.Protocol == AiProtocol.Ollama ? 0 : 1 };
        var endpoint = new TextBox { Text = _profile.BaseUrl, PlaceholderText = "http://localhost:11434 hoặc https://.../v1" };
        var model = new TextBox { Text = _profile.Model, PlaceholderText = "Tên model chính xác" }; var secret = new TextBox { Text = _key, PasswordChar = '●', PlaceholderText = "API key chỉ giữ trong RAM, đóng Lab sẽ xóa" };
        var list = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }; var notice = Label("Không lấy khóa hoặc hồ sơ từ H2 Notes/Codex. Không tự tải model.", 12);
        var refresh = Button("Lấy danh sách model"); var save = Button("Dùng kết nối", true);
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(22), Children = { Label("Kết nối AI riêng của Lab", 23), protocols, endpoint, secret, refresh, list, model, notice, save } };
        var dialog = Dialog("Kết nối AI", panel, 620, 540);
        AiProfile Draft() => new() { Protocol = protocols.SelectedIndex == 0 ? AiProtocol.Ollama : AiProtocol.OpenAiChat, BaseUrl = endpoint.Text ?? "", Model = model.Text ?? "", WaitForCompletion = true };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try { using var client = new AiClient(); list.ItemsSource = await client.ListModels(Draft(), secret.Text ?? ""); notice.Text = "Danh sách từ máy chủ; hỗ trợ tool calling cần kiểm riêng từng model."; }
            catch (Exception ex) { notice.Text = ex.Message; }
            finally { refresh.IsEnabled = true; }
        };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string name) model.Text = name; };
        save.Click += (_, _) =>
        {
            try { var draft = Draft(); _ = AiClient.Endpoint(draft, ""); if (string.IsNullOrWhiteSpace(draft.Model)) throw new IOException("Chọn hoặc nhập model."); File.WriteAllText(Path.Combine(_stateRoot, "connection.json"), System.Text.Json.JsonSerializer.Serialize(draft)); _profile = draft; _key = secret.Text ?? ""; dialog.Close(); _connectionLabel.Text = _profile.ProcessingLocation + " · " + _profile.Model; }
            catch (Exception ex) { notice.Text = ex.Message; }
        };
        await dialog.ShowDialog(this);
    }
    private async Task PickWindow()
    {
        var choices = new ComboBox { ItemsSource = await ComputerTools.List(CancellationToken.None), HorizontalAlignment = HorizontalAlignment.Stretch };
        var select = Button("Cho phép chọn cửa sổ này", true); var clear = Button("Thu hồi quyền cửa sổ");
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { Label("Chỉ một cửa sổ được chọn", 22), Label("Chưa đọc nội dung khi chọn. Mỗi lần đọc/bấm/sửa đều hỏi trước. Không hỗ trợ mọi phần mềm, không tự điều khiển theo tọa độ.", 13), choices, select, clear } };
        var dialog = Dialog("Phạm vi điều khiển", panel, 620, 310);
        select.Click += (_, _) => { if (choices.SelectedItem is WindowTarget t) { _target = t; _computerLabel.Text = t.ToString(); dialog.Close(); } };
        clear.Click += (_, _) => { _target = null; _computerLabel.Text = "Chưa cấp quyền cửa sổ"; dialog.Close(); }; await dialog.ShowDialog(this);
    }
    private Task OpenComputerFixture()
    {
        var box = new TextBox { Name = "FixtureInput", Text = "Bản nháp mẫu", PlaceholderText = "Nội dung thử" };
        var output = Label("Chưa áp dụng", 18); var apply = Button("Áp dụng bản thử");
        Avalonia.Automation.AutomationProperties.SetName(box, "Nội dung thử");
        apply.Click += (_, _) => output.Text = "Đã áp dụng: " + box.Text;
        var win = Dialog("H2 Agent Lab · Vùng thử an toàn", new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = { Label("ĐIỀU KHIỂN THỬ", 20), Label("Không lưu tệp, không gửi mạng. Chọn cửa sổ này trong Lab để thử.", 13), box, apply, output } }, 620, 300);
        win.Show(); return Task.CompletedTask;
    }
    private Task ShowJournal() => Message("Nhật ký có thời gian", string.Join("\n\n", _session.Events.Select(e => $"{e.At.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {e.Kind}\n{e.Text}")));

    private Task ShowTaskInspection()
    {
        if (_lastInspection is null)
            return Message("Tác vụ · tiêu chí · bằng chứng", "Chưa có tác vụ v2 trong phiên UI này.");

        var criteria = string.Join("\n", _lastInspection.Criteria.Select(x =>
            $"- {x.CriterionId}: {x.Requirement} · evidence {x.Evidence.Count}"));
        var trace = string.Join("\n", _lastInspection.TraceEvents.TakeLast(40).Select(x =>
            $"{x.Sequence:00} · {x.Kind} · {x.Code}: {x.Message}"));
        var text =
            $"Task: {_lastInspection.TaskId}\nState: {_lastInspection.State}\nRoute: {_lastInspection.Route}\n"
            + $"Context: {_lastInspection.ActiveContextCharacters:N0} chars · pressure={_lastInspection.ContextUnderPressure}\n"
            + $"Candidate: {_lastInspection.Diagnostics.CandidateContextCharacters:N0} · dropped turns={_lastInspection.Diagnostics.RecentTurnsDropped} · dropped tools={_lastInspection.Diagnostics.ToolSummariesDropped}\n"
            + $"Compaction reasons: {string.Join(", ", _lastInspection.Diagnostics.CompactionReasons)}\n\n"
            + "Acceptance criteria\n" + criteria + "\n\nTyped trace\n" + trace;
        return Message("Tác vụ · tiêu chí · bằng chứng", text);
    }
    private async Task ShowSkills()
    {
        var catalog = new SkillCatalog();
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(Label("Kỹ năng theo cấu trúc Codex", 23));
        panel.Children.Add(Label("AI tự chọn theo yêu cầu; chỉ nạp nội dung khi cần. Word/Excel/PDF được chuyển thể từ skill Codex đã cài, thay các công cụ riêng không có trong Lab.", 13));
        panel.Children.Add(Label(LabEnvironment.Summary(catalog), 13));
        panel.Children.Add(Label("Mang sang máy khác: giải nén toàn bộ gói Portable, không chỉ chép exe. Python đi kèm nằm trong thư mục python cạnh app; không cần Python/.NET cài sẵn. Ollama/model hoặc endpoint API vẫn cần cấu hình riêng. Xem PORTABLE.md.", 13));
        foreach (var skill in catalog.Skills)
        {
            var button = Button(skill.Name + " · Xem skill");
            button.Click += async (_, _) => await Message(skill.Name, catalog.Read(skill.Name, "SKILL.md"));
            panel.Children.Add(button); panel.Children.Add(Label(skill.Description, 12));
        }
        panel.Children.Add(Label("Bản gốc và nguồn: " + Path.Combine(AppContext.BaseDirectory, "skill-sources") + "\nRuntime: " + WindowsPythonSandbox.RuntimeRoot + "\nKhông sao chép tài khoản, API key hoặc bộ máy Codex.", 12));
        await Dialog("Kỹ năng & môi trường", new ScrollViewer { Content = panel }, 760, 640).ShowDialog(this);
    }
    private Task Message(string title, string text) => Dialog(title, new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(15) }, 850, 650).ShowDialog(this);
    private async Task<bool> Confirm(Approval approval, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var yes = Button(approval.Title == "Cho AI tự chạy mã trên bản sao trong lượt này" ? "Cho chạy mã trong lượt này" : "Cho phép một lần", true); var no = Button("Từ chối");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20), RowSpacing = 14 };
        grid.Children.Add(Label(approval.Title, 21)); var preview = new TextBox { Text = approval.Details, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };
        Grid.SetRow(preview, 1); grid.Children.Add(preview); Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        var dialog = Dialog("Duyệt thao tác · H2 Agent Lab", grid, 820, 650);
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }
    private void Bubble(string role, string content, DateTime? at = null)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Label((role == "user" ? "Bạn" : role == "tool" ? "Công cụ · kết quả thực thi" : "AI / Lab") + " · " + (at ?? DateTime.UtcNow).ToLocalTime().ToString("HH:mm"), 11));
        panel.Children.Add(new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 14 });
        _messages.Children.Add(new Border { Background = Brush(role == "user" ? "#F3E4D8" : role == "tool" ? "#F0F1EC" : "#FFFFFF"), BorderBrush = Brush("#E0D6CA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(13), MaxWidth = 760, HorizontalAlignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left, Child = panel });
    }
    private static Window Dialog(string title, Control content, int width, int height) => new() { Title = title, Width = width, Height = height, MinWidth = Math.Min(560, width), MinHeight = Math.Min(300, height), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content };
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string text, bool primary = false) { var button = new Button { Content = text }; if (primary) button.Classes.Add("primary"); return button; }
    private async Task CaptureEvidence(string folder)
    {
        Directory.CreateDirectory(folder); await Task.Delay(400);
        _connectionLabel.Text = "Ollama local · MODEL MẪU · Không gửi AI";
        _messages.Children.Clear(); Bubble("user", "Đổi font một vùng Excel, giữ định dạng còn lại và kiểm tra giúp tôi.");
        Bubble("tool", "MINH HỌA GIAO DIỆN, KHÔNG PHẢI KẾT QUẢ AI\nread_skill → run_python → inspect_artifact → publish_artifact");
        Bubble("assistant", "AI chọn kỹ năng, tự viết mã theo yêu cầu và kiểm kết quả trên bản sao. Chỉ xuất tệp vào thư mục làm việc sau khi bạn duyệt. Mã và kết quả từng lần chạy được lưu để truy vết.");
        foreach (var size in new[] { (1180, 800), (840, 620) })
        {
            Width = size.Item1; Height = size.Item2; await Task.Delay(250);
            using var image = new RenderTargetBitmap(new PixelSize(size.Item1, size.Item2), new Vector(96, 96)); image.Render(this); image.Save(Path.Combine(folder, $"lab-{size.Item1}x{size.Item2}-render96.png"), PngBitmapEncoderOptions.Default);
        }
        File.WriteAllText(Path.Combine(folder, "capture.txt"), $"Release app actual controls; synthetic evidence, no model calls. Native scaling={RenderScaling}. RenderTargetBitmap at 96 DPI, not native screenshot.\n");
        Close();
    }
}
