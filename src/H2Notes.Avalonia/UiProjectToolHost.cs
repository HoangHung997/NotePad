using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia;

/// <summary>Keep reads and mutations on the same UI thread as manual project editing.</summary>
internal sealed class UiProjectToolHost(H2ProjectToolHost inner) : IH2ProjectToolHost, IH2ProjectToolVerifier
{
    private static T Invoke<T>(Func<T> action) => Dispatcher.UIThread.CheckAccess()
        ? action() : Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    public H2ProjectSummary ReadProject(Guid id) => Invoke(() => inner.ReadProject(id));
    public H2ProjectSummary ReadProjectForVerification(Guid id) => Invoke(() => inner.ReadProjectForVerification(id));
    public H2ProjectMutationReceipt AddTask(H2AddProjectTaskRequest request) => Invoke(() => inner.AddTask(request));
    public H2ProjectMutationReceipt AppendNote(H2AppendProjectNoteRequest request) => Invoke(() => inner.AppendNote(request));
    public H2ProjectMutationReceipt ReplaceNote(H2ReplaceProjectNoteRequest request) => Invoke(() => inner.ReplaceNote(request));
}
