#!/usr/bin/env python3
"""
Обновляет docs/index.html под новую версию: подпись с версией и прямые ссылки на файлы релиза.

    python3 packaging/update-site.py 1.9.26

Ссылки ведут на GitHub Releases, поэтому страница работает без запросов к API
(анонимный лимит 60 запросов в час как раз и ломал скачивание с сайта).
"""
import pathlib
import re
import sys

REPO = "https://github.com/KantemirBitokov/SnapMark"
ROOT = pathlib.Path(__file__).resolve().parent.parent
SITE = ROOT / "docs" / "index.html"


def asset_name(suffix: str, ver: str) -> str:
    """Суффикс из data-asset → полное имя файла в релизе."""
    return f"snapmark_{ver}{suffix}" if suffix.startswith("_") else f"SnapMark-{ver}-{suffix}"


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)

    ver = sys.argv[1].lstrip("v")
    base = f"{REPO}/releases/download/v{ver}"
    html = SITE.read_text(encoding="utf-8")

    pattern = re.compile(r'(<a[^>]*data-asset="(?P<suffix>[^"]+)"[^>]*?href=")[^"]*(")')
    html = pattern.sub(lambda m: f"{m.group(1)}{base}/{asset_name(m.group('suffix'), ver)}{m.group(3)}", html)

    html = re.sub(r'(<a class="btn primary" id="main-download" href=")[^"]*(")',
                  lambda m: f"{m.group(1)}{base}/{asset_name('Setup.exe', ver)}{m.group(2)}", html)
    html = re.sub(r'const VERSION = "[^"]*";', f'const VERSION = "{ver}";', html)
    html = re.sub(r'(<div class="small" id="version-line">)[^<]*(</div>)',
                  lambda m: f"{m.group(1)}Version {ver} · free, open source{m.group(2)}", html)

    SITE.write_text(html, encoding="utf-8")
    print(f"Site updated for {ver}")


if __name__ == "__main__":
    main()
