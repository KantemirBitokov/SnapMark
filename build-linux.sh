#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
#  SnapMark — сборка для Linux: AppImage, .deb и .tar.gz (x64 и arm64).
#
#  Где запускать: любой Linux (Ubuntu/Debian), WSL2 на Windows или GitHub Actions.
#  Нужно: .NET 8 SDK, dpkg-deb, curl  (Ubuntu: sudo apt install dotnet-sdk-8.0 dpkg curl)
#
#    ./build-linux.sh            # x64 и arm64
#    ./build-linux.sh x64        # только x64
#
#  Результат в release/:
#    SnapMark-<версия>-linux-x64.AppImage   — один файл, запускается на любом дистрибутиве
#    snapmark_<версия>_amd64.deb            — для Ubuntu/Debian/Mint (sudo apt install ./snapmark_….deb)
#    SnapMark-<версия>-linux-x64.tar.gz     — просто папка с программой
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
cd "$(dirname "$0")"

PROJ=src/SnapMark.App/SnapMark.App.csproj
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)
ARCHS=${1:-"x64 arm64"}
mkdir -p release build

APPIMAGETOOL=build/appimagetool-x86_64.AppImage
if [ ! -f "$APPIMAGETOOL" ]; then
  curl -fsSL -o "$APPIMAGETOOL" \
    https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage || true
  chmod +x "$APPIMAGETOOL" 2>/dev/null || true
fi

for ARCH in $ARCHS; do
  case $ARCH in
    x64)   DEB_ARCH=amd64; AI_ARCH=x86_64 ;;
    arm64) DEB_ARCH=arm64; AI_ARCH=aarch64 ;;
    *) echo "unknown arch $ARCH"; exit 1 ;;
  esac
  echo "== SnapMark $VERSION · linux-$ARCH"
  OUT=build/linux-$ARCH
  rm -rf "$OUT"
  dotnet publish "$PROJ" -c Release -r "linux-$ARCH" --self-contained true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$OUT/publish"
  chmod +x "$OUT/publish/SnapMark"

  # ── tar.gz ──
  tar -C "$OUT/publish" -czf "release/SnapMark-$VERSION-linux-$ARCH.tar.gz" .

  # ── .deb ──
  DEB=$OUT/deb
  mkdir -p "$DEB/DEBIAN" "$DEB/opt/snapmark" "$DEB/usr/bin" \
           "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/512x512/apps"
  cp -R "$OUT/publish/." "$DEB/opt/snapmark/"
  ln -s /opt/snapmark/SnapMark "$DEB/usr/bin/snapmark"
  cp packaging/linux/snapmark.desktop "$DEB/usr/share/applications/snapmark.desktop"
  cp packaging/linux/snapmark.png "$DEB/usr/share/icons/hicolor/512x512/apps/snapmark.png"
  cat > "$DEB/DEBIAN/control" <<EOF
Package: snapmark
Version: $VERSION
Section: graphics
Priority: optional
Architecture: $DEB_ARCH
Maintainer: Kantemir Bitokov <https://sadlybear.pro>
Homepage: https://sadlybear.pro
Depends: libx11-6, libice6, libsm6, libfontconfig1
Recommends: ffmpeg, xclip
Suggests: wl-clipboard, gnome-screenshot
Description: Screenshots with markers and comments, screen recording
 Select an area, annotate with arrows, text and A-B-C markers with comments,
 copy everything at once, record the screen with sound.
EOF
  dpkg-deb --build --root-owner-group "$DEB" "release/snapmark_${VERSION}_${DEB_ARCH}.deb"

  # ── AppImage ──
  if [ -x "$APPIMAGETOOL" ]; then
    APPDIR=$OUT/SnapMark.AppDir
    mkdir -p "$APPDIR/usr/bin"
    cp -R "$OUT/publish/." "$APPDIR/usr/bin/"
    cp packaging/linux/snapmark.desktop "$APPDIR/snapmark.desktop"
    cp packaging/linux/snapmark.png "$APPDIR/snapmark.png"
    cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/SnapMark" "$@"
EOF
    chmod +x "$APPDIR/AppRun"
    ARCH=$AI_ARCH "$APPIMAGETOOL" --appimage-extract-and-run "$APPDIR" \
      "release/SnapMark-$VERSION-linux-$ARCH.AppImage"
  else
    echo "   (appimagetool недоступен — AppImage пропущен)"
  fi
done

echo
ls -la release
