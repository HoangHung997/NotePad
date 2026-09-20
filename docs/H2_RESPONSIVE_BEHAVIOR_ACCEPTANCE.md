# H2M-092 — Responsive behavior acceptance

Status: implementation candidate; close only after exact-head CI is green.

H2 keeps the useful responsive/window infrastructure and applies it to the current Command Center + Agent-first Project Workspace.

Acceptance coverage:

- **Narrow 560×820:** project workspace exposes the compact project picker; project navigation opens as an in-window overlay drawer instead of shrinking the editor; selecting a project closes the drawer.
- **Medium 1040×760:** task + note detail panes stay simultaneously usable with the resize affordance.
- **Short narrow 560×600:** task/note detail switches by tab so two panes are not crushed into unreadable heights.
- **Wide 1440×860:** Agent-first workspace keeps a large primary Agent surface; explicit right-dock remains a bounded secondary AI pane without crushing project detail.
- **Multi-monitor:** valid negative coordinates remain valid when that monitor exists; missing-monitor coordinates recover to the primary work area.
- **DPI:** existing Work Assistant placement acceptance remains DIP/scaling-aware and is retained in the full H2 Notes suite.

No new responsive subsystem or duplicated layout model was introduced. The tests exercise the real Avalonia `MainWindow`, real Command Center navigation, real project tabs/drawer/dock controls and the existing placement helper.
