using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Nodepad.WinForms.Infrastructure;
using Nodepad.WinForms.Services;

namespace Nodepad.WinForms.Models;

public sealed class NoteDocument : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _noteKind = NoteKinds.General;
    private string _title = string.Empty;
    private string _projectName = string.Empty;
    private string _content = string.Empty;
    private string _tags = string.Empty;
    private string _paletteKey = "honey";
    private string _fontFamilyName = "Segoe UI";
    private double _fontSize = 16;
    private double _noteOpacity = 1.0;
    private bool _isPinned;
    private bool _isStarred;
    private bool _isArchived;
    private bool _isVisibleOnDesktop = true;
    private bool _isExpandedInProjectManager;
    private double _left = 140;
    private double _top = 120;
    private double _width = 380;
    private double _height = 460;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime _updatedAt = DateTime.UtcNow;
    private ObservableCollection<ChecklistItem> _checklistItems = [];
    private ObservableCollection<ProjectEntry> _projects = [];

    public NoteDocument()
    {
        AttachChecklistEvents(_checklistItems);
        AttachProjectEvents(_projects);
    }

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string NoteKind
    {
        get => _noteKind;
        set
        {
            if (SetProperty(ref _noteKind, value))
            {
                OnPropertyChanged(nameof(IsLegacyProjectNote));
                OnPropertyChanged(nameof(IsProjectHubNote));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(ProgressLabel));
                OnPropertyChanged(nameof(SummaryLabel));
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(SummaryLabel));
            }
        }
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(SummaryLabel));
            }
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(SummaryLabel));
            }
        }
    }

    public string Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value);
    }

    public string PaletteKey
    {
        get => _paletteKey;
        set
        {
            if (SetProperty(ref _paletteKey, value))
            {
                OnPropertyChanged(nameof(PaletteLabel));
                OnPropertyChanged(nameof(StyleSummary));
                OnPropertyChanged(nameof(NoteBackgroundHex));
                OnPropertyChanged(nameof(NoteSurfaceHex));
                OnPropertyChanged(nameof(NoteBorderHex));
                OnPropertyChanged(nameof(NoteAccentHex));
                OnPropertyChanged(nameof(NoteForegroundHex));
            }
        }
    }

    public string FontFamilyName
    {
        get => _fontFamilyName;
        set
        {
            if (SetProperty(ref _fontFamilyName, value))
            {
                OnPropertyChanged(nameof(StyleSummary));
            }
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (SetProperty(ref _fontSize, value))
            {
                OnPropertyChanged(nameof(StyleSummary));
            }
        }
    }

    public double NoteOpacity
    {
        get => _noteOpacity;
        set => SetProperty(ref _noteOpacity, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    public bool IsStarred
    {
        get => _isStarred;
        set => SetProperty(ref _isStarred, value);
    }

    public bool IsArchived
    {
        get => _isArchived;
        set => SetProperty(ref _isArchived, value);
    }

    public bool IsVisibleOnDesktop
    {
        get => _isVisibleOnDesktop;
        set
        {
            if (SetProperty(ref _isVisibleOnDesktop, value))
            {
                OnPropertyChanged(nameof(DesktopLabel));
            }
        }
    }

    public bool IsExpandedInProjectManager
    {
        get => _isExpandedInProjectManager;
        set => SetProperty(ref _isExpandedInProjectManager, value);
    }

    public double Left
    {
        get => _left;
        set => SetProperty(ref _left, value);
    }

    public double Top
    {
        get => _top;
        set => SetProperty(ref _top, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetProperty(ref _createdAt, value);
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value))
            {
                OnPropertyChanged(nameof(UpdatedLabel));
            }
        }
    }

    public ObservableCollection<ChecklistItem> ChecklistItems
    {
        get => _checklistItems;
        set
        {
            if (ReferenceEquals(_checklistItems, value))
            {
                return;
            }

            DetachChecklistEvents(_checklistItems);
            _checklistItems = value ?? [];
            AttachChecklistEvents(_checklistItems);
            OnPropertyChanged();
            RaiseChecklistSummaryChanged();
        }
    }

    [JsonIgnore]
    public bool IsLegacyProjectNote => string.Equals(NoteKind, NoteKinds.Project, StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<ProjectEntry> Projects
    {
        get => _projects;
        set
        {
            if (ReferenceEquals(_projects, value))
            {
                return;
            }

            DetachProjectEvents(_projects);
            _projects = value ?? [];
            AttachProjectEvents(_projects);
            OnPropertyChanged();
            RaiseProjectSummaryChanged();
        }
    }

    [JsonIgnore]
    public bool IsProjectHubNote => string.Equals(NoteKind, NoteKinds.ProjectHub, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (IsProjectHubNote)
            {
                return string.IsNullOrWhiteSpace(Title) ? "Project Hub" : Title.Trim();
            }

            if (IsLegacyProjectNote)
            {
                if (!string.IsNullOrWhiteSpace(ProjectName))
                {
                    return ProjectName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(Title))
                {
                    return Title.Trim();
                }

                return "Untitled Project";
            }

            if (!string.IsNullOrWhiteSpace(Title))
            {
                return Title.Trim();
            }

            var firstLine = RichTextDocumentSerializer.ExtractPlainText(Content)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(firstLine) ? "Untitled" : firstLine;
        }
    }

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var plainContent = RichTextDocumentSerializer.ExtractPlainText(Content);
            if (IsProjectHubNote)
            {
                var projectHubOverview = plainContent.Replace(Environment.NewLine, " ").Trim();
                if (!string.IsNullOrWhiteSpace(projectHubOverview))
                {
                    return projectHubOverview.Length <= 100 ? projectHubOverview : $"{projectHubOverview[..97]}...";
                }

                return ProjectCount switch
                {
                    0 => "All of your projects will live inside this one sticky note.",
                    1 => "1 project lives inside this sticky note.",
                    _ => $"{ProjectCount} projects live inside this sticky note."
                };
            }

            var compact = plainContent.Replace(Environment.NewLine, " ").Trim();
            return compact.Length switch
            {
                0 when IsLegacyProjectNote => "Project notes and context live here.",
                0 => "Empty note",
                <= 90 => compact,
                _ => $"{compact[..87]}..."
            };
        }
    }

    [JsonIgnore]
    public string PaletteLabel => NotePaletteCatalog.GetLabel(PaletteKey);

    [JsonIgnore]
    public string StyleSummary => $"{PaletteLabel} | {FontFamilyName} {FontSize:0}px";

    [JsonIgnore]
    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("dd/MM HH:mm");

    [JsonIgnore]
    public int TotalChecklistCount => IsProjectHubNote
        ? Projects.Sum(project => project.TotalChecklistCount)
        : ChecklistItems.Count(item => !string.IsNullOrWhiteSpace(item.Text));

    [JsonIgnore]
    public int CompletedChecklistCount => IsProjectHubNote
        ? Projects.Sum(project => project.CompletedChecklistCount)
        : ChecklistItems.Count(item => item.IsCompleted && !string.IsNullOrWhiteSpace(item.Text));

    [JsonIgnore]
    public int ProjectCount => Projects.Count(project =>
        !string.IsNullOrWhiteSpace(project.Name)
        || RichTextDocumentSerializer.HasPlainText(project.Notes)
        || project.TotalChecklistCount > 0);

    [JsonIgnore]
    public string ProgressLabel
    {
        get
        {
            if (IsProjectHubNote)
            {
                return ProjectCount switch
                {
                    0 => "No projects yet",
                    _ when TotalChecklistCount == 0 => $"{ProjectCount} projects",
                    _ => $"{CompletedChecklistCount}/{TotalChecklistCount} tasks done across {ProjectCount} projects"
                };
            }

            return IsLegacyProjectNote
                ? TotalChecklistCount == 0
                    ? "No tasks yet"
                    : $"{CompletedChecklistCount}/{TotalChecklistCount} done"
                : "General note";
        }
    }

    [JsonIgnore]
    public string DesktopLabel => IsVisibleOnDesktop ? "On desktop" : "Hidden";

    [JsonIgnore]
    public string SummaryLabel => IsProjectHubNote || IsLegacyProjectNote ? ProgressLabel : Preview;

    [JsonIgnore]
    public string NoteBackgroundHex => NotePaletteCatalog.Get(PaletteKey).BackgroundHex;

    [JsonIgnore]
    public string NoteSurfaceHex => NotePaletteCatalog.Get(PaletteKey).SurfaceHex;

    [JsonIgnore]
    public string NoteBorderHex => NotePaletteCatalog.Get(PaletteKey).BorderHex;

    [JsonIgnore]
    public string NoteAccentHex => NotePaletteCatalog.Get(PaletteKey).AccentHex;

    [JsonIgnore]
    public string NoteForegroundHex => NotePaletteCatalog.Get(PaletteKey).ForegroundHex;

    public bool Matches(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var token = searchText.Trim();
        return DisplayTitle.Contains(token, StringComparison.OrdinalIgnoreCase)
               || RichTextDocumentSerializer.ExtractPlainText(Content).Contains(token, StringComparison.OrdinalIgnoreCase)
               || Tags.Contains(token, StringComparison.OrdinalIgnoreCase)
               || ProjectName.Contains(token, StringComparison.OrdinalIgnoreCase)
               || ChecklistItems.Any(item => item.Text.Contains(token, StringComparison.OrdinalIgnoreCase))
               || Projects.Any(project => project.Matches(token));
    }

    public void EnsureInitialized()
    {
        ChecklistItems ??= [];
        Projects ??= [];
        AttachChecklistEvents(ChecklistItems);
        AttachProjectEvents(Projects);
    }

    public void TouchSummary()
    {
        RaiseChecklistSummaryChanged();
        RaiseProjectSummaryChanged();
    }

    private void AttachChecklistEvents(ObservableCollection<ChecklistItem> items)
    {
        items.CollectionChanged -= ChecklistItems_OnCollectionChanged;
        items.CollectionChanged += ChecklistItems_OnCollectionChanged;

        foreach (var item in items)
        {
            item.PropertyChanged -= ChecklistItem_OnPropertyChanged;
            item.PropertyChanged += ChecklistItem_OnPropertyChanged;
        }
    }

    private void DetachChecklistEvents(ObservableCollection<ChecklistItem> items)
    {
        items.CollectionChanged -= ChecklistItems_OnCollectionChanged;

        foreach (var item in items)
        {
            item.PropertyChanged -= ChecklistItem_OnPropertyChanged;
        }
    }

    private void ChecklistItems_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChecklistItem item in e.OldItems)
            {
                item.PropertyChanged -= ChecklistItem_OnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChecklistItem item in e.NewItems)
            {
                item.PropertyChanged -= ChecklistItem_OnPropertyChanged;
                item.PropertyChanged += ChecklistItem_OnPropertyChanged;
            }
        }

        RaiseChecklistSummaryChanged();
    }

    private void ChecklistItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChecklistItem.Text) or nameof(ChecklistItem.IsCompleted))
        {
            RaiseChecklistSummaryChanged();
        }
    }

    private void AttachProjectEvents(ObservableCollection<ProjectEntry> projects)
    {
        projects.CollectionChanged -= Projects_OnCollectionChanged;
        projects.CollectionChanged += Projects_OnCollectionChanged;

        foreach (var project in projects)
        {
            project.PropertyChanged -= Project_OnPropertyChanged;
            project.PropertyChanged += Project_OnPropertyChanged;
            project.EnsureInitialized();
        }
    }

    private void DetachProjectEvents(ObservableCollection<ProjectEntry> projects)
    {
        projects.CollectionChanged -= Projects_OnCollectionChanged;

        foreach (var project in projects)
        {
            project.PropertyChanged -= Project_OnPropertyChanged;
        }
    }

    private void Projects_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ProjectEntry project in e.OldItems)
            {
                project.PropertyChanged -= Project_OnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ProjectEntry project in e.NewItems)
            {
                project.EnsureInitialized();
                project.PropertyChanged -= Project_OnPropertyChanged;
                project.PropertyChanged += Project_OnPropertyChanged;
            }
        }

        RaiseProjectSummaryChanged();
    }

    private void Project_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectEntry.Name)
            or nameof(ProjectEntry.NextAction)
            or nameof(ProjectEntry.Notes)
            or nameof(ProjectEntry.References)
            or nameof(ProjectEntry.IsExpanded)
            or nameof(ProjectEntry.TotalChecklistCount)
            or nameof(ProjectEntry.CompletedChecklistCount)
            or nameof(ProjectEntry.ProgressLabel)
            or nameof(ProjectEntry.Preview)
            or nameof(ProjectEntry.HeaderLine))
        {
            RaiseProjectSummaryChanged();
        }
    }

    private void RaiseChecklistSummaryChanged()
    {
        OnPropertyChanged(nameof(TotalChecklistCount));
        OnPropertyChanged(nameof(CompletedChecklistCount));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(SummaryLabel));
    }

    private void RaiseProjectSummaryChanged()
    {
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(TotalChecklistCount));
        OnPropertyChanged(nameof(CompletedChecklistCount));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(SummaryLabel));
    }
}

