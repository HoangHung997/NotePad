using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private readonly Border _documentInspector = new() { Name = "WorkspaceDocumentInspector", IsVisible = false,
        Background = Brush.Parse("#FCFAF7"), BorderBrush = Brush.Parse("#DFDAD3"), BorderThickness = new Thickness(1,0,0,0), Padding = new Thickness(16) };
    private H2AgentEvidence? _openDocument;
    private string _historyFilter = "Tất cả";

    private void InitializeDocumentWorkspace()
    {
        InitializeOverviewDesign();
        WorkAndAi.Children.Add(_documentInspector);
        _chat.DocumentRequested += ShowWorkspaceDocument;
        _chat.ConversationsChanged += () => { _navigatorSignature = ""; RefreshNavigator(); };
        SidebarNewChatButton.Click += (_, _) => { ShowAgentWorkspace(); _chat.NewWorkspaceConversation(); _drawerOpen = false; ApplyResponsive(); };
        DocumentButton.Click += (_, _) =>
        {
            if (_openDocument is not null) { _documentInspector.IsVisible = !_documentInspector.IsVisible; ApplyResponsive(); }
            else ShowProjectDetail(ProjectWorkspaceResourcesMode);
        };
        TabsOverflowButton.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (label, mode) in new[] { ("Công việc", ProjectWorkspaceTasksMode), ("Ghi chú", ProjectWorkspaceNotesMode),
                ("Tệp", ProjectWorkspaceResourcesMode), ("Lịch sử", ProjectWorkspaceHistoryMode), ("Bằng chứng", ProjectWorkspaceEvidenceMode) })
            { var item = new MenuItem { Header = label }; item.Click += (_, _) => ShowProjectDetail(mode); menu.Items.Add(item); }
            menu.Open(TabsOverflowButton);
        };
        TaskSearchBox.TextChanged += (_, _) => Sheet.SetFilter(TaskSearchBox.Text ?? "");
        AskSelectionButton.Click += (_, _) =>
        {
            var selected = NotesEditor.Editor.SelectedText;
            if (string.IsNullOrWhiteSpace(selected)) { SetSaveStatus("Chọn đoạn ghi chú muốn trao đổi trước."); return; }
            FlushNotes(); ShowAgentWorkspace(); _chat.DraftWithSelection(selected);
        };
        LinkFileButton.Click += async (_, _) => await LinkProjectResource(false);
        LinkFolderButton.Click += async (_, _) => await LinkProjectResource(true);
        ResourceSearchBox.TextChanged += (_, _) => { _projectResourceSignature = ""; RefreshProjectResources(); };
        ProjectResourcesList.ContextRequested += (_, e) =>
        {
            if (SelectedProjectResource is not { } selected || _notesProject is null) return;
            var menu = new ContextMenu();
            var use = new MenuItem { Header = "Dùng trong trao đổi", IsEnabled = File.Exists(selected.Target) };
            use.Click += async (_,_) => { ShowAgentWorkspace(); await _chat.UseLinkedFileAsync(selected.Target!); }; menu.Items.Add(use);
            if (_notesProject.Links.FirstOrDefault(l => l.Target == selected.Target) is { } link)
            {
                var relink = new MenuItem { Header = "Liên kết lại…" };
                relink.Click += async (_,_) => await LinkProjectResource(false,link); menu.Items.Add(relink);
            }
            menu.Open(ProjectResourcesList); e.Handled = true;
        };
        ProjectResourcesList.SelectionChanged += (_, _) =>
        {
            if (SelectedProjectResource is { } item && File.Exists(item.Target))
                ShowWorkspaceDocument(new("linked:" + item.ResourceId, "linked-file", null, "Tệp được liên kết · chỉ xem trước",
                    LocalPath: item.Target, Provenance: "Liên kết trong dự án"));
        };
        ProjectResourcesList.ItemTemplate=new FuncDataTemplate<ProjectResourceItem>((item,_)=>
        {
            if(item is null)return new Border();
            var row=new Grid { ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto"),ColumnSpacing=12,Margin=new Thickness(8,10) };
            row.Children.Add(new AppIcon(IconKind.Document,24) { Foreground=Brush.Parse("#A4573D"),VerticalAlignment=VerticalAlignment.Top });
            var info=new StackPanel { Spacing=6,Children={new TextBlock { Text=item.Label,FontSize=15,FontWeight=FontWeight.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis },
                new TextBlock { Text=item.SourceText+" · "+item.MetaText,FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#737A86") }} };
            ToolTip.SetTip(info,item.TargetText);Grid.SetColumn(info,1);row.Children.Add(info);
            if(item.Target is {} path && Path.IsPathFullyQualified(path) && !File.Exists(path) && !Directory.Exists(path)
                && _notesProject?.Links.FirstOrDefault(l=>l.Target==path) is {} link)
            {
                var relink=new Button { Content="Liên kết lại…",FontSize=12 };
                relink.Click+=async(_,e)=> { e.Handled=true;await LinkProjectResource(false,link); };Grid.SetColumn(relink,2);row.Children.Add(relink);
            }
            return new Border { BorderBrush=Brush.Parse("#E1DDD7"),BorderThickness=new Thickness(0,0,0,1),Child=row };
        });
        foreach (var label in new[] { "Tất cả", "Agent", "Công việc", "Ghi chú" })
        {
            var button = new Button { Content = label, Classes = { "quiet" } };
            button.Click += (_, _) => { _historyFilter = label; _projectHistorySignature = ""; RefreshProjectHistory(); };
            HistoryFilters.Children.Add(button);
        }
        ProjectList.ItemTemplate = new FuncDataTemplate<ProjectNavItem>((item, _) =>
        {
            var body = new StackPanel { Spacing = 3, Margin = new Thickness(0,6) };
            if (item is null) return body;
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            title.Children.Add(new AppIcon(IconKind.Folder,18) { Foreground = Brush.Parse("#A4573D") });
            var name = new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(name,1); title.Children.Add(name);
            var progress = new TextBlock { Text = item.Project.Progress, FontSize = 11, Foreground = Brush.Parse("#737A86") };
            Grid.SetColumn(progress,2); title.Children.Add(progress); body.Children.Add(title);
            if (item.Project.Id == _notesProject?.Id)
                foreach (var conversation in item.Project.Conversations)
                {
                    var button = new Button { Content = new TextBlock { Text = conversation.Title, FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(20,2,0,0), Padding = new Thickness(10,7),
                        Background = Brush.Parse(conversation.Id == item.Project.SelectedAiConversationId ? "#EEDCD1" : "#00FFFFFF"), BorderThickness = new Thickness(0) };
                    button.Click += (_, e) => { e.Handled = true; ShowAgentWorkspace(); _chat.SelectWorkspaceConversation(conversation.Id); _drawerOpen = false; ApplyResponsive(); };
                    body.Children.Add(button);
                }
            return body;
        });
    }

    private void CloseWorkspaceDocument()
    {
        _documentInspector.IsVisible = false;
        _documentInspector.Child = null; _openDocument = null; ApplyResponsive();
    }

    private void ShowWorkspaceDocument(H2AgentEvidence evidence)
    {
        _openDocument = evidence;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = Path.GetFileName(evidence.LocalPath) is { Length: > 0 } file ? file : "Bằng chứng",
            FontSize = 20, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var close = AppIcon.Button(IconKind.Close, "Quay lại trao đổi"); close.Click += (_, _) => CloseWorkspaceDocument();
        Grid.SetColumn(close,1); heading.Children.Add(close); root.Children.Add(heading);
        var preview = new AgentArtifactView(evidence, _app.AgentAdapter); Grid.SetRow(preview,1); root.Children.Add(preview);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var back = new Button { Content = "← Quay lại Agent", Classes = { "quiet" } }; back.Click += (_, _) => CloseWorkspaceDocument(); actions.Children.Add(back);
        if (evidence.LocalPath is { } path && File.Exists(path))
        {
            var use = new Button { Content = "Dùng trong trao đổi", Classes = { "accent" } };
            use.Click += async (_, _) => { CloseWorkspaceDocument(); ShowAgentWorkspace(); await _chat.UseLinkedFileAsync(path); };
            actions.Children.Add(use);
        }
        Grid.SetRow(actions,2); root.Children.Add(actions);
        _documentInspector.Child = root; _documentInspector.IsVisible = true; ApplyResponsive();
    }

    private async Task LinkProjectResource(bool folder, ProjectLink? replace = null)
    {
        var project = _notesProject; if (project is null) return;
        try
        {
            string[] paths;
            if (folder)
                paths = (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Liên kết thư mục dự án", AllowMultiple = true }))
                    .Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
            else
                paths = (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = replace is null ? "Liên kết tệp dự án" : "Liên kết lại tệp", AllowMultiple = replace is null }))
                    .Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length == 0) return;
            foreach (var path in paths)
            {
                if (replace is not null) project.Links.Remove(replace);
                if (project.Links.Any(l => string.Equals(l.Target,path,StringComparison.OrdinalIgnoreCase))) continue;
                project.Links.Add(new(replace?.Id ?? Guid.NewGuid(),Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),path));
            }
            project.UpdatedAtUtc = DateTime.UtcNow; _app.MarkProjectDirty(project.Id); _app.ScheduleSave();
            _projectResourceSignature = ""; if (_notesProject == project) RefreshProjectResources();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { SetSaveStatus("Chưa liên kết được: " + ex.Message); }
    }
}
