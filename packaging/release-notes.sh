#!/usr/bin/env bash
# Текст описания релиза на GitHub: изменения из CHANGELOG.md + таблица загрузок + подсказки по установке.
# Использование: packaging/release-notes.sh 1.0.7 > notes.md
set -euo pipefail
V="$1"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
D="https://github.com/KantemirBitokov/SnapMark/releases/download/v$V"

# Раздел «## V…» до следующего «## ».
CHANGES="$(awk -v v="$V" '
  /^## / { if (on) exit; if ($2 == v) { on = 1; next } }
  on' "$ROOT/CHANGELOG.md" | sed -e '/./,$!d' -e :a -e '/^\n*$/{$d;N;ba' -e '}')"
[ -n "$CHANGES" ] || CHANGES="- Maintenance release."

PREV="$(awk -v v="$V" '/^## / { if (seen) { print $2; exit } if ($2 == v) seen = 1 }' "$ROOT/CHANGELOG.md")"

cat <<EOF
## What's new

$CHANGES

## Download

| System | File |
|---|---|
| **Windows 10 / 11** — installer | [SnapMark-$V-Setup.exe]($D/SnapMark-$V-Setup.exe) |
| Windows — portable, no install | [SnapMark-$V-portable.zip]($D/SnapMark-$V-portable.zip) |
| **macOS** — Apple Silicon (M1–M4) | [SnapMark-$V-macOS-arm64.dmg]($D/SnapMark-$V-macOS-arm64.dmg) |
| macOS — Intel | [SnapMark-$V-macOS-x64.dmg]($D/SnapMark-$V-macOS-x64.dmg) |
| **Linux** x64 — any distro | [SnapMark-$V-linux-x64.AppImage]($D/SnapMark-$V-linux-x64.AppImage) |
| Linux x64 — Debian / Ubuntu | [snapmark_${V}_amd64.deb]($D/snapmark_${V}_amd64.deb) |
| Linux ARM64 | [AppImage]($D/SnapMark-$V-linux-arm64.AppImage) · [.deb]($D/snapmark_${V}_arm64.deb) · [.tar.gz]($D/SnapMark-$V-linux-arm64.tar.gz) |
| Linux x64 — archive | [SnapMark-$V-linux-x64.tar.gz]($D/SnapMark-$V-linux-x64.tar.gz) |

Already installed? SnapMark checks for updates itself (Settings → About).

<details>
<summary>First launch — security warnings</summary>

SnapMark is free and open source, but not signed with a paid certificate yet.

- **Windows:** "Windows protected your PC" → **More info** → **Run anyway**.
- **macOS:** right-click the app → **Open** → **Open** (or System Settings → Privacy & Security → **Open Anyway**). Then allow **Screen Recording** when asked.
- **Linux AppImage:** \`chmod +x SnapMark-*.AppImage\` and run it.

</details>
EOF

if [ -n "$PREV" ]; then
  echo
  echo "**Full changelog:** [v$PREV…v$V](https://github.com/KantemirBitokov/SnapMark/compare/v$PREV...v$V)"
fi
