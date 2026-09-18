using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    public void RefreshAfterExternalSync()
    {
        if (_board is null || Sheet is null) return;

        var selectedId = _notesProject?.Id ?? _board.SelectedProjectId;
        var current = selectedId is { } id ? _board.Projects.FirstOrDefault(p => p.Id == id) : null;
        current ??= _board.Projects.FirstOrDefault();

        if (_notesProject?.Id != current?.Id || (current is not null && !ReferenceEquals(_notesProject, current)))
        {
            SelectCurrent(current);
            return;
        }

        if (!Sheet.IsEditing) Sheet.Refresh();
        if (_notesProject is not null && !NotesEditor.HasChanges && !NotesEditor.IsKeyboardFocusWithin)
        {
            NotesEditor.Load(_notesProject.ReadNotes());
            _loadedNotesSource = _notesProject.NotesRich;
        }
        _chat.RefreshFromModel();
        DetachedAiWindow?.UpdateProject(_notesProject);
        UpdateSummary();
    }
}
