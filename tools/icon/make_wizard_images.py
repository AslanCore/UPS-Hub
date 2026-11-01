"""Inno Setup wizard images for UPS Hub (dark AslanCore style). Run from the repo root:
   python3 tools/icon/make_wizard_images.py  ->  installer/wizard-*.png"""
from PIL import Image, ImageDraw, ImageFilter
import os
BG = (10, 9, 8); ORANGE = (255, 92, 26)
root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
icon = Image.open(os.path.join(root, "src/UpsHub.App/Assets/icon-256.png")).convert("RGBA")
out = os.path.join(root, "installer")

def grid(img, step, alpha):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    w, h = img.size
    for x in range(step // 2, w, step): d.line([(x, 0), (x, h)], fill=(255, 255, 255, alpha))
    for y in range(step // 2, h, step): d.line([(0, y), (w, y)], fill=(255, 255, 255, alpha))
    return Image.alpha_composite(img, layer)

def large(scale):
    # Inno's left panel is 164x314 at 100% DPI.
    w, h = 164 * scale, 314 * scale
    img = Image.new("RGBA", (w, h), BG + (255,))
    img = grid(img, 16 * scale, 10)
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse([-w * 0.4, h * 0.18, w * 1.4, h * 0.78], fill=ORANGE + (34,))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(28 * scale)))
    size = 112 * scale
    ic = icon.resize((size, size), Image.LANCZOS)
    img.alpha_composite(ic, ((w - size) // 2, int(h * 0.30)))
    d = ImageDraw.Draw(img)
    d.rectangle([0, h - 3 * scale, w, h], fill=ORANGE)
    return img.convert("RGB")

def small(scale):
    s = 58 * scale
    img = Image.new("RGBA", (s, s), BG + (255,))
    ic = icon.resize((s - 4 * scale, s - 4 * scale), Image.LANCZOS)
    img.alpha_composite(ic, (2 * scale, 2 * scale))
    return img.convert("RGB")

for sc, suf in [(1, "100"), (2, "200")]:
    large(sc).save(os.path.join(out, f"wizard-large-{suf}.png"))
    small(sc).save(os.path.join(out, f"wizard-small-{suf}.png"))
print("ok")
