# NotchTasks for Windows — plan

Folder `windows/`. Port of the macOS app in `../mac/Sources/`.
No notch on a PC: the notch becomes a **pill** floating at the top centre of the primary display.

## Stack: Electron 33 + plain JavaScript (CommonJS), no bundler, no UI framework

Why this and not .NET WPF/WinUI or Tauri:

- **It runs on this Mac.** The whole UI (pill, hover, panes, onboarding) can be launched, clicked and screenshotted
  here; WPF/WinUI compile on macOS at best but never run, so "same UI/UX, no bugs" could not be checked at all.
- **Crypto and network are built in.** Node's `crypto` has AES-256-GCM and SHA-256; `net`, `https`, `fs.watch` cover the
  TCP link, Firebase REST and the Asks folder. Only two runtime packages: `bonjour-service` (mDNS, pure JS) and `qrcode`.
- **Haiku writes JS/HTML/CSS reliably**, and `node:test` runs the core tests with nothing to install.
- **Windows build from this Mac:** `electron-builder --win` makes the installer without a Windows machine.
  A GitHub Actions workflow (`.github/workflows/windows.yml`, not pushed) builds and tests on `windows-latest` too.
- **Linux is planned**: the same code ships there later.
- Costs: ~90 MB installer, ~120 MB RAM. Kept low on CPU: the pill window is never resized or re-laid out per frame,
  sprites step at a few fps with CSS `steps()`, pollers slow down while the pill is closed (Mac lessons).

Things Windows lacks are **hidden**, never faked: fan control and fan speeds, SMC temperatures (shown only if WMI
`MSAcpi_ThermalZoneTemperature` answers), lock-screen notch, calendar (EventKit), camera mirror, volume/brightness HUD
(Windows draws its own), call detection, Bluetooth device batteries, browser-tab media via AppleScript.

## Layout

```
windows/
  package.json            electron, electron-builder (dev); bonjour-service, qrcode
  src/core/               pure Node, no electron import, unit-tested
    l10n.js               L(vi) from ../mac/Sources/l10n-*.json + l10n/windows.json (Vietnamese is the key)
    store.js              settings + state JSON in %APPDATA%/NotchTasks (atomic writes)
    swiftjson.js          Swift JSONEncoder conventions: Date = seconds since 2001-01-01, enums {"allow":{}}
    box.js                Pairing.parse, base64url, AES-GCM seal/open (12-byte nonce|ct|16-byte tag), frames
    wire.js               Snapshot / Answer / AgentAsk shapes as Sources/Sync.swift encodes them
    sync.js               group create, Firebase REST (anon auth, snapshot, answers, members), TCP + mDNS
    asks.js               --hook side + Asks folder watcher + hook installer per agent (AgentAsks.swift)
    providers.js          usage limits: Claude, Codex, Antigravity (Providers.swift, AntigravityLimits)
    devscan.js            build tools / dev tasks from the process list (DevTasks.swift, WatchItems.swift)
    runnerlog.js github.js  CI runners and GitHub runs (Runner.swift, GitHub.swift)
    stats.js              CPU, memory, battery, net, downloads; WMI temps if readable
    media.js              now playing via Windows SMTC (one long-lived PowerShell child, JSON lines)
    track.js              GA4 Measurement Protocol, ids empty = off, opt-out
    updater.js            manifest https://notchtasks.web.app/version-win.json (404 = no update)
  src/main/               electron main process
    main.js               single instance, --hook mode (no window), tray, IPC, wiring core -> state
    pill.js               the pill window
    windows.js            Preferences / onboarding windows
  src/renderer/           HTML/CSS/JS, one `state` object pushed from main, `act(name, args)` back
    pill.html pill.css pill.js       closed + expanded notch, toasts, cards
    prefs.html prefs.css prefs.js    Control Panel panes (Pane enum in Preferences.swift)
    onboarding.html onboarding.js
    sprites.js            SpriteLibrary: ../mac/Resources/Sprites + catalog.json
  test/*.test.js          node --test
  tools/crosscheck/       Swift (iOS/Shared + CryptoKit) seals fixtures, Node opens and reseals; fixtures checked in
  l10n/windows.json       strings only Windows has, 15 languages
```

Assets are not copied by hand: `build.extraResources` takes `../mac/Resources/Sprites` and `../mac/Sources/l10n-*.json` at
build time; dev mode reads them in place.

## The pill (the collapse bug, solved by design)

- One frameless, transparent, always-on-top, skip-taskbar window, **sized once to the largest open size** at the top
  centre of the primary display's work area. It is never resized on hover.
- The window ignores the mouse (`setIgnoreMouseEvents(true, {forward:true})`) except over the pill's real rectangle:
  the renderer reports the pill's rect (closed or open) and main turns mouse events on/off for it.
- Open on hover (or click, per setting) after a short delay; close only when the pointer has been **outside the open
  pill's current rect for 350 ms**, measured by main from `screen.getCursorScreenPoint()` (polled at 10 Hz only while
  open). A layout change inside the pill can never close it, unlike the Mac's `contentSize` race.

## Work split (Haiku writes, main session reviews/builds/tests)

1. Scaffold + `l10n`, `store`, `swiftjson`, `box`, `wire` + crosscheck fixtures and tests.
2. `sync.js` (Firebase REST, TCP, mDNS, pairing QR) + tests against fixtures.
3. `asks.js`, `providers.js` + parser tests (hook payloads of every agent, limit files).
4. `devscan.js`, `runnerlog.js`, `github.js` + tests.
5. `stats.js`, `media.js`, `track.js`, `updater.js`.
6. Pill renderer + `pill.js`.
7. Preferences, onboarding, badge editor renderer.
8. `main.js` wiring, packaging, CI workflow, README.
