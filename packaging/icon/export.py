"""Экспорт иконки SnapMark из SVG во все форматы.

Исходники:  snapmark.svg        — полная версия (1024, сетка macOS: «сквиркл» 824 по центру)
            snapmark-small.svg  — упрощённая для 32 px и меньше (без буквы, толще штрих)
Запуск:     python3 packaging/icon/export.py   (нужны playwright с Chromium и Pillow)

macOS (.icns) — с полями по сетке Apple; Windows (.ico), Linux, окна приложения и сайт —
та же картинка крупнее (кадр 884 вместо 1024), как у иконок Windows 11.
"""
import pathlib
from PIL import Image
from playwright.sync_api import sync_playwright

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
FULL = (HERE / "snapmark.svg").read_text()
SMALL = (HERE / "snapmark-small.svg").read_text()


def svg_for(size, tight, small=None):
    svg = SMALL if (size <= 32 if small is None else small) else FULL
    if tight:
        svg = svg.replace('viewBox="0 0 1024 1024"', 'viewBox="70 64 884 884"', 1)
    return svg.replace('width="1024" height="1024"', f'width="{size}" height="{size}"', 1)


def render(page, size, tight, small=None):
    page.set_viewport_size({"width": size, "height": size})
    page.set_content(f'<html><body style="margin:0;background:transparent">{svg_for(size, tight, small)}</body></html>')
    tmp = HERE / f".r{size}.png"
    page.screenshot(path=str(tmp), omit_background=True, clip={"x": 0, "y": 0, "width": size, "height": size})
    im = Image.open(tmp).convert("RGBA")
    im.load()
    tmp.unlink()
    return im


def main():
    with sync_playwright() as p:
        b = p.chromium.launch()
        page = b.new_page()
        win = {s: render(page, s, True) for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024)}
        tray = render(page, 64, True, small=True)  # трей показывается в 16–32 px — упрощённая версия
        mac = {s: render(page, s, False) for s in (16, 32, 64, 128, 256, 512, 1024)}
        b.close()

    assets = ROOT / "src/SnapMark.App/Assets"
    ico_sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    win[256].save(assets / "snapmark.ico", format="ICO", sizes=[(s, s) for s in ico_sizes],
                  append_images=[win[s] for s in ico_sizes if s != 256])
    win[256].save(assets / "snapmark.png")
    win[1024].save(assets / "snapmark_1024.png")
    tray.save(assets / "tray.png")
    win[512].save(ROOT / "packaging/linux/snapmark.png")
    win[256].save(ROOT / "docs/icon.png")
    mac[1024].save(ROOT / "packaging/macos/snapmark.icns", format="ICNS",
                   append_images=[mac[s] for s in (16, 32, 64, 128, 256, 512)])
    mac[1024].save(HERE / "snapmark-macos-1024.png")
    print("ok")


if __name__ == "__main__":
    main()
