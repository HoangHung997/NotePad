using Avalonia.Controls;
using Avalonia.Layout;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    public ProjectAiWindow? DetachedAiWindow { get; private set; }
    internal IEnumerable<ProjectRecord> AiProjects => _board.Projects;
    public void SelectAiProject(Guid id)
    {
        if (_board.Projects.FirstOrDefault(p => p.Id == id) is { } project) SelectCurrent(project);
    }
    public void ShowProjectAiWindow()
    {
        if (_notesProject is null) { _app.ShowMain(); return; }
        var session = _app.State.DesktopSession ??= new();
        var placement = session.ProjectAiWindow ??= new();
        if (DetachedAiWindow is null)
        {
            DetachedAiWindow = new ProjectAiWindow(_app, this, placement);
            _app.TrackWindow(DetachedAiWindow);
        }
        // Move the same editor, not a second chat session that could overwrite its draft.
        _chat.MoveComposerTo(null);
        DetachChatHost(AiHost); _chat.SetDetached(true); _chat.SetCompact(false);
        DetachedAiWindow.AttachChat(_chat); DetachedAiWindow.UpdateProject(_notesProject);
        placement.IsVisible = true; DetachedAiWindow.Show(); DetachedAiWindow.Activate();
        ApplyResponsive(); _app.ScheduleSave();
    }
    public void DockProjectAi(string mode) => SetAiDock(mode);
    internal static void DetachChatHost(ContentControl host)
    {
        if (host.Content is null) return;
        var root = TopLevel.GetTopLevel(host);
        host.Content = null;
        host.Presenter?.UpdateChild();
        // Drain the old root while the editor is detached, before assigning a new
        // layout root. Otherwise queued arrange work can reference the moved editor.
        root?.UpdateLayout();
    }
    private void ReturnProjectAiToBoard()
    {
        if (DetachedAiWindow is null) return;
        DetachedAiWindow.Flush(); DetachedAiWindow.ReleaseChat(); DetachedAiWindow.Hide();
        if (_app.State.DesktopSession?.ProjectAiWindow is { } placement) placement.IsVisible = false;
        AiHost.Content = _chat; _chat.SetDetached(false);
    }
}
