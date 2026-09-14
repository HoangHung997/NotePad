# Nodepad

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
