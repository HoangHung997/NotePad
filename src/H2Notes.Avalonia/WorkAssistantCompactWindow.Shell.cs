using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;

namespace H2Notes.Avalonia;

public sealed partial class WorkAssistantCompactWindow
{
    private readonly StackPanel _threadNavigation = new() { Spacing=6,Margin=new Thickness(12) };
    private Grid? _assistantBody;
    private Border? _assistantSidebar;
    private string _threadNavigationSignature="";
    public bool FullMode { get; private set; }
    public void SetFullMode(bool full)
    {
        FullMode=full; Width=full ? 1040 : 640; Height=full ? 760 : 610;
        UpdateAssistantNavigation();
    }
    private Control BuildAssistantShell(Control header,Control context,Control footer)
    {
        WindowDecorations=WindowDecorations.None;
        var body=new Grid { ColumnDefinitions=new ColumnDefinitions("0,*") }; _assistantBody=body;
        var sidebar=new Border { Background=Brush.Parse("#F3EEE7"),BorderBrush=Brush.Parse("#DFDAD3"),BorderThickness=new Thickness(0,0,1,0),
            Child=new ScrollViewer { Content=_threadNavigation } }; _assistantSidebar=sidebar; body.Children.Add(sidebar);
        var main=new Grid { RowDefinitions=new RowDefinitions("Auto,Auto,*") };
        main.Children.Add(_workspaceButton); Grid.SetRow(context,1); main.Children.Add(context);
        Grid.SetRow(_chatSurface,2); main.Children.Add(_chatSurface); _chatSurface.AttachComposer(footer);
        Grid.SetColumn(main,1); body.Children.Add(main);
        var root=new Grid { RowDefinitions=new RowDefinitions("Auto,*") }; root.Children.Add(header); Grid.SetRow(body,1); root.Children.Add(body);
        SizeChanged+=(_,_)=>UpdateAssistantNavigation();
        return new Border { Background=Brush.Parse("#FCFAF7"),BorderBrush=Brush.Parse("#D5D0CA"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Child=root };
    }
    private void UpdateAssistantNavigation()
    {
        if (_assistantBody is null || _assistantSidebar is null) return;
        var wide=Bounds.Width>=900;
        _assistantBody.ColumnDefinitions[0].Width=new GridLength(wide ? 200 : 0); _assistantSidebar.IsVisible=wide;
        var signature=string.Join("|",Threads.Select(t=>t.ThreadId+t.Title))+ConversationId;
        if(signature==_threadNavigationSignature) return;
        _threadNavigationSignature=signature; _threadNavigation.Children.Clear();
        var home=new Button { Content=AppIcon.Label(IconKind.Folder,"Dự án"),Classes={"quiet"} }; home.Click+=(_,_)=>OpenWorkspaceRequested?.Invoke();
        _threadNavigation.Children.Add(home);
        _threadNavigation.Children.Add(new TextBlock { Text="Trợ lý",FontSize=18,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#A4573D"),Margin=new Thickness(8,12) });
        _threadNavigation.Children.Add(new Separator());
        var add=new Button { Content="＋ Trao đổi mới",Classes={"accent"} }; add.Click+=(_,_)=>NewConversationRequested?.Invoke(); _threadNavigation.Children.Add(add);
        var search=new TextBox { PlaceholderText="Tìm trao đổi…",FontSize=12 }; _threadNavigation.Children.Add(search);
        var list=new StackPanel { Spacing=4 }; _threadNavigation.Children.Add(list);
        void Filter()
        {
            list.Children.Clear();
            foreach(var thread in Threads.Where(t=>string.IsNullOrWhiteSpace(search.Text)||t.Title.Contains(search.Text,StringComparison.OrdinalIgnoreCase)))
            {
                var button=new Button { Content=new TextBlock { Text=thread.Title,TextWrapping=TextWrapping.Wrap,FontSize=13 },
                    HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,BorderThickness=new Thickness(0),
                    Background=Brush.Parse(thread.ThreadId==ConversationId ? "#EEDCD1" : "#00FFFFFF") };
                button.Click+=(_,_)=>ConversationSelected?.Invoke(thread.ThreadId); list.Children.Add(button);
            }
        }
        search.TextChanged+=(_,_)=>Filter(); Filter();
    }
}
