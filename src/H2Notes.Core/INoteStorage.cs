namespace H2Notes.Core;

public interface INoteStorage
{
    string FilePath { get; }
    SheetState LoadOrImport(string? legacyPath = null);
    void Save(SheetState state);
    LegacyImportRecord ImportCopies(SheetState current, LegacyImportPreview preview);
}
