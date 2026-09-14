using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Nodepad.Desktop.Models;

namespace Nodepad.Desktop.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow(
        AppSettings settings,
        IEnumerable<NotePalette> paletteOptions,
        IEnumerable<string> fontOptions,
        IEnumerable<double> fontSizeOptions)
    {
        Settings = settings;
        PaletteOptions = new ObservableCollection<NotePalette>(paletteOptions);
        FontOptions = new ObservableCollection<string>(fontOptions);
        FontSizeOptions = new ObservableCollection<double>(fontSizeOptions);

        InitializeComponent();
        DataContext = this;
    }

    public AppSettings Settings { get; }
    public ObservableCollection<NotePalette> PaletteOptions { get; }
    public ObservableCollection<string> FontOptions { get; }
    public ObservableCollection<double> FontSizeOptions { get; }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
