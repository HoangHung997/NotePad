using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using Nodepad.Desktop.Models;

namespace Nodepad.Desktop.Services;

public sealed class AppStorage
{
    private readonly string _statePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public AppStorage(string? statePath = null)
    {
        var appFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nodepad");

        Directory.CreateDirectory(appFolder);
        _statePath = statePath ?? Path.Combine(appFolder, "state.json");
    }

    public AppState Load()
    {
        if (!File.Exists(_statePath))
        {
            return new AppState();
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize<AppState>(json, _serializerOptions) ?? new AppState();
            Normalize(state);
            return state;
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        Normalize(state);
        var json = JsonSerializer.Serialize(state, _serializerOptions);
        File.WriteAllText(_statePath, json);
    }

    private static void Normalize(AppState state)
    {
        state.Settings ??= new AppSettings();
        state.Notes ??= [];

        foreach (var note in state.Notes)
        {
            NormalizeNote(note);
        }

        ConsolidateProjectHub(state);

        foreach (var note in state.Notes)
        {
            NormalizeNote(note);
        }
    }

    private static void NormalizeNote(NoteDocument note)
    {
        note.NoteKind = string.IsNullOrWhiteSpace(note.NoteKind) ? NoteKinds.General : note.NoteKind.Trim();
        note.Title ??= string.Empty;
        note.ProjectName ??= string.Empty;
        note.Content ??= string.Empty;
        note.Tags ??= string.Empty;
        note.PaletteKey = string.IsNullOrWhiteSpace(note.PaletteKey) ? "honey" : note.PaletteKey;
        note.FontFamilyName = string.IsNullOrWhiteSpace(note.FontFamilyName) ? "Segoe UI" : note.FontFamilyName;
        note.ChecklistItems ??= [];
        note.Projects ??= [];
        note.EnsureInitialized();

        foreach (var project in note.Projects)
        {
            project.Name = StripLeadingOrdinalPrefix(project.Name);
            project.NextAction ??= string.Empty;
            project.Notes ??= string.Empty;
            project.References ??= string.Empty;
            project.ChecklistItems ??= [];
            project.EnsureInitialized();
            MigrateProjectFields(project);
        }

        if (note.IsLegacyProjectNote && string.IsNullOrWhiteSpace(note.ProjectName))
        {
            note.ProjectName = string.IsNullOrWhiteSpace(note.Title) ? "Untitled Project" : note.Title;
        }

        if (note.IsProjectHubNote && string.IsNullOrWhiteSpace(note.Title))
        {
            note.Title = "Project Hub";
        }

        if (note.FontSize <= 0)
        {
            note.FontSize = 16;
        }

        if (note.NoteOpacity > 1)
        {
            note.NoteOpacity = 1;
        }
        else if (note.NoteOpacity < 0.01)
        {
            note.NoteOpacity = 0.01;
        }

        if (note.Width < 280)
        {
            note.Width = note.IsProjectHubNote ? 520 : 380;
        }

        if (note.Height < 320)
        {
            note.Height = note.IsProjectHubNote ? 680 : 460;
        }

        if (note.CreatedAt == default)
        {
            note.CreatedAt = DateTime.UtcNow;
        }

        if (note.UpdatedAt == default)
        {
            note.UpdatedAt = note.CreatedAt;
        }
    }

    private static void ConsolidateProjectHub(AppState state)
    {
        var hubs = state.Notes.Where(note => note.IsProjectHubNote).ToList();
        var legacyProjectNotes = state.Notes.Where(note => note.IsLegacyProjectNote).ToList();

        if (hubs.Count == 0 && legacyProjectNotes.Count == 0)
        {
            return;
        }

        var hub = hubs.FirstOrDefault();
        if (hub is null)
        {
            var seed = legacyProjectNotes
                .OrderBy(note => note.CreatedAt)
                .FirstOrDefault();

            hub = new NoteDocument
            {
                Id = Guid.NewGuid(),
                NoteKind = NoteKinds.ProjectHub,
                Title = "Project Hub",
                Content = string.Empty,
                Tags = seed?.Tags ?? string.Empty,
                PaletteKey = seed?.PaletteKey ?? "honey",
                FontFamilyName = seed?.FontFamilyName ?? "Segoe UI",
                FontSize = seed?.FontSize ?? 16,
                NoteOpacity = seed?.NoteOpacity ?? 1,
                IsPinned = seed?.IsPinned ?? false,
                IsStarred = seed?.IsStarred ?? false,
                IsArchived = false,
                IsVisibleOnDesktop = legacyProjectNotes.Any(note => note.IsVisibleOnDesktop),
                Left = seed?.Left ?? 180,
                Top = seed?.Top ?? 120,
                Width = Math.Max(seed?.Width ?? 0, 520),
                Height = Math.Max(seed?.Height ?? 0, 680),
                CreatedAt = seed?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = legacyProjectNotes.Select(note => note.UpdatedAt).DefaultIfEmpty(DateTime.UtcNow).Max(),
                Projects = []
            };
            state.Notes.Add(hub);
        }

        foreach (var extraHub in hubs.Skip(1).ToList())
        {
            MergeProjects(hub, extraHub.Projects);
            if (!RichTextDocumentSerializer.HasPlainText(hub.Content))
            {
                hub.Content = extraHub.Content;
            }

            hub.IsVisibleOnDesktop |= extraHub.IsVisibleOnDesktop;
            state.Notes.Remove(extraHub);
        }

        foreach (var legacyNote in legacyProjectNotes)
        {
            hub.Projects.Add(ConvertLegacyProjectNote(legacyNote));
            hub.IsVisibleOnDesktop |= legacyNote.IsVisibleOnDesktop;
            hub.UpdatedAt = legacyNote.UpdatedAt > hub.UpdatedAt ? legacyNote.UpdatedAt : hub.UpdatedAt;
            state.Notes.Remove(legacyNote);
        }
    }

    private static void MergeProjects(NoteDocument hub, IEnumerable<ProjectEntry> projects)
    {
        foreach (var project in projects)
        {
            hub.Projects.Add(CloneProjectEntry(project));
        }
    }

    private static ProjectEntry ConvertLegacyProjectNote(NoteDocument legacyNote)
    {
        var entry = new ProjectEntry
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(legacyNote.ProjectName) ? legacyNote.DisplayTitle : legacyNote.ProjectName,
            NextAction = string.Empty,
            Notes = legacyNote.Content,
            References = string.Empty,
            IsExpanded = legacyNote.IsExpandedInProjectManager,
            ChecklistItems = new ObservableCollection<ChecklistItem>(
                legacyNote.ChecklistItems.Select(item => new ChecklistItem
                {
                    Id = Guid.NewGuid(),
                    Text = item.Text,
                    IsCompleted = item.IsCompleted
                }))
        };
        entry.TouchSummary();
        return entry;
    }

