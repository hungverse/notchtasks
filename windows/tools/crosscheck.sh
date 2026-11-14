#!/bin/sh
# Seals with Swift/CryptoKit and the phone's wire types (iOS/Shared), opens them in the Node tests, and back.
# Needs Xcode's swiftc. The fixtures gen writes are checked in, so the Node tests also run without it.
set -e
cd "$(dirname "$0")/.."
S=../iOS/Shared
mkdir -p build
swiftc -swift-version 5 -suppress-warnings -o build/swiftcheck tools/crosscheck/main.swift tools/crosscheck/stubs.swift \
  $S/Wire.swift $S/Notch.swift $S/Seal.swift $S/Risk.swift $S/L10n.swift
build/swiftcheck gen test/fixtures/crosscheck
node --test test/box.test.js test/wire.test.js
build/swiftcheck check build/crosscheck
