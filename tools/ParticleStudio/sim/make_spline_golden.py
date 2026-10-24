"""Golden data for the graph spline test (W0 acceptance item for P3).

Traces the magenta Size curve of the W0 Workbench screenshot (keys 0.1 / 0.1 / 1 / 1 at 0 / 0.25 / 0.5 / 1) the same
way as the W0 probe's curvefit.py, and writes per traced column: t, the uniform Catmull-Rom fit of curvefit.py, and
the drawn value.

usage: py -3.12 make_spline_golden.py <32_us_size.png> <out.csv>
"""
import sys

from PIL import Image

KEYS = [(0, 0.1), (0.25, 0.1), (0.5, 1.0), (1.0, 1.0)]


def uniform(t, ks):
    n = len(ks)
    for i in range(n - 1):
        ta, tb = ks[i][0], ks[i + 1][0]
        if ta <= t <= tb:
            s = (t - ta) / (tb - ta)
            pa, pb = ks[i][1], ks[i + 1][1]
            ma = (ks[min(i + 1, n - 1)][1] - ks[max(i - 1, 0)][1]) / 2.0
            mb = (ks[min(i + 2, n - 1)][1] - ks[i][1]) / 2.0
            return ((2 * s**3 - 3 * s**2 + 1) * pa + (s**3 - 2 * s**2 + s) * ma
                    + (-2 * s**3 + 3 * s**2) * pb + (s**3 - s**2) * mb)
    return ks[-1][1]


def main(png, out):
    im = Image.open(png).convert("RGB")
    cols = {}
    for x in range(im.size[0]):
        ys = [y for y in range(0, 175) if (lambda p: p[0] > 200 and p[2] > 200 and p[1] < 80)(im.getpixel((x, y)))]
        if ys:
            cols[x] = sum(ys) / len(ys)
    xs = sorted(cols)
    x0, x1 = xs[0], xs[-1]
    top = min(cols.values())
    scale = (cols[x0] - top) / 0.9
    y0 = top + scale
    with open(out, "w", newline="\n") as f:
        f.write("t,fit,drawn\n")
        for x in xs:
            t = (x - x0) / (x1 - x0)
            fit = min(1.0, max(0.0, uniform(t, KEYS)))
            f.write("%.6f,%.6f,%.6f\n" % (t, fit, (y0 - cols[x]) / scale))
    print("wrote", len(xs), "columns to", out)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
