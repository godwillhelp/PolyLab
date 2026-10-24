namespace Pfx.Link;

// A MakeScreenshot frame: the engine writes 24-bit bottom-up BMPs without alpha (memory dayz-makescreenshot-autotest-gate).
public sealed class BmpFrame
{
    private BmpFrame(int width, int height, byte[] luma)
    {
        Width = width;
        Height = height;
        Luma = luma;
    }

    public int Width { get; }
    public int Height { get; }
    // Grey value per pixel, top row first (PIL's "L": (299 R + 587 G + 114 B) / 1000).
    public byte[] Luma { get; }

    public static BmpFrame Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 54 || b[0] != 'B' || b[1] != 'M')
            throw new InvalidDataException("Not a BMP: " + path);
        int offset = BitConverter.ToInt32(b, 10);
        int w = BitConverter.ToInt32(b, 18);
        int h = BitConverter.ToInt32(b, 22);
        int bpp = BitConverter.ToInt16(b, 28);
        if (bpp != 24 && bpp != 32)
            throw new InvalidDataException($"BMP with {bpp} bits per pixel: {path}");
        bool bottomUp = h > 0;
        h = Math.Abs(h);
        int px = bpp / 8;
        int stride = (w * px + 3) & ~3;
        var luma = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = offset + (bottomUp ? h - 1 - y : y) * stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * px;
                luma[y * w + x] = (byte)((b[i + 2] * 299 + b[i + 1] * 587 + b[i] * 114) / 1000);
            }
        }
        return new BmpFrame(w, h, luma);
    }
}

// The blink measure of the tornado work (CG_AnomalyTools shots/flicker.py): between consecutive frames of a series, the
// mean absolute grey difference and the share of pixels that jump by 40 or more, inside the middle of the frame (the
// HUD stays out). A series is compared against a baseline series of the same view with no effect in it (spec 2.11:
// a true empty scene, not a soft-stopped one).
public sealed record SeriesStats(int Frames, int IdenticalPairs, double MeanDiff, double JumpPercent, double MaxJumpPercent)
{
    public const int JumpThreshold = 40;

    public static SeriesStats Of(IReadOnlyList<BmpFrame> frames)
    {
        if (frames.Count < 2)
            return new SeriesStats(frames.Count, 0, 0, 0, 0);
        var first = frames[0];
        int x0 = first.Width / 4, x1 = first.Width * 3 / 4, y0 = first.Height / 4, y1 = first.Height * 3 / 4;
        double n = (double)(x1 - x0) * (y1 - y0);
        int same = 0;
        double sumMean = 0, sumJump = 0, maxJump = 0;
        for (int f = 1; f < frames.Count; f++)
        {
            var a = frames[f - 1].Luma;
            var b = frames[f].Luma;
            long diff = 0, jumps = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * first.Width;
                for (int x = x0; x < x1; x++)
                {
                    int d = Math.Abs(a[row + x] - b[row + x]);
                    diff += d;
                    if (d >= JumpThreshold)
                        jumps++;
                }
            }
            if (diff == 0)
                same++;
            sumMean += diff / n;
            double jump = jumps / n * 100;
            sumJump += jump;
            maxJump = Math.Max(maxJump, jump);
        }
        int pairs = frames.Count - 1;
        return new SeriesStats(frames.Count, same, sumMean / pairs, sumJump / pairs, maxJump);
    }
}
