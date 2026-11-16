# NotchTasks for Windows

Native Windows app (C#, WPF, .NET 10) that shows AI agents' questions in a floating pill at the top of the screen. It replaces the Electron app on Windows. The Mac app in `mac/` is the reference for behaviour and look; `electron/` is kept for Linux and is read only for Windows-specific details.

What Phase 2 adds:

- **Control Panel**: the Mac's panes that Windows can show: Overview, Modules, Permissions, Watching (agents), Appearance, Icons & lock, Icon Library, Sound and Behavior.
- **First-run guide**: five pages (welcome, modules, agents, permissions, ready), shown on the first start and again from Overview or the tray.
- **Permissions**: one row per permission the chosen modules need (notifications, start at login, microphone, camera, firewall rule, agent hooks), with its state.
- **Start at login**: a row in Permissions, as the Mac's login item is in its own list.
- **Lighter app**: less work while idle, and lighter sounds.

Opening the Control Panel:

- From the tray icon: a double-click, or the **Control Panel** item in its menu.
- From the pill: the gear button on the open pill.

Layout:

- `src/NotchTasks` - the WPF app (`NotchTasks.exe`)
- `src/NotchTasks.Core` - shared, AOT-safe core logic
- `src/NotchTasksHook` - the hook executable (`NotchTasksHook.exe`), copied next to the app
- `tests/NotchTasks.Core.Tests` - xUnit tests for the core
- `tests/NotchTasks.App.Tests` - WPF smoke tests: every Control Panel pane, the guide's five pages and the pill are built on an STA thread

Commands, from the repo root:

- `dotnet build windows/NotchTasks.sln`
- `dotnet test windows/NotchTasks.sln` runs both test projects
- `dotnet run --project windows/src/NotchTasks`
