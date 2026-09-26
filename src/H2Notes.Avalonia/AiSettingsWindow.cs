using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed partial class AiSettingsWindow : Window
{
    public AiSettingsWindow(App app)
    {
        Title = "Thiết lập AI · H2 Notes"; Width = 620; Height = 720; MinHeight = 540;
        ShowInTaskbar = CanMinimize = CanMaximize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var settings = app.LocalSettings.Ai;
        var ollamaTab=new Button { Content="Ollama",Classes={"workspace-tab"} };var apiTab=new Button { Content="API online",Classes={"workspace-tab"} };
        var profiles = new ComboBox { ItemsSource = settings.Profiles, HorizontalAlignment = HorizontalAlignment.Stretch };
        var presets = new ComboBox { ItemsSource = new[] { "Ollama trên máy", "OpenAI", "Google Gemini", "DeepSeek", "API tùy chỉnh" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var add = new Button { Content = "Thêm kết nối" };
        var name = new TextBox(); var url = new TextBox { Name = "AiServerUrl", PlaceholderText = "http://localhost:11434 hoặc http://192.168.1.208:11434" };
        var model = new TextBox { PlaceholderText = "Tên model chính xác hoặc chọn từ danh sách" };
        var addressHint = new TextBlock { Name = "AiServerHint", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = RichEditor.Brush("#796C62") };
        var protocol = new ComboBox { ItemsSource = Enum.GetValues<AiProtocol>(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var key = new TextBox { PasswordChar = '●', PlaceholderText = "Khóa API được mã hóa bằng tài khoản Windows" };
        var reasoningSummary = new CheckBox { Name = "AiReasoningSummary", Content = "Yêu cầu bản tóm tắt suy nghĩ từ API", FontSize = 12 };
        var waitForCompletion = new CheckBox { Name = "AiWaitForCompletion", Content = "Chờ AI hoàn tất, không tự ngắt vì phản hồi chậm", FontSize = 12 };
        ToolTip.SetTip(waitForCompletion, "Áp dụng cho chat local, LAN và API. Kể cả khi model nạp lâu hoặc tạm ngừng gửi suy nghĩ, app tiếp tục chờ đến khi xong hoặc bạn bấm Dừng. Không tự thử lại khi mất kết nối. Tắt: ngắt sau thời gian im lặng đã cấu hình (mặc định 180 giây), không giới hạn tổng thời gian.");
        var ollamaThinking = new ComboBox { Name = "AiOllamaThinking", ItemsSource = new[] { "Suy nghĩ: theo mặc định model", "Bật suy nghĩ (model có hỗ trợ)", "Tắt suy nghĩ" }, SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        ToolTip.SetTip(ollamaThinking, "Chỉ bật với model có hỗ trợ thinking. Có thể cần thêm RAM và thời gian; nếu máy chủ từ chối, chọn lại theo mặc định model.");
        ToolTip.SetTip(reasoningSummary, "Chỉ bật với model hỗ trợ trên OpenAI Responses/Gemini chính thức. Model khác vẫn hiện tiến trình nếu máy chủ tự gửi; không yêu cầu suy nghĩ ẩn.");
        var models = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Danh sách model từ máy chủ" };
        var list = new Button { Content = "Lấy danh sách model" }; var load = new Button { Content = "Nạp model" }; var unload = new Button { Content = "Giải phóng RAM" };
        var test = new Button { Name = "TestAiConnection", Content = "Thử kết nối" };
        ToolTip.SetTip(test, "Gửi câu thử ngắn tới cấu hình đang nhập. Có thể tính phí API; không gửi dữ liệu dự án, không lưu hội thoại.");
        var save = new Button { Content = "Lưu kết nối", Classes = { "accent" } };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AiProfile? selected = null; string oldEndpoint = ""; string oldKey = "";
        var cancel = new CancellationTokenSource(); Closed += (_, _) => cancel.Cancel();
        void Select()
        {
            selected = profiles.SelectedItem as AiProfile;
            list.IsEnabled=test.IsEnabled=load.IsEnabled=unload.IsEnabled=save.IsEnabled=selected is not null;
            if (selected is null)
            {
                name.Text=url.Text=model.Text=key.Text=oldEndpoint=oldKey="";models.ItemsSource=null;return;
            }
            name.Text = selected.Name; url.Text = oldEndpoint = selected.BaseUrl; model.Text = selected.Model; protocol.SelectedItem = selected.Protocol;
            reasoningSummary.IsChecked = selected.RequestReasoningSummary;
            waitForCompletion.IsChecked = selected.WaitForCompletion;
            ollamaThinking.SelectedIndex = selected.OllamaThinking switch { true => 1, false => 2, _ => 0 };
            try { key.Text = oldKey = SecretVault.Read(selected.Id); } catch (Exception) { key.Text = oldKey = ""; status.Text = "Không giải mã được khóa cũ trên tài khoản này. Hãy nhập lại khóa."; }
            models.ItemsSource = null;
        }
        AiProfile Draft()
        {
            if (selected is null) throw new InvalidOperationException("Chọn một kết nối trước.");
            var result = new AiProfile { Id = selected.Id, Name = name.Text?.Trim() ?? "", BaseUrl = url.Text?.Trim() ?? "", Model = model.Text?.Trim() ?? "", Protocol = protocol.SelectedItem is AiProtocol value ? value : AiProtocol.Ollama, TimeoutSeconds = selected.TimeoutSeconds,
                RequestReasoningSummary = reasoningSummary.IsVisible && reasoningSummary.IsChecked == true,
                WaitForCompletion = waitForCompletion.IsChecked == true,
                ReasoningEffort = selected.ReasoningEffort,
                RequestBudget = selected.RequestBudget,
                OllamaThinking = ollamaThinking.IsVisible ? ollamaThinking.SelectedIndex switch { 1 => true, 2 => false, _ => (bool?)null } : null };
            AiClient.Endpoint(result, "models");
            if (result.Name.Length == 0) throw new InvalidOperationException("Nhập tên kết nối.");
            if (!string.Equals(oldEndpoint, result.BaseUrl, StringComparison.OrdinalIgnoreCase) && oldKey.Length > 0 && key.Text == oldKey)
                throw new InvalidOperationException("Đã đổi địa chỉ máy chủ. Xóa hoặc nhập lại khóa dành cho địa chỉ mới trước khi gửi.");
            return result;
        }
        async Task Run(Func<AiClient, AiProfile, Task> action)
        {
            list.IsEnabled = test.IsEnabled = load.IsEnabled = unload.IsEnabled = save.IsEnabled = profiles.IsEnabled = add.IsEnabled = false;
            status.Text = "Đang kết nối…";
            try { using var client = new AiClient(); await action(client, Draft()); }
            catch (OperationCanceledException) { status.Text = "Đã dừng hoặc quá thời gian chờ."; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or System.Text.Json.JsonException)
            { status.Text = ex is HttpRequestException ? AiFailure.Describe(ex) : ex.Message; }
            finally
            {
                profiles.IsEnabled=add.IsEnabled=true;
                list.IsEnabled=test.IsEnabled=load.IsEnabled=unload.IsEnabled=save.IsEnabled=selected is not null;
            }
        }
        profiles.SelectionChanged += (_, _) => Select();
        void UpdateAddressHint()
        {
            var local = protocol.SelectedItem is AiProtocol.Ollama;
            load.IsVisible = unload.IsVisible = local; key.IsVisible = !local;
            reasoningSummary.IsVisible = protocol.SelectedItem is AiProtocol.OpenAiResponses or AiProtocol.Gemini;
            ollamaThinking.IsVisible = local;
            addressHint.Text = !local ? "API online dùng HTTPS. Đổi máy chủ cần nhập lại khóa tương ứng."
                : "Ollama nhận localhost hoặc IP mạng LAN, ví dụ http://192.168.1.208:11434. HTTP không mã hóa; chỉ dùng trong mạng tin cậy. Máy chủ cần cho phép kết nối từ máy này.";
        }
        protocol.SelectionChanged += (_, _) => UpdateAddressHint();
        models.SelectionChanged += (_, _) => { if (models.SelectedItem is string value) model.Text = value; };
        list.Click += async (_, _) => await Run(async (client, draft) =>
        {
            models.ItemsSource = await client.ListModels(draft, key.Text ?? "", cancel.Token);
            status.Text = "Đã tải danh sách. Chọn model rồi bấm Thử kết nối để kiểm tra gửi tin.";
            if (draft.Protocol == AiProtocol.Ollama) { var loaded = await client.LoadedOllamaModels(draft, cancel.Token); status.Text += "\nĐang nạp trong RAM: " + (loaded.Count == 0 ? "chưa có model" : string.Join(", ", loaded)); }
        });
        test.Click += async (_, _) => await Run(async (client, draft) =>
        {
            if (!await Dialogs.Confirm(this, "Thử kết nối AI", draft.ProcessingLocation + "\nModel: " + draft.Model
                + "\n\nChỉ gửi: Reply with exactly OK. No explanation.\nKhông gửi dữ liệu dự án hoặc lưu vào lịch sử. API/cloud có thể tính phí.", "Gửi câu thử")) { status.Text = "Đã hủy thử kết nối."; return; }
            var chars = 0;
            await foreach (var part in client.Stream(draft, key.Text ?? "", [new("user", "Reply with exactly OK. No explanation.")], cancel.Token)) chars += part.Length;
            status.Text = "Gửi/nhận thành công · " + draft.ProcessingLocation + " · " + chars + " ký tự. Chọn Lưu kết nối nếu muốn dùng cấu hình này.";
        });
        load.Click += async (_, _) => await Run(async (client, draft) => { await client.SetOllamaLoaded(draft, true, cancel.Token); status.Text = "Đã nạp model; Ollama giữ trong RAM khoảng 10 phút khi không sử dụng."; });
        unload.Click += async (_, _) => await Run(async (client, draft) => { await client.SetOllamaLoaded(draft, false, cancel.Token); status.Text = "Đã yêu cầu Ollama giải phóng model khỏi RAM."; });
        add.Click += (_, _) =>
        {
            var p = presets.SelectedIndex switch
            {
                1 => new AiProfile { Name = "OpenAI", Protocol = AiProtocol.OpenAiResponses, BaseUrl = "https://api.openai.com/v1" },
                2 => new AiProfile { Name = "Google Gemini", Protocol = AiProtocol.Gemini, BaseUrl = "https://generativelanguage.googleapis.com/v1beta" },
                3 => new AiProfile { Name = "DeepSeek", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://api.deepseek.com" },
                4 => new AiProfile { Name = "API tùy chỉnh", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://" },
                _ => new AiProfile()
            };
            settings.Profiles.Add(p); FilterProfiles(p.Protocol==AiProtocol.Ollama,p.Id);
        };
        save.Click += async (_, _) =>
        {
            try
            {
                var p = Draft(); await app.StopAiAsync(); SecretVault.Save(p.Id, p.Protocol == AiProtocol.Ollama ? "" : key.Text ?? "");
                var index = settings.Profiles.FindIndex(i => i.Id == p.Id); settings.Profiles[index] = p; settings.SelectedId = p.Id;
                app.LocalSettings.Save(); oldEndpoint = p.BaseUrl; oldKey = key.Text ?? "";
                FilterProfiles(p.Protocol==AiProtocol.Ollama,p.Id); status.Text = "Đã lưu. Khóa chỉ ở máy này, không nằm trong tệp dự án.";
                app.RefreshAiConnections();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { await Dialogs.Message(this, "Chưa lưu được kết nối", ex.Message); }
        };
        var form = new StackPanel { Margin=new Thickness(24),Spacing=12 };
        void Label(string text)=>form.Children.Add(new TextBlock { Text=text,FontWeight=FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text="Kết nối AI",FontSize=25,FontWeight=FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text="Thiết lập model dùng trong dự án và trò chuyện.",TextWrapping=TextWrapping.Wrap });
        var tabs=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        tabs.Children.Add(ollamaTab);tabs.Children.Add(apiTab);form.Children.Add(tabs);
        void FilterProfiles(bool local, Guid? preferredId=null)
        {
            ollamaTab.Classes.Set("selected",local);apiTab.Classes.Set("selected",!local);
            var filtered=settings.Profiles.Where(p=>(p.Protocol==AiProtocol.Ollama)==local).ToArray();
            profiles.ItemsSource=filtered;profiles.SelectedItem=filtered.FirstOrDefault(p=>p.Id==(preferredId??settings.SelectedId))??filtered.FirstOrDefault();
            Select();
            presets.SelectedIndex=local ? 0 : 1;
            if(filtered.Length==0)status.Text="Chưa có kết nối trong nhóm này. Chọn Thêm kết nối để tạo.";
        }
        ollamaTab.Click+=(_,_)=>FilterProfiles(true);apiTab.Click+=(_,_)=>FilterProfiles(false);
        Label("Kết nối đã lưu");form.Children.Add(profiles);
        var manage=new StackPanel { Spacing=8,Children={presets,add} };
        form.Children.Add(new Expander { Header="Thêm kết nối",Content=manage });
        Label("Tên kết nối");form.Children.Add(name);
        Label("Địa chỉ máy chủ");form.Children.Add(url);form.Children.Add(addressHint);form.Children.Add(key);
        Label("Model mặc định");form.Children.Add(model);form.Children.Add(models);
        form.Children.Add(new WrapPanel { Orientation=Orientation.Horizontal,Children={list,test,load,unload} });
        form.Children.Add(new TextBlock { Text="Câu thử không gửi dữ liệu dự án.",FontSize=12,Foreground=RichEditor.Brush("#737A86") });
        form.Children.Add(status);form.Children.Add(ollamaThinking);form.Children.Add(waitForCompletion);
        form.Children.Add(new Expander { Header="Tùy chọn nâng cao",Content=new StackPanel { Spacing=12,Children={protocol,reasoningSummary} } });
        var scroll=new ScrollViewer { Content=form };
        var page=new Grid { RowDefinitions=new RowDefinitions("*,Auto") };page.Children.Add(scroll);
        save.Margin=new Thickness(24,12);save.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetRow(save,1);page.Children.Add(save);
        Content=SettingsFrame.Build(this,page,"Kết nối AI",label=>
        {
            if(label=="Kết nối AI") { scroll.Content=form;save.IsVisible=true; }
            else if(label=="Tiện ích") { scroll.Content=BuildPdfSettings(app);save.IsVisible=false; }
            else { var window=new SettingsWindow(app,label);window.Show(this); }
        });
        FilterProfiles(settings.Profiles.FirstOrDefault(p=>p.Id==settings.SelectedId)?.Protocol is null or AiProtocol.Ollama);
    }
}
