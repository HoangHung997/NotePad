# Nodepad

## Approved H2 Notes direction (2026-09-15)

The ten responsive hybrid mockups are now the approved UI baseline. Future UI
work must compare actual app screenshots against them at each implementation
stage. This approval also records per-project files, configurable data folders,
and Ollama/multi-provider AI settings. A first functional implementation is now
available, with 77 passing tests. It is not fully accepted against the baseline;
see [implementation status and remaining gaps](docs/RESPONSIVE_IMPLEMENTATION.md).

See [approved product requirements](docs/APPROVED_PRODUCT_SPEC.md),
[acceptance checklist](docs/UI_ACCEPTANCE.md), and
[approved image gallery](docs/ui-concepts/2026-09-15-responsive-hybrid/README.md).
The sections below describe the earlier implemented prototypes.

## Repository layout

- `src/H2Notes.Core/` — shared H2 Notes data, storage, AI and document logic.
- `src/H2Notes.Avalonia/` — current Avalonia H2 Notes application.
- `src/Nodepad.Desktop/` — original WPF desktop app kept for compatibility/reference.
- `tests/` — H2 Notes automated tests and OCR probe.
- `tools/ocr/` — local OCR/layout bridge tooling.
- `docs/` — approved product requirements, UI baseline and verification evidence.
- `experiments/H2AgentLab/` — isolated AI-agent research lab; not automatically integrated into H2 Notes.
- `_ver2/` — older WinForms source retained as legacy reference.

## H2 Notes Project Sheet (Avalonia branch)

The new lightweight project-table app lives in `H2Notes.Avalonia.slnx`.
It uses selected NeraSpreadSheet scrolling/grid source, not the full spreadsheet
engine. The original WPF solution and user data remain unchanged.

```powershell
dotnet build .\H2Notes.Avalonia.slnx -c Release
dotnet run --project .\src\H2Notes.Avalonia\H2Notes.Avalonia.csproj -c Release
dotnet run --project .\tests\H2Notes.Tests\H2Notes.Tests.csproj -c Release
```

New data is isolated at `%LOCALAPPDATA%\H2Notes\project-sheet-v1.json`, with an
initial backup of the legacy data. Add `-- --demo` to run a separate sample board.
See [implementation and known limits](docs/PROJECT_SHEET_IMPLEMENTATION.md).

## Original WPF App

Nodepad is now a C# WPF desktop sticky-notes app built around a Simple Sticky Notes style workflow:

- tray-first behavior
- small detached note windows
- project notes with checklist tracking
- one shared project manager view for expanding and collapsing each project

## What this build supports

- `General notes` for normal sticky-note usage
- `Project notes` where each project gets one note with its own checklist
- `Project Manager` in the explorer for reviewing all projects in one expandable view
- per-note color, font, size, opacity, star, and always-on-top
- show/hide notes on the desktop
- autosave to `%LOCALAPPDATA%\Nodepad\state.json`
- tray menu for new notes, show all, hide all, open explorer, and exit

## Project structure

```text
Nodepad.slnx
src/
  Nodepad.Desktop/
    MainWindow.xaml                 # Explorer + project manager
    Windows/NoteWindow.xaml         # Sticky note window
    Models/                         # Notes, checklist items, app settings
    Services/                       # Persistence, note palettes, note kinds
    Converters/                     # UI value converters
    Infrastructure/                 # Observable base class
```

## Run

```powershell
dotnet run --project .\src\Nodepad.Desktop\Nodepad.Desktop.csproj
```

## Build

```powershell
dotnet build .\Nodepad.slnx
```

## Current product direction

The app now follows this structure:

- `Desktop-first`: sticky notes are the main experience
- `Explorer-second`: the explorer is for browsing, filtering, and project oversight
- `Projects as notes`: each project has one note, one checklist, and one place to expand/collapse its details
