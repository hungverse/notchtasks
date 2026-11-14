#!/bin/bash
# Builds a signed app image and refreshes the update feed; upload/deployment stays manual.
set -euo pipefail
cd "$(dirname "$0")/.."
# the website lives in the mobile repo (private), checked out next to this one; override with SITE=<path>
SITE=${SITE:-../../NotchTasks-mobile/site}
test -f "$SITE/version.json" || { echo "Website not found at $SITE (set SITE=<path to the mobile repo's site/>)." >&2; exit 1; }

DEVELOPER_ID=$(security find-identity -v -p codesigning | grep -m1 'Developer ID Application:.*(827KQMHNVL)' | awk '{print $2}' || true)
test -n "$DEVELOPER_ID" || { echo "Developer ID Application for 827KQMHNVL is missing." >&2; exit 1; }
SIGN_ID="$DEVELOPER_ID" ./build.sh
APP=build/NotchTasks.app
DMG="$SITE/assets/NotchTasks.dmg"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

cp -R "$APP" "$STAGE/NotchTasks.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname NotchTasks -srcfolder "$STAGE" -ov -format UDZO "$DMG"
codesign --force --timestamp -s "$DEVELOPER_ID" "$DMG"
if xcrun notarytool history --keychain-profile notchtasks-notary >/dev/null 2>&1; then
  if ! xcrun notarytool submit "$DMG" --keychain-profile notchtasks-notary --wait --output-format json > "$STAGE/notary.json"; then
    cat "$STAGE/notary.json" >&2
    NOTARY_ID=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id", ""))' "$STAGE/notary.json" 2>/dev/null || true)
    if [[ -n "$NOTARY_ID" ]]; then xcrun notarytool log "$NOTARY_ID" --keychain-profile notchtasks-notary >&2; fi
    exit 1
  fi
  NOTARY_ID=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d.get("id", ""))' "$STAGE/notary.json")
  if ! python3 -c 'import json,sys; sys.exit(0 if json.load(open(sys.argv[1])).get("status") == "Accepted" else 1)' "$STAGE/notary.json"; then
    cat "$STAGE/notary.json" >&2
    if [[ -n "$NOTARY_ID" ]]; then xcrun notarytool log "$NOTARY_ID" --keychain-profile notchtasks-notary >&2; fi
    exit 1
  fi
  xcrun stapler staple "$DMG"
  spctl -a -t open --context context:primary-signature -v "$DMG"
else
  echo "NOT NOTARIZED: run xcrun notarytool store-credentials notchtasks-notary …"
fi

VERSION=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")
BUILD=$(/usr/libexec/PlistBuddy -c 'Print CFBundleVersion' "$APP/Contents/Info.plist")
HASH=$(shasum -a 256 "$DMG" | awk '{print $1}')
VERSION="$VERSION" BUILD="$BUILD" HASH="$HASH" NOTES_EN="${NOTES_EN-}" NOTES_VI="${NOTES_VI-}" \
NOTES_EN_SET="${NOTES_EN+x}" NOTES_VI_SET="${NOTES_VI+x}" python3 - "$SITE/version.json" <<'PY'
import json
import os
import sys

path = sys.argv[1]
version = os.environ["VERSION"]
build = int(os.environ["BUILD"])
old = {}
if os.path.exists(path):
    with open(path) as source:
        old = json.load(source)
    old_build = old.get("build")
    if not isinstance(old_build, int) or build <= old_build:
        raise SystemExit("Refusing to replace version.json without a higher build.")

old_notes = old.get("notes", {}) if old.get("version") == version and old.get("build") == build else {}
notes = {
    "en": os.environ["NOTES_EN"] if os.environ["NOTES_EN_SET"] else old_notes.get("en", "Bug fixes and improvements."),
    "vi": os.environ["NOTES_VI"] if os.environ["NOTES_VI_SET"] else old_notes.get("vi", "Sửa lỗi và cải thiện."),
}
manifest = {"version": version, "build": build, "url": "https://notchtasks.web.app/assets/NotchTasks.dmg",
            "sha256": os.environ["HASH"], "minOS": "14.0", "notes": notes}
temporary = path + ".new"
with open(temporary, "w") as output:
    json.dump(manifest, output, ensure_ascii=False, separators=(",", ":"))
    output.write("\n")
os.replace(temporary, path)
PY
echo "Ready to upload: $DMG and $SITE/version.json"
