namespace Nodepad.Desktop.Models;

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();

    public List<NoteDocument> Notes { get; set; } = [];
}
