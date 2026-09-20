using Avalonia.Controls;
using Avalonia.Interactivity;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private H2CommandCenterQueryService _commandCenterQuery = null!;
    private bool _showCommandCenter = true;
    private bool _updatingCommandCenter;
    private string _commandCenterSignature = "";

    private void InitializeCommandCenter()
    {
        _commandCenterQuery = new H2CommandCenterQueryService(
            new H2ProductProjectionService(_app.AgentAdapter));

        CommandCenterAddProjectButton.Click += async (_, _) => await AddProject();
        CommandCenterList.SelectionChanged += (_, _) =>
        {
            if (_updatingCommandCenter || CommandCenterList.SelectedItem is not CommandCenterProjectItem item)
                return;
            OpenProjectWorkspace(item.Board, item.Project);
        };
    }

    private IReadOnlyList<NoteRecord> CommandCenterBoards()
    {
        var boards = _app.State.Notes
            .Where(note => note.IsBoard && !note.IsArchived)
            .ToList();
        if (boards.All(board => board.Id != _board.Id))
            boards.Insert(0, _board);
        return boards;
    }

    private void RefreshCommandCenter()
    {
        if (_commandCenterQuery is null || CommandCenterList is null) return;

        var boards = CommandCenterBoards();
        var pairs = boards
            .SelectMany(board => board.Projects.Select(project => (Board: board, Project: project)))
            .ToArray();
        var byProject = pairs.ToDictionary(pair => pair.Project.Id);
        var health = _app.CurrentWorkspaceHealth;
        var projections = _commandCenterQuery.GetProjects(
            pairs.Select(pair => pair.Project),
            health);

        var items = projections
            .Where(projection => byProject.ContainsKey(projection.ProjectId))
            .Select(projection =>
            {
                var source = byProject[projection.ProjectId];
                return new CommandCenterProjectItem(source.Board, source.Project, projection);
            })
            .ToArray();

        var signature = string.Join("|", items.Select(item => item.Signature))
            + "|" + health.State + "|" + health.Code + "|" + health.HasPendingChanges;
        if (signature != _commandCenterSignature)
        {
            _commandCenterSignature = signature;
            _updatingCommandCenter = true;
            CommandCenterList.ItemsSource = items;
            CommandCenterList.SelectedItem = null;
            _updatingCommandCenter = false;
        }

        var attention = items.Sum(item => item.AttentionCount);
        var active = items.Count(item => item.AgentStatus is
            H2AgentTaskStatus.Queued or H2AgentTaskStatus.Running or H2AgentTaskStatus.WaitingForApproval);
        CommandCenterSummary.Text = $"{items.Length} dự án · {attention} cần xem"
            + (active > 0 ? $" · {active} Agent đang hoạt động" : "");
        CommandCenterSync.Text = CommandCenterProjectItem.SyncTextFor(health.State);
        CommandCenterEmpty.IsVisible = items.Length == 0;
        CommandCenterList.IsVisible = items.Length != 0;
    }

    private void ShowCommandCenter()
    {
        Flush();
        _showCommandCenter = true;
        _drawerOpen = false;
        RefreshCommandCenter();
        ApplyResponsive();
    }

    private void OpenProjectWorkspace(NoteRecord board, ProjectRecord project)
    {
        if (_board.Id != board.Id)
            SetBoard(board);

        _showCommandCenter = false;
        SelectCurrent(project);
        _drawerOpen = false;
        CommandCenterList.SelectedItem = null;
        ApplyResponsive();
    }

    private sealed class CommandCenterProjectItem
    {
        public CommandCenterProjectItem(
            NoteRecord board,
            ProjectRecord project,
            ProjectOverviewProjection projection)
        {
            Board = board;
            Project = project;
            Projection = projection;
        }

        public NoteRecord Board { get; }
        public ProjectRecord Project { get; }
        public ProjectOverviewProjection Projection { get; }

        public Guid ProjectId => Projection.ProjectId;
        public string Name => Projection.Name;
        public int AttentionCount => Projection.AttentionCount;
        public H2AgentTaskStatus? AgentStatus => Projection.AgentStatus;
        public string ProgressText => $"{Projection.CompletedTasks}/{Projection.TotalTasks} công việc";
        public double ProgressPercent => Projection.TotalTasks == 0
            ? 0d
            : 100d * Projection.CompletedTasks / Projection.TotalTasks;
        public string NextText => string.IsNullOrWhiteSpace(Projection.NextTask)
            ? "Tiếp theo: Đã hoàn thành"
            : "Tiếp theo: " + Projection.NextTask;
        public string AgentText => Projection.AgentStatus is null
            ? "Agent: chưa có hoạt động"
            : "Agent: " + AgentStatusText(Projection.AgentStatus.Value);
        public string AttentionText => Projection.AttentionCount == 0
            ? "Không cần xử lý"
            : $"{Projection.AttentionCount} cần xem";
        public string ActivityText => Projection.LatestVerifiedActivityUtc is { } at
            ? "Hoạt động mới nhất: " + ToLocal(at).ToString("dd/MM HH:mm")
            : "Chưa có hoạt động xác minh";
        public string SyncText => SyncTextFor(Projection.SyncState);
        public string BoardText => string.IsNullOrWhiteSpace(Board.Title) ? "" : Board.Title;

        public string Signature =>
            $"{ProjectId:N}:{Name}:{Projection.CompletedTasks}:{Projection.TotalTasks}:"
            + $"{Projection.NextTaskId}:{Projection.NextTask}:{Projection.AgentStatus}:"
            + $"{Projection.AttentionCount}:{Projection.LatestVerifiedActivityUtc:O}:{Projection.SyncState}";

        public static string SyncTextFor(H2WorkspaceSyncState state)
            => state switch
            {
                H2WorkspaceSyncState.Healthy => "Đồng bộ: ổn",
                H2WorkspaceSyncState.Busy => "Đồng bộ: đang bận",
                H2WorkspaceSyncState.PendingLocal => "Đồng bộ: có bản chờ cục bộ",
                H2WorkspaceSyncState.Warning => "Đồng bộ: cần kiểm tra",
                H2WorkspaceSyncState.RecoveryRequired => "Đồng bộ: cần phục hồi",
                H2WorkspaceSyncState.Offline => "Đồng bộ: ngoại tuyến",
                _ => "Đồng bộ: chưa rõ"
            };

        private static string AgentStatusText(H2AgentTaskStatus status)
            => status switch
            {
                H2AgentTaskStatus.Queued => "đang chờ",
                H2AgentTaskStatus.Running => "đang làm",
                H2AgentTaskStatus.WaitingForApproval => "chờ phê duyệt",
                H2AgentTaskStatus.Completed => "đã hoàn thành",
                H2AgentTaskStatus.Blocked => "bị chặn",
                H2AgentTaskStatus.Cancelled => "đã hủy",
                H2AgentTaskStatus.Failed => "thất bại",
                _ => status.ToString()
            };

        private static DateTime ToLocal(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
    }
}
