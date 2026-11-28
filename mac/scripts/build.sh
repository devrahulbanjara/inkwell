#!/usr/bin/env bash
# Builds build/Inkwell.app (universal arm64 + x86_64 by default).
#   VERSION=1.2.0 ./scripts/build.sh
#   ARCHS="" ./scripts/build.sh            # native arch only (works with Command Line Tools)
#   SIGN_ID="Developer ID Application: …"  # optional real signing; default is ad-hoc
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="Inkwell"
BUNDLE_ID="${BUNDLE_ID:-io.github.inkwell}"
VERSION="${VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//')}"
VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-$(git rev-list --count HEAD 2>/dev/null || echo 1)}"
ARCHS="${ARCHS---arch arm64 --arch x86_64}"
SIGN_ID="${SIGN_ID:--}"

echo "▸ Building $APP_NAME $VERSION ($BUILD)"
# shellcheck disable=SC2086
swift build -c release $ARCHS
# shellcheck disable=SC2086
BIN_DIR="$(swift build -c release $ARCHS --show-bin-path)"

APP="build/$APP_NAME.app"
rm -rf build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$APP_NAME" "$APP/Contents/MacOS/$APP_NAME"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" -e "s/__BUNDLE_ID__/$BUNDLE_ID/" \
    Resources/Info.plist > "$APP/Contents/Info.plist"

echo "▸ Icon"
ICONSET="build/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s Resources/AppIcon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) Resources/AppIcon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

echo "▸ Signing ($SIGN_ID)"
codesign --force --options runtime --timestamp=none --sign "$SIGN_ID" "$APP"
codesign --verify --verbose=1 "$APP"

echo "✓ $APP"
