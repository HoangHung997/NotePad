using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private string _projectHistorySignature = "";

    private void InitializeProjectHistory()
    {
        ProjectHistoryList.SelectionChanged += (_, _) =>
        {
            if (ProjectHistoryList.SelectedItem is not ProjectHistoryItem { AgentTaskId: { } taskId }) return;
            ProjectHistoryList.SelectedItem = null;
            var observation=_app.AgentAdapter.ObserveTask(taskId);
            var turn=new AgentTurnView();turn.Present(_app.AgentAdapter,observation);
            var close=new Button { Content="← Quay lại lịch sử" };close.Click+=(_,_)=>CloseWorkspaceDocument();
            var content=new Grid { RowDefinitions=new RowDefinitions("Auto,*"),RowSpacing=16 };
            content.Children.Add(close);var scroll=new ScrollViewer { Content=turn };Grid.SetRow(scroll,1);content.Children.Add(scroll);
            _documentInspector.Child=content;_documentInspector.IsVisible=true;ApplyResponsive();
        };
    }

    private void RefreshProjectHistory()
    {
        if (_commandCenterQuery is null || ProjectHistoryList is null)
            return;

        if (_notesProject is null)
        {
            SetProjectHistoryItems(Array.Empty<ProjectHistoryItem>());
            return;
        }

        var items = _commandCenterQuery
            .GetProjectHistory(_notesProject, _app.CurrentWorkspaceHealth, limit: 120)
            .Where(item => _historyFilter == "Tất cả" || (_historyFilter == "Agent" ? item.AgentTaskId.HasValue : _historyFilter == "Công việc" ? item.Kind.StartsWith("project-task") : item.Kind == "project-edited"))
            .Select(item => new ProjectHistoryItem(item))
            .ToArray();

        var signature = string.Join("|", items.Select(item => item.Signature));
        if (signature != _projectHistorySignature)
        {
            _projectHistorySignature = signature;
            SetProjectHistoryItems(items);
        }

        ProjectHistorySummary.Text = $"{items.Length} hoạt động";
        ProjectHistoryEmpty.IsVisible = items.Length == 0;
        ProjectHistoryList.IsVisible = items.Length != 0;
    }

    private void SetProjectHistoryItems(IReadOnlyList<ProjectHistoryItem> items)
    {
        ProjectHistoryList.ItemsSource = items;
        ProjectHistoryList.SelectedItem = null;
        ProjectHistorySummary.Text = $"{items.Count} hoạt động";
        ProjectHistoryEmpty.IsVisible = items.Count == 0;
        ProjectHistoryList.IsVisible = items.Count != 0;
    }

    private sealed class ProjectHistoryItem
    {
        public ProjectHistoryItem(ProjectActivityProjection projection)
            => Projection = projection;

        public ProjectActivityProjection Projection { get; }
        public string Summary => Projection.Kind switch
        {
            "project-created" when Projection.Summary=="Project created."=>"Đã tạo dự án",
            "project-edited" when Projection.Summary=="Project updated."=>"Đã cập nhật dự án",
            "project-task-created"=>TranslatePrefix(Projection.Summary,"Task created: ","Đã tạo việc: "),
            "project-task-completed"=>TranslatePrefix(Projection.Summary,"Task completed: ","Đã hoàn thành: "),
            "project-task-updated"=>TranslatePrefix(Projection.Summary,"Task updated: ","Đã cập nhật: "),
            _=>Projection.Summary
        };
        private static string TranslatePrefix(string value,string source,string translated)
            =>value.StartsWith(source,StringComparison.Ordinal) ? translated+value[source.Length..] : value;
        public Guid? AgentTaskId => Projection.AgentTaskId;
        public string? EvidenceId => Projection.EvidenceId;

        public string KindText => Projection.Kind switch
        {
            "project-created" => "Dự án",
            "project-edited" => "Dự án",
            "project-task-created" => "Công việc",
            "project-task-updated" => "Công việc",
            "project-task-completed" => "Hoàn thành",
            "agent-lifecycle" => "Agent",
            "verified-mutation" => "Đã xác minh",
            "agent-evidence" => "Bằng chứng",
            "sync" => "Đồng bộ",
            _ => "Hoạt động"
        };

        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (Projection.AgentTaskId is not null)
                    parts.Add("Mở chi tiết lượt Agent");
                if (!string.IsNullOrWhiteSpace(Projection.EvidenceId))
                    parts.Add("Có bằng chứng");
                return string.Join(" · ", parts);
            }
        }

        public string TimeText
        {
            get
            {
                var value = Projection.AtUtc.Kind == DateTimeKind.Utc
                    ? Projection.AtUtc.ToLocalTime()
                    : Projection.AtUtc;
                return value.ToString("dd/MM/yyyy HH:mm");
            }
        }

        public string Signature =>
            $"{Projection.AtUtc:O}:{Projection.Kind}:{Projection.Summary}:"
            + $"{Projection.AgentTaskId}:{Projection.EvidenceId}";
    }
}
