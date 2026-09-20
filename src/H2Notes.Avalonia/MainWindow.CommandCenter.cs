using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private H2CommandCenterQueryService _commandCenterQuery = null!;
    private bool _showCommandCenter = true;
    private bool _updatingCommandCenter;
    private bool _updatingCommandCenterAttention;
    private string _commandCenterSignature = "";
    private string _commandCenterAttentionSignature = "";
    private readonly DispatcherTimer _commandCenterRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    private void InitializeCommandCenter()
    {
        _commandCenterQuery = new H2CommandCenterQueryService(
            new H2ProductProjectionService(_app.AgentAdapter));

        CommandCenterGroupFilter.ItemsSource = CommandCenterGroupFilterItem.All;
        CommandCenterGroupFilter.SelectedIndex = 0;
        CommandCenterGroupFilter.SelectionChanged += (_, _) =>
        {
            if (_showCommandCenter)
                RefreshCommandCenter();
        };

        CommandCenterAddProjectButton.Click += async (_, _) => await AddProject();
        CommandCenterList.SelectionChanged += (_, _) =>
        {
            if (_updatingCommandCenter || CommandCenterList.SelectedItem is not CommandCenterProjectItem item)
                return;
            OpenProjectWorkspace(item.Board, item.Project);
        };

        CommandCenterAttentionList.SelectionChanged += (_, _) =>
        {
            if (_updatingCommandCenterAttention
                || CommandCenterAttentionList.SelectedItem is not CommandCenterAttentionItem item)
                return;

            CommandCenterAttentionList.SelectedItem = null;
            if (item.Board is not null && item.Project is not null)
                OpenProjectWorkspace(item.Board, item.Project);
            else if (item.Kind == "workspace")
                _app.ShowSettings(this);
        };

        _commandCenterRefreshTimer.Tick += (_, _) =>
        {
            if (_showCommandCenter && IsVisible)
                RefreshCommandCenter();
        };
        _commandCenterRefreshTimer.Start();
        Closed += (_, _) => _commandCenterRefreshTimer.Stop();
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

        var allItems = projections
            .Where(projection => byProject.ContainsKey(projection.ProjectId))
            .Select(projection =>
            {
                var source = byProject[projection.ProjectId];
                return new CommandCenterProjectItem(source.Board, source.Project, projection);
            })
            .ToArray();

        var selectedGroup = (CommandCenterGroupFilter.SelectedItem as CommandCenterGroupFilterItem)?.Group;
        var items = selectedGroup is null
            ? allItems
            : allItems.Where(item => item.Group == selectedGroup.Value).ToArray();

        var attentionItems = _commandCenterQuery.GetNeedsAttention(
                pairs.Select(pair => pair.Project),
                health)
            .Select(projection =>
            {
                if (projection.ProjectId is { } projectId && byProject.TryGetValue(projectId, out var source))
                    return new CommandCenterAttentionItem(source.Board, source.Project, projection);
                return new CommandCenterAttentionItem(null, null, projection);
            })
            .ToArray();

        var attentionSignature = string.Join("|", attentionItems.Select(item => item.Signature));
        if (attentionSignature != _commandCenterAttentionSignature)
        {
            _commandCenterAttentionSignature = attentionSignature;
            _updatingCommandCenterAttention = true;
            CommandCenterAttentionList.ItemsSource = attentionItems;
            CommandCenterAttentionList.SelectedItem = null;
            _updatingCommandCenterAttention = false;
        }
        CommandCenterAttentionSection.IsVisible = attentionItems.Length != 0;
        CommandCenterAttentionHeader.Text = $"Cần bạn xử lý · {attentionItems.Length}";

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

        var attention = attentionItems.Length;
        var active = allItems.Count(item => item.AgentStatus is
            H2AgentTaskStatus.Queued or H2AgentTaskStatus.Running or H2AgentTaskStatus.WaitingForApproval);
        var projectCount = selectedGroup is null
            ? $"{allItems.Length} dự án"
            : $"{items.Length}/{allItems.Length} dự án";
        CommandCenterSummary.Text = $"{projectCount} · {attention} cần xem"
            + (active > 0 ? $" · {active} Agent đang hoạt động" : "");
        CommandCenterSync.Text = CommandCenterProjectItem.SyncTextFor(health.State);
        ToolTip.SetTip(CommandCenterSync,
            string.IsNullOrWhiteSpace(health.Message)
                ? CommandCenterProjectItem.SyncDetailFor(health.State)
                : health.Message);
        CommandCenterEmpty.Text = allItems.Length == 0
            ? "Chưa có dự án. Tạo dự án đầu tiên để bắt đầu."
            : "Không có dự án trong nhóm đang lọc.";
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

    private sealed record CommandCenterGroupFilterItem(
        string Text,
        H2CommandCenterGroup? Group)
    {
        public static IReadOnlyList<CommandCenterGroupFilterItem> All { get; } =
        [
            new("Tất cả", null),
            new("Cần xử lý", H2CommandCenterGroup.NeedsAttention),
            new("Đang làm", H2CommandCenterGroup.Working),
            new("Đang chờ", H2CommandCenterGroup.Waiting),
            new("Bình thường", H2CommandCenterGroup.Normal),
            new("Hoàn thành", H2CommandCenterGroup.Completed)
        ];
    }

    private sealed class CommandCenterAttentionItem
    {
        public CommandCenterAttentionItem(
            NoteRecord? board,
            ProjectRecord? project,
            NeedsAttentionProjection projection)
        {
            Board = board;
            Project = project;
            Projection = projection;
        }

        public NoteRecord? Board { get; }
        public ProjectRecord? Project { get; }
        public NeedsAttentionProjection Projection { get; }

        public Guid? ProjectId => Projection.ProjectId;
        public Guid? AgentTaskId => Projection.AgentTaskId;
        public string Kind => Projection.Kind;
        public string Code => Projection.Code;
        public string Title => Projection.Title;
        public string SourceText => Projection.Kind == "workspace"
            ? "Nguồn: Lưu trữ / đồng bộ"
            : Project is null
                ? "Nguồn: Agent task"
                : "Nguồn: " + ProjectName(Project)
                    + (Projection.AgentTaskId is { } taskId ? $" · Agent {taskId.ToString("N")[..8]}" : "");
        public string TimeText => Projection.AtUtc is { } at
            ? (at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at).ToString("dd/MM HH:mm")
            : "";
        public string Signature =>
            $"{ProjectId}:{AgentTaskId}:{Kind}:{Code}:{Title}:{Projection.AtUtc:O}";

        private static string ProjectName(ProjectRecord project)
            => project.NameRich?.Text ?? RichDocument.FromLegacy(project.Name ?? "").Text;
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
        public H2CommandCenterGroup Group => H2CommandCenterQueryService.GroupFor(Projection);
        public string GroupText => Group switch
        {
            H2CommandCenterGroup.NeedsAttention => "Cần xử lý",
            H2CommandCenterGroup.Working => "Đang làm",
            H2CommandCenterGroup.Waiting => "Đang chờ",
            H2CommandCenterGroup.Completed => "Hoàn thành",
            _ => "Bình thường"
        };
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
            $"{ProjectId:N}:{Name}:{Group}:{Projection.CompletedTasks}:{Projection.TotalTasks}:"
            + $"{Projection.NextTaskId}:{Projection.NextTask}:{Projection.AgentStatus}:"
            + $"{Projection.AttentionCount}:{Projection.LatestVerifiedActivityUtc:O}:{Projection.SyncState}";

        public static string SyncTextFor(H2WorkspaceSyncState state)
            => state switch
            {
                H2WorkspaceSyncState.Healthy => "Đã đồng bộ",
                H2WorkspaceSyncState.Busy => "Đang lưu",
                H2WorkspaceSyncState.PendingLocal => "Chờ đồng bộ",
                H2WorkspaceSyncState.Offline => "Ngoại tuyến",
                H2WorkspaceSyncState.RecoveryRequired => "Lỗi · cần phục hồi",
                H2WorkspaceSyncState.Warning => "Lỗi đồng bộ",
                _ => "Trạng thái lưu trữ chưa rõ"
            };

        public static string SyncDetailFor(H2WorkspaceSyncState state)
            => state switch
            {
                H2WorkspaceSyncState.Healthy => "Dữ liệu chia sẻ đang ở trạng thái đồng bộ.",
                H2WorkspaceSyncState.Busy => "H2 Notes đang ghi dữ liệu.",
                H2WorkspaceSyncState.PendingLocal => "Thay đổi đã giữ cục bộ và đang chờ ghi vào kho chia sẻ.",
                H2WorkspaceSyncState.Offline => "Kho chia sẻ hiện không truy cập được; thay đổi cục bộ vẫn được giữ an toàn khi có pending snapshot.",
                H2WorkspaceSyncState.RecoveryRequired => "Kho chia sẻ cần phục hồi trước khi tiếp tục ghi an toàn.",
                H2WorkspaceSyncState.Warning => "Kho chia sẻ có cảnh báo đồng bộ cần kiểm tra.",
                _ => "Không xác định được trạng thái lưu trữ."
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
