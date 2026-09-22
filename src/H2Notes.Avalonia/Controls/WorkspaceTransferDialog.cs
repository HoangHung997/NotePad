using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

internal static class WorkspaceTransferDialog
{
    public static Task<string?> Show(Window owner,string source,string target,WorkspaceTransfer transfer)
        =>Create(source,target,transfer).ShowDialog<string?>(owner);

    internal static Window Create(string source,string target,WorkspaceTransfer transfer)
    {
        var window=new Window { Title="Chuyển thư mục lưu cục bộ",WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,CanMinimize=false,CanMaximize=false };
        var body=new StackPanel { Margin=new Thickness(28),Spacing=20 };
        body.Children.Add(new TextBlock { Text="Chuyển thư mục lưu cục bộ",FontSize=25,FontWeight=FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text="Chọn cách xử lý dữ liệu trước khi chuyển",FontSize=15,TextWrapping=TextWrapping.Wrap });
        Border Card(string title,string path,string count)=>new() { Padding=new Thickness(18),BorderThickness=new Thickness(1),BorderBrush=Brush.Parse("#D7D1C9"),CornerRadius=new CornerRadius(6),Background=Brushes.White,
            Child=new StackPanel { Spacing=8,Children={new TextBlock { Text=title,FontWeight=FontWeight.SemiBold },new TextBlock { Text=path,TextWrapping=TextWrapping.Wrap },new TextBlock { Text=count,FontSize=12,Foreground=Brush.Parse("#737A86") }} } };
        var folders=new Grid { ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=20 };
        folders.Children.Add(Card("Kho đang dùng",source,$"{transfer.SourceProjects} dự án · {transfer.SourceNotes} ghi chú"));
        var to=Card("Thư mục đã chọn",target,$"{transfer.DestinationProjects} dự án · {transfer.DestinationNotes} ghi chú");Grid.SetColumn(to,1);folders.Children.Add(to);body.Children.Add(folders);
        body.Children.Add(new TextBlock { Text=$"{transfer.Conflicts.Count} xung đột cần xem khi hợp nhất",Foreground=Brush.Parse("#A4573D"),TextWrapping=TextWrapping.Wrap });
        string? selected=null;bool previewed=false;
        var apply=new Button { Name="TransferPreview",Content="Xem trước thay đổi",IsEnabled=false,Classes={"accent"} };
        var options=new StackPanel { Spacing=10 };
        foreach(var (key,label,hint) in new[]{("merge","Hợp nhất","Giữ dữ liệu hai kho; chọn cách xử lý từng chỗ trùng."),("overwrite","Ghi đè","Thay dữ liệu H2 ở đích sau khi sao lưu."),("existing","Dùng dữ liệu hiện có","Dùng kho đích; không sao chép dữ liệu.")})
        {
            var radio=new RadioButton { GroupName="transfer",Name="Transfer"+key,Content=new StackPanel { Spacing=4,Children={new TextBlock { Text=label,FontWeight=FontWeight.SemiBold },new TextBlock { Text=hint,FontSize=12,TextWrapping=TextWrapping.Wrap }} } };
            radio.IsCheckedChanged+=(_,_)=> { if(radio.IsChecked==true) { selected=key;previewed=false;apply.IsEnabled=true;apply.Content="Xem trước thay đổi"; } };
            options.Children.Add(new Border { Background=Brushes.White,BorderBrush=Brush.Parse("#D7D1C9"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(14),Child=radio });
        }
        body.Children.Add(options);
        var preview=new TextBlock { Name="TransferSummary",Text="Sao lưu trước khi áp dụng · Kho cũ được giữ nguyên.",TextWrapping=TextWrapping.Wrap };body.Children.Add(preview);
        apply.Click+=(_,_)=>
        {
            if(selected is null)return;
            if(!previewed) { previewed=true;preview.Text=selected switch {"overwrite"=>$"Sẽ sao lưu kho đích, rồi thay dữ liệu H2 bằng {transfer.SourceProjects} dự án và {transfer.SourceNotes} ghi chú từ kho đang dùng.","existing"=>$"Sẽ dùng {transfer.DestinationProjects} dự án và {transfer.DestinationNotes} ghi chú ở đích. Không sao chép kho nguồn.",_=>$"Sẽ hợp nhất hai kho. Có {transfer.Conflicts.Count} xung đột được chọn ở bước tiếp theo."}; apply.Content="Tiếp tục";return; }
            window.Close(selected);
        };
        var cancel=new Button { Content="Hủy",IsCancel=true };cancel.Click+=(_,_)=>window.Close(null);
        body.Children.Add(new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8,Children={cancel,apply} });
        window.Content=SettingsFrame.Build(window,new ScrollViewer { Content=body },"Dữ liệu và đồng bộ",null);
        return window;
    }
}
