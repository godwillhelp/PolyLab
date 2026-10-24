namespace Pfx.Textures;

// Ports of the hires.py texture steps that proved themselves in the anomaly work (prompt 2.11). Float maths and
// round-half-to-even follow numpy, so results match the Python reference within one level.
public static class TextureTools
{
    // Frames smaller than this on a mip level are not shrunk one by one any more (hires.py MIN_FRAME).
    public const int MinFrame = 4;

    // Colour below which a pixel counts as unlit when brightness goes into alpha (hires.py PACK_FLOOR).
    public const int PackFloor = 8;

    public static IReadOnlyList<RgbaImage> Split(RgbaImage image, int tileU, int tileV)
    {
        if (image.Width % tileU != 0 || image.Height % tileV != 0)
            throw new ArgumentException($"{image.Width}x{image.Height} does not split into {tileU}x{tileV} frames");
        int fw = image.Width / tileU, fh = image.Height / tileV;
        var frames = new List<RgbaImage>(tileU * tileV);
        for (int y = 0; y < tileV; y++)
            for (int x = 0; x < tileU; x++)
                frames.Add(image.Crop(x * fw, y * fh, fw, fh));
        return frames;
    }

    public static RgbaImage Join(IReadOnlyList<RgbaImage> frames, int tileU, int tileV)
    {
        if (frames.Count != tileU * tileV)
            throw new ArgumentException($"{frames.Count} frames for a {tileU}x{tileV} grid");
        int fw = frames[0].Width, fh = frames[0].Height;
        var output = new RgbaImage(fw * tileU, fh * tileV);
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Width != fw || frames[i].Height != fh)
                throw new ArgumentException($"Frame {i} is {frames[i].Width}x{frames[i].Height}, frame 0 is {fw}x{fh}");
            output.Paste(frames[i], i % tileU * fw, i / tileU * fh);
        }
        return output;
    }

    // Full chain down to 1x1. Each level is resized from the top image; while a frame stays at least MinFrame pixels
    // and whole, frames shrink one by one so neighbours do not bleed into each other (hires.py mip_chain).
    public static IReadOnlyList<RgbaImage> MipChain(RgbaImage top, int tileU, int tileV)
    {
        var levels = new List<RgbaImage> { top };
        int w = top.Width, h = top.Height;
        IReadOnlyList<RgbaImage>? frames = null;
        while (w != 1 || h != 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            if (w / tileU >= MinFrame && h / tileV >= MinFrame && w % tileU == 0 && h % tileV == 0)
            {
                frames ??= Split(top, tileU, tileV);
                levels.Add(Join(frames.Select(f => Resample.Lanczos(f, w / tileU, h / tileV)).ToList(), tileU, tileV));
            }
            else
                levels.Add(Resample.Lanczos(top, w, h));
        }
        return levels;
    }

    // Fills the colour under alpha 0 from the nearest covered pixels (average over a 2x2 pyramid), so filtering and
    // mips do not pull dark fringes in. Alpha is kept.
    public static RgbaImage Bleed(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        var solid = new bool[w * h];
        int count = 0;
        for (int i = 0; i < solid.Length; i++)
            if (px[i * 4 + 3] > 0)
            {
                solid[i] = true;
                count++;
            }
        if (count == 0 || count == solid.Length)
            return image.Clone();

        var colours = new List<double[]>();
        var weights = new List<double[]>();
        var sizes = new List<(int W, int H)>();
        var c0 = new double[w * h * 3];
        var w0 = new double[w * h];
        for (int i = 0; i < w0.Length; i++)
        {
            if (!solid[i])
                continue;
            w0[i] = 1.0;
            for (int c = 0; c < 3; c++)
                c0[i * 3 + c] = px[i * 4 + c];
        }
        colours.Add(c0);
        weights.Add(w0);
        sizes.Add((w, h));
        while (Math.Max(sizes[^1].W, sizes[^1].H) > 1)
        {
            var (pw, ph) = sizes[^1];
            int nw = (pw + pw % 2) / 2, nh = (ph + ph % 2) / 2;
            var pc = colours[^1];
            var pwt = weights[^1];
            var nc = new double[nw * nh * 3];
            var nwt = new double[nw * nh];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int s = y * pw + x, d = y / 2 * nw + x / 2;
                    nwt[d] += pwt[s];
                    for (int c = 0; c < 3; c++)
                        nc[d * 3 + c] += pc[s * 3 + c];
                }
            colours.Add(nc);
            weights.Add(nwt);
            sizes.Add((nw, nh));
        }

        int top = sizes.Count - 1;
        var fill = new double[colours[top].Length];
        for (int i = 0; i < weights[top].Length; i++)
            for (int c = 0; c < 3; c++)
                fill[i * 3 + c] = colours[top][i * 3 + c] / Math.Max(weights[top][i], 1e-9);
        for (int l = top - 1; l >= 0; l--)
        {
            var (lw, lh) = sizes[l];
            int uw = sizes[l + 1].W;
            var next = new double[lw * lh * 3];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x, up = y / 2 * uw + x / 2;
                    double wt = weights[l][i];
                    for (int c = 0; c < 3; c++)
                        next[i * 3 + c] = wt > 0 ? colours[l][i * 3 + c] / Math.Max(wt, 1e-9) : fill[up * 3 + c];
                }
            fill = next;
        }

        var output = image.Clone();
        for (int i = 0; i < solid.Length; i++)
            if (!solid[i])
                for (int c = 0; c < 3; c++)
                    output.Pixels[i * 4 + c] = ToByte(fill[i * 3 + c]);
        return output;
    }

    // Colour ^ gamma (gamma > 1 darkens the mid tones, raises contrast); alpha is kept.
    public static RgbaImage Contrast(RgbaImage image, double gamma)
    {
        var output = image.Clone();
        var p = output.Pixels;
        for (int i = 0; i < p.Length; i += 4)
            for (int c = 0; c < 3; c++)
                p[i + c] = ToByte(Math.Pow(p[i + c] / 255.0, gamma) * 255.0);
        return output;
    }

    // For additive (emissive) materials: alpha = brightest channel, colour normalised to full brightness, so DXT5 keeps
    // dark gradients in its 8-bit alpha instead of the 5-6 bit colour. Unlit pixels take the mean lit colour. The
    // source alpha is not used.
    public static RgbaImage PackIntensity(RgbaImage image)
    {
        var p = image.Pixels;
        int n = p.Length / 4;
        var norm = new double[n * 3];
        var lit = new bool[n];
        double[] mean = new double[3];
        int litCount = 0;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(p[i * 4], Math.Max(p[i * 4 + 1], p[i * 4 + 2]));
            double scale = 255.0 / Math.Max(a, 1.0);
            for (int c = 0; c < 3; c++)
                norm[i * 3 + c] = p[i * 4 + c] * scale;
            if (a >= PackFloor)
            {
                lit[i] = true;
                litCount++;
                for (int c = 0; c < 3; c++)
                    mean[c] += norm[i * 3 + c];
            }
        }
        for (int c = 0; c < 3; c++)
            mean[c] = litCount > 0 ? mean[c] / litCount : 255.0;
        var output = new RgbaImage(image.Width, image.Height);
        var o = output.Pixels;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++)
                o[i * 4 + c] = ToByte(lit[i] ? norm[i * 3 + c] : mean[c]);
            o[i * 4 + 3] = (byte)Math.Max(p[i * 4], Math.Max(p[i * 4 + 1], p[i * 4 + 2]));
        }
        return output;
    }

    // Scales alpha to the source's mean alpha, then luma so the alpha-weighted mean luma matches the source: keeps a
    // reworked frame (upscaled, cleaned) as bright and as dense as the original.
    public static RgbaImage MatchMean(RgbaImage image, RgbaImage source)
    {
        int n = image.Pixels.Length / 4, m = source.Pixels.Length / 4;
        var p = image.Pixels;
        var s = source.Pixels;
        var alpha = new double[n];
        double outAlpha = 0, refAlpha = 0, refLuma = 0;
        for (int i = 0; i < n; i++)
            outAlpha += alpha[i] = p[i * 4 + 3];
        for (int i = 0; i < m; i++)
        {
            refAlpha += s[i * 4 + 3];
            refLuma += Luma(s, i) * s[i * 4 + 3];
        }
        outAlpha /= n;
        refAlpha /= m;
        refLuma /= m;
        if (outAlpha > 0)
            for (int i = 0; i < n; i++)
                alpha[i] = Math.Clamp(alpha[i] * (refAlpha / outAlpha), 0, 255);
        double have = 0;
        for (int i = 0; i < n; i++)
            have += Luma(p, i) * alpha[i];
        have /= n;
        double gain = have > 0 ? refLuma / have - 1.0 : 0.0;
        var output = new RgbaImage(image.Width, image.Height);
        var o = output.Pixels;
        for (int i = 0; i < n; i++)
        {
            double y = Luma(p, i);
            for (int c = 0; c < 3; c++)
                o[i * 4 + c] = ToByte(p[i * 4 + c] + gain * y);
            o[i * 4 + 3] = ToByte(alpha[i]);
        }
        return output;
    }

    // Semi-transparent pixels (0 < alpha < 255) and how many of them have no channel above their alpha: colour already
    // multiplied by alpha looks like that everywhere (a dark smoke can too, so this is a hint, not proof). The game
    // multiplies by alpha itself (AlphaBlend / Additive_AlphaModulate), so premultiplied colour shows dark fringes.
    public static (int Semi, int AtMostAlpha) PremultiplyCheck(RgbaImage image)
    {
        var p = image.Pixels;
        int semi = 0, under = 0;
        for (int i = 0; i < p.Length; i += 4)
        {
            byte a = p[i + 3];
            if (a is 0 or 255)
                continue;
            semi++;
            if (Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])) <= a)
                under++;
        }
        return (semi, under);
    }

    private static double Luma(byte[] p, int i) => p[i * 4] * 0.299 + p[i * 4 + 1] * 0.587 + p[i * 4 + 2] * 0.114;

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255);
}
