"""Writes golden inputs and outputs of the hires.py texture steps for tests/Pfx.Tests/Golden.

usage: py -3.12 make_golden.py <hires.py folder> <out folder>

Inputs are synthetic and seeded, so a rerun writes the same files. texconv outputs (DXT1/DXT5 of one level, exactly as
hires.py encode() makes them) are written only when hires.py finds its texconv.
"""
import os
import sys

import numpy as np
from PIL import Image


def blob(w, h, seed):
    rng = np.random.default_rng(seed)
    ys, xs = np.mgrid[0:h, 0:w] + 0.5
    r = np.hypot((xs - w * 0.45) / (w * 0.35), (ys - h * 0.55) / (h * 0.3))
    alpha = np.clip(1.2 - r, 0, 1) ** 1.5 * 255
    alpha[alpha < 12] = 0
    rgb = np.dstack([xs / w * 255, ys / h * 255, (1 - r.clip(0, 1)) * 255]) + rng.normal(0, 18, (h, w, 3))
    return Image.fromarray(np.dstack([rgb.clip(0, 255), alpha]).round().astype(np.uint8), "RGBA")


def flipbook(fw, fh, cols, rows, seed):
    frames = [blob(fw, fh, seed + i) for i in range(cols * rows)]
    out = Image.new("RGBA", (fw * cols, fh * rows))
    for i, f in enumerate(frames):
        out.paste(f, ((i % cols) * fw, (i // cols) * fh))
    return out


def main():
    sys.path.insert(0, sys.argv[1])
    import hires

    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)

    def save(img, name):
        img.save(os.path.join(out, name))

    a = blob(48, 40, 1)
    b = blob(48, 40, 2)
    save(a, "in_a.png")
    save(b, "in_b.png")
    save(hires.bleed(a), "bleed_a.png")
    save(hires.contrast(a, 1.6), "contrast_a_1.6.png")
    save(hires.pack_intensity(a), "pack_a.png")
    save(hires.match_mean(b, a), "match_b_to_a.png")
    for w, h in ((24, 20), (17, 9), (96, 40), (48, 13), (5, 77)):
        save(a.resize((w, h), Image.LANCZOS), "lanczos_a_%dx%d.png" % (w, h))

    fb = flipbook(32, 32, 4, 2, 10)
    save(fb, "in_flip_4x2.png")
    for i, lv in enumerate(hires.mip_chain(fb, (4, 2))):
        save(lv, "mip_flip_4x2_%02d.png" % i)
    for i, lv in enumerate(hires.mip_chain(a, (1, 1))):
        save(lv, "mip_a_%02d.png" % i)

    if os.path.exists(hires.TEXCONV_EXE):
        sq = blob(64, 64, 3)
        save(sq, "in_sq.png")
        for fmt in ("DXT1", "DXT5"):
            levels = hires.mip_chain(sq, (1, 1))
            with open(os.path.join(out, "texconv_sq_%s.bin" % fmt), "wb") as f:
                for raw in hires.encode(levels, fmt, os.path.join(out, "_work")):
                    f.write(raw)
        import shutil
        shutil.rmtree(os.path.join(out, "_work"), ignore_errors=True)


if __name__ == "__main__":
    main()
