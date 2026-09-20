using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private string _projectHistorySignature = "";

    private void InitializeProjectHistory()
    {
        // History is a disposable projection. No timer/store is needed here: normal project,
        // Agent and sync refresh paths call UpdateSummary(), which rebuilds this view when open.
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
        public string Summary => Projection.Summary;
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
            "agent-evidence" => "Evidence",
            "sync" => "Đồng bộ",
            _ => "Hoạt động"
        };

        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (Projection.AgentTaskId is { } taskId)
                    parts.Add("Agent " + taskId.ToString("N")[..8]);
                if (!string.IsNullOrWhiteSpace(Projection.EvidenceId))
                    parts.Add("Evidence " + Projection.EvidenceId);
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