    private static ProjectEntry CloneProjectEntry(ProjectEntry source)
    {
        var clone = new ProjectEntry
        {
            Id = Guid.NewGuid(),
            Name = source.Name,
            NextAction = source.NextAction,
            Notes = source.Notes,
            References = source.References,
            IsExpanded = source.IsExpanded,
            IsChecklistExpanded = source.IsChecklistExpanded,
            ChecklistItems = new ObservableCollection<ChecklistItem>(
                source.ChecklistItems.Select(item => new ChecklistItem
                {
                    Id = Guid.NewGuid(),
                    Text = item.Text,
                    IsCompleted = item.IsCompleted
                }))
        };
        clone.TouchSummary();
        return clone;
    }

    private static void MigrateProjectFields(ProjectEntry project)
    {
        var legacyNextAction = project.NextAction.Trim();
        if (!string.IsNullOrWhiteSpace(legacyNextAction)
            && !project.ChecklistItems.Any(item =>
                string.Equals(
                    RichTextDocumentSerializer.ExtractPlainText(item.Text).Trim(),
                    legacyNextAction,
                    StringComparison.OrdinalIgnoreCase)))
        {
            project.ChecklistItems.Insert(0, new ChecklistItem
            {
                Id = Guid.NewGuid(),
                Text = legacyNextAction,
                IsCompleted = false
            });
        }

        project.NextAction = string.Empty;
    }

    private static string StripLeadingOrdinalPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value, @"^\s*\d+\.\s*", string.Empty);
    }
}
