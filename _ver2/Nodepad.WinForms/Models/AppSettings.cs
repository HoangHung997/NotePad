using Nodepad.WinForms.Infrastructure;

namespace Nodepad.WinForms.Models;

public sealed class AppSettings : ObservableObject
{
    private string _profileName = "Hoang Hung";
    private string _workspaceLabel = "Desktop notes first, project tracking where it actually helps.";
    private string _defaultPaletteKey = "honey";
    private string _defaultFontFamily = "Segoe UI";
    private double _defaultFontSize = 16;
    private string _searchText = string.Empty;
    private bool _favoritesOnly;
    private bool _showArchived;
    private bool _showNotesOnStartup = true;
    private string _activeFilter = "desktop";
    private string _projectExplorerViewMode = "list";

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string WorkspaceLabel
    {
        get => _workspaceLabel;
        set => SetProperty(ref _workspaceLabel, value);
    }

    public string DefaultPaletteKey
    {
        get => _defaultPaletteKey;
        set => SetProperty(ref _defaultPaletteKey, value);
    }

    public string DefaultFontFamily
    {
        get => _defaultFontFamily;
        set => SetProperty(ref _defaultFontFamily, value);
    }

    public double DefaultFontSize
    {
        get => _defaultFontSize;
        set => SetProperty(ref _defaultFontSize, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set => SetProperty(ref _favoritesOnly, value);
    }

    public bool ShowArchived
    {
        get => _showArchived;
        set => SetProperty(ref _showArchived, value);
    }

    public bool ShowNotesOnStartup
    {
        get => _showNotesOnStartup;
        set => SetProperty(ref _showNotesOnStartup, value);
    }

    public string ActiveFilter
    {
        get => _activeFilter;
        set => SetProperty(ref _activeFilter, value);
    }

    public string ProjectExplorerViewMode
    {
        get => _projectExplorerViewMode;
        set => SetProperty(ref _projectExplorerViewMode, value);
    }
}

