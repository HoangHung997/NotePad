using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

internal static class SettingsFrame
{
    public static Control Build(Window window, Control content, string selected, Action<string>? navigate)
    {
        window.Width=1040; window.Height=760; window.MinWidth=560; window.MinHeight=600; window.MaxHeight=double.PositiveInfinity;
        window.SizeToContent=SizeToContent.Manual; window.CanResize=true; window.WindowDecorations=WindowDecorations.None;
        var root=new Grid { RowDefinitions=new RowDefinitions("44,*") };
        var header=new Grid { ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto,Auto"),Margin=new Thickness(16,4) };
        var logo=new Border { Width=28,Height=28,CornerRadius=new CornerRadius(6),Background=Brush.Parse("#A4573D"),Child=new TextBlock { Text="H2",Foreground=Brushes.White,FontSize=16,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center } };
        header.Children.Add(logo); var title=new TextBlock { Text="H2 Notes",FontSize=18,FontWeight=FontWeight.SemiBold,Margin=new Thickness(12,0),VerticalAlignment=VerticalAlignment.Center }; Grid.SetColumn(title,1);header.Children.Add(title);
        var pin=AppIcon.Button(IconKind.Pin,"Ghim trên cùng");pin.Click+=(_,_)=>window.Topmost=!window.Topmost;Grid.SetColumn(pin,2);header.Children.Add(pin);
        var close=AppIcon.Button(IconKind.Close,"Đóng cài đặt");close.Click+=(_,_)=>window.Close();Grid.SetColumn(close,3);header.Children.Add(close);root.Children.Add(header);
        DesktopWindowChrome.Attach(window,header);
        var body=new Grid { ColumnDefinitions=new ColumnDefinitions("200,*") };Grid.SetRow(body,1);root.Children.Add(body);
        var nav=new StackPanel { Spacing=8,Margin=new Thickness(12,18) };nav.Children.Add(new TextBlock { Text="Cài đặt",FontSize=20,FontWeight=FontWeight.SemiBold,Margin=new Thickness(8,0,0,16) });
        var buttons=new List<(string Label,Button Button)>();
        foreach(var label in new[]{"Giao diện","Kết nối AI","Dữ liệu và đồng bộ","Trợ lý desktop","Tiện ích"})
        {
            var button=new Button { Content=new TextBlock { Text=label,TextWrapping=TextWrapping.Wrap },HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,
                Background=Brush.Parse(label==selected ? "#EEDCD1" : "#00FFFFFF"),BorderThickness=new Thickness(0),Padding=new Thickness(12,12) };
            button.IsHitTestVisible=navigate is not null;button.Focusable=navigate is not null;
            button.Click+=(_,_)=>
            {
                navigate?.Invoke(label);
                foreach(var item in buttons)item.Button.Background=Brush.Parse(item.Label==label ? "#EEDCD1" : "#00FFFFFF");
            };
            buttons.Add((label,button));nav.Children.Add(button);
        }
        var sidebar=new Border { Background=Brush.Parse("#F3EEE7"),Child=nav };body.Children.Add(sidebar);Grid.SetColumn(content,1);body.Children.Add(content);
        window.SizeChanged+=(_,_)=> { body.ColumnDefinitions[0].Width=new GridLength(window.Bounds.Width<800 ? 150 : 200); };
        return new Border { BorderThickness=new Thickness(1),BorderBrush=Brush.Parse("#D7D1C9"),CornerRadius=new CornerRadius(6),Child=root };
    }
}
