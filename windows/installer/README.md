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
3. Print the feed entry and publish it as `version-win.json` at `https://notchtasks.web.app/version-win.json`:

```powershell
.\manifest.ps1 -Installer .\out\NotchTasks-3.0.0-win-x64-setup.exe -Url https://notchtasks.web.app/download/NotchTasks-3.0.0-win-x64-setup.exe
# optional: -Notes notes.json   ({ "en": "...", "ja": "..." })
```

The version and build come from the installer's product version (build = major*10000 + minor*100 + patch). minOS is `10.0.19045`.
