import os
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(__file__), '..', 'Assets', 'Art')
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(163451)


def fbm(w, h, octaves=6, base=4, persistence=0.55, stretch=(1.0, 1.0)):
    acc = np.zeros((h, w), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        gw = max(2, int(base * (2 ** o) * stretch[0]))
        gh = max(2, int(base * (2 ** o) * stretch[1]))
        g = rng.random((gh, gw)).astype(np.float32)
        layer = np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC), np.float32) / 255
        acc += layer * amp
        total += amp
        amp *= persistence
    return acc / total


def lerp(a, b, t):
    return a + (b - a) * t


def spruce(d, x, base, hh, ww, col):
    tiers = int(rng.integers(6, 10))
    top = base - hh
    left, right = [], []
    for i in range(tiers):
        f0 = i / tiers
        f1 = (i + 1) / tiers
        y0 = top + hh * f0 + hh / tiers * 0.25
        y1 = top + hh * f1
        outer = ww * (0.15 + 0.85 * f1) * rng.uniform(0.85, 1.1)
        inner = outer * rng.uniform(0.45, 0.65)
        left += [(x - inner, y0), (x - outer, y1 + rng.uniform(-2, 3))]
        right += [(x + inner, y0), (x + outer * rng.uniform(0.9, 1.05), y1 + rng.uniform(-2, 3))]
    pts = [(x, top - hh * 0.04)] + right + [(x + ww * 0.1, base + 20), (x - ww * 0.1, base + 20)] + left[::-1]
    d.polygon(pts, fill=col)


def tree_mask(w, h, base, hmin, hmax, count, ratio, blur=0.0):
    m = Image.new('L', (w, h), 0)
    d = ImageDraw.Draw(m)
    d.rectangle([0, int(base), w, h], fill=255)
    xs = np.sort(rng.integers(-60, w + 60, count))
    for x in xs:
        hh = rng.uniform(hmin, hmax)
        spruce(d, float(x), base + rng.uniform(-4, 8), hh, hh * ratio * rng.uniform(0.8, 1.2), 255)
    if blur:
        m = m.filter(ImageFilter.GaussianBlur(blur))
    return np.asarray(m, np.float32) / 255


def draw_tower(d, cx, ground, height, base_w, top_w, lw, col, light=None):
    top = ground - height
    segs = 14
    def half(y):
        t = (ground - y) / height
        return lerp(base_w / 2, top_w / 2, t ** 0.8)
    ys = [ground - height * (i / segs) ** 0.93 for i in range(segs + 1)]
    for a, b in zip(ys[:-1], ys[1:]):
        ha, hb = half(a), half(b)
        d.line([(cx - ha, a), (cx - hb, b)], fill=col, width=lw + 1)
        d.line([(cx + ha, a), (cx + hb, b)], fill=col, width=lw + 1)
        d.line([(cx - ha, a), (cx + hb, b)], fill=col, width=max(1, lw - 1))
        d.line([(cx + ha, a), (cx - hb, b)], fill=col, width=max(1, lw - 1))
        d.line([(cx - hb, b), (cx + hb, b)], fill=col, width=lw)
    mast_top = top - height * 0.12
    d.line([(cx, top), (cx, mast_top)], fill=col, width=lw + 1)
    for k, frac in enumerate((0.18, 0.32, 0.5)):
        y = ground - height * (1 - frac * 0.35)
        s = top_w * (1.6 - k * 0.3)
        d.rectangle([cx + half(y) + 1, y - s * 0.9, cx + half(y) + s * 0.55, y + s * 0.9], fill=col)
        d.rectangle([cx - half(y) - s * 0.55, y - s * 0.7, cx - half(y) - 1, y + s * 0.7], fill=col)
    py = ground - height * 0.62
    d.rectangle([cx - half(py) - 10, py - 3, cx + half(py) + 10, py + 2], fill=col)
    return (cx, mast_top)


def draw_pole(d, x, ground, h, col, lw):
    d.line([(x - h * 0.08, ground), (x - h * 0.05, ground - h)], fill=col, width=lw)
    d.line([(x + h * 0.08, ground), (x + h * 0.05, ground - h)], fill=col, width=lw)
    d.line([(x - h * 0.2, ground - h * 0.9), (x + h * 0.2, ground - h * 0.9)], fill=col, width=lw)
    return [(x - h * 0.18, ground - h * 0.9), (x, ground - h * 0.9), (x + h * 0.18, ground - h * 0.9)]


def sag(d, p0, p1, depth, col, width):
    pts = []
    for i in range(41):
        t = i / 40
        x = lerp(p0[0], p1[0], t)
        y = lerp(p0[1], p1[1], t) + depth * 4 * t * (1 - t)
        pts.append((x, y))
    d.line(pts, fill=col, width=width)


