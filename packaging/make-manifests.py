#!/usr/bin/env python3
"""
Манифесты для winget, Homebrew и AUR под конкретную версию.

    python3 packaging/make-manifests.py 1.0.7 [папка-с-файлами-релиза]

Если файлов в папке нет, контрольные суммы берутся с GitHub (файлы релиза уже опубликованы).

Берёт файлы релиза (по умолчанию из release/), считает SHA-256 и пишет:
  packaging/winget/manifests/s/SadlyBear/SnapMark/<версия>/*.yaml
  packaging/homebrew/Casks/snapmark.rb
  packaging/aur/PKGBUILD
Ссылки ведут на GitHub Releases, поэтому репозиторий должен быть публичным.
"""
import hashlib
import pathlib
import sys

REPO = "https://github.com/KantemirBitokov/SnapMark"
ROOT = pathlib.Path(__file__).resolve().parent


def sha(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def github_digests(ver: str, names) -> dict:
    import json, urllib.request
    api = REPO.replace("https://github.com/", "https://api.github.com/repos/")
    with urllib.request.urlopen(f"{api}/releases/tags/v{ver}") as r:
        assets = {a["name"]: a.get("digest") or "" for a in json.load(r)["assets"]}
    out = {}
    for n in names:
        d = assets.get(n, "")
        if not d.startswith("sha256:"):
            sys.exit(f"No file or GitHub digest for {n}")
        out[n] = d.split(":", 1)[1]
    return out


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    ver = sys.argv[1].lstrip("v")
    rel = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else ROOT.parent / "release")
    url = f"{REPO}/releases/download/v{ver}"

    setup = f"SnapMark-{ver}-Setup.exe"
    dmg_arm = f"SnapMark-{ver}-macOS-arm64.dmg"
    dmg_x64 = f"SnapMark-{ver}-macOS-x64.dmg"
    appimage = f"SnapMark-{ver}-linux-x64.AppImage"
    names = (setup, dmg_arm, dmg_x64, appimage)
    if all((rel / n).exists() for n in names):
        h = {n: sha(rel / n) for n in names}
    else:
        # Файлов нет локально — берём SHA-256, который GitHub сам считает для файлов релиза.
        h = github_digests(ver, names)

    # ── winget ──
    w = ROOT / "winget" / "manifests" / "s" / "SadlyBear" / "SnapMark" / ver
    w.mkdir(parents=True, exist_ok=True)
    head = "# yaml-language-server: $schema=https://aka.ms/winget-manifest.{kind}.1.6.0.schema.json\n"
    (w / "SadlyBear.SnapMark.yaml").write_text(head.format(kind="version") + f"""
PackageIdentifier: SadlyBear.SnapMark
PackageVersion: {ver}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
""", encoding="utf-8")
    (w / "SadlyBear.SnapMark.installer.yaml").write_text(head.format(kind="installer") + f"""
PackageIdentifier: SadlyBear.SnapMark
PackageVersion: {ver}
InstallerType: inno
Scope: user
InstallModes:
  - interactive
  - silent
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: {url}/{setup}
    InstallerSha256: {h[setup].upper()}
ManifestType: installer
ManifestVersion: 1.6.0
""", encoding="utf-8")
    (w / "SadlyBear.SnapMark.locale.en-US.yaml").write_text(head.format(kind="defaultLocale") + f"""
PackageIdentifier: SadlyBear.SnapMark
PackageVersion: {ver}
PackageLocale: en-US
Publisher: SadlyBear.pro
PublisherUrl: https://github.com/KantemirBitokov
Author: Kantemir Bitokov
PackageName: SnapMark
PackageUrl: {REPO}
License: MIT
LicenseUrl: {REPO}/blob/main/LICENSE
ShortDescription: Screenshots with A-B-C markers and comments, series copied with one Ctrl+C, screen recording with sound.
Description: |-
  SnapMark captures a region or the full screen and lets you mark it up with lettered markers, each with a comment.
  Comments are copied as text together with the images, a whole series can be copied with one Ctrl+C,
  and the screen can be recorded to MP4, MKV, WebM or GIF with microphone and computer audio.
Moniker: snapmark
Tags:
  - screenshot
  - annotation
  - screen-recorder
  - bug-report
  - printscreen
ReleaseNotesUrl: {REPO}/releases/tag/v{ver}
ManifestType: defaultLocale
ManifestVersion: 1.6.0
""", encoding="utf-8")

    # ── Homebrew (свой tap: репозиторий homebrew-tap) ──
    url_tpl = url.replace(ver, "#{version}")
    c = ROOT / "homebrew" / "Casks"
    c.mkdir(parents=True, exist_ok=True)
    (c / "snapmark.rb").write_text(f'''cask "snapmark" do
  arch arm: "arm64", intel: "x64"

  version "{ver}"
  sha256 arm:   "{h[dmg_arm]}",
         intel: "{h[dmg_x64]}"

  url "{url_tpl}/SnapMark-#{{version}}-macOS-#{{arch}}.dmg"
  name "SnapMark"
  desc "Screenshots with A-B-C markers and comments, screen recording with sound"
  homepage "{REPO}"

  depends_on macos: ">= :big_sur"

  app "SnapMark.app"

  postflight do
    # Программа не нотаризована Apple — снимаем карантин, чтобы она запускалась без «повреждено».
    system_command "/usr/bin/xattr", args: ["-cr", "#{{appdir}}/SnapMark.app"]
  end

  zap trash: [
    "~/.config/SnapMark",
    "~/Library/LaunchAgents/pro.sadlybear.snapmark.plist",
  ]

  caveats <<~EOS
    For video recording install ffmpeg:  brew install ffmpeg
    On first launch allow Screen Recording in System Settings → Privacy & Security.
  EOS
end
''', encoding="utf-8")

    # ── AUR (snapmark-bin, из AppImage) ──
    a = ROOT / "aur"
    a.mkdir(parents=True, exist_ok=True)
    (a / "PKGBUILD").write_text(f'''# Maintainer: Kantemir Bitokov <119302116+KantemirBitokov@users.noreply.github.com>
pkgname=snapmark-bin
pkgver={ver}
pkgrel=1
pkgdesc="Screenshots with A-B-C markers and comments, series in one Ctrl+C, screen recording with sound"
arch=('x86_64')
url="{REPO}"
license=('MIT')
depends=('fuse2' 'libx11')
optdepends=('ffmpeg: screen recording' 'xclip: clipboard fallback' 'wl-clipboard: clipboard on Wayland')
provides=('snapmark')
conflicts=('snapmark')
options=('!strip')
source=("SnapMark-$pkgver.AppImage::{REPO}/releases/download/v$pkgver/SnapMark-$pkgver-linux-x64.AppImage")
sha256sums=('{h[appimage]}')

prepare() {{
  chmod +x "SnapMark-$pkgver.AppImage"
  ./"SnapMark-$pkgver.AppImage" --appimage-extract >/dev/null
}}

package() {{
  install -Dm755 "SnapMark-$pkgver.AppImage" "$pkgdir/opt/snapmark/SnapMark.AppImage"
  install -d "$pkgdir/usr/bin"
  ln -s /opt/snapmark/SnapMark.AppImage "$pkgdir/usr/bin/snapmark"
  install -Dm644 squashfs-root/snapmark.desktop "$pkgdir/usr/share/applications/snapmark.desktop"
  install -Dm644 squashfs-root/snapmark.png "$pkgdir/usr/share/icons/hicolor/512x512/apps/snapmark.png"
}}
''', encoding="utf-8")

    print(f"Manifests for {ver} written.")
    for n, v in h.items():
        print(f"  {v}  {n}")


if __name__ == "__main__":
    main()
