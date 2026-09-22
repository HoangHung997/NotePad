using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed class AgentArtifactWindow : Window
{
    public AgentArtifactWindow(H2AgentEvidence evidence, IH2AgentAdapter? adapter = null)
    {
        Title = "Kết quả · H2 Notes"; Width = 760; Height = 620; MinWidth = 380; MinHeight = 320;
        Background = Brush.Parse("#FCFAF7");
        Content = new AgentArtifactView(evidence, adapter) { Margin = new Thickness(16) };
    }
}
