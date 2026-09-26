using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Reflection;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;
using NeraSpreadSheet.Scrolling;

if (args.Length == 4 && args[0] == "--ar040-job-probe")
    return H2AgentLongJobTests.Probe(args[1], args[2], args[3]);
if (args.Length == 4 && args[0] == "--ar040-process-probe")
    return H2AgentProcessDrainTests.Probe(args[1], args[2], args[3]);
if (args.Length == 3 && args[0] == "--ar031-crash-probe")
    return H2AgentArchiveJournalTests.Probe(args[1], args[2]);
if (args.Length >= 2 && args[0] == "--desktop-session-probe")
    return DesktopSessionProbe.Run(args[1], args.Contains("--startup"));
if (args.Length >= 2 && args[0] == "--live-ai-probe" && args.Contains("--allow-live-ai"))
    return await LiveAiProbe.Run(args);
if (args.Length == 3 && args[0] == "--agent-chat-live-probe" && args[2] == "--allow-live-ai")
    return await H2AgentChatLiveProbe.Run(args[1]);
if (args.Length == 4 && args[0] == "--agent-capability-live-probe" && args[3] == "--allow-live-ai")
    return await H2AgentCapabilityLiveProbe.Run(args[1], args[2]);
if (args.Length == 2 && args[0] == "--office-fixture-manifest")
    return await H2AgentCapabilityLiveProbe.OfficeManifest(args[1]);
if (args.Length == 3 && args[0] == "--word-cv-live-probe" && args[2] == "--allow-live-office")
    return await H2WordCvLiveProbe.Run(args[1]);
if (args.Length == 3 && args[0] == "--word-cv-close-fixtures" && args[2] == "--allow-live-office")
    return H2WordCvLiveProbe.CloseFixtures(args[1]);
if (args.Length >= 3 && args[0] == "--ollama-stream-probe" && args.Contains("--allow-live-ai"))
{
    // Explicit diagnostic: synthetic text only, no profiles, credentials or project files.
    using var client = new AiClient();
    var profile = new AiProfile { BaseUrl = args[1], Model = args[2], TimeoutSeconds = 180, OllamaThinking = args.Contains("--think") ? true : null };
    try
    {
        var models = await client.ListModels(profile, "");
        var reasoningChars = 0; var answerChars = 0;
        await foreach (var item in client.StreamEvents(profile, "", [new("user", "B must follow A. C must follow B. Return only their execution order.")]))
            if (item.Kind == AiStreamEventKind.Text) answerChars += item.Text.Length; else reasoningChars += item.Text.Length;
        Console.WriteLine(JsonSerializer.Serialize(new { Connected = true, Models = models.Count, profile.Model, ReasoningChars = reasoningChars, AnswerChars = answerChars }));
        return answerChars > 0 ? 0 : 1;
    }
    catch (Exception ex) { Console.WriteLine(AiFailure.Describe(ex)); return 1; }
}

