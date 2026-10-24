"""Runs Real-ESRGAN x4 on the CPU through the user's esrgan_cpu.py (ncnn weights executed by PyTorch; the GPU is hidden
before torch loads, so nothing runs on the graphics card). NDZ Particle Studio starts it with a job file:

    <python> pfx_upscale.py <job.json>

job.json: {"executor": "<path to esrgan_cpu.py>", "param": "<model .param>", "bin": "<model .bin>",
           "items": [["<in.png>", "<out.png>"], ...]}
Each input is read as RGB and written 4x larger. Prints "ok <n>" after each item and "error <text>" on failure.
"""
import json
import os
import sys

os.environ["HIP_VISIBLE_DEVICES"] = "-1"
os.environ["CUDA_VISIBLE_DEVICES"] = "-1"


def main():
    with open(sys.argv[1], encoding="utf-8") as f:
        job = json.load(f)
    sys.path.insert(0, os.path.dirname(os.path.abspath(job["executor"])))
    import numpy as np
    from PIL import Image
    import esrgan_cpu

    for n, (src, dst) in enumerate(job["items"], 1):
        rgb = np.asarray(Image.open(src).convert("RGB"))
        Image.fromarray(esrgan_cpu.upscale(rgb, job["param"], job["bin"]), "RGB").save(dst)
        print("ok %d" % n, flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print("error %s: %s" % (type(e).__name__, e), flush=True)
        sys.exit(1)
