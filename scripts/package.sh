#!/usr/bin/env bash
# Packages BoardSim for one platform with its emulator, the example and the
# Python clients - nothing else needs installing:
#
#   scripts/package.sh <platform> <version> <qemu dir>
#     platform: windows-x64 | linux-x64 | linux-arm64 | macos-arm64
#     qemu dir: an unpacked qemu-boardsim archive (the folder holding bin/ and share/)
#
# Output in dist/: BoardSim-<version>-<platform>.zip (Windows, macOS) or .tar.gz (Linux).
set -euo pipefail
PLATFORM="$1"; VERSION="$2"; QEMU="$(cd "$3" && pwd)"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist"
case "$PLATFORM" in
  windows-x64) RID=win-x64 ;;
  linux-x64)   RID=linux-x64 ;;
  linux-arm64) RID=linux-arm64 ;;
  macos-arm64) RID=osx-arm64 ;;
  *) echo "unknown platform $PLATFORM"; exit 2 ;;
esac
mkdir -p "$OUT"

if [ "$PLATFORM" = macos-arm64 ]; then
  QEMU_DIR="$QEMU" "$ROOT/scripts/package-macos.sh" "$VERSION"
  exit 0
fi

STAGE="$OUT/stage-$PLATFORM/BoardSim"
rm -rf "$OUT/stage-$PLATFORM"; mkdir -p "$STAGE"
dotnet publish "$ROOT/src/BoardSim.App" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -o "$STAGE" > "$OUT/publish-$PLATFORM.log"
dotnet publish "$ROOT/src/BoardSim.Cli" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -o "$STAGE" >> "$OUT/publish-$PLATFORM.log"
# "boardsim" would clash with "BoardSim" on case-insensitive file systems.
for ext in "" .exe; do [ -f "$STAGE/BoardSim.Cli$ext" ] && mv "$STAGE/BoardSim.Cli$ext" "$STAGE/boardsim-cli$ext"; done
cp -R "$QEMU" "$STAGE/qemu"
cp -R "$ROOT/examples" "$STAGE/examples"
cp -R "$ROOT/clients" "$STAGE/clients"
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"

if [ "$PLATFORM" = windows-x64 ]; then
  cat > "$STAGE/README.txt" <<TXT
BoardSim $VERSION for Windows

Start BoardSim.exe and press "Try the Julelys example".
The command line tool is boardsim-cli.exe (boardsim-cli chip board.kicad_pcb ...).
Flash the emulated chip:  idf.py -p rfc2217://localhost:4141 flash monitor
More: https://github.com/egernet/BoardSim
TXT
  (cd "$OUT/stage-$PLATFORM" && rm -f "$OUT/BoardSim-$VERSION-$PLATFORM.zip" && zip -qr "$OUT/BoardSim-$VERSION-$PLATFORM.zip" BoardSim)
else
  cp "$ROOT/assets/logo-1024.png" "$STAGE/boardsim.png"
  cat > "$STAGE/install-desktop-entry.sh" <<'SH'
#!/bin/sh
# Adds BoardSim to the application menu (for this user).
DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/boardsim.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=BoardSim
Comment=Check, simulate and run KiCad boards
Exec="$DIR/BoardSim" %f
Icon=$DIR/boardsim.png
Categories=Development;Electronics;
MimeType=application/x-kicad-pcb;
DESKTOP
echo "BoardSim is in the application menu."
SH
  chmod +x "$STAGE/install-desktop-entry.sh" "$STAGE/BoardSim" "$STAGE/boardsim-cli" "$STAGE/qemu/bin/qemu-system-riscv32"
  cat > "$STAGE/README.txt" <<TXT
BoardSim $VERSION for Linux ($PLATFORM)

Run ./BoardSim and press "Try the Julelys example".
./install-desktop-entry.sh adds it to the application menu.
The command line tool is ./boardsim-cli (boardsim-cli chip board.kicad_pcb ...).
Flash the emulated chip:  idf.py -p rfc2217://localhost:4141 flash monitor
Needs a desktop with X11 or XWayland; the emulator and .NET are included.
More: https://github.com/egernet/BoardSim
TXT
  tar -czf "$OUT/BoardSim-$VERSION-$PLATFORM.tar.gz" -C "$OUT/stage-$PLATFORM" BoardSim
fi
rm -rf "$OUT/stage-$PLATFORM"
ls -la "$OUT"/BoardSim-"$VERSION"-"$PLATFORM".*