var passed = 0;
var failed = 0;
// UI fixtures call LocalSettings.Save just like the app. Never let them write user settings,
// drafts or Agent state. Child restart probes inherit this process-local override.
var isolatedSettings = Path.Combine(Path.GetTempPath(), "H2Notes-test-settings-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable(H2Notes.Avalonia.LocalConfiguration.SettingsDirectoryEnvironmentVariable, isolatedSettings);
var filterIndex = Array.IndexOf(args, "--filter");
var testFilter = filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : null;
void Test(string name, Action action)
{
    if (testFilter is not null && !name.Contains(testFilter, StringComparison.OrdinalIgnoreCase)) return;
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
void True(bool value) { if (!value) throw new Exception("Expected true"); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

Test("Plain text round trip", () => Equal("Ghi chú\nTiếng Việt", RichDocument.FromLegacy("Ghi chú\nTiếng Việt").Text));
Test("Import WPF styled spans without running XAML", () =>
{
    var doc = RichDocument.FromLegacy("<Section xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' FontFamily='Cambria' FontSize='18'><Paragraph><Run>Đường </Run><Run FontWeight='Bold' Foreground='#FF0000'>Ninh Thuận</Run></Paragraph><Paragraph><Italic>Ghi chú</Italic></Paragraph></Section>");
    Equal("Đường Ninh Thuận\nGhi chú", doc.Text); True(doc.Runs.Any(r => r.Style.Bold && r.Style.Color == "#FF0000")); True(doc.Runs.Any(r => r.Style.Italic));
});
Test("XML external entity is not evaluated", () => { var value = "<!DOCTYPE a [<!ENTITY t SYSTEM 'file:///private'>]><Section>&t;</Section>"; Equal(value, RichDocument.FromLegacy(value).Text); });
Test("Format only selected text", () => { var doc = RichDocument.Plain("alpha beta"); doc.Format(6, 4, s => s with { Bold = true }); True(!doc.StyleAt(0).Bold); True(doc.StyleAt(6).Bold); Equal("alpha beta", doc.Text); });
Test("Insertion preserves surrounding formatting", () => { var doc = RichDocument.Plain("abCD"); doc.Format(2, 2, s => s with { Color = "#FF0000" }); doc.Replace(1, 1, "xyz"); Equal("axyzCD", doc.Text); Equal("#FF0000", doc.StyleAt(4).Color); });
Test("Deletion crossing spans", () => { var doc = RichDocument.Plain("abcdef"); doc.Format(2, 2, s => s with { Italic = true }); doc.Replace(1, 4, ""); Equal("af", doc.Text); });
Test("Draft clone does not mutate original", () => { var doc = RichDocument.Plain("hello"); var draft = doc.Clone(); draft.Format(0, 5, s => s with { Underline = true }); True(!doc.StyleAt(0).Underline); });
Test("Invalid rich-text range rejected", () => Throws<ArgumentOutOfRangeException>(() => RichDocument.Plain("a").Replace(2, 1, "b")));
Test("Selected formatting reports shared and mixed properties independently", () =>
{
    var doc = RichDocument.Plain("abcd");
    doc.Format(0, 4, s => s with { Font = "Cambria", Italic = true });
    doc.Format(0, 2, s => s with { Bold = true, Size = 20, Color = "#FF0000", Highlight = "#FFFF00" });
    var mixed = doc.SelectionStyleAt(0, 4);
    Equal("Cambria", mixed.Font); Equal(true, mixed.Italic); Equal<bool?>(null, mixed.Bold);
    Equal<double?>(null, mixed.Size); Equal<string?>(null, mixed.Color); True(mixed.MixedHighlight);
    Equal(true, doc.SelectionStyleAt(0, 2).Bold); Equal(false, doc.SelectionStyleAt(2, 2).Bold);
    Equal(true, doc.SelectionStyleAt(2, 0).Bold);
});

Test("Local configuration preserves friendly path and resolved workspace endpoint metadata", () =>
{
    var id = Guid.NewGuid();
    var config = new H2Notes.Avalonia.LocalConfiguration
    {
        DataFolder = @"X:\Dữ liệu Hưng\.Note",
        WorkspaceLocation = new WorkspaceLocationProfile(
            id,
            WorkspaceLocationKind.MappedNetwork,
            @"X:\Dữ liệu Hưng\.Note",
            @"\\NAS-SERVER\Share\Dữ liệu Hưng\.Note",
            @"\\NAS-SERVER\Share\Dữ liệu Hưng\.Note",
            DateTime.UtcNow)
    };
    var json = JsonSerializer.Serialize(config);
    var restored = JsonSerializer.Deserialize<H2Notes.Avalonia.LocalConfiguration>(json)!;
    Equal(config.DataFolder, restored.DataFolder);
    Equal(id, restored.WorkspaceLocation!.WorkspaceId);
    Equal(WorkspaceLocationKind.MappedNetwork, restored.WorkspaceLocation.Kind);
    Equal(config.WorkspaceLocation.DisplayPath, restored.WorkspaceLocation.DisplayPath);
    Equal(config.WorkspaceLocation.CanonicalPath, restored.WorkspaceLocation.CanonicalPath);
});

Test("Work Assistant settings round-trip only through local configuration", () =>
{
    var config = new H2Notes.Avalonia.LocalConfiguration
    {
        DeviceId = "test-device",
        WorkAssistant = new H2Notes.Avalonia.WorkAssistantSettings
        {
            Enabled = true,
            StartWithH2 = false,
            StartCollapsed = false,
            AlwaysOnTop = false,
            Hotkey = "Ctrl+Alt+Space",
            BubblePosition = new H2Notes.Avalonia.WorkAssistantBubblePosition(125.5, 340.25),
            PreferredMonitor = @"\\.\DISPLAY2",
            NotificationPreference = "attention-only"
        }
    };

    var json = JsonSerializer.Serialize(config);
    var restored = JsonSerializer.Deserialize<H2Notes.Avalonia.LocalConfiguration>(json)!;
    var wa = restored.WorkAssistant;

    True(wa.Enabled);
    True(!wa.StartWithH2);
    True(!wa.StartCollapsed);
    True(!wa.AlwaysOnTop);
    Equal("Ctrl+Alt+Space", wa.Hotkey);
    Equal(125.5, wa.BubblePosition!.XDip);
    Equal(340.25, wa.BubblePosition.YDip);
    Equal(@"\\.\DISPLAY2", wa.PreferredMonitor);
    Equal("attention-only", wa.NotificationPreference);

    var legacy = JsonSerializer.Deserialize<H2Notes.Avalonia.LocalConfiguration>(
        "{\"DeviceId\":\"legacy-device\"}")!;
    True(!legacy.WorkAssistant.Enabled);
    True(legacy.WorkAssistant.StartWithH2);
    True(legacy.WorkAssistant.StartCollapsed);
    True(legacy.WorkAssistant.AlwaysOnTop);
    Equal("Ctrl+Shift+Space", legacy.WorkAssistant.Hotkey);
    Equal("attention-and-completed", legacy.WorkAssistant.NotificationPreference);
});

Test("Work Assistant settings never enter shared workspace models", () =>
{
    var shared = new SheetState
    {
        Notes =
        [
            new NoteRecord
            {
                Projects =
                [
                    new ProjectRecord
                    {
                        Name = "Shared project",
                        Layout = new ProjectLayout()
                    }
                ]
            }
        ]
    };
    var json = JsonSerializer.Serialize(shared);

    foreach (var forbidden in new[]
    {
        "WorkAssistant", "StartWithH2", "StartCollapsed", "AlwaysOnTop",
        "Hotkey", "BubblePosition", "PreferredMonitor", "NotificationPreference"
    })
        True(!json.Contains(forbidden, StringComparison.Ordinal));

    foreach (var type in new[] { typeof(SheetState), typeof(ProjectRecord), typeof(ProjectLayout) })
        True(type.GetProperties().All(property =>
            !property.Name.Contains("WorkAssistant", StringComparison.OrdinalIgnoreCase)
            && property.PropertyType != typeof(H2Notes.Avalonia.WorkAssistantSettings)
            && property.PropertyType != typeof(H2Notes.Avalonia.WorkAssistantBubblePosition)));
});

Test("Project move preserves identity and child order", () =>
{
    var board = SheetStorage.Demo().Notes[0]; var project = board.Projects[2]; var ids = project.ChecklistItems.Select(t => t.Id).ToArray();
    True(SheetOperations.MoveProject(board, project.Id, board.Projects[0].Id, false)); True(ReferenceEquals(project, board.Projects[0])); True(ids.SequenceEqual(project.ChecklistItems.Select(t => t.Id)));
    Equal(1, SheetOperations.Rows(board, "").First().Ordinal);
});
Test("Task reorder updates Next only on move", () =>
{
    var p = SheetStorage.Demo().Notes[0].Projects[0]; var before = p.Next!; var target = p.ChecklistItems[2];
    Equal(before.Id, p.Next!.Id); True(SheetOperations.MoveTask(p, target.Id, before.Id, false)); Equal(target.Id, p.Next!.Id);
});
Test("Foreign task cannot be moved into another project", () =>
{
    var b = SheetStorage.Demo().Notes[0]; True(!SheetOperations.MoveTask(b.Projects[0], b.Projects[1].ChecklistItems[0].Id, b.Projects[0].ChecklistItems[0].Id, false)); Equal(3, b.Projects[0].ChecklistItems.Count);
});
Test("Search keeps parent and original ordinal", () =>
{
    var b = SheetStorage.Demo().Notes[0]; b.Projects[3].ChecklistItems[1].TextRich = RichDocument.Plain("needle");
    var rows = SheetOperations.Rows(b, "needle"); Equal(2, rows.Count); Equal(4, rows[0].Ordinal); True(rows[0].IsProject);
});
Test("Collapse hides checklist, not the model", () => { var b = SheetStorage.Demo().Notes[0]; foreach (var p in b.Projects) p.IsExpanded = false; Equal(5, SheetOperations.Rows(b, "").Count); Equal(15, b.Projects.Sum(p => p.ChecklistItems.Count)); });

var folder = Path.Combine(Path.GetTempPath(), "H2Notes-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
Test("Column preferences and rich task comments persist and old files get defaults", () =>
{
    var state = SheetStorage.Demo(); state.SheetPreferences.AutoHeightTitle = false; state.SheetPreferences.AutoHeightComment = true;
    var task = state.Notes[0].Projects[0].ChecklistItems[0]; task.CommentRich = RichDocument.Plain("custom comment");
    task.CommentRich.Format(0, 6, s => s with { Bold = true, Color = "#FF0000" });
    var storage = new SheetStorage(Path.Combine(folder, "preferences.json")); storage.Save(state); var loaded = storage.LoadOrImport();
    True(!loaded.SheetPreferences.AutoHeightTitle); True(loaded.SheetPreferences.AutoHeightComment); True(loaded.SheetPreferences.AutoHeightProgress);
    True(loaded.Notes[0].Projects[0].ChecklistItems[0].ReadComment().StyleAt(0).Bold);
    Equal(2, SheetOperations.Rows(loaded.Notes[0], "custom comment").Count);
    var old = JsonSerializer.Deserialize<SheetState>("{\"SheetPreferences\":{}}")!;
    True(old.SheetPreferences.AutoHeightTitle); True(!old.SheetPreferences.AutoHeightComment);
});
Test("Isolated import retains originals, unknown fields, IDs and rich text", () =>
{
    var legacy = Path.Combine(folder, "legacy.json"); var path = Path.Combine(folder, "test.json");
    var json = "{\"Settings\":{\"Profile\":\"keep\"},\"Notes\":[{\"Title\":\"Test\",\"NoteKind\":\"project-hub\",\"Unknown\":42,\"Projects\":[{\"Name\":\"Original\",\"Notes\":\"<Section><Paragraph>ghi chú</Paragraph></Section>\",\"References\":\"keep refs\"}]}]}";
    File.WriteAllText(legacy, json); var storage = new SheetStorage(path); var state = storage.LoadOrImport(legacy);
    var id = state.Notes[0].Projects[0].Id;
    state.Notes[0].Projects[0].NameRich = RichDocument.Plain("Đã sửa"); storage.Save(state);
    var loaded = storage.LoadOrImport(); Equal(id, loaded.Notes[0].Projects[0].Id); Equal("Đã sửa", loaded.Notes[0].Projects[0].DisplayName);
    Equal(json, File.ReadAllText(legacy)); Equal(json, File.ReadAllText(path + ".legacy-backup"));
    Equal("Original", loaded.Notes[0].Projects[0].Name); Equal(42, loaded.Notes[0].Extra!["Unknown"].GetInt32()); True(loaded.Extra!.ContainsKey("Settings")); True(File.Exists(path + ".bak"));
});
Test("Corrupt state never replaced with empty state", () => { var path = Path.Combine(folder, "corrupt.json"); File.WriteAllText(path, "invalid"); Throws<JsonException>(() => new SheetStorage(path).LoadOrImport()); Equal("invalid", File.ReadAllText(path)); });
Test("Future schema cannot be overwritten", () => { var path = Path.Combine(folder, "future.json"); File.WriteAllText(path, "{\"SheetSchemaVersion\":99}"); Throws<InvalidDataException>(() => new SheetStorage(path).LoadOrImport()); });
Test("Nera fractional scroll preserved", () => { var c = new ContinuousScrollController(); c.QueueDelta(new(.25, 7.5, ScrollInputKind.Precision)); var frame = c.AdvanceFrame(TimeSpan.FromSeconds(1d / 60), new(1000, 1000)); Equal(.25, frame.Snapshot.OffsetX); Equal(7.5, frame.Snapshot.OffsetY); });
Test("Nera wheel animates and clamps", () => { var c = new ContinuousScrollController(); c.QueueDelta(new(0, 100, ScrollInputKind.Wheel)); var f = c.AdvanceFrame(TimeSpan.FromSeconds(1d / 60), new(1000, 1000)); True(f.Snapshot.OffsetY is > 0 and < 100); c.ScrollTo(500, 500, false); Equal(200d, c.AdvanceFrame(TimeSpan.Zero, new(100, 200)).Snapshot.OffsetY); });

Test("Manual import converts legacy hub and general notes without writing during preview", () =>
{
    var source = Path.Combine(folder, "manual-legacy.json");
    var legacy = new SheetState { Notes = [new() { Title = "Board", Projects = [new()
    {
        Name = "<Section><Paragraph><Bold>Dự án</Bold></Paragraph></Section>",
        Notes = "<Section><Paragraph><Run Foreground='#FF0000'>Ghi chú</Run></Paragraph></Section>",
        ChecklistItems = [new() { Text = "Việc một", IsCompleted = true }, new() { Text = "Việc hai" }]
    }] }, new() { Title = "Note", NoteKind = "general", Content = "<Section><Paragraph><Italic>Hello</Italic></Paragraph></Section>" }] };
    File.WriteAllText(source, JsonSerializer.Serialize(legacy), new System.Text.UTF8Encoding(true));
    var originalBytes = File.ReadAllBytes(source); var preview = LegacyImport.Prepare(source);
    Equal(1, preview.Boards); Equal(1, preview.GeneralNotes); Equal(1, preview.Projects); Equal(2, preview.Tasks);
    True(originalBytes.SequenceEqual(File.ReadAllBytes(source)));
    var current = new SheetState(); new SheetStorage(Path.Combine(folder, "manual-result.json")).ImportCopies(current, preview);
    var project = current.Notes[0].Projects[0]; Equal("Dự án", project.DisplayName); True(project.ReadName().StyleAt(0).Bold);
    Equal("#FF0000", project.ReadNotes().StyleAt(0).Color); True(project.ChecklistItems[0].IsCompleted); Equal("Việc hai", project.Next!.DisplayText);
    True(current.Notes[1].ReadContent().StyleAt(0).Italic); True(current.Notes.All(n => !n.IsVisibleOnDesktop));
});
Test("Manual import appends copies, retains edits/preferences and permanent source/pre-import backups", () =>
{
    var current = SheetStorage.Demo(); current.SheetPreferences.AutoHeightComment = true;
    var existing = current.Notes[0]; var oldId = existing.Id; var stateBefore = JsonSerializer.Serialize(current);
    var source = Path.Combine(folder, "same-id-legacy.json"); var storage = new SheetStorage(Path.Combine(folder, "append-state.json"));
    File.WriteAllText(source, stateBefore); storage.Save(current);
    var preview = LegacyImport.Prepare(source); var record = storage.ImportCopies(current, preview);
    Equal(2, current.Notes.Count); True(ReferenceEquals(existing, current.Notes[0])); Equal(oldId, existing.Id);
    True(current.Notes[1].Id != oldId); True(current.Notes[1].Projects[0].Id != existing.Projects[0].Id);
    True(current.Notes[1].Projects[0].ChecklistItems[0].Id != existing.Projects[0].ChecklistItems[0].Id);
    True(current.Notes[1].Title.Contains("nhập từ app cũ")); True(current.SheetPreferences.AutoHeightComment);
    Equal(stateBefore, File.ReadAllText(source)); Equal(stateBefore, File.ReadAllText(Path.Combine(record.BackupDirectory, "source.json")));
    var before = SheetStorage.Read(Path.Combine(record.BackupDirectory, "before-import.json")); Equal(1, before.Notes.Count); Equal(oldId, before.Notes[0].Id);
    storage.Save(current); True(File.Exists(Path.Combine(record.BackupDirectory, "before-import.json")));
    var reloaded = storage.LoadOrImport(); Equal(2, reloaded.Notes.Count); True(LegacyImport.AlreadyImported(reloaded, preview));
    Throws<InvalidDataException>(() => storage.ImportCopies(reloaded, preview)); Equal(2, reloaded.Notes.Count);
});
Test("Manual import rejects invalid structures and future schemas before changing data", () =>
{
    foreach (var json in new[] { "{}", "[]", "{\"Notes\":[]}", "{\"Notes\":[null]}", "{\"Notes\":[{\"Title\":\"x\",\"Projects\":[null]}]}",
        "{\"Notes\":[{\"Title\":\"x\",\"Projects\":[{\"Name\":\"P\",\"ChecklistItems\":[null]}]}]}", "{\"SheetSchemaVersion\":99,\"Notes\":[{\"Title\":\"x\"}]}" })
    {
        var source = Path.Combine(folder, "invalid-import.json"); File.WriteAllText(source, json);
        Throws<InvalidDataException>(() => LegacyImport.Prepare(source)); Equal(json, File.ReadAllText(source));
    }
    var broken = Path.Combine(folder, "invalid-syntax.json"); File.WriteAllText(broken, "not json"); Throws<JsonException>(() => LegacyImport.Prepare(broken));
});
Test("Manual import supports older standalone project notes and missing general NoteKind", () =>
{
    var source = Path.Combine(folder, "standalone-legacy.json");
    File.WriteAllText(source, "{\"Settings\":{\"RunOnStartup\":true},\"Notes\":[{\"NoteKind\":\"project\",\"Title\":\"Old title\",\"ProjectName\":\"Project name\",\"Content\":\"Old notes\",\"ChecklistItems\":[{\"Text\":\"Task\",\"IsCompleted\":true}]},{\"Title\":\"General\",\"Content\":\"Text\",\"IsArchived\":true,\"CustomField\":42}]}");
    var preview = LegacyImport.Prepare(source); Equal(1, preview.Boards); Equal(1, preview.GeneralNotes); Equal(1, preview.Archived);
    var current = new SheetState(); new SheetStorage(Path.Combine(folder, "standalone-result.json")).ImportCopies(current, preview);
    Equal("Project name", current.Notes[0].Projects[0].DisplayName); Equal("Old notes", current.Notes[0].Projects[0].NotesText);
    True(current.Notes[0].Projects[0].ChecklistItems[0].IsCompleted); True(!current.SheetPreferences.RunOnSystemStart);
    True(current.Notes[1].IsArchived); Equal(42, current.Notes[1].Extra!["CustomField"].GetInt32());
});
Test("Manual import uses the confirmed snapshot even if selected file later changes", () =>
{
    var source = Path.Combine(folder, "snapshot-legacy.json"); var original = "{\"Notes\":[{\"Title\":\"First\",\"Content\":\"Original\"}]}";
    File.WriteAllText(source, original); var preview = LegacyImport.Prepare(source); File.WriteAllText(source, "changed after preview");
    var current = new SheetState(); var record = new SheetStorage(Path.Combine(folder, "snapshot-result.json")).ImportCopies(current, preview);
    Equal("Original", current.Notes[0].ReadContent().Text); Equal(original, File.ReadAllText(Path.Combine(record.BackupDirectory, "source.json")));
    Equal("changed after preview", File.ReadAllText(source));
});
Test("Failed import save leaves live state and current file unchanged", () =>
{
    var current = SheetStorage.Demo(); var path = Path.Combine(folder, "failed-import.json"); var storage = new SheetStorage(path); storage.Save(current);
    var existing = current.Notes[0]; var fileBefore = File.ReadAllText(path);
    var source = Path.Combine(folder, "failure-source.json"); File.WriteAllText(source, "{\"Notes\":[{\"Title\":\"Imported\",\"Content\":\"Hello\"}]}");
    Directory.CreateDirectory(path + ".tmp"); var failedImport = false;
    try { storage.ImportCopies(current, LegacyImport.Prepare(source)); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failedImport = true; }
    True(failedImport); Equal(fileBefore, File.ReadAllText(path)); True(ReferenceEquals(existing, current.Notes[0])); Equal(1, current.Notes.Count); Equal(0, current.ImportHistory.Count);
});
Test("Manual import rejects active file and oversized input", () =>
{
    var path = Path.Combine(folder, "active-state.json"); var current = SheetStorage.Demo(); var storage = new SheetStorage(path); storage.Save(current);
    Throws<InvalidDataException>(() => storage.ImportCopies(current, LegacyImport.Prepare(path))); Equal(1, current.Notes.Count);
    var large = Path.Combine(folder, "large-import.json"); using (var stream = File.Create(large)) stream.SetLength(LegacyImport.MaximumBytes + 1L);
    Throws<InvalidDataException>(() => LegacyImport.Prepare(large));
});
Test("Desktop session restores the selected board and note stacking order on both launch paths", () =>
{
    var first = new NoteRecord(); var selected = new NoteRecord();
    var a = new NoteRecord { NoteKind = "general", Left = -1150, Top = 73, Width = 487, Height = 612, IsPinned = true };
    var b = new NoteRecord { NoteKind = "general", Left = 719, Top = 311, Width = 351, Height = 424 };
    var hidden = new NoteRecord { NoteKind = "general", IsVisibleOnDesktop = false };
    var state = new SheetState { Notes = [first, selected, a, b, hidden], DesktopSession = new()
        { SelectedBoardId = selected.Id, OpenWindowIds = [a.Id, selected.Id, b.Id] } };
    var path = Path.Combine(folder, "desktop-session.json"); new SheetStorage(path).Save(state);
    var restored = SheetStorage.Read(path);
    foreach (var startup in new[] { false, true })
    {
        var plan = DesktopRestorePlan.Create(restored, startup); Equal(selected.Id, plan.Board.Id);
        True(plan.OpenWindows.Select(n => n.Id).SequenceEqual(new[] { a.Id, selected.Id, b.Id }));
        Equal(-1150d, plan.OpenWindows[0].Left); Equal(487d, plan.OpenWindows[0].Width); True(plan.OpenWindows[0].IsPinned);
    }
});
Test("Hidden board and all-hidden session are not forced open", () =>
{
    var board = new NoteRecord(); var note = new NoteRecord { NoteKind = "general" };
    var state = new SheetState { Notes = [board, note], DesktopSession = new() { SelectedBoardId = board.Id, OpenWindowIds = [note.Id] } };
    Equal(note.Id, DesktopRestorePlan.Create(state, false).OpenWindows.Single().Id);
    state.DesktopSession.OpenWindowIds.Clear();
    Equal(0, DesktopRestorePlan.Create(state, false).OpenWindows.Count); Equal(0, DesktopRestorePlan.Create(state, true).OpenWindows.Count);
});
Test("Session restore ignores deleted, archived, duplicate and inactive board IDs", () =>
{
    var board = new NoteRecord(); var other = new NoteRecord(); var archived = new NoteRecord { NoteKind = "general", IsArchived = true };
    var state = new SheetState { Notes = [board, other, archived], DesktopSession = new()
        { SelectedBoardId = board.Id, OpenWindowIds = [Guid.NewGuid(), archived.Id, other.Id, board.Id, board.Id] } };
    Equal(board.Id, DesktopRestorePlan.Create(state, true).OpenWindows.Single().Id);
    state.DesktopSession.SelectedBoardId = Guid.NewGuid(); Equal(board.Id, DesktopRestorePlan.Create(state, true).Board.Id);
});
Test("First launch and pre-session data retain visible-note defaults", () =>
{
    var fresh = new SheetState(); var initial = DesktopRestorePlan.Create(fresh, false);
    Equal(1, fresh.Notes.Count); Equal(initial.Board.Id, initial.OpenWindows.Single().Id);
    var hidden = new NoteRecord { IsVisibleOnDesktop = false };
    var note = new NoteRecord { NoteKind = "general" }; var legacy = new SheetState { Notes = [hidden, note] };
    Equal(note.Id, DesktopRestorePlan.Create(legacy, false).OpenWindows.Single().Id);
});
Test("Disabling session restore starts quietly with Windows, manually opens only the board", () =>
{
    var state = new SheetState { Notes = [new(), new() { NoteKind = "general" }] };
    state.SheetPreferences.RestoreVisibleNotes = false;
    Equal(0, DesktopRestorePlan.Create(state, true).OpenWindows.Count);
    True(DesktopRestorePlan.Create(state, false).OpenWindows.Single().IsBoard);
});
Test("Manual import preserves the current desktop session on disk", () =>
{
    var current = SheetStorage.Demo(); current.DesktopSession = new()
        { SelectedBoardId = current.Notes[0].Id, OpenWindowIds = [current.Notes[0].Id] };
    var source = Path.Combine(folder, "session-import-source.json"); File.WriteAllText(source, JsonSerializer.Serialize(SheetStorage.Demo()));
    var path = Path.Combine(folder, "session-import-target.json");
    new SheetStorage(path).ImportCopies(current, LegacyImport.Prepare(source));
    var loaded = SheetStorage.Read(path); Equal(current.Notes[0].Id, loaded.DesktopSession!.SelectedBoardId);
    Equal(current.Notes[0].Id, loaded.DesktopSession.OpenWindowIds.Single());
});

AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
Test("Settings exposes manual import without changing state on open or close", () =>
{
    var app = new H2Notes.Avalonia.App(); typeof(H2Notes.Avalonia.App).GetField("_storage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, new SheetStorage(Path.Combine(folder, "settings-data.json")));
    var before = JsonSerializer.Serialize(app.State); var window = new H2Notes.Avalonia.SettingsWindow(app); window.Show(); Dispatcher.UIThread.RunJobs();
    True(window.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "RecoverWorkspaceButton" && !b.IsEnabled));
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Content is TextBlock { Text: "Tiện ích" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(window.GetLogicalDescendants().OfType<Button>().Any(b => b.Name == "ImportLegacyButton" && b.IsEnabled));
    window.Close(); Equal(before, JsonSerializer.Serialize(app.State));
});
void Pump() => Dispatcher.UIThread.RunJobs();
H2Notes.Avalonia.App SessionApp(SheetState state, string file)
{
    var app = new H2Notes.Avalonia.App(); var type = app.GetType();
    type.GetProperty("State")!.SetValue(app, state);
    type.GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, new SheetStorage(Path.Combine(folder, file)));
    type.GetField("_storageReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
    return app;
}
void StopSessionApp(H2Notes.Avalonia.App app)
{
    app.GetType().GetProperty("IsExiting")!.SetValue(app, true);
    ((DispatcherTimer)app.GetType().GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!).Stop();
    foreach (var window in app.OpenWindows) window.Close();
}
Test("Notes have no taskbar/minimize/maximize; X hides, flushes draft and permits reopening", () =>
{
    var note = new NoteRecord { NoteKind = "general", Width = 420, Height = 490, Left = 146, Top = 157 };
    var app = SessionApp(new() { Notes = [note] }, "note-close.json"); app.OpenNote(note); Pump();
    var window = (H2Notes.Avalonia.NoteWindow)app.OpenWindows.Single();
    True(!window.ShowInTaskbar && !window.CanMinimize && !window.CanMaximize); Equal(WindowDecorations.None, window.WindowDecorations);
    var editor = window.GetVisualDescendants().OfType<RichEditor>().Single(); editor.Editor.Document.Insert(0, "Unsaved draft");
    window.Position = new PixelPoint(253, 183); window.Width = 477; window.Height = 558;
    var closed = false; window.Closed += (_, _) => closed = true;
    window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(!closed && !window.IsVisible && !app.IsExiting); True(!note.IsVisibleOnDesktop);
    var saved = SheetStorage.Read(app.DataPath); Equal("Unsaved draft", saved.Notes[0].ReadContent().Text);
    Equal(253d, saved.Notes[0].Left); Equal(477d, saved.Notes[0].Width); Equal(0, saved.DesktopSession!.OpenWindowIds.Count);
    app.OpenNote(note); Pump(); True(window.IsVisible); Equal(new PixelPoint(253, 183), window.Position);
    app.SaveNow(); Equal(note.Id, SheetStorage.Read(app.DataPath).DesktopSession!.OpenWindowIds.Single());
    StopSessionApp(app);
});
Test("Board X hides without closing and switched board is the saved session selection", () =>
{
    var first = new NoteRecord(); var second = new NoteRecord();
    var app = SessionApp(new() { Notes = [first, second] }, "board-close.json");
    var window = new H2Notes.Avalonia.MainWindow(app, first);
    app.GetType().GetField("_main", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, window);
    app.GetType().GetMethod("TrackWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [window]);
    app.ShowMain(); Pump();
    True(!window.ShowInTaskbar && !window.CanMinimize && !window.CanMaximize);
    True(window.FindControl<Button>("MinButton") is null && window.FindControl<Button>("MaxButton") is null);
    window.SetBoard(second); window.Position = new PixelPoint(178, 119); window.Width = 912; window.Height = 647;
    app.SaveNow(); var saved = SheetStorage.Read(app.DataPath);
    Equal(second.Id, saved.DesktopSession!.SelectedBoardId); Equal(second.Id, saved.DesktopSession.OpenWindowIds.Single());
    Equal(178, saved.Notes[1].SheetLeft); Equal(912d, saved.Notes[1].SheetWidth);
    window.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(!window.IsVisible && !app.IsExiting); Equal(0, SheetStorage.Read(app.DataPath).DesktopSession!.OpenWindowIds.Count);
    app.ShowMain(); Pump(); True(window.IsVisible); Equal(second.Id, window.BoardId); StopSessionApp(app);
});
Test("Application exit retains visible windows and captures draft and geometry before closing", () =>
{
    var note = new NoteRecord { NoteKind = "general", Width = 450, Height = 510 };
    var app = SessionApp(new() { Notes = [note] }, "note-exit.json"); app.OpenNote(note); Pump();
    var window = app.OpenWindows.Single(); window.Position = new PixelPoint(173, 149); window.Width = 1137; window.Height = 527;
    window.GetVisualDescendants().OfType<RichEditor>().Single().Editor.Document.Insert(0, "Before exit");
    app.ExitApp(); window.Close(); Pump();
    var saved = SheetStorage.Read(app.DataPath); True(saved.Notes[0].IsVisibleOnDesktop);
    Equal(note.Id, saved.DesktopSession!.OpenWindowIds.Single()); Equal(1137d, saved.Notes[0].Width); Equal("Before exit", saved.Notes[0].ReadContent().Text);
    var reopened = new H2Notes.Avalonia.NoteWindow(new H2Notes.Avalonia.App(), saved.Notes[0]); reopened.Show(); Pump();
    Equal(1137d, reopened.Width); Equal(new PixelPoint(173, 149), reopened.Position); reopened.Hide(); StopSessionApp(app);
});
Test("Moving and resizing a note schedule autosave even when snapping is off", () =>
{
    var app = SessionApp(new() { Notes = [new() { NoteKind = "general" }] }, "move-save.json");
    app.State.SheetPreferences.SnapWindows = false; app.OpenNote(app.State.Notes[0]); Pump(); app.SaveNow();
    var timer = (DispatcherTimer)app.GetType().GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
    var window = app.OpenWindows.Single(); True(!timer.IsEnabled);
    window.Position = new PixelPoint(161, 153); True(timer.IsEnabled); app.SaveNow(); True(!timer.IsEnabled);
    window.Width = 613; Pump(); True(timer.IsEnabled); app.SaveNow();
    Equal(161d, SheetStorage.Read(app.DataPath).Notes[0].Left); Equal(613d, SheetStorage.Read(app.DataPath).Notes[0].Width); StopSessionApp(app);
});
Test("Settings and dialogs stay off the taskbar without minimize/maximize", () =>
{
    var settings = new H2Notes.Avalonia.SettingsWindow(SessionApp(new(), "settings-chrome.json"));
    True(!settings.ShowInTaskbar && !settings.CanMinimize && !settings.CanMaximize); settings.Close();
    var owner = new Window();
    var dialog = (Window)typeof(Dialogs).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [owner, "Test"] )!;
    True(!dialog.ShowInTaskbar && !dialog.CanMinimize && !dialog.CanMaximize); dialog.Close(); owner.Close();
});
Test("Missing monitor is recovered while valid negative monitor coordinates are kept", () =>
{
    var type = typeof(H2Notes.Avalonia.MainWindow).Assembly.GetType("H2Notes.Avalonia.WindowPlacement")!;
    var method = type.GetMethod("ReachablePosition", BindingFlags.Static | BindingFlags.NonPublic)!;
    var primary = new PixelRect(0, 0, 1920, 1040); var left = new PixelRect(-1920, 0, 1920, 1040);
    Equal(new PixelPoint(-1200, 80), (PixelPoint)method.Invoke(null, [new PixelPoint(-1200, 80), new[] { primary, left }, primary])!);
    Equal(new PixelPoint(30, 30), (PixelPoint)method.Invoke(null, [new PixelPoint(-1200, 80), new[] { primary }, primary])!);
});
Test("Toolbar and menu follow uniform/mixed selection without edits or saves", () =>
{
    var editor = new RichEditor(); var toolbar = (StackPanel)((ScrollViewer)editor.CreateToolbar()).Content!;
    var doc = RichDocument.Plain("abcdef"); doc.Format(0, 3, s => s with { Font = "Cambria", Size = 21, Bold = true, Color = "#FF0000" });
    editor.Load(doc); var changes = 0; editor.Changed += () => changes++;
    var bold = toolbar.Children.OfType<ToggleButton>().Single(b => b.Name == "FormatBold");
    var font = toolbar.Children.OfType<ComboBox>().Single(b => b.Name == "FormatFont");
    var size = toolbar.Children.OfType<ComboBox>().Single(b => b.Name == "FormatSize");
    var menu = editor.Editor.ContextMenu!;
    editor.Editor.Select(0, 3); Equal(true, bold.IsChecked); Equal("Cambria", font.SelectedItem); Equal(21d, size.SelectedItem);
    True(((MenuItem)menu.Items[0]!).IsChecked); True(((MenuItem)menu.Items[4]!).Header!.ToString()!.Contains("Cambria"));
    editor.Editor.SelectAll(); Equal<bool?>(null, bold.IsChecked); Equal<object?>(null, font.SelectedItem); Equal<object?>(null, size.SelectedItem);
    True(((MenuItem)menu.Items[0]!).Header!.ToString()!.Contains("Nhiều"));
    editor.Editor.Select(3, 3); Equal(false, bold.IsChecked); Equal("Segoe UI", font.SelectedItem);
    Equal(0, changes); True(!editor.HasChanges); True(doc.Runs.SequenceEqual(editor.Snapshot().Runs));
});
Test("Mixed bold toggles all on from menu and toolbar follows undo", () =>
{
    var editor = new RichEditor(); var toolbar = (StackPanel)((ScrollViewer)editor.CreateToolbar()).Content!;
    var doc = RichDocument.Plain("abcd"); doc.Format(0, 2, s => s with { Bold = true }); editor.Load(doc); editor.Editor.SelectAll();
    ((MenuItem)editor.Editor.ContextMenu!.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    True(editor.Snapshot().Runs.All(r => r.Style.Bold));
    var bold = toolbar.Children.OfType<ToggleButton>().Single(b => b.Name == "FormatBold"); Equal(true, bold.IsChecked);
    editor.Undo(); editor.Editor.SelectAll(); Equal<bool?>(null, bold.IsChecked);
    bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); True(editor.Snapshot().Runs.All(r => r.Style.Bold));
    bold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); True(editor.Snapshot().Runs.All(r => !r.Style.Bold));
});
Test("Typing format and caret formatting agree without changing other text", () =>
{
    var editor = new RichEditor(); editor.Load(RichDocument.Plain("abc")); editor.Editor.CaretOffset = 3;
    editor.Apply(s => s with { Underline = true, Size = 20 }); Equal(true, editor.SelectedStyle.Underline);
    editor.Editor.Document.Insert(3, "d"); editor.Editor.CaretOffset = 4;
    True(editor.Snapshot().StyleAt(3).Underline); Equal(true, editor.SelectedStyle.Underline);
    editor.Editor.CaretOffset = 1; Equal(false, editor.SelectedStyle.Underline);
    editor.Editor.SelectAll(); editor.Editor.SelectedText = "x"; Equal("x", editor.Snapshot().Text);
});
object LayoutRowAt(ProjectGrid sheet, int index) => ((System.Collections.IList)typeof(ProjectGrid).GetField("_layout", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sheet)!)[index]!;
T LayoutProperty<T>(object row, string property) => (T)row.GetType().GetProperty(property)!.GetValue(row)!;
Test("Four columns; STT checkbox changes only its task and keeps ordinal", () =>
{
    var board = new NoteRecord { Projects = [new() { Name = "One", ChecklistItems = [new() { Text = "Task" }] }] };
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(board); Pump();
    var widths = (double[])typeof(ProjectGrid).GetField("_widths", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sheet)!; Equal(4, widths.Length);
    window.MouseDown(new Point(32, 90), MouseButton.Left); window.MouseUp(new Point(32, 90), MouseButton.Left); Pump();
    True(board.Projects[0].ChecklistItems[0].IsCompleted); Equal("1/1", board.Projects[0].Progress); Equal(1, SheetOperations.Rows(board, "")[0].Ordinal);
    window.Close();
});
Test("Auto height uses only enabled columns; clipped cells retain full tooltip text", () =>
{
    var text = string.Join(" ", Enumerable.Repeat("Nội dung dài", 30));
    var project = new ProjectRecord { Name = text, Notes = "Dòng đầu\n" + text, IsExpanded = false, ChecklistItems = [new() { Text = text }] };
    var preferences = new SheetPreferences { AutoHeightTitle = false, AutoHeightProgress = false, AutoHeightComment = false };
    var sheet = new ProjectGrid { Preferences = preferences }; var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(new() { Projects = [project] }); Pump();
    var compact = LayoutRowAt(sheet, 0); Equal(40d, LayoutProperty<double>(compact, "Height"));
    foreach (var property in new[] { "Title", "Comment", "Progress" })
    {
        var formatted = LayoutProperty<TextLayout>(compact, property);
        True(formatted.TextLines[^1].TextRuns.OfType<ShapedTextRun>().Any(r => r.Text.Span.Contains('…')));
        True(formatted.Height <= 24);
    }
    ToolTip.SetShowDelay(sheet, 1); window.MouseMove(new Point(800, 60)); Pump();
    var frame = new DispatcherFrame(); DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(50)); Dispatcher.UIThread.PushFrame(frame);
    True(ToolTip.GetIsOpen(sheet));
    var tip = (ScrollViewer)ToolTip.GetTip(sheet)!; Equal(project.NotesText, ((TextBlock)tip.Content!).Text);
    preferences.AutoHeightTitle = true; sheet.Refresh(); Pump(); True(LayoutProperty<double>(LayoutRowAt(sheet, 0), "Height") > 40);
    preferences.AutoHeightTitle = false; preferences.AutoHeightComment = true; sheet.Refresh(); Pump(); True(LayoutProperty<double>(LayoutRowAt(sheet, 0), "Height") > 40);
    preferences.AutoHeightComment = false; preferences.AutoHeightProgress = true; sheet.Refresh(); Pump(); True(LayoutProperty<double>(LayoutRowAt(sheet, 0), "Height") > 40);
    project.IsExpanded = true; sheet.Refresh(); Pump(); Equal(40d, LayoutProperty<double>(LayoutRowAt(sheet, 0), "Height"));
    window.Close();
});
Test("Cell comment formats survive commit, autosave, cancel and reopening", () =>
{
    var project = new ProjectRecord { Name = "One", ChecklistItems = [new() { Text = "Task", Comment = "comment" }] };
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet }; window.Show(); sheet.SetBoard(new() { Projects = [project] }); Pump();
    var row = new SheetRow(project, project.ChecklistItems[0], 1); sheet.BeginEdit(row, 3); Pump();
    var editor = sheet.GetVisualDescendants().OfType<RichEditor>().Single(); editor.Editor.SelectAll(); editor.Apply(s => s with { Italic = true, Color = "#FF0000" }); sheet.CommitEdit();
    sheet.BeginEdit(row, 3); Pump(); Equal(true, editor.SelectedStyle.Italic); Equal("#FF0000", editor.SelectedStyle.Color);
    editor.Apply(s => s with { Bold = true }); sheet.FlushDraft(); True(row.Task!.ReadComment().StyleAt(0).Bold); sheet.CancelEdit();
    True(!row.Task.ReadComment().StyleAt(0).Bold); True(row.Task.ReadComment().StyleAt(0).Italic); window.Close();
});
Test("Auto wraps unbroken text; fixed cells fit large fonts and retain span backgrounds", () =>
{
    var original = new string('W', 120);
    var doc = RichDocument.Plain(original); doc.Format(0, original.Length, s => s with { Size = 32, Highlight = "#FFFF00" });
    var project = new ProjectRecord { NameRich = doc };
    var sheet = new ProjectGrid(); var window = new Window { Width = 760, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(new() { Projects = [project] }); Pump();
    var full = LayoutProperty<TextLayout>(LayoutRowAt(sheet, 0), "Title");
    True(full.TextLines.Count > 1); True(full.TextLines.All(l => !l.HasCollapsed && l.Width <= full.MaxWidth + .1));
    var fullHeight = full.Height;
    sheet.Preferences.AutoHeightTitle = false; sheet.Refresh(); Pump();
    var compact = LayoutProperty<TextLayout>(LayoutRowAt(sheet, 0), "Title");
    True(compact.Height > 24 && compact.Height < fullHeight);
    True(compact.TextLines[^1].TextRuns.OfType<ShapedTextRun>().Any(r => r.Text.Span.Contains('…')));
    True(compact.TextLines[0].TextRuns.OfType<ShapedTextRun>().Any(r => r.Properties.BackgroundBrush is not null));
    Equal(original, project.DisplayName); window.Close();
});
Test("Rich editor applies formatting and undoes it", () =>
{
    var editor = new RichEditor(); editor.Load(RichDocument.Plain("alpha beta")); editor.Editor.Select(6, 4);
    editor.Apply(s => s with { Bold = true, Color = "#FF0000" }); True(editor.Snapshot().StyleAt(6).Bold); True(!editor.Snapshot().StyleAt(0).Bold);
    editor.Undo(); True(!editor.Snapshot().StyleAt(6).Bold); editor.Redo(); True(editor.Snapshot().StyleAt(6).Bold);
});
Test("Text edit undo retains existing rich spans", () =>
{
    var editor = new RichEditor(); var doc = RichDocument.Plain("hello"); doc.Format(0, 5, s => s with { Italic = true }); editor.Load(doc);
    editor.Editor.Document.Insert(5, " world"); Equal("hello world", editor.Snapshot().Text); editor.Undo(); Equal("hello", editor.Snapshot().Text); True(editor.Snapshot().StyleAt(0).Italic);
});
Test("Grid edit commit and Escape after autosave", () =>
{
    var board = SheetStorage.Demo().Notes[0]; var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(board); Pump(); var row = SheetOperations.Rows(board, "")[0]; var original = row.Title;
    sheet.BeginEdit(row); Pump(); var editor = sheet.GetVisualDescendants().OfType<RichEditor>().Single();
    editor.Editor.Document.Replace(0, editor.Editor.Text.Length, "Renamed"); sheet.FlushDraft(); Equal("Renamed", board.Projects[0].DisplayName);
    sheet.CancelEdit(); Equal(original, board.Projects[0].DisplayName);
    sheet.BeginEdit(row); Pump(); editor.Editor.Document.Replace(0, editor.Editor.Text.Length, "Committed"); sheet.CommitEdit(); Equal("Committed", board.Projects[0].DisplayName);
    window.Close();
});
Test("Grid task drag preview leaves Next and project order unchanged", () =>
{
    var board = new NoteRecord { Projects = [new() { Name = "Project", ChecklistItems = [new() { Text = "One" }, new() { Text = "Two" }, new() { Text = "Three" }] }, new() { Name = "Other" }] };
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(board); Pump(); var p = board.Projects[0]; var oldNext = p.Next!.Id;
    window.MouseDown(new Point(220, 138), MouseButton.Left); window.MouseMove(new Point(220, 88), RawInputModifiers.LeftMouseButton); Pump();
    Equal(oldNext, p.Next!.Id); Equal("Project", board.Projects[0].DisplayName);
    window.MouseUp(new Point(220, 88), MouseButton.Left); Pump(); Equal("Two", p.Next!.DisplayText); Equal("Project", board.Projects[0].DisplayName);
    window.Close();
});
Test("Project drag preview/cancel preserves groups and ordinals", () =>
{
    var board = new NoteRecord { Projects = [new() { Name = "One", IsExpanded = false }, new() { Name = "Two", IsExpanded = false }, new() { Name = "Three", IsExpanded = false }] };
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(board); Pump();
    window.MouseDown(new Point(200, 138), MouseButton.Left); window.MouseMove(new Point(200, 49), RawInputModifiers.LeftMouseButton); Pump();
    Equal("One", board.Projects[0].DisplayName); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.MouseUp(new Point(200, 49), MouseButton.Left); Pump(); Equal("One", board.Projects[0].DisplayName);
    // Begin at a different point so this independent drag is not a double-click edit.
    window.MouseDown(new Point(230, 138), MouseButton.Left); window.MouseMove(new Point(230, 49), RawInputModifiers.LeftMouseButton); window.MouseUp(new Point(230, 49), MouseButton.Left); Pump();
    Equal("Three", board.Projects[0].DisplayName); Equal(1, SheetOperations.Rows(board, "")[0].Ordinal); window.Close();
});
Test("Filtered grid does not reorder underlying data on drag", () =>
{
    var board = new NoteRecord { Projects = [new() { Name = "One" }, new() { Name = "Two" }, new() { Name = "Three" }] };
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet };
    window.Show(); sheet.SetBoard(board); sheet.SetFilter("o"); Pump();
    window.MouseDown(new Point(200, 99), MouseButton.Left); window.MouseMove(new Point(200, 49), RawInputModifiers.LeftMouseButton); window.MouseUp(new Point(200, 49), MouseButton.Left); Pump();
    Equal("One", board.Projects[0].DisplayName); window.Close();
});
Test("Main window constructs its named controls and loads notes", () =>
{
    var app = new H2Notes.Avalonia.App(); var board = SheetStorage.Demo().Notes[0];
    var window = new H2Notes.Avalonia.MainWindow(app, board); window.Show(); Pump();
    True(window.FindControl<ProjectGrid>("Sheet") is not null);
    var editor = window.FindControl<RichEditor>("NotesEditor")!; Equal(board.Projects[0].NotesText, editor.Snapshot().Text);
    window.Hide();
});
Test("Viewport visual count stays bounded for 10,000 tasks", () =>
{
    var board = new NoteRecord();
    for (var i = 0; i < 1000; i++) board.Projects.Add(new ProjectRecord { Name = "Project " + i, ChecklistItems = Enumerable.Range(0, 10).Select(n => new TaskRecord { Text = "Task " + n }).ToList() });
    var sheet = new ProjectGrid(); var window = new Window { Width = 1000, Height = 600, Content = sheet }; window.Show(); Pump();
    var timer = Stopwatch.StartNew(); sheet.SetBoard(board); Pump(); timer.Stop();
    var count = sheet.GetVisualDescendants().Count(); True(count < 100); Equal(11000, SheetOperations.Rows(board, "").Count);
    Console.WriteLine($"METRIC 1000 projects/10000 tasks: initial layout {timer.ElapsedMilliseconds} ms; {count} visual elements (headless, not native FPS)");
    window.Close();
});
WorkspaceTests.Run(Test);
H2AgentAdapterContractTests.Run(Test);
H2SyncCoordinatorContractTests.Run(Test);
H2CoordinatorSqliteStoreTests.Run(Test);
H2CoordinatorClientSyncTests.Run(Test);
H2CoordinatorConflictSnapshotTests.Run(Test);
H2ProjectAiQueueTests.Run(Test);
H2ProjectAiLeaseFencingTests.Run(Test);
H2AgentTaskCorrelationTests.Run(Test);
H2ProductProjectionTests.Run(Test);
H2ProjectDataBoundaryTests.Run(Test);
H2ProjectStateArchitectureTests.Run(Test);
H2DeterministicProgressTests.Run(Test);
H2CommandCenterUiTests.Run(Test);
H2ProjectWorkspaceUiTests.Run(Test);
H2ProjectTasksDetailTests.Run(Test);
H2ProjectNotesDetailTests.Run(Test);
H2ProjectResourcesTests.Run(Test);
H2ProjectHistoryTests.Run(Test);
H2ProjectEvidenceInspectorTests.Run(Test);
H2AgentPresentationTests.Run(Test);
H2LegacyConversationHistoryTests.Run(Test);
H2LegacyRequestContextRetirementTests.Run(Test);
H2TypedProjectToolTests.Run(Test);
H2WorkAssistantBubbleTests.Run(Test);
H2WorkAssistantHotkeyTests.Run(Test);
H2ActiveWorkContextTests.Run(Test);
H2WorkAssistantContextChipTests.Run(Test);
H2WorkAssistantQuickTaskTests.Run(Test);
H2WorkAssistantPermissionTests.Run(Test);
H2WorkAssistantCompletionTests.Run(Test);
H2WorkAssistantRepairTests.Run(Test);
H2ProjectLayoutStorageTests.Run(Test);
H2DesktopSessionStorageTests.Run(Test);
H2ResponsiveProductTests.Run(Test);
H2ProductArchitectureGuardTests.Run(Test);
H2ProductionAgentBridgeTests.Run(Test);
H2AgentChatSurfaceTests.Run(Test);
H2DocumentsDesignTests.Run(Test);
H2AgentSteeringWireTests.Run(Test);
H2ProductionRepairTests.Run(Test);
H2WordCvRepairTests.Run(Test);
H2AgentCapabilityRepairTests.Run(Test);
H2AgentReliabilityContractTests.Run(Test);
H2AgentRuntimeHookTests.Run(Test);
H2AgentToolOutcomeTests.Run(Test);
H2AgentResourceBindingTests.Run(Test);
H2AgentLiveBindingTests.Run(Test);
H2OfficeDiscoveryTests.Run(Test);
H2AgentDesktopLaunchTests.Run(Test);
H2ExcelRangeReadTests.Run(Test);
H2ExcelBatchWriteTests.Run(Test);
H2WordPagedReadTests.Run(Test);
H2AgentGoalRevisionTests.Run(Test);
H2AgentArchiveJournalTests.Run(Test);
H2AgentRestartReconcileTests.Run(Test);
H2AgentResumeRebaseTests.Run(Test);
H2AgentSteeringConcurrencyTests.Run(Test);
H2AgentHistoryRetrievalTests.Run(Test);
H2AgentCompletionTests.Run(Test);
H2AgentProcessDrainTests.Run(Test);
H2AgentLongJobTests.Run(Test);
H2AgentJobProductionTests.Run(Test);
H2AgentJobObservationTests.Run(Test);
H2AgentRequestBudgetTests.Run(Test);
H2AgentWorkCompactionTests.Run(Test);
H2AgentPluginLifecycleTests.Run(Test);
H2ProductAcceptanceScenarioTests.Run(Test);
H2LegacyCleanupTests.Run(Test);
H2FinalPerformanceTests.Run(Test);
AiTests.Run(Test);
Test("Responsive shell keeps rich draft, selection and undo across narrow and wide sizes", () =>
{
    var app = new H2Notes.Avalonia.App(); var board = SheetStorage.Demo().Notes[0]; var p = board.Projects[2]; board.SelectedProjectId = p.Id;
    var window = new H2Notes.Avalonia.MainWindow(app, board); window.Show(); Pump();
    H2UiTestNavigation.OpenProjectWorkspace(window, p.Id); Pump();
    var editor = window.FindControl<RichEditor>("NotesEditor")!;
    editor.Editor.Document.Insert(editor.Editor.Text.Length, "\nBản nháp tiếng Việt"); editor.Editor.Select(0, 4); var draft = editor.Editor.Text;
    window.Width = 560; window.Height = 600; Pump();
    True(window.FindControl<StackPanel>("CompactTabs")!.IsVisible); True(!window.FindControl<Border>("Sidebar")!.IsVisible);
    window.FindControl<Button>("NotesTabButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(window.FindControl<Border>("NotesPane")!.IsVisible); True(!window.FindControl<Border>("TasksPane")!.IsVisible);
    Equal(draft, editor.Editor.Text); Equal(4, editor.Editor.SelectionLength);
    window.Width = 1440; window.Height = 860; Pump(); True(window.FindControl<Border>("Sidebar")!.IsVisible);
    Equal(draft, editor.Editor.Text); True(editor.Editor.CanUndo); window.Flush(); Equal(draft, p.NotesText); window.Hide();
});
Test("Project drawer switches selected project and AI draft remains with its owner", () =>
{
    var app = new H2Notes.Avalonia.App(); var board = SheetStorage.Demo().Notes[0];
    board.Projects[0].Conversations.Add(new AiConversation { Draft = "Nháp riêng dự án 1" });
    var window = new H2Notes.Avalonia.MainWindow(app, board); window.Show(); window.Width = 560; window.Height = 820; Pump();
    window.FindControl<Button>("CompactProjectPicker")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(window.FindControl<Border>("Sidebar")!.IsVisible);
    window.FindControl<ListBox>("ProjectList")!.SelectedIndex = 2; Pump();
    Equal(board.Projects[2].Id, window.SelectedProjectId); True(!window.FindControl<Border>("Sidebar")!.IsVisible);
    Equal("Nháp riêng dự án 1", board.Projects[0].Conversations[0].Draft); Equal(0, board.Projects[2].Conversations.Count);
    window.FindControl<Button>("AskAiButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    True(window.FindControl<Border>("AiHostBorder")!.IsVisible);
    True(window.FindControl<Grid>("WorkContent")!.IsVisible);
    True(!window.FindControl<Grid>("EditorSplit")!.IsVisible);
    True(window.FindControl<Button>("AgentTabButton")!.Classes.Contains("selected"));
    window.Hide();
});
Test("Compact task drag only commits Next on release and edits comment separately", () =>
{
    var p = new ProjectRecord { ChecklistItems = [new() { Text = "First", Comment = "Comment A" }, new() { Text = "Second", Comment = "Comment B" }] };
    var board = new NoteRecord { Projects = [p] }; var sheet = new ProjectGrid(); var window = new Window { Width = 460, Height = 420, Content = sheet };
    window.Show(); sheet.SetBoard(board); sheet.FocusProject(p); sheet.SetCompact(true); Pump();
    window.MouseDown(new Point(130, 90), MouseButton.Left); window.MouseMove(new Point(130, 5), RawInputModifiers.LeftMouseButton); Pump();
    Equal("First", p.Next!.DisplayText); window.MouseUp(new Point(130, 5), MouseButton.Left); Pump(); Equal("Second", p.Next!.DisplayText);
    var row = new SheetRow(p, p.ChecklistItems[0], 1); sheet.BeginEdit(row, 3); Pump(); var edit = sheet.GetVisualDescendants().OfType<RichEditor>().Single();
    edit.Editor.Document.Replace(0, edit.Editor.Text.Length, "Changed comment"); sheet.CommitEdit(); Equal("Changed comment", p.ChecklistItems[0].CommentText); Equal("Second", p.ChecklistItems[0].DisplayText); window.Close();
});
IconResizeTests.Run(Test);
ChatTests.Run(Test, folder);
ProjectChatTests.Run(Test, folder);
AiDocumentTests.Run(Test, folder);
ThinkingUiTests.Run(Test);
AiStreamingTests.Run(Test);
AiLayoutTests.Run(Test);
AiComposerUpgradeTests.Run(Test, folder);
ProjectActionTests.Run(Test);
ProjectActionUiTests.Run(Test);
PdfComposerTests.Run(Test);
PdfAiTests.Run(Test);
ReasoningCapabilityTests.Run(Test);
AiComposerOptionsTests.Run(Test);
Console.WriteLine($"RESULT: {passed} passed, {failed} failed. Temporary test evidence: {folder}");
return failed == 0 ? 0 : 1;

sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new global::Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AvaloniaEdit/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }
}
