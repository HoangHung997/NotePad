using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

internal sealed class NoteConflictPanel : Border
{
    public NoteConflictPanel(WorkspaceNoteConflictReview review,Func<ConflictResolution,bool> resolve)
    {
        Background=Brush.Parse("#FFF6F0");BorderBrush=Brush.Parse("#C5805D");BorderThickness=new Thickness(1);CornerRadius=new CornerRadius(6);Padding=new Thickness(16);
        var body=new StackPanel { Spacing=14 };Child=body;
        body.Children.Add(new TextBlock { Text="Xung đột ghi chú · "+review.Title,FontSize=19,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text="Hai phiên bản khác nhau. Chọn nội dung muốn giữ.",TextWrapping=TextWrapping.Wrap });
        var versions=new Grid { ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=12 };
        foreach(var (label,document,col) in new[]{("Máy này",review.Local,0),("Máy khác",review.Remote,1)})
        {
            var panel=new StackPanel { Spacing=8,Children={new TextBlock { Text=label,FontWeight=FontWeight.SemiBold },new ScrollViewer { MaxHeight=150,Content=new SelectableTextBlock { Text=document.Text,TextWrapping=TextWrapping.Wrap } }} };
            var card=new Border { Padding=new Thickness(12),Background=Brushes.White,BorderBrush=Brush.Parse("#DEDAD4"),BorderThickness=new Thickness(1),Child=panel };Grid.SetColumn(card,col);versions.Children.Add(card);
        }
        body.Children.Add(versions);ConflictResolution? selected=null;
        var apply=new Button { Content="Áp dụng lựa chọn",Classes={"accent"},IsEnabled=false };
        foreach(var (label,value) in new[]{("Giữ bản máy này",ConflictResolution.KeepCurrent),("Giữ bản máy khác",ConflictResolution.KeepDestination),("Giữ cả hai để biên tập",ConflictResolution.KeepBoth)})
        {
            var radio=new RadioButton { Content=label,GroupName=review.Id };radio.IsCheckedChanged+=(_,_)=> { if(radio.IsChecked==true) { selected=value;apply.IsEnabled=true; } };body.Children.Add(radio);
        }
        var status=new TextBlock { Text="Cả hai bản gốc vẫn được giữ trong lịch sử xung đột.",TextWrapping=TextWrapping.Wrap,FontSize=12 };
        apply.Click+=(_,_)=> { if(selected is null)return;apply.IsEnabled=false;
            if(resolve(selected.Value)) { status.Text="Đã lưu lựa chọn.";IsEnabled=false; }
            else status.Text="Nội dung đã thay đổi hoặc chưa lưu được. Đóng và mở lại cài đặt để đối chiếu bản mới."; };
        body.Children.Add(apply);body.Children.Add(status);
    }
}
