using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

/// <summary>A view of one authoritative Agent task, also used for attention/history deep links.</summary>
public sealed class AgentTaskWindow : Window
{
    private readonly IH2AgentAdapter _adapter;
    private readonly Guid _taskId;
    private readonly TextBlock _state = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _evidence = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly AgentApprovalPanel _approval = new();
    private readonly AgentChatSurface _surface = new();
    private readonly Button _cancel = new() { Content = "Dừng tác vụ" };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public AgentTaskWindow(IH2AgentAdapter adapter, Guid taskId)
    {
        _adapter = adapter; _taskId = taskId;
        Title = "Chi tiết tác vụ · H2 Notes"; Width = 640; Height = 560; MinWidth = 460; MinHeight = 400;
        Background = Brush.Parse("#FCFAF7"); ShowInTaskbar = false;
        var close = new Button { Content = "Đóng" }; close.Click += (_, _) => Close();
        _cancel.Click += (_, _) => { _adapter.CancelTask(_taskId); Refresh(); };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(_state);
        root.Children.Add(_surface); Grid.SetRow(_surface, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, close } };
        root.Children.Add(buttons); Grid.SetRow(buttons, 3); Content = root;
        _timer.Tick += (_, _) => Refresh(); Closed += (_, _) => _timer.Stop();
        Refresh(); _timer.Start();
    }

    private void Refresh()
    {
        try
        {
            var task = _adapter.GetTaskSummary(_taskId);
            var thread = _adapter.GetThread(task.ThreadId ?? task.TaskId);
            var tasks = (thread?.TaskIds ?? [_taskId]).Select(_adapter.GetTaskSummary).ToArray();
            _surface.PresentTasks(_adapter, tasks, (task.ThreadId ?? task.TaskId).ToString());
            _state.Text = task.Status switch { H2AgentTaskStatus.WaitingForApproval => "Cần bạn xác nhận", H2AgentTaskStatus.Completed => "Đã hoàn tất",
                H2AgentTaskStatus.Blocked => "Chưa thể hoàn tất", H2AgentTaskStatus.Failed => "Tác vụ gặp lỗi", H2AgentTaskStatus.Cancelled => "Đã dừng", _ => "Agent đang làm việc" };
            _result.Text = task.Goal + "\n\n" + (task.Error ?? task.FinalText ?? "Đang chờ kết quả…");
            _approval.Present(_adapter, _taskId, task.PendingApproval);
            _evidence.Text = string.Join("\n\n", task.Evidence.Select(item =>
                (item.VerificationPassed == true ? "Đã xác minh: " : item.VerificationPassed == false ? "Xác minh chưa đạt: " : "Bằng chứng: ")
                + (item.Summary ?? item.Kind)));
            _cancel.IsVisible = task.Status is H2AgentTaskStatus.Queued or H2AgentTaskStatus.Running or H2AgentTaskStatus.WaitingForApproval;
        }
        catch (KeyNotFoundException) { _state.Text = "Tác vụ không còn trong lịch sử Agent."; _cancel.IsVisible = false; _timer.Stop(); }
    }
}
