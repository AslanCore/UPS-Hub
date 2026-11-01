#!/usr/bin/env python3
"""
UPS Hub app icon generator (PIL only, no external art).

The glyph is WinNUT's mark (an upright battery with charge bars and a mains plug on a cable)
redrawn in the AslanCore Cloud app-icon style: near-black plate with the home page's 12 px
"rounded-md" corner (21.4 % of the tile), a warm radial glow behind the glyph, a soft top light,
the 8 % hairline ring, and the orange (#FF5C1A) neon hexagon frame used by the Cloud app art.

Sizes 48 px and up get the full art; 16-32 px get a simplified glyph (no hexagon, no cable,
thicker strokes) so the tray icon stays legible.

usage: make_icon.py OUT_DIR
"""
import math, sys, os
from PIL import Image, ImageDraw, ImageFilter, ImageChops

ORANGE = (255, 92, 26)
CREAM = (243, 241, 238)
BG = (10, 9, 8)
STATUS = {  # Palette.cs
    "up": (63, 163, 106), "primary": (255, 92, 26), "down": (226, 75, 58),
    "degraded": (196, 138, 34), "subtle": (107, 103, 98),
}

def lerp(a, b, t): return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))

def radial(size, center, radius, inner, outer):
    w = h = size
    im = Image.new("RGB", (w, h))
    px = im.load()
    cx, cy = center[0] * w, center[1] * h
    r = radius * w
    for y in range(h):
        for x in range(w):
            t = min(1.0, math.hypot(x - cx, y - cy) / r)
            px[x, y] = lerp(inner, outer, t * t * (3 - 2 * t))
    return im

