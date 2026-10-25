"""Poly Lab brand marks: app icons (.ico) and header marks (.png).

The logo is drawn from shapes on a 1000-unit square, measured from the owner's original: a dark rounded square,
a white "P" made of a top bar with a bowl and a separate stem, and an accent block under the bowl. Small sizes
snap the shape edges to whole pixels so the gaps between the parts stay visible.

    py -3 tools/brandgen.py
"""
import math
import os

from PIL import Image, ImageDraw

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
SS = 8
ICO_SIZES = [256, 64, 48, 40, 32, 24, 20, 16]
INK = (37, 42, 46, 255)
PAPER = (250, 249, 245, 255)
ACCENTS = {
    'launcher': (255, 122, 80, 255),
    'pbog': (163, 179, 92, 255),
    'pfx': (46, 196, 214, 255),
}

SQUARE_R = 192
XS = (280, 455, 505, 720, 760)
YS = (190, 370, 415, 580, 665, 810)
BOWL_TR, BOWL_BR, STEM_TL, FILLET = 160, 215, 160, 28


def arc(cx, cy, r, a0, a1, n=48):
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def rrect(x0, y0, x1, y1, tl=0, tr=0, br=0, bl=0):
    m = min(x1 - x0, y1 - y0)
    tl, tr, br, bl = (min(r, m) for r in (tl, tr, br, bl))
    return (arc(x0 + tl, y0 + tl, tl, 180, 270) + arc(x1 - tr, y0 + tr, tr, 270, 360)
            + arc(x1 - br, y1 - br, br, 0, 90) + arc(x0 + bl, y1 - bl, bl, 90, 180))


def grid(px):
    if px > 64:
        return XS, YS
    s = px / 1000
    xs = [round(v * s) / s for v in XS]
    ys = [round(v * s) / s for v in YS]
    xs[2] = max(xs[2], xs[1] + 1 / s)
    ys[2] = max(ys[2], ys[1] + 1 / s)
    ys[4] = max(ys[4], ys[3] + 1 / s)
    return xs, ys


def mark(px, accent):
    n = px * SS
    k = n / 1000
    im = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)

    def fill(points, color):
        d.polygon([(x * k, y * k) for x, y in points], fill=color)

    (x0, x1, x2, x3, x4), (y0, y1, y2, y3, y4, y5) = grid(px)
    fill(rrect(0, 0, 1000, 1000, SQUARE_R, SQUARE_R, SQUARE_R, SQUARE_R), INK)
    fill(rrect(x0, y0, x2, y1), PAPER)
    fill(rrect(x2, y0, x3, y3, tr=BOWL_TR, br=BOWL_BR), PAPER)
    fill([(x2, y1), (x2, y1 + FILLET)] + arc(x2 - FILLET, y1 + FILLET, FILLET, 0, -90), PAPER)
    fill(rrect(x0, y2, x1, y5, tl=STEM_TL), PAPER)
    fill(rrect(x2, y4, x4, y5), accent)
    return im.resize((px, px), Image.BOX)


def write_ico(app, path):
    frames = [mark(s, ACCENTS[app]) for s in ICO_SIZES]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    frames[0].save(path, format='ICO', sizes=[(s, s) for s in ICO_SIZES], append_images=frames[1:])
    print('ico', os.path.relpath(path, ROOT))


def write_png(app, path, px):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mark(px, ACCENTS[app]).save(path)
    print('png', os.path.relpath(path, ROOT))


ICO = {
    'launcher': 'src/Ui/Assets/Brand/polylab.ico',
    'pbog': 'src/PBOGPacker/Assets/app.ico',
    'pfx': 'src/ParticleStudio/Pfx.App/Assets/app.ico',
}
PNG = [
    ('launcher', 'src/Ui/Assets/Brand/polylab.png', 256),
    ('pbog', 'src/PBOGPacker/Assets/Brand/logo.png', 256),
    ('pfx', 'src/ParticleStudio/Pfx.App/Assets/logo.png', 36),
]

if __name__ == '__main__':
    for app, rel in ICO.items():
        write_ico(app, os.path.join(ROOT, rel))
    for app, rel, px in PNG:
        write_png(app, os.path.join(ROOT, rel), px)
