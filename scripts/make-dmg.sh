#!/usr/bin/env bash
# Packages build/Inkwell.app into build/Inkwell-<version>.dmg with an Applications shortcut.
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/Inkwell.app"
[ -d "$APP" ] || { echo "Run scripts/build.sh first"; exit 1; }
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")"
DMG="build/Inkwell-$VERSION.dmg"
STAGE="build/dmg"

rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

hdiutil create -volname "Inkwell" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
rm -rf "$STAGE"

echo "✓ $DMG"
shasum -a 256 "$DMG"
