using Nodepad.WinForms.Infrastructure;

namespace Nodepad.WinForms.Models;

public sealed class ChecklistItem : ObservableObject
{
    private Guid _id = Guid.NewGuid();
    private string _text = string.Empty;
    private bool _isCompleted;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                OnPropertyChanged(nameof(StatusGlyph));
            }
        }
    }

    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? "Untitled task" : Text.Trim();

    public string StatusGlyph => IsCompleted ? "x" : "o";
}

