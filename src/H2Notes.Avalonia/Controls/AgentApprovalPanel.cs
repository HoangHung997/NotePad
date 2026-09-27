using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed class AgentApprovalPanel : Border
{
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button _approve = new() { Name = "AgentApprove", Content = "Cho phép một lần", Classes = { "accent" } };
    private readonly Button _deny = new() { Name = "AgentDeny", Content = "Từ chối" };
    private IH2AgentAdapter? _adapter;
    private Guid _taskId;
    private Guid? _approvalId;

    public AgentApprovalPanel()
    {
        Name = "AgentApprovalPanel"; IsVisible = false;
        AutomationProperties.SetName(this, "Yêu cầu xác nhận của Agent");
        AutomationProperties.SetName(_approve, "Cho phép thay đổi này một lần");
        AutomationProperties.SetName(_deny, "Từ chối thay đổi này");
        Background = Brush.Parse("#FFF4E8"); BorderBrush = Brush.Parse("#DEC3AA");
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6); Padding = new Thickness(10);
        Child = new StackPanel { Spacing = 6, Children = { _title,
            new ScrollViewer { MaxHeight = 130, Content = _details },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _approve, _deny } } } };
        _approve.Click += (_, _) => Respond(true);
        _deny.Click += (_, _) => Respond(false);
    }

    public void Present(IH2AgentAdapter adapter, Guid taskId, H2AgentApproval? approval)
    {
        _adapter = adapter; _taskId = taskId; _approvalId = approval?.ApprovalId;
        IsVisible = approval is not null;
        _title.Text = approval?.Title ?? ""; _details.Text = approval?.Details ?? "";
        _approve.IsEnabled = _deny.IsEnabled = approval is not null;
        if (approval is not null)
        {
            var label = "Cần xác nhận: " + approval.Title;
            AutomationProperties.SetName(this, label);
            ToolTip.SetTip(this, label + " · tạo lúc " + approval.CreatedUtc.ToLocalTime().ToString("HH:mm:ss"));
        }
    }

    private void Respond(bool approved)
    {
        if (_adapter is null || _approvalId is not { } id) return;
        _approve.IsEnabled = _deny.IsEnabled = false;
        try
        {
            if (_adapter.RespondToApproval(_taskId, id, approved)) IsVisible = false;
            else { _title.Text = "Yêu cầu xác nhận này không còn hiệu lực."; AutomationProperties.SetName(this, _title.Text); _approvalId = null; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        { _title.Text = ex.Message; AutomationProperties.SetName(this, _title.Text); _approvalId = null; }
    }
}
