using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;
using Microsoft.Win32;

namespace H2Notes.Avalonia;

public sealed class SettingsWindow : Window
{
    private bool _importInProgress;
    public SettingsWindow(App app)
    {
        Title = "Cài đặt · H2 Notes"; Icon = app.Icon; Width = 540; SizeToContent = SizeToContent.Height;
        CanResize = false; MaxHeight = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; CanMinimize = CanMaximize = false;
        var settings = app.State.SheetPreferences;
        var aiSettings = new Button { Content = "Thiết lập AI: Ollama / API…", Name = "AiSettingsButton" };
        aiSettings.Click += async (_, _) => { await app.StopAiAsync(); await new AiSettingsWindow(app).ShowDialog(this); };

        var workAssistantEnabled = new CheckBox
        {
            Name = "WorkAssistantEnabled",
            Content = "Bật Work Assistant",
            IsChecked = app.LocalSettings.WorkAssistant.Enabled
        };
        var workAssistantHotkey = new TextBox
        {
            Name = "WorkAssistantHotkey",
            Text = app.LocalSettings.WorkAssistant.Hotkey,
            Watermark = "Ctrl+Shift+Space"
        };
        var workAssistantHotkeyStatus = new TextBlock
        {
            Name = "WorkAssistantHotkeyStatus",
            Text = string.IsNullOrWhiteSpace(app.WorkAssistantHotkeyError)
                ? "Hotkey chỉ mở trợ lý; không tự thực hiện thay đổi."
                : "Hotkey: " + app.WorkAssistantHotkeyError,
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11
        };
        var startup = new CheckBox { Content = "Chạy bản Avalonia khi khởi động Windows", IsChecked = settings.RunOnSystemStart, IsEnabled = OperatingSystem.IsWindows() };
        var restore = new CheckBox { Content = "Khôi phục cửa sổ và bố cục khi mở lại app", IsChecked = settings.RestoreVisibleNotes };
        var snap = new CheckBox { Content = "Bám viền màn hình và viền các ghi chú", IsChecked = settings.SnapWindows };
        var autoTitle = new CheckBox { Name = "AutoHeightTitle", Content = "Dự án / Công việc", IsChecked = settings.AutoHeightTitle };
        var autoProgress = new CheckBox { Name = "AutoHeightProgress", Content = "Tiến độ / Next", IsChecked = settings.AutoHeightProgress };
        var autoComment = new CheckBox { Name = "AutoHeightComment", Content = "Ghi chú", IsChecked = settings.AutoHeightComment };
        var opacity = new Slider { Minimum = 1, Maximum = 100, Value = settings.InactiveOpacity * 100, TickFrequency = 1, IsSnapToTickEnabled = true };
        var label = new TextBlock { Text = $"Độ rõ khi không active: {opacity.Value:0}%" };
        opacity.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) label.Text = $"Độ rõ khi không active: {opacity.Value:0}%"; };
        var save = new Button { Content = "Lưu cài đặt", IsDefault = true };
        var cancel = new Button { Content = "Hủy", IsCancel = true };
        var import = new Button { Name = "ImportLegacyButton", Content = "Nhập từ app cũ…", HorizontalAlignment = HorizontalAlignment.Left };
        var importStatus = new TextBlock { Text = "Chọn file JSON cũ. Nhập thêm thành bản sao riêng, không ghi đè dữ liệu đang dùng.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        var changeFolder = new Button { Content = "Chọn thư mục lưu…", Name = "ChangeDataFolderButton" };
        var defaultFolder = new Button { Content = "Trở về thư mục mặc định" };
        var openFolder = new Button { Content = "Mở thư mục đang dùng" };
        openFolder.Click += (_, _) => { if (Directory.Exists(app.DataFolder)) Process.Start(new ProcessStartInfo(app.DataFolder) { UseShellExecute = true }); };

        var currentWorkspace = app.LocalSettings.WorkspaceLocation;
        var currentWorkspaceText = currentWorkspace is null
            ? "Loại lưu trữ: chưa phân loại"
            : "Loại lưu trữ: " + currentWorkspace.Kind
                + (string.IsNullOrWhiteSpace(currentWorkspace.ResolvedNetworkPath) ? "" : "\nNetwork target: " + currentWorkspace.ResolvedNetworkPath)
                + (currentWorkspace.WorkspaceId is { } workspaceId ? "\nWorkspaceId: " + workspaceId : "");

        string RecoveryStatusText(WorkspaceSyncDiagnostic? diagnostic)
        {
            if (diagnostic is null) return "Chưa có lỗi generation NAS được ghi nhận trên máy này.";
            if (diagnostic.Recovered)
                return "Đã phục hồi generation an toàn. Bản lỗi được cách ly tại: " + (diagnostic.QuarantinePath ?? "(không rõ)");
            if (diagnostic.IsPersistent && diagnostic.RecoveryAvailable)
                return "Phát hiện lỗi generation kéo dài"
                    + (diagnostic.RelativeFile is { Length: > 0 } file ? " ở " + file : "")
                    + ". Máy này có last-known-good hợp lệ để phục hồi có kiểm soát.";
            if (diagnostic.IsPersistent)
                return "Phát hiện lỗi generation kéo dài nhưng máy này chưa có last-known-good hợp lệ.";
            return "Lần đồng bộ gần nhất ghi nhận: " + diagnostic.Code
                + (diagnostic.RelativeFile is { Length: > 0 } relative ? " · " + relative : "");
        }

        var recoveryStatus = new TextBlock
        {
            Text = RecoveryStatusText(app.LastWorkspaceSyncDiagnostic),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            FontSize = 12
        };
        var recoverWorkspace = new Button
        {
            Name = "RecoverWorkspaceButton",
            Content = "Phục hồi từ bản an toàn gần nhất…",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = app.LastWorkspaceSyncDiagnostic is { IsPersistent: true, RecoveryAvailable: true, Recovered: false }
        };
        recoverWorkspace.Click += async (_, _) =>
        {
            var current = app.LastWorkspaceSyncDiagnostic;
            var detail = current?.RelativeFile is { Length: > 0 } file ? "\n\nTệp phát hiện lệch generation: " + file : "";
            if (!await Dialogs.Confirm(this, "Phục hồi kho NAS?",
                    "H2 Notes sẽ sao chép đầy đủ generation lỗi vào quarantine cục bộ trước, sau đó mới khôi phục bản last-known-good đã kiểm hash."
                    + detail + "\n\nKhông sửa hash để hợp thức hóa dữ liệu lỗi.", "Cách ly và phục hồi")) return;

            recoverWorkspace.IsEnabled = false;
            var diagnostic = await app.RecoverSharedWorkspaceAsync();
            recoveryStatus.Text = RecoveryStatusText(diagnostic);
            recoverWorkspace.IsEnabled = diagnostic is { IsPersistent: true, RecoveryAvailable: true, Recovered: false };
            if (diagnostic.Recovered)
                await Dialogs.Message(this, "Đã phục hồi kho",
                    "Generation lỗi đã được giữ lại để kiểm tra.\n\nQuarantine:\n" + (diagnostic.QuarantinePath ?? "(không rõ)"));
            else
                await Dialogs.Message(this, "Chưa phục hồi kho", diagnostic.Message);
        };

        async Task ChangeFolder(string? target = null)
        {
            if (_importInProgress) return;
            _importInProgress = true;
            FileStream? targetLock = null;
            try
            {
                await app.StopAiAsync();
                app.SaveNow();
                if (app.LastSaveError is not null) throw new IOException(app.LastSaveError);
                if (target is null)
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Chọn thư mục dữ liệu H2 Notes", AllowMultiple = false });
                    if (folders.Count == 0) return;
                    using var selected = folders[0]; target = selected.TryGetLocalPath() ?? throw new IOException("Cần thư mục Windows truy cập được (local, mapped network hoặc UNC).");
                }
                ProjectWorkspaceStore.ValidateDestination(app.DataFolder, target);
                var destination = new ProjectWorkspaceStore(target); targetLock = destination.AcquireLock();
                var existing = await Task.Run(() => destination.LoadOrImport());
                var transfer = new WorkspaceTransfer(app.State, existing);
                var hasData = existing.Notes.Count > 0;
                var location = destination.Location;
                var network = string.IsNullOrWhiteSpace(location.ResolvedNetworkPath) ? "" : $"\nNetwork target: {location.ResolvedNetworkPath}";
                var identity = destination.WorkspaceId == Guid.Empty ? "chưa tạo" : destination.WorkspaceId.ToString();
                var summary = $"Đang dùng: {app.DataFolder}\nĐích: {target}\nLoại: {location.Kind}{network}\nWorkspaceId: {identity}\n\nHiện tại: {transfer.SourceProjects} dự án, {transfer.SourceNotes} note.\nỞ đích: {transfer.DestinationProjects} dự án, {transfer.DestinationNotes} note.\nXung đột cần chọn: {transfer.Conflicts.Count}.\n\nBản sao lưu: {Path.Combine(target, "backups")}\nThư mục cũ luôn được giữ nguyên.";
                var choice = hasData ? await ChoiceDialog.Show(this, "Thư mục đã có dữ liệu", summary,
                    ("merge", "Đồng bộ", "Hợp nhất hai kho. Chọn cách xử lý từng xung đột."),
                    ("overwrite", "Ghi đè", "Thay dữ liệu H2 Notes ở đích bằng dữ liệu hiện tại; có sao lưu và xác nhận riêng."),
                    ("existing", "Không làm gì", "Dùng dữ liệu đã có ở đích. Không sao chép hoặc ghi đè."))
                    : await ChoiceDialog.Show(this, "Đồng bộ sang thư mục mới?", summary,
                    ("merge", "Đồng bộ sang", "Sao chép dữ liệu hiện tại sang thư mục này."),
                    ("existing", "Không đồng bộ", "Dùng kho mới trống. Dữ liệu cũ vẫn ở thư mục cũ."));
                if (choice is null) return;
                var mode = choice == "overwrite" ? WorkspaceTransferMode.Overwrite : choice == "existing" ? WorkspaceTransferMode.UseExisting : WorkspaceTransferMode.Merge;
                if (mode == WorkspaceTransferMode.Overwrite && !await Dialogs.Confirm(this, "Xác nhận ghi đè kho", "Sao lưu và thay toàn bộ dữ liệu H2 Notes ở:\n" + target + "\n\nCác tệp không thuộc H2 Notes không bị thay đổi.", "Sao lưu và ghi đè")) return;
                Dictionary<Guid, ConflictResolution> resolutions = [];
                if (mode == WorkspaceTransferMode.Merge)
                    foreach (var conflict in transfer.Conflicts)
                    {
                        var answer = await ChoiceDialog.Show(this, "Xung đột dữ liệu", conflict.Title + "\nHai phiên bản có nội dung khác nhau. Không tự ghép rich text hoặc lịch sử chat.",
                            ("both", "Giữ cả hai", "Giữ bản ở đích và thêm bản sao đầy đủ từ kho hiện tại; xung đột bảng sẽ sao chép cả bảng."),
                            ("current", "Giữ bản hiện tại", "Dùng nội dung từ thư mục đang làm việc."),
                            ("destination", "Giữ bản ở đích", "Giữ nguyên nội dung đã có trong thư mục mới."));
                        if (answer is null) return;
                        resolutions[conflict.Id] = answer == "both" ? ConflictResolution.KeepBoth : answer == "current" ? ConflictResolution.KeepCurrent : ConflictResolution.KeepDestination;
                    }
                app.SwitchWorkspace(destination, targetLock, existing, transfer, mode, resolutions);
                targetLock = null; _importInProgress = false; Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { await Dialogs.Message(this, "Chưa đổi thư mục lưu", ex.Message + "\n\nKho nguồn được giữ nguyên."); }
            finally { targetLock?.Dispose(); _importInProgress = false; }
        }
        changeFolder.Click += async (_, _) => await ChangeFolder();
        defaultFolder.Click += async (_, _) => await ChangeFolder(LocalConfiguration.DefaultDataFolder);
        LegacyImportRecord? lastImported = null;
        Closing += (_, e) => { if (_importInProgress && !app.IsChangingStore) e.Cancel = true; };
        Closed += (_, _) => { if (lastImported is not null) app.ShowImported(lastImported); };
        import.Click += async (_, _) =>
        {
            if (_importInProgress) return;
            _importInProgress = true; import.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
            var previousStatus = importStatus.Text;
            try
            {
                if (!StorageProvider.CanOpen) throw new InvalidOperationException("Không mở được hộp chọn file trên máy này.");
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Chọn file JSON lưu từ H2 Notes cũ", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Dữ liệu H2 Notes (*.json)") { Patterns = ["*.json"] }]
                });
                if (files.Count == 0) return;
                using var selected = files[0];
                var source = selected.TryGetLocalPath() ?? throw new InvalidDataException("Hãy chọn file JSON đã lưu trên máy tính.");
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(app.DataPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Đây là file dữ liệu đang dùng. Hãy chọn file từ app cũ.");
                importStatus.Text = "Đang kiểm tra và chuyển đổi file…";
                var preview = await Task.Run(() => LegacyImport.Prepare(source));
                if (LegacyImport.AlreadyImported(app.State, preview))
                {
                    await Dialogs.Message(this, "File đã được nhập", "Nội dung file này đã được nhập trước đó. Không tạo thêm bản trùng.");
                    return;
                }
                var summary = $"File: {Path.GetFileName(source)}\n\n{preview.Boards} bảng dự án · {preview.Projects} dự án · {preview.Tasks} công việc\n{preview.GeneralNotes} ghi chú thường · {preview.Archived} ghi chú lưu trữ\n\n"
                    + "Nhập thêm thành bản sao riêng. Giữ nội dung, trạng thái checklist và các định dạng chữ được hỗ trợ. Không thay đổi cài đặt hoặc ghi đè ghi chú hiện tại.\n\nFile gốc được giữ nguyên; app tạo bản sao lưu trước khi nhập.";
                if (!await Dialogs.Confirm(this, "Xác nhận nhập dữ liệu", summary, "Nhập dữ liệu")) return;
                importStatus.Text = "Đang sao lưu và lưu dữ liệu mới…";
                lastImported = app.ImportLegacy(preview);
                previousStatus = $"Đã nhập {preview.Projects} dự án, {preview.Tasks} công việc và {preview.GeneralNotes} ghi chú thường. Đóng Cài đặt để xem dữ liệu vừa nhập.";
                await Dialogs.Message(this, "Nhập thành công", previousStatus + "\n\nBản sao lưu trước khi nhập và file nguồn:\n" + lastImported.BackupDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
            {
                await Dialogs.Message(this, "Chưa nhập được dữ liệu", ex.Message + "\n\nFile nguồn không bị thay đổi. Dữ liệu đang dùng được giữ nguyên.");
            }
            finally
            {
                importStatus.Text = previousStatus;
                _importInProgress = false; import.IsEnabled = save.IsEnabled = cancel.IsEnabled = true;
            }
        };
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) =>
        {
            try
            {
                if (OperatingSystem.IsWindows() && settings.RunOnSystemStart != (startup.IsChecked == true))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    if (startup.IsChecked == true)
                    {
                        var executable = Path.Combine(AppContext.BaseDirectory, "H2Notes.Avalonia.exe");
                        if (!File.Exists(executable)) throw new IOException("Không tìm thấy executable để đăng ký khởi động.");
                        key.SetValue("H2Notes.Avalonia", $"\"{executable}\" --startup");
                    }
                    else key.DeleteValue("H2Notes.Avalonia", false);
                }
                WorkAssistantHotkey parsedHotkey = default;
                var hotkeyParseError = "";
                if (workAssistantEnabled.IsChecked == true
                    && !WorkAssistantHotkeyParser.TryParse(
                        workAssistantHotkey.Text,
                        out parsedHotkey,
                        out hotkeyParseError))
                {
                    workAssistantHotkeyStatus.Text = "Hotkey: " + hotkeyParseError;
                    return;
                }

                app.LocalSettings.WorkAssistant.Enabled = workAssistantEnabled.IsChecked == true;
                if (workAssistantEnabled.IsChecked == true)
                    app.LocalSettings.WorkAssistant.Hotkey = parsedHotkey.Normalized;
                else if (!string.IsNullOrWhiteSpace(workAssistantHotkey.Text))
                    app.LocalSettings.WorkAssistant.Hotkey = workAssistantHotkey.Text.Trim();
                app.ApplyWorkAssistantSettings();
                if (workAssistantEnabled.IsChecked == true
                    && !string.IsNullOrWhiteSpace(app.WorkAssistantHotkeyError))
                {
                    workAssistantHotkeyStatus.Text = "Hotkey: " + app.WorkAssistantHotkeyError;
                    return;
                }

                settings.RunOnSystemStart = startup.IsChecked == true; settings.RestoreVisibleNotes = restore.IsChecked == true;
                settings.SnapWindows = snap.IsChecked == true; settings.InactiveOpacity = opacity.Value / 100;
                settings.AutoHeightTitle = autoTitle.IsChecked == true;
                settings.AutoHeightProgress = autoProgress.IsChecked == true;
                settings.AutoHeightComment = autoComment.IsChecked == true;
                foreach (var note in app.State.Notes) note.NoteOpacity = settings.InactiveOpacity;
                app.SaveNow(); Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { await Dialogs.Message(this, "Chưa lưu được cài đặt", ex.Message); }
        };
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            new TextBlock { Text = "Cài đặt", FontSize = 24, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = "Dữ liệu và lưu trữ", FontSize = 18, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = app.DataFolder, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            new TextBlock { Text = currentWorkspaceText, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            changeFolder, defaultFolder, openFolder,
            recoverWorkspace, recoveryStatus,
            aiSettings,
            new Separator(),
            new TextBlock { Text = "Work Assistant", FontSize = 18, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            workAssistantEnabled,
            new TextBlock { Text = "Global hotkey", FontSize = 12, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            workAssistantHotkey,
            workAssistantHotkeyStatus,
            import, importStatus, new Separator(),
            startup, restore, snap, new Separator(), label, opacity,
            new TextBlock { Text = "Cửa sổ đang active luôn rõ 100%. Click ra ngoài không ẩn cửa sổ.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            new Separator(), new TextBlock { Text = "Tự giãn chiều cao hàng theo cột", FontSize = 17, FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            autoTitle, autoProgress, autoComment,
            new TextBlock { Text = "Bật: hiện đủ chữ, hàng tự cao lên. Tắt: giữ chiều cao do các cột còn lại quyết định, chữ dư hiện dấu … Hover vào ô để đọc đầy đủ. STT / ô tích luôn gọn, không cần tự giãn.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            new Separator(), new TextBlock { Text = "Dữ liệu bản thử (độc lập với WPF):\n" + app.DataPath, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            new TextBlock { Text = app.LastSaveError ?? "Giữ nguyên bản sao nguồn và một bản sao lưu lần lưu trước.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
        } } };
    }
}
