namespace H2Notes.Core;

public sealed class DesktopSessionState
{
    public Guid? SelectedBoardId { get; set; }
    // Back to front, including the board only when its window is visible.
    public List<Guid> OpenWindowIds { get; set; } = [];
    public ProjectAiWindowState? ProjectAiWindow { get; set; }
}

// Window placement only. Conversations and drafts remain in the selected project.
public sealed class ProjectAiWindowState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsVisible { get; set; }
    public bool IsPinned { get; set; } = true;
    public int Left { get; set; } = 180;
    public int Top { get; set; } = 120;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 660;
}

public sealed record DesktopRestorePlan(NoteRecord Board, IReadOnlyList<NoteRecord> OpenWindows)
{
    public Guid? ProjectAiWindowId { get; init; }
    public static DesktopRestorePlan Create(SheetState state, bool systemStartup)
    {
        var boards = state.Notes.Where(n => n.IsBoard && !n.IsArchived).ToList();
        var board = boards.FirstOrDefault(n => n.Id == state.DesktopSession?.SelectedBoardId)
            ?? boards.FirstOrDefault(n => n.IsVisibleOnDesktop) ?? boards.FirstOrDefault();
        if (board is null) { board = new NoteRecord(); state.Notes.Add(board); }
        if (!state.SheetPreferences.RestoreVisibleNotes)
            return new(board, systemStartup ? [] : [board]);

        var ids = state.DesktopSession is { } session
            ? session.OpenWindowIds ?? []
            : state.Notes.Where(n => n.IsVisibleOnDesktop).Select(n => n.Id).ToList();
        var notes = state.Notes.Where(n => !n.IsArchived && (!n.IsBoard || n.Id == board.Id))
            .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        return new(board, ids.Distinct().Where(notes.ContainsKey).Select(id => notes[id]).ToList())
        {
            ProjectAiWindowId = state.DesktopSession?.ProjectAiWindow is { IsVisible: true } ai && board.Projects.Count > 0 ? ai.Id : null
        };
    }
}
