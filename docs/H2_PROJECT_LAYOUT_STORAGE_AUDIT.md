# H2M-090 — ProjectLayout storage audit

Status: implementation candidate; close only after exact-head CI is green.

## Classification

Current `ProjectLayout` properties were reviewed by ownership rather than by convenience.

| Property | Classification | Persistence decision |
| --- | --- | --- |
| `Tab` | user preference | stays with shared project preference state |
| `AiDock` | user preference | stays with shared project preference state; responsive layout still adapts per window |
| `AiExplicitlyHidden` | user preference | stays with shared project preference state |
| `TasksCollapsed` | user preference | stays with shared project preference state |
| `NotesCollapsed` | user preference | stays with shared project preference state |
| `NotesFraction` | machine-local layout | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |
| `HasCustomSplit` | machine-local layout | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |
| `AiWidth` | machine-local geometry | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |
| `AiHeight` | machine-local geometry | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |
| `AiX` | machine-local geometry | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |
| `AiY` | machine-local geometry | moved to `LocalConfiguration.ProjectLayouts[ProjectId]` |

None of the current `ProjectLayout` fields are authoritative project/business content.

## Why some preferences remain shared

`Tab`, AI dock intent and collapsed/expanded preference are portable user choices tied to a project rather than monitor coordinates. They remain small project preferences. Responsive rules are still authoritative for the actual rendered arrangement, so a saved `right` dock preference does not force invalid pixel geometry onto another machine.

This decision can be revisited by later UX work, but it does not permit monitor- or window-specific coordinates in NAS project files.

## Machine-local storage

Machine-specific layout is now held in:

`LocalConfiguration.ProjectLayouts : Dictionary<Guid, LocalProjectLayout>`

and persists in the existing machine-local:

`%LocalAppData%\H2Notes\local-config-v2.json`

The key is the stable H2 `ProjectId`, which lets one Windows profile remember different geometry for different projects without copying that geometry into shared project data.

`LocalProjectLayout` contains only:

- notes/task split fraction plus the custom-split flag;
- floating AI width/height;
- floating AI X/Y coordinates.

Values are normalized and bounded when read.

## NAS boundary

`ProjectLayout` no longer defines any of the machine-local geometry properties above. Therefore normal `ProjectWorkspaceStore` project serialization cannot emit those fields into `projects/*.h2project.json`.

Legacy shared JSON may still contain old geometry field names. They are intentionally ignored on read because those values were machine-specific and unsafe to inherit on a different PC. The receiving machine starts from safe local defaults until its own user adjusts the layout.

## Save ownership

- changing split ratio -> local config save only;
- resizing floating AI -> local config save only;
- moving floating AI -> local config save only;
- changing portable AI dock/collapse/tab preference -> normal project save;
- reset layout -> resets both the portable preference and local geometry.

H2M-091 remains responsible for the broader `DesktopSessionState` audit. This task does not move or redesign DesktopSession ownership.
