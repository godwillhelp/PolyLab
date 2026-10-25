"""Writes golden inputs and outputs of the hires.py texture steps for tests/Pfx.Tests/Golden.

usage: py -3.12 make_golden.py <hires.py folder> <out folder>

Inputs are synthetic and seeded, so a rerun writes the same files. texconv outputs (DXT1/DXT5 of one level, exactly as
hires.py encode() makes them, and texconv's RGBA decode of random BC7 blocks) are written only when hires.py finds its
texconv.
"""
import os
import struct
import subprocess
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


# Row k < 8 holds eight random blocks of BC7 mode k (the lowest set bit of the first byte); the last row starts with a
# reserved block (no mode bit) and goes on with random modes.
def bc7_blocks(seed):
    rng = np.random.default_rng(seed)
    blocks = rng.integers(0, 256, (9, 8, 16), dtype=np.uint8)
    for mode in range(8):
        blocks[mode, :, 0] = blocks[mode, :, 0] & ((0xFF << (mode + 1)) & 0xFF) | 1 << mode
    blocks[8, 0, 0] = 0
    return blocks


# DDS with a DX10 header, DXGI 98 (BC7_UNORM), one level.
def bc7_dds(blocks):
    rows, cols = blocks.shape[:2]
    header = struct.pack("<4s7I44x2I4s5I5I5I", b"DDS ", 124, 0x81007, rows * 4, cols * 4, blocks.size, 0, 1,
                         32, 0x4, b"DX10", 0, 0, 0, 0, 0, 0x1000, 0, 0, 0, 0, 98, 3, 0, 1, 0)
    return header + blocks.tobytes()


def texconv_rgba(exe, dds, work):
    src, out = os.path.join(work, "bc7.dds"), os.path.join(work, "bc7_out")
    os.makedirs(out, exist_ok=True)
    with open(src, "wb") as f:
        f.write(dds)
    subprocess.run([exe, "-f", "R8G8B8A8_UNORM", "-m", "1", "-nogpu", "-y", "-nologo", "-o", out, src],
                   check=True, capture_output=True)
    with open(os.path.join(out, "bc7.dds"), "rb") as f:
        data = f.read()
    return data[148 if data[84:88] == b"DX10" else 128:]


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
        bc7 = bc7_dds(bc7_blocks(4))
        with open(os.path.join(out, "bc7_modes.dds"), "wb") as f:
            f.write(bc7)
        with open(os.path.join(out, "texconv_bc7_modes.bin"), "wb") as f:
            f.write(texconv_rgba(hires.TEXCONV_EXE, bc7, os.path.join(out, "_work")))
        import shutil
        shutil.rmtree(os.path.join(out, "_work"), ignore_errors=True)


if __name__ == "__main__":
    main()
