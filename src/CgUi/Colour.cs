namespace PolyLab.CgUi;

public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb Of8(int r, int g, int b) => new(r / 255.0, g / 255.0, b / 255.0);
    public Rgb Over(Rgb under, double a) => new(a * R + (1 - a) * under.R, a * G + (1 - a) * under.G, a * B + (1 - a) * under.B);
}

public static class Colour
{
    public static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double Srgb(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;

    public static int To8(double c) => (int)Math.Floor(Math.Clamp(c, 0, 1) * 255 + 0.5);

    public static Rgb Quantise(Rgb c) => Rgb.Of8(To8(c.R), To8(c.G), To8(c.B));

    public static int Argb(int a, int r, int g, int b) => unchecked((int)((uint)a << 24 | (uint)r << 16 | (uint)g << 8 | (uint)b));

    public static int Argb(int a, Rgb c) => Argb(a, To8(c.R), To8(c.G), To8(c.B));

    public static (int A, int R, int G, int B) Split(int argb) =>
        ((argb >> 24) & 255, (argb >> 16) & 255, (argb >> 8) & 255, argb & 255);

    public static double CanvasError(Rgb s, int alpha8, Rgb d, Rgb target)
    {
        double k = alpha8 / 255.0, err = 0;
        foreach (var (sc, dc, tc) in new[] { (s.R, d.R, target.R), (s.G, d.G, target.G), (s.B, d.B, target.B) })
        {
            double o = 255 * (Srgb(k * Lin(sc) + (1 - k) * Lin(dc)) - tc);
            err += o * o;
        }
        return err;
    }

    public static int Alpha8(Rgb s, double a, Rgb? d)
    {
        if (a >= 1)
            return 255;
        if (d is not { } dd || dd == s)
            return To8(a);
        var target = s.Over(dd, a);
        int best = To8(a);
        double bestErr = CanvasError(s, best, dd, target);
        for (int k = 0; k <= 255; k++)
        {
            double err = CanvasError(s, k, dd, target);
            if (err < bestErr)
            {
                best = k;
                bestErr = err;
            }
        }
        return best;
    }

    public static Rgb LinBlend(Rgb dstLin, Rgb src, double a) =>
        new(a * Lin(src.R) + (1 - a) * dstLin.R, a * Lin(src.G) + (1 - a) * dstLin.G, a * Lin(src.B) + (1 - a) * dstLin.B);

    public static int[] Lut(IReadOnlyList<(double Pos, Rgb C, double A)> stops, double opacity, Rgb? d)
    {
        var lut = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            var (c, a) = Sample(stops, t);
            c = Quantise(c);
            lut[i] = Argb(Alpha8(c, a * opacity, d), c);
        }
        return lut;
    }

    private static (Rgb, double) Sample(IReadOnlyList<(double Pos, Rgb C, double A)> stops, double t)
    {
        if (t <= stops[0].Pos)
            return (stops[0].C, stops[0].A);
        for (int i = 1; i < stops.Count; i++)
        {
            if (t <= stops[i].Pos)
            {
                var (p0, c0, a0) = stops[i - 1];
                var (p1, c1, a1) = stops[i];
                double f = p1 > p0 ? (t - p0) / (p1 - p0) : 1;
                int steps = new[] { To8(c1.R) - To8(c0.R), To8(c1.G) - To8(c0.G), To8(c1.B) - To8(c0.B), To8(a1) - To8(a0) }
                    .Max(Math.Abs);
                f = steps == 0 ? 0 : Math.Floor(f * steps + 0.5) / steps;
                return (c1.Over(c0, f), a0 + (a1 - a0) * f);
            }
        }
        return (stops[^1].C, stops[^1].A);
    }

    public static int LutIndex(double t) => Math.Clamp((int)Math.Floor(t * 255 + 0.5), 0, 255);
}
