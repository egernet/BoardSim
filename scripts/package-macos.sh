#!/bin/bash
# Builds dist/BoardSim.app and dist/BoardSim-<version>-macos-arm64.zip:
# the app with its own .NET runtime, BoardSim's QEMU fork (and its
# libraries) and the Julelys example. Nothing else needs installing.
#
# usage: scripts/package-macos.sh [version]
#   QEMU_BUILD / QEMU_SRC: the qemu-boardsim build and source folders
#   (default ../qemu-esp/build and ../qemu-esp/src)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-0.1.0}"
QEMU_BUILD="${QEMU_BUILD:-$ROOT/../qemu-esp/build}"
QEMU_SRC="${QEMU_SRC:-$ROOT/../qemu-esp/src}"
OUT="$ROOT/dist"
APP="$OUT/BoardSim.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "== .NET app (self-contained, osx-arm64)"
dotnet publish "$ROOT/src/BoardSim.App" -c Release -r osx-arm64 --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -o "$APP/Contents/MacOS" > "$OUT/publish.log"

echo "== QEMU fork"
if [ -n "${QEMU_DIR:-}" ]; then
  # A ready qemu-boardsim archive (CI): already bundled and signed.
  cp -R "$QEMU_DIR" "$APP/Contents/MacOS/qemu"
else
  "$ROOT/scripts/bundle-qemu-macos.sh" "$QEMU_BUILD" "$QEMU_SRC" "$APP/Contents/MacOS/qemu" > "$OUT/qemu.log"
fi

echo "== Example"
cp -R "$ROOT/examples" "$APP/Contents/MacOS/examples"
cp -R "$ROOT/clients" "$APP/Contents/MacOS/clients"

echo "== Icon"
ICONSET="$OUT/BoardSim.iconset"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/assets/logo-1024.png" --out "$ICONSET/icon_${s}x${s}.png" > /dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/assets/logo-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" > /dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/BoardSim.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>BoardSim</string>
  <key>CFBundleDisplayName</key><string>BoardSim</string>
  <key>CFBundleIdentifier</key><string>io.github.egernet.boardsim</string>
  <key>CFBundleExecutable</key><string>BoardSim</string>
  <key>CFBundleIconFile</key><string>BoardSim</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

echo "== Sign (ad hoc) and zip"
codesign --force --deep -s - "$APP" 2> /dev/null
ditto -c -k --keepParent "$APP" "$OUT/BoardSim-$VERSION-macos-arm64.zip"
du -sh "$APP" "$OUT/BoardSim-$VERSION-macos-arm64.zip"
