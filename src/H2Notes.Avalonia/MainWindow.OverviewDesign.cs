using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private double _overviewCardWidth = 360;
    private readonly List<Border> _overviewCards = [];
    private string _overviewSearch = "";
    private void InitializeOverviewDesign()
    {
        CommandCenterGroupFilter.IsVisible = false;
        var filters = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var item in CommandCenterGroupFilterItem.All)
        {
            var button = new Button { Content = item.Text, Margin = new Thickness(0,0,8,6) };
            button.Click += (_, _) =>
            {
                CommandCenterGroupFilter.SelectedItem = item;
                foreach (var other in filters.Children.OfType<Button>()) other.Classes.Set("accent",ReferenceEquals(other,button));
            };
            filters.Children.Add(button);
        }
        var search = new TextBox { Name = "OverviewSearch", PlaceholderText = "Tìm dự án, công việc, ghi chú…", Margin = new Thickness(0,0,0,12) };
        search.TextChanged += (_, _) => { _overviewSearch = search.Text?.Trim() ?? ""; RefreshCommandCenter(); };
        var toolbar = new StackPanel { Children = { search, filters } };
        var row = new RowDefinition(GridLength.Auto); CommandCenter.RowDefinitions.Insert(2,row);
        Grid.SetRow(CommandCenterAttentionSection,3);
        foreach (var child in CommandCenter.Children.Where(c => Grid.GetRow(c) == 3 && c != CommandCenterAttentionSection)) Grid.SetRow(child,4);
        Grid.SetRow(toolbar,2); CommandCenter.Children.Add(toolbar);
        CommandCenterList.ItemsPanel = new FuncTemplate<Panel>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        CommandCenterList.ItemTemplate = new FuncDataTemplate<CommandCenterProjectItem>((item, _) =>
        {
            if (item is null) return new Border();
            TextBlock Text(string value,int size=13,string color="#26303C") => new() { Text=value,FontSize=size,Foreground=Brush.Parse(color),TextWrapping=TextWrapping.Wrap };
            var title = Text(item.Name,17); title.FontWeight = FontWeight.SemiBold;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing=10 };
            header.Children.Add(new AppIcon(IconKind.Folder,23) { Foreground=Brush.Parse("#A4573D") });
            Grid.SetColumn(title,1); header.Children.Add(title);
            var more = AppIcon.Button(IconKind.More,"Tác vụ dự án");
            more.Click += (_,e) => { e.Handled=true; OpenProjectWorkspace(item.Board,item.Project); OpenProjectMenu(); };
            Grid.SetColumn(more,2); header.Children.Add(more);
            var body = new StackPanel { Spacing=12, Children={header,Text(item.ProgressText,14),
                new ProgressBar { Value=item.ProgressPercent,Maximum=100,Height=7,Foreground=Brush.Parse("#A4573D"),Background=Brush.Parse("#E8E5E1") },
                Text(item.NextText), new Separator() } };
            var states = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"),ColumnSpacing=12 };
            states.Children.Add(Text(item.AgentText,12,item.AttentionCount>0 ? "#A4573D" : "#657080"));
            var sync=Text(item.SyncText,12,"#657080"); Grid.SetColumn(sync,1); states.Children.Add(sync); body.Children.Add(states);
            var card=new Border { Name="OverviewProjectCard",Width=_overviewCardWidth,MinHeight=212,Padding=new Thickness(18),Margin=new Thickness(0,0,12,12),
                BorderThickness=new Thickness(1),BorderBrush=Brush.Parse(item.AttentionCount>0 ? "#BD7657" : "#DEDAD4"),CornerRadius=new CornerRadius(8),Background=Brushes.White,Child=body };
            _overviewCards.Add(card); card.DetachedFromVisualTree += (_,_) => _overviewCards.Remove(card); return card;
        });
    }
    private void UpdateOverviewCardWidth(double width)
    {
        var available=Math.Max(320,width-56-40);
        var columns=available>=1120 ? 3 : available>=730 ? 2 : 1;
        _overviewCardWidth=Math.Max(270,available/columns-40);
        foreach(var card in _overviewCards) card.Width=_overviewCardWidth;
    }
}
