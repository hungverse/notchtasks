# NotchTasks for Windows

Native Windows app (C#, WPF, .NET 10) that shows AI agents' questions in a floating pill at the top of the screen. It replaces the Electron app on Windows. The Mac app in `mac/` is the reference for behaviour and look; `electron/` is kept for Linux and is read only for Windows-specific details.

What Phase 3b adds:

- **Running agents**: Claude and Codex sessions, and any app or process you watch, show as cards on the pill. A card reads **Working** while the process uses CPU, and a **done** toast appears when it has finished (after the time set in Behavior, 30 s by default).
- **Agents**: Claude, Codex, Antigravity and local models (LM Studio and Ollama) are found on this PC. Claude and Codex are read from their own session files.
- **Codex questions**: when Codex asks something, the question appears in the pill and you answer it there, without opening Codex.
- **AI limits**: each agent that reports its usage shows a badge with the share used, and a chip in the pill once usage passes the warning level (80% by default, set in Behavior). A toast warns at each step from that level up to 100%, with the time the limit resets.
- **Watch list**: the Watching pane lists what the notch watches. Add an app, a program name or a process, and edit, turn off or remove each item. The list holds at most 64 items.

What Phase 3a adds:

- **Timer**: the Mac's timer in the pill, with its chip and card. It finishes with a sound and a toast.
- **Battery**: toasts when charging starts, when the PC is unplugged and when the battery is low. A PC without a battery hides the row.
- **Network**: toasts when the connection drops, when it is back, and when a VPN turns on or off.
- **Downloads**: a toast when a download finishes, with a **Show in folder** button.
- **Health**: CPU and RAM on the open pill, and a toast when a disk is almost full.
- **Mic, camera and calls**: a chip while the mic or camera is in use, and a call card with a timer and **Mute**. Windows' own record of mic and camera use is read, so no permission is asked.
- **Temperatures (optional)**: off by default. Reading temperatures on Windows needs administrator, so turning this on runs a small helper with administrator rights, and Windows asks for them only at that click. Declining leaves everything else working. **Start with Windows** installs the helper into Program Files as a task that runs as SYSTEM. Turning it off removes it again.

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