def render_scene(W, H, seed_shift=0):
    SS = 2
    w, h = W * SS, H * SS
    horizon = 0.64 * h
    y = np.linspace(0, 1, h, dtype=np.float32)[:, None]
    sky_top = np.array([22, 22, 21], np.float32)
    sky_hz = np.array([132, 127, 115], np.float32)
    t = np.clip(y / 0.64, 0, 1) ** 1.6
    img = lerp(sky_top, sky_hz, t[..., None]) * np.ones((1, w, 1), np.float32)
    clouds = fbm(w, h, octaves=6, base=3, stretch=(2.2, 0.7))
    cmask = np.clip((clouds - 0.42) * 2.4, 0, 1) * np.clip(1 - y / 0.7, 0, 1)
    img = img * (1 - 0.35 * cmask[..., None]) + np.array([150, 145, 132], np.float32) * (0.22 * cmask[..., None])

    fog = np.array([138, 132, 120], np.float32)
    layers = [
        (horizon + 12 * SS, 24 * SS, 60 * SS, 700, 0.2, 0.8 * SS, 0.16),
        (horizon + 64 * SS, 40 * SS, 100 * SS, 420, 0.22, 0.5 * SS, 0.42),
    ]
    for base, hmin, hmax, count, ratio, blur, dark in layers:
        mask = tree_mask(w, h, base, hmin, hmax, count, ratio, blur)
        col = lerp(fog, np.array([28, 28, 25], np.float32), dark)
        img = img * (1 - mask[..., None]) + col * mask[..., None]

    layer = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    ground = horizon + 70 * SS
    lights = []
    lights.append(draw_tower(d, 0.745 * w, ground + 40 * SS, 0.62 * h, 150 * SS, 14 * SS, 3 * SS, (24, 24, 22, 255)))
    far = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    fd = ImageDraw.Draw(far)
    lights.append(draw_tower(fd, 0.905 * w, horizon + 22 * SS, 0.33 * h, 70 * SS, 8 * SS, 2 * SS, (70, 68, 62, 255)))
    lights.append(draw_tower(fd, 0.40 * w, horizon + 14 * SS, 0.2 * h, 40 * SS, 5 * SS, 1 * SS, (96, 93, 85, 255)))

    poles = []
    for i, (px, ph) in enumerate([(0.30, 0.10), (0.46, 0.14), (0.60, 0.19), (0.99, 0.30)]):
        gy = horizon + (20 + i * 18) * SS
        poles.append(draw_pole(d, px * w, gy, ph * h, (30, 30, 27, 255), 3 * SS if i > 1 else 2 * SS))
    for a, b in zip(poles[:-1], poles[1:]):
        for k in range(3):
            sag(d, a[k], b[k], 26 * SS, (34, 34, 30, 255), SS)

    base = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    far = far.filter(ImageFilter.GaussianBlur(1.2 * SS))
    base = Image.alpha_composite(base, far)
    base = Image.alpha_composite(base, layer)

    nm = tree_mask(w, h, h * 0.97, 140 * SS, 330 * SS, 60, 0.24, 2.2 * SS)
    near = np.zeros((h, w, 4), np.uint8)
    near[..., 0:3] = (13, 13, 12)
    near[..., 3] = (nm * 255).astype(np.uint8)
    base = Image.alpha_composite(base, Image.fromarray(near, 'RGBA'))

    arr = np.asarray(base.convert('RGB'), np.float32)
    mist = fbm(w, h, octaves=5, base=3, stretch=(3.0, 0.6))
    band = np.exp(-((y - 0.7) / 0.13) ** 2)
    arr = lerp(arr, fog[None, None, :], (np.clip(mist - 0.35, 0, 1) * 0.55 * band)[..., None])

    glow = np.zeros((h, w), np.float32)
    for (lx, ly) in lights[:2]:
        yy, xx = np.ogrid[:h, :w]
        glow += 0.9 * np.exp(-(((xx - lx) ** 2 + (yy - ly) ** 2) / (2 * (5 * SS) ** 2)))
        glow += 0.22 * np.exp(-(((xx - lx) ** 2 + (yy - ly) ** 2) / (2 * (34 * SS) ** 2)))
    arr += np.array([230, 110, 40], np.float32) * np.clip(glow, 0, 1)[..., None]

    yy, xx = np.ogrid[:h, :w]
    warm = np.exp(-(((xx - 0.86 * w) / (0.3 * w)) ** 2 + ((yy - 1.02 * h) / (0.28 * h)) ** 2))
    arr += np.array([120, 55, 18], np.float32) * (0.55 * warm)[..., None]

    arr *= np.array([1.0, 0.975, 0.92], np.float32)
    img = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)).resize((W, H), Image.LANCZOS)
    return img


