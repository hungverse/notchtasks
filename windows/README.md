# NotchTasks for Windows

The Windows port of NotchTasks: a pill at the top centre of the screen that shows what your AI agents, builds, CI
runs, downloads and limits are doing, and lets you answer an agent's question from it. It is a port of the macOS app
in `../mac/Sources/`; behaviour follows the Mac code, and the phone app pairs with it the same way.

Stack: Electron 33 with plain CommonJS JavaScript, no bundler and no UI framework. Why this and not WPF or Tauri, and
what it costs, is in [PLAN.md](PLAN.md).

## Run it

```sh
npm install     # electron, electron-builder, bonjour-service, qrcode
npm start       # runs the app; on macOS or Linux the Windows-only parts stay quiet
npm test        # node:test, the core modules (no Electron needed)
sh tools/crosscheck.sh   # Swift seals the fixtures, Node opens and reseals them (needs Xcode's swiftc)
npm run dist:win         # installer (NSIS) and zip in dist/
```

Settings and state live in `%APPDATA%\NotchTasks` (`settings.json`, `state.json`, `group.bin` for the pairing, which
Electron encrypts with the Windows key store). The agent hook folder is `%APPDATA%\NotchTasks\Asks`.

## The agent hook and the statusline

The Control Panel (Watching, "Answer agents on the notch") writes the hook into each agent's own settings and keeps a
backup of the file beside it. The command it writes is:

```
"C:\...\NotchTasks.exe" --hook claude        (the agent name is claude, codex, gemini, antigravity, cursor or opencode)
```

`--hook <agent>` runs with no window and no single-instance lock, reads the agent's request from stdin, and waits for
an answer from the pill. With the app closed it prints nothing and the agent asks as usual.

Claude's plan limits reach the pill through the statusline. Set Claude Code's `statusLine` to:

```
"command": "\"C:\\...\\NotchTasks.exe\" --statusline"
```

It saves the statusline JSON to `%USERPROFILE%\.claude\notchtasks-status.json` (or `CLAUDE_CONFIG_DIR`) and prints the
short "5h N% · 7d N%" line. Nothing else is written.

## What is hidden on Windows

Not shown, never faked: fan control and fan speeds; SMC temperatures (only WMI `MSAcpi_ThermalZoneTemperature` is
read, and only when it answers); the lock-screen notch; the calendar (EventKit); the camera mirror; the system
volume and brightness HUD (Windows draws its own); call detection; Bluetooth device batteries; browser-tab media
through AppleScript; the Mac permissions page. Haptics and the menu bar icon setting do not exist here.

## Not verified without a Windows PC

These were written and checked on macOS only. Each needs a run on Windows 10 or 11 before it is trusted.

- **SMTC now-playing** (`src/core/media-smtc.ps1`): the WinRT calls, the thumbnail and the command lines. The CI
  workflow only parses the script and runs it for 5 s.
- **Process list** (`src/core/proc.js`, `WIN_SCRIPT`): the `Get-CimInstance Win32_Process` encoding and the date format.
- **Process names of apps** (`src/core/watchitems.js`, `WIN_APP_EXE`): the exe names Cursor, Windsurf, Antigravity,
  Docker and the others use on Windows.
- **Temperatures** (`src/core/stats.js`, `THERMAL_SCRIPT`): whether WMI answers at all on the machine.
- **Battery** (`src/main/main.js`, `readBatteryWmi`): `Win32_Battery` and its `BatteryStatus` codes.
- **Agent folders** (`src/core/providers.js`): Claude in `%USERPROFILE%\.claude`, Codex in `%USERPROFILE%\.codex`,
  Antigravity's daemon folder (assumed the same as the Mac's), and `lms.exe` under `%USERPROFILE%\.lmstudio\bin`.
- **OpenCode** (`src/core/asks.js`): its plugin path under `%USERPROFILE%\.config\opencode`.
- **Antigravity jobs** (`src/core/providers.js`): needs `sqlite3` on PATH, which Windows does not install by default.
- **Claude `/usage`** (`execConst` in `src/core/providers.js`): the `.cmd` npm shim is run through `cmd.exe`.
- **Codex answers**: `codexReply` refuses the npm `.cmd` shim, so only a real `codex.exe` gets answers from the pill.
- **Hooks end to end**: the real agents calling `NotchTasks.exe --hook` and the reply coming back.
- **GitLab runner logs** (`src/main/main.js`, `gitlabLogs`): the folders `%ProgramData%\gitlab-runner` and
  `C:\GitLab-Runner` are guesses.
- **GitHub self-hosted runner** (`src/core/runnerlog.js`): the default folder `C:\actions-runner`.
- **Sounds**: the files named in `SOUND_FILES` (`src/main/main.js`) under `C:\Windows\Media`.
- **Mica** on the Control Panel (`src/main/prefs-window.js`) and the tray icon's look.
- **Sync on Windows**: the same-Wi-Fi link (mDNS through bonjour-service). Windows Firewall asks to allow the app's TCP
  port the first time; allow it on private networks or phones on the same Wi-Fi will not connect directly.
- **Installer and update**: the NSIS install, the SHA-256 check of the downloaded installer, and the handover to it.
  The installer is not Authenticode-signed; the manifest's SHA-256 is the only check (`src/core/updater.js`).
- **Safe storage**: the pairing group is kept with `safeStorage` (DPAPI on Windows). Without it the group lives in
  memory only and the phone page says so.
- **Start at login**: set once, on the first packaged run.
- **Downloads and file transfers**: `Downloads` and the other top-level folders are watched; subfolders are not.
- **Shortcuts that are known**: the process list is read every 5 to 15 s (not every second), so a task can end a few
  seconds late; GitHub Actions runs are followed only after a `git push` whose folder Windows does not report
  (the working directory is not read on Windows), so that path stays off.
