"""Pixel check of PfxTex decodes against the Python reference (hires.py parse) and, for PAA, ImageToPAA.exe.

usage: py -3.12 compare_reference.py <pfxtex.dll> <hires.py folder> <ImageToPAA.exe> <work dir> <root>... [--per N]

Picks N files per format spread over the sorted list of each root, decodes the top level with both sides and prints
per file: size, differing pixels, max channel difference. EDDS/DDS must match exactly (same integer DXT maths);
ImageToPAA has its own DXT decoder, so PAA differences are reported, not required to be zero.
"""
import collections
import os
import struct
import subprocess
import sys

import numpy as np
from PIL import Image


def kind(path):
    d = open(path, "rb").read(256)
    if path.lower().endswith(".paa"):
        return "paa " + {b"\x01\xff": "DXT1", b"\x05\xff": "DXT5"}.get(d[:2], d[:2].hex())
    enf = "edds" if d[36:40] == b"ENF1" else "dds"
    fourcc = d[84:88]
    if fourcc == b"\0\0\0\0":
        bits = struct.unpack_from("<I", d, 88)[0]
        amask = struct.unpack_from("<I", d, 104)[0]
        return "%s RGB%d%s" % (enf, bits, "A" if amask else "")
    return "%s %s" % (enf, fourcc.decode("latin-1"))


def main():
    args = sys.argv[1:]
    per = 12
    if "--per" in args:
        i = args.index("--per")
        per = int(args[i + 1])
        del args[i:i + 2]
    pfxtex, hires_dir, image_to_paa, work = args[:4]
    roots = args[4:]
    sys.path.insert(0, hires_dir)
    import hires

    os.makedirs(work, exist_ok=True)
    groups = collections.defaultdict(list)
    for root in roots:
        for dp, _, fs in os.walk(root):
            for f in sorted(fs):
                if f.lower().endswith((".edds", ".dds", ".paa")):
                    groups[kind(os.path.join(dp, f))].append(os.path.join(dp, f))
    total_bad = 0
    for k in sorted(groups):
        files = sorted(groups[k])
        step = max(1, len(files) // per)
        picked = files[::step][:per]
        if k.startswith(("edds DX10", "dds RGB8", "edds RGB8")):
            print("%s: %d files, skipped (hires.py cannot read this format)" % (k, len(files)))
            continue
        print("%s: %d files, checking %d" % (k, len(files), len(picked)))
        for n, path in enumerate(picked):
            ours_png = os.path.join(work, "%s_%02d_ours.png" % (k.replace(" ", "_"), n))
            subprocess.run(["dotnet", pfxtex, "decode", path, ours_png], check=True, capture_output=True)
            ours = np.asarray(Image.open(ours_png).convert("RGBA")).astype(int)
            if k.startswith("paa"):
                ref_png = os.path.join(work, "%s_%02d_ref.png" % (k.replace(" ", "_"), n))
                subprocess.run([image_to_paa, path, ref_png], check=True, capture_output=True)
                ref = np.asarray(Image.open(ref_png).convert("RGBA")).astype(int)
            else:
                ref = np.asarray(hires.load(path)[0]).astype(int)
            if ours.shape[1] == ref.shape[1] and ours.shape[0] > ref.shape[0] and ours.shape[0] % ref.shape[0] == 0:
                print("  volume texture: PIL reads the first slice, comparing that")
                ours = ours[:ref.shape[0]]
            if ours.shape != ref.shape:
                print("  SHAPE %s ours %s ref %s" % (path, ours.shape, ref.shape))
                total_bad += 1
                continue
            diff = np.abs(ours - ref).max(2)
            bad = int((diff > 0).sum())
            total_bad += bad > 0 and not k.startswith("paa")
            print("  %-70s %5dx%-5d differing px %8d (%.3f %%), max %3d, mean %.4f" % (
                path, ref.shape[1], ref.shape[0], bad, 100.0 * bad / diff.size, diff.max(), np.abs(ours - ref).mean()))
    print("EDDS/DDS files with any difference: %d" % total_bad)


if __name__ == "__main__":
    main()
