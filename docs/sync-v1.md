# NotchTasks sync v1 (Mac ↔ phone over Firebase)

No accounts. Every device signs in to Firebase **anonymously**. Devices that belong together form a **group**.
Firebase only ever stores ciphertext: the key travels in the QR code, never through Firebase.
The Mac talks to Firebase over its REST API (Sources/Sync.swift); the iPhone uses the Firebase SDK.

## Pairing

1. The Mac creates a group once: `g` = 16 random bytes (base64url), `key` = 32 random bytes (AES-256).
   Both are kept in the Mac's Keychain.
2. The Mac shows a QR code with the JSON `{"v":1,"g":"<g>","k":"<key base64url>","n":"<Mac name>"}`.
3. The phone scans it, keeps `g` and `key` in its Keychain, signs in anonymously and writes
   `/groups/<g>/members/<uid> = {"role":"phone","name":"<phone name>","at":<server time>}`.
   The Mac wrote its own member entry (`"role":"mac"`) when it created the group.
4. Unpairing a phone = the Mac deletes its member entry. Unpairing everything = the Mac creates a new group
   (new `g` and `key`) and shows a new QR code. Old phones can no longer read or write.

Knowing `g` is what lets a device join (128 random bits, shown only on the Mac's screen);
knowing `key` is what lets it read. The QR code carries both, so scanning it is the pairing.

## Data (all under `/groups/<g>`)

| Path | Written by | Value |
|---|---|---|
| `members/<uid>` | that device | `{role, name, at}` |
| `snapshot` | Mac | `{box, at}`: `box` seals the `Snapshot` JSON (iOS `Wire.swift`) |
| `answers/<pushId>` | phone | `{box, at}`: `box` seals the `Answer` JSON; the Mac deletes it once applied |
| `claims/<uid>` | that phone | the signed StoreKit transaction (JWS) of its Pro; `claimPro` checks it and removes it |
| `entitlement` | Cloud Function only | `{pro, until (ms; year 3000 = lifetime), product, tx, at}` (see monetization-plan.md, F1) |

`box` = base64 of AES-GCM `combined` (12-byte nonce + ciphertext + 16-byte tag), key = the group `key`.
Times are Firebase server timestamps (ms since 1970). An answer older than the ask's hook wait (590 s) is ignored.

## Rules (summary; the real file is `firebase/database.rules.json`)

- A device can create only its own member entry, and only once.
- Only members read anything under the group; only the Mac member writes `snapshot`;
  members write `answers`; nobody writes `entitlement` from a client.

## Same Wi-Fi first, Firebase when away

- The Mac advertises Bonjour `_notchtasks._tcp` named `NotchTasks <first 6 bytes of SHA-256(g), hex>`.
  The phone browses for exactly that name, so it finds its own Mac and the name reveals nothing.
- TCP frames: 4-byte big-endian length, then one AES-GCM box (combined bytes, same key as Firebase).
  Phone → Mac: sealed `Answer`s; the first one (ask `""`) is a hello that proves the phone holds the key.
  Mac → phone: sealed `Snapshot`s. Connections that send no valid hello within 5 s are dropped.
- The hello also names the phone (its Firebase uid), so the Mac knows which phones are on its Wi-Fi right now.
- While the app is awake on Wi-Fi, snapshots and answers use TCP and nothing goes to Firebase. On backgrounding the app
  closes TCP at once (a suspended app cannot start a Live Activity), so the Mac writes to Firebase and the Cloud Function
  starts or updates the Live Activity by push. With several phones, `snapshot/direct` lists those on the Wi-Fi, which the
  function skips.
- Firebase writes: right away when the asks change, otherwise at most once a minute, and only when something changed
  (limits rounded to whole percents and minutes). The phone keeps the newest snapshot by `at` from either road.
- Both apps have a switch "Nối trực tiếp khi cùng Wi‑Fi" (on by default); off means Firebase only.

## Push when the app is closed

- The phone writes its FCM token to `/groups/<g>/tokens/<uid>` (only its own, only as a `phone` member) and removes it when unpairing.
- The Mac's Firebase snapshot also carries `asks` (how many are waiting) and `newest` (ms time of the newest ask).
  These two numbers are the only readable data: no command, file or question text ever leaves the sealed box.
- Cloud Function `askPush` (functions/index.js, asia-southeast1) runs on each snapshot write; when `newest` grew it
  pushes "Agent đang chờ bạn" to every token whose uid is still a phone member, and drops tokens FCM says are gone.
- Tokens: `fcm` (alerts), `la` (the running Live Activity), `start` (push-to-start, iOS 17.2+). The function updates or
  ends a running Live Activity first, else starts one, else sends an alert that the phone's notification service opens
  (the sealed `push` box) and gives Allow / Deny buttons. The APNs key lives only in Firebase: the Mac never sends to Apple.
- Needs the APNs auth key (.p8) uploaded in Firebase console → Project settings → Cloud Messaging → Apple apps.

## Seeing the Live Activity on a simulator

Simulators here get no APNs token, so no push reaches them. The app's Settings → "Xem thử Live Activity" starts the Live
Activity locally with three sample asks ("preview:" boxes, "preview-" ids, answered by the app and never sent to the Mac).
The widget also has Xcode previews. Push delivery (start/update/end with the app closed) needs a physical iPhone.

## Build & CI, Mac health, events (phase 1)

- The snapshot also carries `builds` (Build & CI jobs at work), `health` (battery, charging, thermal 0–3, CPU and RAM in
  5 % steps) and `events` (the last 20 notch announcements of the last day: taskDone, ciPassed, ciFailed, limitWarn,
  lowBattery, warning). Older phones ignore them; older Macs simply send none.
- "Chỉ báo iPhone khi tôi rời Mac" (on by default): while the screen is unlocked and there was input in the last 2 minutes,
  the Mac writes `quiet: true` and the function sends no alerts (a running Live Activity still updates silently). Leaving
  the Mac flips it and the asks still waiting reach the phone.
- An event recorded while away (except limit warnings) sets `eventAt` and `eventBox` (the event, sealed). The function then
  pushes a plain alert with `{box, kind: "event"}`; the notification service opens it into the real title and detail.

## Builds in the Live Activity, widgets (phase 2)

- The Mac follows one build: the longest one at work, then the same one as `done` (or `failed`, when a CI failure lands
  within 30 s of it ending) for 5 minutes. Firebase gets it sealed in `job`, with readable `jobState` and `jobAt` (its start).
  Elapsed clocks and estimates ("· 0:12", "· ≈ 40s nữa") are stripped: the Live Activity counts time itself.
- `AskActivity.ContentState` is `{count, box, job?}`. An ask always comes first; with none, the build shows (hammer and live
  clock, then a check or a cross and how long it took).
- The function starts the Live Activity for a build that begins while you are away (silently), rings it once when the build
  ends (no second plain alert for that phone), and ends it when no ask waits and the build is gone.
- Widgets (home small/medium, lock screen circular/rectangular/inline) read a `WidgetSummary` the app writes to the App Group
  `group.com.notchtasks.shared` after each snapshot, only when what they show changed. No command text on the widgets.
  They show the last state the app saw: with the app closed for long, they lag until the next launch.

## Đợt 3: icon theo trạng thái

- Bộ icon động của Mac (`Resources/Sprites`, chép sang `iOS/Live/Sprites`) dùng cho 5 trạng thái: rảnh, đang chạy, chờ bạn, xong, lỗi. Đổi trong Cài đặt → Icon theo trạng thái (lưu ở App Group, widget dùng chung). Widget và Live Activity chỉ vẽ khung hình đầu.
- Webhook và tác vụ trên iPhone đã làm thử rồi gỡ (2026-11-05): người dùng chọn hướng khác, xem kế hoạch đợt 4.
