#!/usr/bin/env python3
"""Icon previews: 256/32 PNGs, tray states, and a side-by-side with Cloud home tiles.
usage: preview.py ICON_DIR OUT_DIR [CLOUD_COVER.webp ...]"""
import sys, os
from PIL import Image, ImageDraw, ImageFont
src, out, covers = sys.argv[1], sys.argv[2], sys.argv[3:]
os.makedirs(out, exist_ok=True)
Image.open(os.path.join(src, "icon-256.png")).save(os.path.join(out, "upshub-256.png"))
ico = Image.open(os.path.join(src, "upshub.ico")); ico.size = (32, 32); ico.copy().save(os.path.join(out, "upshub-32.png"))
def tile(path, S=256):
    c = Image.open(path).convert("RGBA"); w, h = c.size
    c = c.crop(((w - h) // 2, 0, (w + h) // 2, h)).resize((S, S), Image.LANCZOS)   # object-cover, like AppRow
    m = Image.new("L", (S, S), 0); ImageDraw.Draw(m).rounded_rectangle([0, 0, S - 1, S - 1], radius=round(S * 0.214), fill=255)
    o = Image.new("RGBA", (S, S), (0, 0, 0, 0)); o.paste(c, (0, 0), m)
    ImageDraw.Draw(o).rounded_rectangle([0, 0, S - 1, S - 1], radius=round(S * 0.214), outline=(243, 241, 238, 20), width=3)
    return o
tiles = [("UPS Hub (new)", Image.open(os.path.join(src, "icon-256.png")).convert("RGBA"))]
tiles += [("Cloud · " + os.path.basename(p).split(".")[0].capitalize(), tile(p)) for p in covers]
sheet = Image.new("RGBA", (len(tiles) * 296 + 20, 420), (18, 17, 16, 255)); d = ImageDraw.Draw(sheet)
font_path = os.path.join(os.path.dirname(__file__), "../../src/UpsHub.App/Assets/Fonts/IBMPlexSans-Medium.ttf")
f = ImageFont.truetype(font_path, 18) if os.path.exists(font_path) else ImageFont.load_default()
x = 20
for name, t in tiles:
    sheet.alpha_composite(t, (x, 20)); d.text((x, 290), name, fill=(243, 241, 238), font=f)
    sheet.alpha_composite(t.resize((56, 56), Image.LANCZOS), (x, 330))       # Cloud AppRow size
    if name.startswith("UPS"):
        ico.size = (32, 32); sheet.alpha_composite(ico.copy().convert("RGBA"), (x + 76, 342))
    else:
        sheet.alpha_composite(t.resize((32, 32), Image.LANCZOS), (x + 76, 342))
    x += 296
sheet.save(os.path.join(out, "side-by-side-cloud.png"))
t = Image.new("RGBA", (5 * 110 + 20, 120), (32, 32, 34, 255)); x = 20
for n in ["up", "primary", "down", "degraded", "subtle"]:
    ic = Image.open(os.path.join(src, "Tray", f"tray-{n}.ico")) if os.path.isdir(os.path.join(src, "Tray")) else Image.open(os.path.join(src, f"tray-{n}.ico"))
    for s in (16, 24, 32):
        ic.size = (s, s); t.alpha_composite(ic.copy().convert("RGBA"), (x, 60 - s // 2)); x += s + 6
    x += 20
t.save(os.path.join(out, "tray-states.png"))
