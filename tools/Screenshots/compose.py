# Сборка картинок для README и сайта из сырых кадров workflow "Screenshots" (ветка ci-screenshots):
#   python3 tools/Screenshots/compose.py <кадры> tools/Screenshots docs/media
import json, sys
from PIL import Image, ImageFilter, ImageDraw, ImageEnhance

src, assets, out = sys.argv[1], sys.argv[2], sys.argv[3]
meta = json.load(open(f"{src}/meta.json"))
page = Image.open(f"{assets}/page.png").convert("RGB")

def pos(name):
    m = meta[name]
    return m["x"], m["y"]

def matte(name):
    # Окно снято на чёрном и на белом фоне: альфа = 1 - (белый - чёрный), цвет = чёрный / альфа.
    import numpy as np
    k = np.asarray(Image.open(f"{src}/{name}.k.png").convert("RGB")).astype(float)
    w = np.asarray(Image.open(f"{src}/{name}.w.png").convert("RGB")).astype(float)
    a = 1 - (w - k).mean(axis=2) / 255
    a = a.clip(0, 1)
    c = np.where(a[..., None] > 0.004, k / np.maximum(a[..., None], 1e-3), 0).clip(0, 255)
    return Image.fromarray(np.dstack([c, a * 255]).astype("uint8"), "RGBA")

def desktop():
    # Страница под окнами чуть размыта и притемнена — окно в фокусе.
    bg = page.filter(ImageFilter.GaussianBlur(3))
    return ImageEnhance.Brightness(bg).enhance(0.93).convert("RGBA")

def window(canvas, name, radius=12):
    im = Image.open(f"{src}/{name}.png").convert("RGBA")
    x, y = pos(name)
    w, h = im.size
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius, fill=255)
    sh = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    sm = Image.new("L", canvas.size, 0)
    ImageDraw.Draw(sm).rounded_rectangle((x, y + 14, x + w, y + h + 14), radius, fill=95)
    sh.putalpha(sm.filter(ImageFilter.GaussianBlur(26)))
    canvas.alpha_composite(sh)
    border = Image.new("RGBA", (w + 2, h + 2), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle((0, 0, w + 1, h + 1), radius + 1, fill=(0, 0, 0, 40))
    canvas.alpha_composite(border, (x - 1, y - 1))
    canvas.paste(im, (x, y), mask)

def save(img, name, box, width=1600):
    img = img.crop(box).convert("RGB")
    if img.width > width:
        img = img.resize((width, round(img.height * width / img.width)), Image.LANCZOS)
    img.save(f"{out}/{name}.png", optimize=True)
    print(name, img.size)

ed = Image.open(f"{src}/editor.png")
save(ed, "markers", (80, 64, 1870, 996))
save(Image.open(f"{src}/editor-style.png"), "tools", (80, 64, 1870, 996))
save(Image.open(f"{src}/record.png"), "recording", (380, 64, 1614, 900))

# OCR: окно лежит прямо поверх страницы (оно и есть кусок экрана).
c = page.convert("RGBA")
o = matte("ocr")
c.alpha_composite(o, pos("ocr"))
x, y = pos("ocr")
save(c, "ocr", (x - 420, y - 90, x + o.width + 120, y + o.height + 60))

# В README — страница «Видео» в настройках (больше всего возможностей на одном экране).
for name in ["series", "longshot", "trim", "settings-video"]:
    c = desktop()
    window(c, name)
    if name == "series":
        hud = matte("stephud")
        c.alpha_composite(hud, pos("stephud"))
    x, y = pos(name)
    w, h = Image.open(f"{src}/{name}.png").size
    top = 0 if name == "series" else y - 60
    save(c, {"series": "steps", "settings-video": "settings"}.get(name, name), (x - 70, top, x + w + 70, min(1080, y + h + 70)))
