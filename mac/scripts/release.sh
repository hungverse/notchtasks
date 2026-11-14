#!/bin/bash
# Release builds from NotchTasks.xcodeproj.
#   scripts/release.sh web       → build/release/NotchTasks-<version>.dmg, Developer ID signed + notarized (download from a website)
#   scripts/release.sh appstore  → archive with the sandbox, uploaded to App Store Connect
# Needs Xcode signed in to the team's account (Xcode → Settings → Accounts). For web, once per machine:
#   xcrun notarytool store-credentials notchtasks --apple-id <email> --team-id 827KQMHNVL
set -euo pipefail
cd "$(dirname "$0")/.."
MODE=${1:-}
TEAM=${TEAM_ID:-827KQMHNVL}
OUT=build/release

case "$MODE" in
  web)      SCHEME=NotchTasks;             METHOD=developer-id;      DEST=export ;;
  appstore) SCHEME="NotchTasks App Store"; METHOD=app-store-connect; DEST=upload ;;
  *) echo "Usage: scripts/release.sh web|appstore" >&2; exit 1 ;;
esac
rm -rf "$OUT" && mkdir -p "$OUT"

cat > "$OUT/ExportOptions.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>$METHOD</string>
  <key>destination</key><string>$DEST</string>
  <key>teamID</key><string>$TEAM</string>
  <key>signingStyle</key><string>automatic</string>
</dict></plist>
EOF

xcodebuild -project NotchTasks.xcodeproj -scheme "$SCHEME" -archivePath "$OUT/NotchTasks.xcarchive" \
  DEVELOPMENT_TEAM="$TEAM" -allowProvisioningUpdates archive
xcodebuild -exportArchive -archivePath "$OUT/NotchTasks.xcarchive" -exportPath "$OUT" \
  -exportOptionsPlist "$OUT/ExportOptions.plist" -allowProvisioningUpdates

if [[ "$MODE" == appstore ]]; then
  echo "Uploaded. Finish the listing in App Store Connect → Apps."
  exit 0
fi

# DMG with an Applications shortcut: drag the app across to install
STAGE="$OUT/dmg" && mkdir -p "$STAGE"
cp -R "$OUT/NotchTasks.app" "$STAGE/" && ln -s /Applications "$STAGE/Applications"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$OUT/NotchTasks.app/Contents/Info.plist")
DMG="$OUT/NotchTasks-$VERSION.dmg"
hdiutil create -volname NotchTasks -srcfolder "$STAGE" -ov -format UDZO "$DMG"
codesign -s "Developer ID Application" --timestamp "$DMG"
# notarizing the DMG covers the app inside; stapling lets Gatekeeper accept it offline
xcrun notarytool submit "$DMG" --keychain-profile "${NOTARY_PROFILE:-notchtasks}" --wait
xcrun stapler staple "$DMG"
spctl -a -t open --context context:primary-signature -v "$DMG"
echo "Ready to upload: $DMG"
