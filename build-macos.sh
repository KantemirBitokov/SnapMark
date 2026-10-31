#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
#  SnapMark — сборка для macOS: SnapMark.app и .dmg (Apple Silicon и Intel).
#
#  Запускается НА MAC (нужны codesign и hdiutil) или в GitHub Actions (см. .github/workflows/release.yml).
#  Нужно: .NET 8 SDK  (brew install --cask dotnet-sdk)
#
#    ./build-macos.sh                 # обе архитектуры, подпись «ad-hoc»
#    ./build-macos.sh arm64           # только Apple Silicon
#
#  Подпись разработчика и нотаризация (чтобы macOS не ругалась при первом запуске) — по желанию:
#    export SIGN_IDENTITY="Developer ID Application: Имя (TEAMID)"
#    export NOTARY_PROFILE="snapmark"   # создаётся один раз: xcrun notarytool store-credentials snapmark ...
#
#  Результат: release/SnapMark-<версия>-macOS-arm64.dmg, release/SnapMark-<версия>-macOS-x64.dmg
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
cd "$(dirname "$0")"

PROJ=src/SnapMark.App/SnapMark.App.csproj
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJ" | head -1)
ARCHS=${1:-"arm64 x64"}
SIGN_IDENTITY=${SIGN_IDENTITY:--}   # «-» = ad-hoc подпись (обязательна для Apple Silicon)
mkdir -p release

for ARCH in $ARCHS; do
  echo "== SnapMark $VERSION · macOS $ARCH"
  OUT=build/macos-$ARCH
  APP=$OUT/SnapMark.app
  rm -rf "$OUT"
  dotnet publish "$PROJ" -c Release -r "osx-$ARCH" --self-contained true \
    -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false \
    -o "$OUT/publish"

  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
  cp packaging/macos/snapmark.icns "$APP/Contents/Resources/"
  sed "s/__VERSION__/$VERSION/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
  chmod +x "$APP/Contents/MacOS/SnapMark"

  # Подпись: сначала все библиотеки, потом сам бандл (Hardened Runtime нужен для нотаризации).
  if [ "$SIGN_IDENTITY" = "-" ]; then
    codesign --force --deep --sign - "$APP"
  else
    find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -111 \) -print0 |
      xargs -0 codesign --force --timestamp --options runtime --entitlements packaging/macos/entitlements.plist --sign "$SIGN_IDENTITY"
    codesign --force --timestamp --options runtime --entitlements packaging/macos/entitlements.plist --sign "$SIGN_IDENTITY" "$APP"
  fi
  codesign --verify --deep --strict "$APP"

  # DMG: окно с программой и ярлыком «Программы» — перетащить и готово.
  DMG=release/SnapMark-$VERSION-macOS-$ARCH.dmg
  STAGE=$OUT/dmg
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  rm -f "$DMG"
  hdiutil create -volname "SnapMark $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"

  if [ "$SIGN_IDENTITY" != "-" ]; then
    codesign --force --sign "$SIGN_IDENTITY" "$DMG"
    if [ -n "${NOTARY_PROFILE:-}" ]; then
      xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
      xcrun stapler staple "$DMG"
    fi
  fi
  echo "   → $DMG"
done
