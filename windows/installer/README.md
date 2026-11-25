# Windows installer

Per-user setup (no admin) that installs NotchTasks to `%LOCALAPPDATA%\Programs\NotchTasks`. It never deletes `%APPDATA%\NotchTasks`.

## Build

Needs .NET 10 SDK and Inno Setup 6 (`ISCC.exe` on PATH, or in `Program Files (x86)\Inno Setup 6` or `%LOCALAPPDATA%\Programs\Inno Setup 6`).

```powershell
.\build.ps1            # version from windows\src\NotchTasks\NotchTasks.csproj
.\build.ps1 -Version 3.0.1 -Out D:\release
```

Output: `out\NotchTasks-<version>-win-x64-setup.exe` (git-ignored), with the staged files in `out\stage`. The hook is Native AOT when the MSVC linker is installed (`-p:UseAot=true`); otherwise it is self-contained and the build prints a warning. The app and the sensors helper are self-contained. The installer is not code-signed.

## Release

1. Build the installer (above).
2. Upload the setup exe to `https://notchtasks.web.app/download/<setup file name>`.
3. Write the feed entry with `-OutFile` and publish it as `version-win.json` at `https://notchtasks.web.app/version-win.json`. Use `-OutFile`, not `>`: Windows PowerShell writes UTF-16 or a BOM there, and the update clients then find no update (they read UTF-8 without a BOM):

```powershell
.\manifest.ps1 -Installer .\out\NotchTasks-3.0.0-win-x64-setup.exe -Url https://notchtasks.web.app/download/NotchTasks-3.0.0-win-x64-setup.exe -OutFile .\out\version-win.json
# optional: -Notes notes.json   ({ "en": "...", "ja": "..." })
```

The version and build come from the installer's product version (build = major*10000 + minor*100 + patch). minOS is `10.0.19045`. `build.ps1 -Version` also stamps NotchTasks.dll with that version, so the installed app reports the same build the feed offers (the build fails if it does not).

## Upgrading and uninstalling

- An update closes the running app and runs the new setup silently; the app starts again afterwards. The Electron build's own start-at-login value (`com.notchtasks.windows`) is moved to the native `NotchTasks` value at the first start, so the Start at login switch keeps its state. Uninstall removes both values.
- The temperature helper's copy in Program Files (`%ProgramFiles%\NotchTasks Sensors`) is not refreshed by an update. It is refreshed the next time the user turns on the temperature helper's "Start with Windows" (one administrator prompt). Until then a running sign-in helper keeps the copy it has.
- Uninstall (interactive only) asks once, default No, whether to also remove the temperature helper's sign-in task. Removing it needs administrator rights, so it only happens on Yes, after one Windows prompt. A silent uninstall keeps the task and logs it.
