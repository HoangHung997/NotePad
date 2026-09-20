namespace H2Notes.Core;

public sealed class DesktopSessionState
{
    public Guid? SelectedBoardId { get; set; }
    // Back to front, including the board only when its window is visible.
    public List<Guid> OpenWindowIds { get; set; } = [];
    // Machine-local placements keyed by stable note/notebook ID. In NAS mode this object is
    // persisted only through LocalConfiguration; ProjectWorkspaceStore never publishes it.
    public Dictionary<Guid, NoteWindowPlacementState> NoteWindows { get; set; } = [];
    public ProjectAiWindowState? ProjectAiWindow { get; set; }
}

public sealed class NoteWindowPlacementState
{
    public int Left { get; set; } = 120;
    public int Top { get; set; } = 100;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 740;
    public bool IsPinned { get; set; }

    public void Normalize(bool board)
    {
        var minWidth = board ? 640d : 320d;
        var minHeight = board ? 480d : 300d;
        Width = double.IsFinite(Width) ? Math.Clamp(Width, minWidth, 8_000) : (board ? 1160 : 1100);
        Height = double.IsFinite(Height) ? Math.Clamp(Height, minHeight, 8_000) : (board ? 810 : 740);
        Left = Math.Clamp(Left, -100_000, 100_000);
        Top = Math.Clamp(Top, -100_000, 100_000);
    }

    public static NoteWindowPlacementState From(NoteRecord note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var placement = new NoteWindowPlacementState
        {
            Left = note.IsBoard ? note.SheetLeft ?? 120 : (int)Math.Round(note.Left),
            Top = note.IsBoard ? note.SheetTop ?? 100 : (int)Math.Round(note.Top),
            Width = note.IsBoard ? note.SheetWidth ?? 1160 : note.Width,
            Height = note.IsBoard ? note.SheetHeight ?? 810 : note.Height,
            IsPinned = note.IsPinned
        };
        placement.Normalize(note.IsBoard);
        return placement;
    }

    public void ApplyTo(NoteRecord note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Normalize(note.IsBoard);
        note.IsPinned = IsPinned;
        if (note.IsBoard)
        {
            note.SheetLeft = Left;
            note.SheetTop = Top;
            note.SheetWidth = Width;
            note.SheetHeight = Height;
        }
        else
        {
            note.Left = Left;
            note.Top = Top;
            note.Width = Width;
            note.Height = Height;
        }
    }
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