def rounded_mask(size, inset, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([inset, inset, size - 1 - inset, size - 1 - inset], radius=radius, fill=255)
    return m

def glow(layer, blur, strength=1.0):
    g = layer.filter(ImageFilter.GaussianBlur(blur))
    if strength != 1.0:
        a = g.getchannel("A").point(lambda v: min(255, int(v * strength)))
        g.putalpha(a)
    return g

def plate(S, small):
    inset = round(S * (0.035 if not small else 0.02))
    radius = round((S - 2 * inset) * 0.214)
    base = radial(S // 4, (0.5, 0.47), 0.70, (36, 14, 8) if not small else (46, 18, 9), BG).resize((S, S), Image.BICUBIC).convert("RGBA")
    # soft top light
    light = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(light)
    for y in range(int(S * 0.45)):
        a = int(18 * (1 - y / (S * 0.45)) ** 2)
        ld.line([(0, y), (S, y)], fill=(255, 240, 230, a))
    base.alpha_composite(light)
    mask = rounded_mask(S, inset, radius)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(base, (0, 0), mask)
    # hairline ring (shadow-border: 1px foreground at 8 %), a touch brighter on top
    ring = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    w = max(1, round(S * 0.012))
    ImageDraw.Draw(ring).rounded_rectangle([inset, inset, S - 1 - inset, S - 1 - inset], radius=radius,
                                           outline=CREAM + (34 if not small else 46,), width=w)
    out.alpha_composite(ring)
    return out, mask

def hexagon(cx, cy, r):
    # pointy-top hexagon, the Cloud HexIcon proportions (50,4 93,27 93,73 50,96 7,73 7,27)
    pts = [(50, 4), (93, 27), (93, 73), (50, 96), (7, 73), (7, 27)]
    return [(cx + (x - 50) / 46 * r, cy + (y - 50) / 46 * r) for x, y in pts]

def battery(d, S, box, stroke, bars, bar_gap, color_body, color_bar, nub=True, filled_bars=None):
    x0, y0, x1, y1 = [v * S for v in box]
    r = (x1 - x0) * 0.18
    if nub:
        nw = (x1 - x0) * 0.36
        nh = (y1 - y0) * 0.075
        cx = (x0 + x1) / 2
        d.rounded_rectangle([cx - nw / 2, y0 - nh, cx + nw / 2, y0 + stroke * 0.5], radius=nh * 0.45, fill=color_body)
    d.rounded_rectangle([x0, y0, x1, y1], radius=r, outline=color_body, width=round(stroke))
    pad = stroke * 1.9
    ix0, iy0, ix1, iy1 = x0 + pad, y0 + pad, x1 - pad, y1 - pad
    bh = (iy1 - iy0 - bar_gap * (bars - 1)) / bars
    n = bars if filled_bars is None else filled_bars
    for i in range(bars):
        if i < bars - n: continue
        by0 = iy0 + i * (bh + bar_gap)
        d.rounded_rectangle([ix0, by0, ix1, by0 + bh], radius=min(bh, ix1 - ix0) * 0.22, fill=color_bar)

def plug(d, S, stroke, color):
    # mains plug on the right, cable dropping down and curling into the battery's foot
    hx0, hy0, hx1, hy1 = 0.625 * S, 0.385 * S, 0.735 * S, 0.475 * S
    pw = stroke * 0.85
    for px in (hx0 + (hx1 - hx0) * 0.28, hx1 - (hx1 - hx0) * 0.28):
        d.rounded_rectangle([px - pw / 2, hy0 - 0.06 * S, px + pw / 2, hy0 + stroke], radius=pw / 2, fill=color)
    d.rounded_rectangle([hx0, hy0, hx1, hy1], radius=(hx1 - hx0) * 0.22, fill=color)
    cx = (hx0 + hx1) / 2
    d.rectangle([cx - stroke * 0.45, hy1, cx + stroke * 0.45, hy1 + 0.03 * S], fill=color)
    # cable: down, then a quarter turn left toward the battery foot
    pts = []
    top, turn_y, end_x = hy1 + 0.03 * S, 0.64 * S, 0.585 * S
    rr = cx - end_x
    for i in range(20): pts.append((cx, top + (turn_y - top) * i / 19))
    for i in range(1, 21):
        a = math.pi / 2 * i / 20
        pts.append((cx - rr + rr * math.cos(a), turn_y + rr * math.sin(a)))
    d.line(pts, fill=color, width=round(stroke * 0.9), joint="curve")
    e = pts[-1]; s2 = stroke * 0.45
    d.ellipse([cx - s2, top - s2, cx + s2, top + s2], fill=color)
    d.ellipse([e[0] - s2, e[1] - s2, e[0] + s2, e[1] + s2], fill=color)

def full_art(S):
    out, mask = plate(S, small=False)
    # Concentric hexagon frames like the Cloud app art: a dark bevel, a bright neon core line,
    # then fainter inner echoes.
    bevel = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    h0 = hexagon(S * 0.5, S * 0.5, S * 0.415)
    ImageDraw.Draw(bevel).line(h0 + [h0[0]], fill=(52, 44, 40, 255), width=round(S * 0.03), joint="curve")
    neon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    nd = ImageDraw.Draw(neon)
    hx = hexagon(S * 0.5, S * 0.5, S * 0.395)
    nd.line(hx + [hx[0]], fill=ORANGE + (255,), width=round(S * 0.013), joint="curve")
    core = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(core).line(hx + [hx[0]], fill=(255, 196, 150, 200), width=max(1, round(S * 0.004)), joint="curve")
    inner = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    idr = ImageDraw.Draw(inner)
    for rr, a, w in ((0.35, 120, 0.007), (0.308, 60, 0.005)):
        h = hexagon(S * 0.5, S * 0.5, S * rr)
        idr.line(h + [h[0]], fill=ORANGE + (a,), width=max(1, round(S * w)), joint="curve")
    bars = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(bars)
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bod = ImageDraw.Draw(body)
    stroke = S * 0.034
    battery(bod, S, (0.335, 0.335, 0.555, 0.705), stroke, 3, S * 0.022, CREAM + (255,), (0, 0, 0, 0))
    battery(bd, S, (0.335, 0.335, 0.555, 0.705), stroke, 3, S * 0.022, (0, 0, 0, 0), ORANGE + (255,), nub=False)
    plug(bd, S, stroke, ORANGE + (255,))
    # light: the cream body gets a vertical sheen (brighter top)
    # brushed-metal body: light from the top, warm orange rim light from the bars below
    metal = Image.new("RGBA", (S, S))
    for y in range(S):
        t = max(0.0, min(1.0, (y / S - 0.30) / 0.42))
        metal.paste(lerp((236, 230, 222), (126, 114, 104), t) + (255,), (0, y, S, y + 1))
    metal.putalpha(body.getchannel("A"))
    body = metal
    out.alpha_composite(bevel)
    for layer, blur, k in ((neon, S * 0.035, 2.2), (inner, S * 0.02, 1.2), (bars, S * 0.03, 1.5)):
        out.alpha_composite(glow(layer, blur, k))
    out.alpha_composite(inner)
    out.alpha_composite(neon)
    out.alpha_composite(core)
    out.alpha_composite(glow(body, S * 0.012, 0.35))
    out.alpha_composite(body)
    out.alpha_composite(bars)
    clip = Image.new("RGBA", (S, S), (0, 0, 0, 0)); clip.paste(out, (0, 0), mask)
    return clip

def small_art(S, px, dot=None):
    """Simplified glyph for 16-32 px: bigger battery, fewer bars, no frame or cable."""
    out, mask = plate(S, small=True)
    lay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(lay)
    bars_n = 2 if px <= 16 else 3
    stroke = S * (0.085 if px <= 16 else 0.07)
    box = (0.29, 0.25, 0.71, 0.84) if dot is None else (0.22, 0.22, 0.62, 0.80)
    battery(d, S, box, stroke, bars_n, S * 0.05, CREAM + (255,), (0, 0, 0, 0))
    bars = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    battery(ImageDraw.Draw(bars), S, box, stroke, bars_n, S * 0.05, (0, 0, 0, 0), ORANGE + (255,), nub=False)
    out.alpha_composite(glow(bars, S * 0.04, 1.3))
    out.alpha_composite(lay); out.alpha_composite(bars)
    clip = Image.new("RGBA", (S, S), (0, 0, 0, 0)); clip.paste(out, (0, 0), mask)
    if dot is not None:
        dd = ImageDraw.Draw(clip)
        r1, r0 = S * 0.25, S * 0.18
        cx = cy = S - r1 - S * 0.0
        dd.ellipse([cx - r1, cy - r1, cx + r1, cy + r1], fill=BG + (255,))
        dd.ellipse([cx - r0, cy - r0, cx + r0, cy + r0], fill=dot + (255,))
    return clip

def render(px, dot=None, simplified=None):
    simplified = px <= 32 if simplified is None else simplified
    S = max(512, px * 16)
    art = small_art(S, px, dot) if simplified or dot is not None else full_art(S)
    im = art.resize((px, px), Image.LANCZOS)
    if px <= 32:
        im = im.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=0))
    return im

def save_ico(path, images):
    images[-1].save(path, format="ICO", sizes=[im.size for im in images], append_images=images[:-1])

def main(out):
    os.makedirs(out, exist_ok=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    imgs = [render(s) for s in sizes]
    # PIL writes the ICO from the largest image + the size list, so pass the 256 base
    big = imgs[-1]
    big.save(os.path.join(out, "upshub.ico"), format="ICO", sizes=[(s, s) for s in sizes],
             append_images=imgs[:-1], bitmap_format="png")
    for s, im in zip(sizes, imgs):
        if s in (32, 64, 256): im.save(os.path.join(out, f"icon-{s}.png"))
    render(512, simplified=False).save(os.path.join(out, "icon-512.png"))
    for name, rgb in STATUS.items():
        t = [render(s, dot=rgb) for s in (16, 20, 24, 32, 48)]
        t[-1].save(os.path.join(out, f"tray-{name}.ico"), format="ICO", sizes=[im.size for im in t],
                   append_images=t[:-1], bitmap_format="png")
        t[3].save(os.path.join(out, f"tray-{name}-32.png"))

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
