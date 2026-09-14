using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Nodepad.WinForms.Infrastructure;
using Nodepad.WinForms.Services;

namespace Nodepad.WinForms.Models;

public sealed class ProjectEntry : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _name = string.Empty;
    private string _nextAction = string.Empty;
    private string _notes = string.Empty;
    private string _references = string.Empty;
    private bool _isExpanded = true;
    private bool _isChecklistExpanded = true;
    private bool _isEditingName;
    private string _draftTaskText = string.Empty;
    private ObservableCollection<ChecklistItem> _checklistItems = [];

    public ProjectEntry()
    {
        AttachChecklistEvents(_checklistItems);
    }

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }
    }

    public string NextAction
    {
        get => _nextAction;
        set
        {
            if (SetProperty(ref _nextAction, value))
            {
                OnPropertyChanged(nameof(DisplayNextAction));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value))
            {
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(HeaderLine));
            }
        }
    }

    public string References
    {
        get => _references;
        set
        {
            if (SetProperty(ref _references, value))
            {
                OnPropertyChanged(nameof(ReferencesPreview));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsChecklistExpanded
    {
        get => _isChecklistExpanded;
        set => SetProperty(ref _isChecklistExpanded, value);
    }

    [JsonIgnore]
    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
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
            RaiseSummaryChanged();
        }
    }

    [JsonIgnore]
    public string DraftTaskText
    {
        get => _draftTaskText;
        set => SetProperty(ref _draftTaskText, value);
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled Project" : Name.Trim();

    [JsonIgnore]
    public ChecklistItem? CurrentChecklistItem => ChecklistItems.FirstOrDefault(item =>
        !item.IsCompleted && !string.IsNullOrWhiteSpace(item.Text));

    [JsonIgnore]
    public string DisplayNextAction
    {
        get
        {
            if (CurrentChecklistItem is not null)
            {
                return CurrentChecklistItem.Text.Trim();
            }

            return string.IsNullOrWhiteSpace(NextAction) ? "No next action" : NextAction.Trim();
        }
    }

    [JsonIgnore]
    public int TotalChecklistCount => ChecklistItems.Count(item => !string.IsNullOrWhiteSpace(item.Text));

    [JsonIgnore]
    public int CompletedChecklistCount => ChecklistItems.Count(item => item.IsCompleted && !string.IsNullOrWhiteSpace(item.Text));

    [JsonIgnore]
    public string ProgressLabel => TotalChecklistCount == 0 ? "No tasks yet" : $"{CompletedChecklistCount}/{TotalChecklistCount} done";

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var compact = RichTextDocumentSerializer.ExtractPlainText(Notes).Replace(Environment.NewLine, " ").Trim();
            return compact.Length switch
            {
                0 => "No project notes yet.",
                <= 120 => compact,
                _ => $"{compact[..117]}..."
            };
        }
    }

    [JsonIgnore]
    public string ReferencesPreview
    {
        get
        {
            var compact = References.Replace(Environment.NewLine, " | ").Trim();
            return compact.Length switch
            {
                0 => "No references yet.",
                <= 120 => compact,
                _ => $"{compact[..117]}..."
            };
        }
    }

    [JsonIgnore]
    public string HeaderLine => $"{ProgressLabel}  |  Next: {DisplayNextAction}";

    public bool Matches(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var token = searchText.Trim();
        return DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)
               || NextAction.Contains(token, StringComparison.OrdinalIgnoreCase)
               || RichTextDocumentSerializer.ExtractPlainText(Notes).Contains(token, StringComparison.OrdinalIgnoreCase)
               || References.Contains(token, StringComparison.OrdinalIgnoreCase)
               || ChecklistItems.Any(item => item.Text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public void EnsureInitialized()
    {
        ChecklistItems ??= [];
        AttachChecklistEvents(ChecklistItems);
    }

    public void TouchSummary()
    {
        RaiseSummaryChanged();
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

        RaiseSummaryChanged();
    }

    private void ChecklistItem_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChecklistItem.Text) or nameof(ChecklistItem.IsCompleted))
        {
            RaiseSummaryChanged();
        }
    }

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(CurrentChecklistItem));
        OnPropertyChanged(nameof(DisplayNextAction));
        OnPropertyChanged(nameof(TotalChecklistCount));
        OnPropertyChanged(nameof(CompletedChecklistCount));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(ReferencesPreview));
        OnPropertyChanged(nameof(HeaderLine));
    }
}

