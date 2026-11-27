<div align="center">

<img src=".github/assets/icon.png" width="112" alt="NotchTasks icon" />

# NotchTasks

### Your agents ask. The notch answers.

Every running AI agent and build lives at the top of your screen.<br />
When one needs permission, you allow it right there, without hunting for the terminal.

[![macOS 14+](https://img.shields.io/badge/macOS-14%2B-000000?style=flat-square&logo=apple&logoColor=white)](#install)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows11&logoColor=white)](#install)
[![iPhone](https://img.shields.io/badge/App%20Store-iPhone-0D96F6?style=flat-square&logo=appstore&logoColor=white)](https://apps.apple.com/us/app/notchtasks-ai-agent-remote/id6831539007)
[![Android](https://img.shields.io/badge/Google%20Play-Android-34A853?style=flat-square&logo=googleplay&logoColor=white)](https://play.google.com/store/apps/details?id=com.notchtasks.android)
![Free](https://img.shields.io/badge/price-free-FF9F0A?style=flat-square)
![No account](https://img.shields.io/badge/account-none-8E8E93?style=flat-square)

**[Website](https://notchtasks.web.app)** · **[Install](#install)** · **[Phone app](#phone-app)** · **[Questions](#questions)**

<br />

<img src=".github/assets/notch-macos.gif" width="760" alt="NotchTasks in the MacBook notch: animated icons, a build running, Claude asking to run git push, then charging and AirPods" />

<sub><b>macOS</b> · grows out of the notch</sub>

<img src=".github/assets/pill-windows.gif" width="760" alt="NotchTasks on Windows: a floating pill with a build running, Claude asking to run git push and the push allowed, then AirPods connected" />

<sub><b>Windows 10 and 11</b> · a floating pill at the top of the screen</sub>

</div>

<br />

## How it works

| 1. Your agent works | 2. It stops to ask | 3. You answer on the notch |
|:--|:--|:--|
| Claude Code, Codex or Gemini runs in the background. The notch shows the session and how long it has been working. | Before it runs `git push` or edits a file, the notch opens with the exact command. | **Allow**, **Deny** or pick an answer. The agent carries on at once. Away from your desk? Answer from your phone. |

## Features

<table>
<tr>
<td width="50%" valign="top">

**🙋 Answer your agents**<br />
Allow, deny or pick an answer when an agent stops to ask. Codex desktop and CLI sessions show while they run.

</td>
<td width="50%" valign="top">

**📊 AI plan limits**<br />
Claude's and Codex's 5-hour and weekly usage, as rings on the notch and on your phone.

</td>
</tr>
<tr>
<td valign="top">

**🔨 Builds and CI**<br />
Xcode, Gradle, npm, Flutter, GitHub Actions and more are found on their own. One line when they finish or fail.

</td>
<td valign="top">

**🎧 Everyday glance**<br />
Now playing, devices and battery, meetings with a Join button, a focus timer and a file shelf.

</td>
</tr>
<tr>
<td valign="top">

**👾 Make it yours**<br />
Animated pixel icons, themes, colors and sounds, set from the computer or from your phone.

</td>
<td valign="top">

**📱 Phone companion**<br />
Pair once with a QR code, then answer from the Lock Screen. Direct on the same Wi‑Fi, end-to-end encrypted elsewhere.

</td>
</tr>
<tr>
<td valign="top">

**⚡ Native on each system**<br />
Swift on the Mac. On Windows, a WPF app with Fluent icons, Mica, and light and dark themes.

</td>
<td valign="top">

**🔒 Private by design**<br />
No account. Your commands and code are never readable by us. 16 languages.

</td>
</tr>
</table>

### Supported agents

| Agent | Approve commands | Approve file edits | Answer questions |
|:--|:--:|:--:|:--:|
| Claude Code | ✅ | ✅ | ✅ |
| Gemini CLI | ✅ | ✅ | ✅ |
| Antigravity | ✅ | ✅ | ✅ |
| Codex | ✅ | ✅ | |
| OpenCode | ✅ | ✅ | |
| Cursor | ✅ | | |

Turn each one on in **Watching** in the settings. NotchTasks adds one entry to the agent's config and backs up the old file first.

## Install

<table>
<tr>
<th width="50%">🍎 macOS 14 or later</th>
<th width="50%">🪟 Windows 10 or 11 (64-bit)</th>
</tr>
<tr>
<td valign="top">

Paste this in **Terminal**:

```sh
curl -fsSL https://notchtasks.web.app/install.sh | bash
```

It installs NotchTasks into Applications and opens it.<br />
Or [download the disk image](https://notchtasks.web.app/assets/NotchTasks.dmg).

</td>
<td valign="top">

Paste this in **PowerShell**:

```powershell
irm https://notchtasks.web.app/install.ps1 | iex
```

It checks the download's SHA-256, installs for your user only (no administrator rights) and opens NotchTasks.<br />
Or [download the installer](https://notchtasks.web.app/assets/NotchTasks-Setup.exe).

</td>
</tr>
</table>

> [!NOTE]
> The Windows installer is not code-signed yet. If Windows protects your PC, choose **More info**, then **Run anyway**.
> Linux is paused for now.

## Phone app

Answer your agents and see your limits when you are away from the computer. Scan a code with your phone's camera, then pair it from **Pairing** in the NotchTasks settings.

<table>
<tr>
<td align="center" width="200"><a href="https://apps.apple.com/us/app/notchtasks-ai-agent-remote/id6831539007"><img src=".github/assets/qr-app-store.png" width="160" alt="QR code for NotchTasks on the App Store" /></a><br /><a href="https://apps.apple.com/us/app/notchtasks-ai-agent-remote/id6831539007"><b>iPhone · App Store</b></a></td>
<td align="center" width="200"><a href="https://play.google.com/store/apps/details?id=com.notchtasks.android"><img src=".github/assets/qr-google-play.png" width="160" alt="QR code for NotchTasks on Google Play" /></a><br /><a href="https://play.google.com/store/apps/details?id=com.notchtasks.android"><b>Android · Google Play</b></a></td>
</tr>
</table>

## Questions

<details>
<summary><b>My screen has no notch. Does it work?</b></summary>
<br />
Yes. Without a notch, NotchTasks appears as a small floating pill at the top of the screen and works the same way. That is how it looks on Windows.
</details>

<details>
<summary><b>Can you see my commands or code?</b></summary>
<br />
No. On the same Wi‑Fi nothing leaves your network. Away from it, messages are encrypted on your devices before they are relayed, and the key never leaves your paired devices. See the <a href="https://notchtasks.web.app/privacy.html">privacy policy</a>.
</details>

<details>
<summary><b>What if NotchTasks is closed when an agent asks?</b></summary>
<br />
The agent asks in its own window as usual. NotchTasks never blocks an agent.
</details>

<details>
<summary><b>How do I remove it from an agent?</b></summary>
<br />
Turn the agent off in <b>Watching</b>. Only the NotchTasks entry is removed; the rest of the agent's config stays as it was.
</details>

## Build from source

```sh
mac/build.sh                         # macOS app → mac/build/NotchTasks.app
pwsh windows/installer/build.ps1     # Windows installer (.NET 10 SDK, Inno Setup 6) → windows/installer/out/
dotnet test windows/NotchTasks.sln   # Windows tests
```

| Folder | What it is |
|:--|:--|
| [`mac/`](mac) | The Mac app: Swift, AppKit and SwiftUI |
| [`windows/`](windows) | The Windows app: C#, WPF on .NET 10 ([release steps](windows/installer/README.md)) |
| [`l10n/`](l10n) | Translations; run `python3 l10n/build.py` after editing a table |
| [`electron/`](electron) | The Linux app, paused for now |

## License

Not chosen yet: all rights reserved for now. The NotchTasks name and icon are not part of any code license.

<div align="center">
<br />
<sub>Made by Hung Nguyen · <a href="https://notchtasks.web.app">notchtasks.web.app</a> · <a href="mailto:notchtasks.app@gmail.com">notchtasks.app@gmail.com</a></sub>
</div>
