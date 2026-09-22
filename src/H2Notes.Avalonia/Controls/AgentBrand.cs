using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

internal static class AgentBrand
{
    public static Control Avatar()=>new Border { Width=36,Height=36,CornerRadius=new CornerRadius(18),Background=Brush.Parse("#A4573D"),
        Child=new TextBlock { Text="H2",FontSize=18,FontWeight=FontWeight.SemiBold,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center } };
    public static Control Heading()=>new StackPanel { Orientation=Orientation.Horizontal,Spacing=12,Children={Avatar(),
        new TextBlock { Text="H2 Agent",FontSize=14,FontWeight=FontWeight.SemiBold,VerticalAlignment=VerticalAlignment.Center } } };
}