def finish(img, left=0.0, top=0.0, bottom=0.0, vignette=0.55, grain=7):
    arr = np.asarray(img, np.float32)
    H, W = arr.shape[:2]
    y = np.linspace(0, 1, H, dtype=np.float32)[:, None]
    x = np.linspace(0, 1, W, dtype=np.float32)[None, :]
    v = 1 - vignette * (((x - 0.55) / 0.75) ** 2 + ((y - 0.45) / 0.8) ** 2)
    v = np.clip(v, 0, 1)
    if left:
        v *= np.clip(0.35 + x / left * 0.65, 0.35, 1)
    if top:
        v *= np.clip(0.45 + y / top * 0.55, 0.45, 1)
    if bottom:
        v *= np.clip(1 - np.clip((y - (1 - bottom)) / bottom, 0, 1) * 0.55, 0, 1)
    arr = arr * v[..., None]
    arr += rng.normal(0, grain, arr.shape[:2])[..., None]
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))


def sand_button(W=640, H=150):
    y = np.linspace(0, 1, H, dtype=np.float32)[:, None]
    x = np.linspace(0, 1, W, dtype=np.float32)[None, :]
    c0 = np.array([236, 229, 208], np.float32)
    c1 = np.array([196, 184, 150], np.float32)
    t = np.clip(0.55 * np.abs(x - 0.45) * 1.6 + 0.6 * y ** 1.4, 0, 1)
    arr = lerp(c0, c1, t[..., None]) * np.ones((H, W, 1), np.float32)
    streak = fbm(W, H, octaves=4, base=6, stretch=(0.3, 3.0))
    arr *= (0.94 + 0.1 * streak)[..., None]
    sil = Image.new('L', (W * 2, H * 2), 0)
    d = ImageDraw.Draw(sil)
    draw_tower(d, 170, H * 2 + 20, H * 2 * 1.02, 120, 10, 3, 255)
    for i, px in enumerate((330, 470)):
        draw_pole(d, px, H * 2 + 10, 150 - i * 30, 255, 3)
    sil = np.asarray(sil.resize((W, H), Image.LANCZOS).filter(ImageFilter.GaussianBlur(0.6)), np.float32) / 255
    fade = np.clip(1 - x * 1.8, 0, 1)
    arr = lerp(arr, np.array([150, 138, 108], np.float32), (sil * 0.55 * fade)[..., None])
    arr += rng.normal(0, 4, (H, W))[..., None]
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))


def grunge(W=1024, H=256):
    n = fbm(W, H, octaves=6, base=8)
    speck = rng.random((H, W)) > 0.985
    a = np.where(n > 0.66, 0.35, 1.0)
    a = np.where(speck, 0.0, a)
    scratch = Image.new('L', (W, H), 0)
    d = ImageDraw.Draw(scratch)
    for _ in range(26):
        x0, y0 = rng.integers(0, W), rng.integers(0, H)
        d.line([(x0, y0), (x0 + rng.integers(-160, 160), y0 + rng.integers(-18, 18))], fill=255, width=int(rng.integers(1, 3)))
    a = np.where(np.asarray(scratch) > 0, 0.25, a)
    rgba = np.zeros((H, W, 4), np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = (np.clip(a, 0, 1) * 255).astype(np.uint8)
    return Image.fromarray(rgba, 'RGBA').filter(ImageFilter.GaussianBlur(0.5))


scene = render_scene(1920, 1080)
finish(scene, left=0.22, top=0.16, bottom=0.34).save(os.path.join(OUT, 'bg_main.jpg'), quality=88)
side = scene.crop((1250, 60, 1650, 780)).resize((300, 540), Image.LANCZOS)
finish(side, vignette=0.7, bottom=0.4).save(os.path.join(OUT, 'dialog_side.jpg'), quality=88)
for i, box in enumerate([(700, 520, 1160, 780), (1300, 180, 1760, 440), (1420, 560, 1880, 820)]):
    tile = scene.crop(box).resize((460, 260), Image.LANCZOS)
    arr = np.asarray(tile, np.float32) * 0.55
    finish(Image.fromarray(arr.astype(np.uint8)), vignette=0.8, grain=5).save(os.path.join(OUT, f'tile_{i + 1}.jpg'), quality=85)
sand_button().save(os.path.join(OUT, 'sand.png'))
grunge().save(os.path.join(OUT, 'grunge.png'))
print('ok', os.listdir(OUT))
