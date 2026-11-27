#!/bin/bash
# Build NotchTasks.app. Usage: ./build.sh [--install]  (--install copies to /Applications)
set -euo pipefail
cd "$(dirname "$0")"
APP=build/NotchTasks.app
rm -rf "$APP"
# Same project Xcode opens (NotchTasks.xcodeproj); signed below so local installs keep one stable identity
xcodebuild -project NotchTasks.xcodeproj -scheme NotchTasks -configuration Release -derivedDataPath build/DerivedData \
  CODE_SIGNING_ALLOWED=NO NT_GA_SECRET="${NT_GA_SECRET:-$(cat ~/.notchtasks/ga-secret 2>/dev/null || true)}" -quiet build
cp -R build/DerivedData/Build/Products/Release/NotchTasks.app "$APP"
# A real identity keeps the signature stable across builds. Ad-hoc ("-") signs by content hash, so macOS sees every
# build as a new app and drops Calendar/Accessibility grants. Override with SIGN_ID=<hash>.
IDENTITIES=$(security find-identity -v -p codesigning)
SIGN_ID=${SIGN_ID:-$(printf '%s\n' "$IDENTITIES" | grep -m1 'Developer ID Application:.*(827KQMHNVL)' | awk '{print $2}' || true)}
SIGN_ID=${SIGN_ID:-$(printf '%s\n' "$IDENTITIES" | grep -i "Apple Development: $(id -F)" | head -1 | awk '{print $2}' || true)}
SIGN_ID=${SIGN_ID:-$(printf '%s\n' "$IDENTITIES" | grep -m1 "Apple Development" | awk '{print $2}' || true)}
SIGN_FLAGS=()
if [[ "$SIGN_ID" == *"Developer ID Application"* ]] || printf '%s\n' "$IDENTITIES" | grep -F "$SIGN_ID" | grep -q 'Developer ID Application'; then
  SIGN_FLAGS+=(--timestamp)
fi
HELPER="$APP/Contents/Library/PrivilegedHelperTools/com.notchtasks.fanhelper"
test -x "$HELPER"
codesign --force --options runtime "${SIGN_FLAGS[@]}" -i com.notchtasks.fanhelper -s "${SIGN_ID:--}" "$HELPER"
codesign --force --options runtime "${SIGN_FLAGS[@]}" --entitlements Config/NotchTasks.entitlements -s "${SIGN_ID:--}" "$APP"
CI_SELFTEST=1 "$APP/Contents/MacOS/NotchTasks" --selftest
if [[ "${1:-}" == "--install" ]]; then
  rm -rf /Applications/NotchTasks.app && cp -R "$APP" /Applications/
  # so Finder opens .notchsounds files with this copy right away
  LSREG=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
  $LSREG -f /Applications/NotchTasks.app
  # only the installed copy may answer for the bundle id: a stale build copy left notifications on the old icon
  $LSREG -u "$PWD/$APP" "$PWD/build/DerivedData/Build/Products/Release/NotchTasks.app" 2>/dev/null || true
  echo "Installed to /Applications/NotchTasks.app"
fi
echo "Built $APP"
