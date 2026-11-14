# NotchTasks — desktop

The computer side of NotchTasks: it watches your AI coding agents, builds, CI runs and limits, shows them in the
notch (a pill on screens without one), and lets you answer an agent from there or from the phone app.

| Folder | What it is |
| --- | --- |
| `mac/` | macOS app (Swift, AppKit + SwiftUI). `mac/build.sh` builds it, `mac/scripts/release-site.sh` makes the notarized DMG and update feed in the website (the mobile repo's `site/`, checked out next to this one). |
| `windows/` | Windows app (Electron): the same features in a pill at the top of the screen. See `windows/README.md`. |
| `l10n/` | Translations (15 languages; the Vietnamese text is the key). `python3 l10n/build.py` writes `mac/Sources/l10n-*.json`, which the Windows app packs too. |
| `docs/` | `sync-v1.md`: the encrypted wire protocol between computer and phone. |

The phone apps (iOS, Android), the Firebase backend and the website live in a separate, private repository.

## Build

```sh
mac/build.sh                 # macOS app → mac/build/NotchTasks.app
cd windows && npm ci && npm test && npm run dist:win   # Windows installer → windows/dist/
```
