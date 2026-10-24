namespace Pfx.Textures;

// Lanczos-3 resize computed the way Pillow does Image.resize(size, LANCZOS) on an RGBA image (Resample.c), so mip
// chains match hires.py: premultiply alpha, a horizontal then a vertical pass with coefficients rounded to 22-bit fixed
// point and results clamped to 8 bits after each pass, then un-premultiply.
public static class Resample
{
    private const int PrecisionBits = 32 - 8 - 2;
    private const double Support = 3.0;

    public static RgbaImage Lanczos(RgbaImage source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
            return source.Clone();
        var image = Premultiply(source);
        var (vBounds, vKernel, vSize) = Coefficients(image.Height, height);
        if (width != image.Width)
        {
            int first = vBounds[0], last = vBounds[height * 2 - 2] + vBounds[height * 2 - 1];
            for (int i = 0; i < height; i++)
                vBounds[i * 2] -= first;
            image = Horizontal(image, width, first, last - first);
        }
        if (height != source.Height)
            image = Vertical(image, height, vBounds, vKernel, vSize);
        return Unpremultiply(image);
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double Filter(double x) => -3.0 <= x && x < 3.0 ? Sinc(x) * Sinc(x / 3) : 0.0;

    private static (int[] Bounds, int[] Kernel, int Size) Coefficients(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = Support * filterScale;
        int size = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var kernel = new int[outSize * size];
        var k = new double[size];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double sum = 0.0, ss = 1.0 / filterScale;
            int xmin = Math.Max(0, (int)(center - support + 0.5));
            int xmax = Math.Min(inSize, (int)(center + support + 0.5)) - xmin;
            for (int x = 0; x < xmax; x++)
            {
                k[x] = Filter((x + xmin - center + 0.5) * ss);
                sum += k[x];
            }
            for (int x = 0; x < xmax; x++)
            {
                double v = sum != 0.0 ? k[x] / sum : k[x];
                kernel[xx * size + x] = v < 0 ? (int)(-0.5 + v * (1 << PrecisionBits)) : (int)(0.5 + v * (1 << PrecisionBits));
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kernel, size);
    }

    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    private static RgbaImage Horizontal(RgbaImage src, int width, int firstRow, int rows)
    {
        var (bounds, kernel, size) = Coefficients(src.Width, width);
        var output = new RgbaImage(width, rows);
        var s = src.Pixels;
        var o = output.Pixels;
        for (int y = 0; y < rows; y++)
        {
            int row = (y + firstRow) * src.Width * 4;
            for (int xx = 0; xx < width; xx++)
            {
                int xmin = bounds[xx * 2], xmax = bounds[xx * 2 + 1], kAt = xx * size;
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                for (int x = 0; x < xmax; x++)
                {
                    int p = row + (x + xmin) * 4, w = kernel[kAt + x];
                    s0 += s[p] * w;
                    s1 += s[p + 1] * w;
                    s2 += s[p + 2] * w;
                    s3 += s[p + 3] * w;
                }
                int at = (y * width + xx) * 4;
                o[at] = Clip8(s0);
                o[at + 1] = Clip8(s1);
                o[at + 2] = Clip8(s2);
                o[at + 3] = Clip8(s3);
            }
        }
        return output;
    }

    private static RgbaImage Vertical(RgbaImage src, int height, int[] bounds, int[] kernel, int size)
    {
        var output = new RgbaImage(src.Width, height);
        var s = src.Pixels;
        var o = output.Pixels;
        int stride = src.Width * 4;
        for (int yy = 0; yy < height; yy++)
        {
            int ymin = bounds[yy * 2], ymax = bounds[yy * 2 + 1], kAt = yy * size;
            for (int x = 0; x < src.Width; x++)
            {
                int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0, s3 = s0;
                for (int y = 0; y < ymax; y++)
                {
                    int p = (y + ymin) * stride + x * 4, w = kernel[kAt + y];
                    s0 += s[p] * w;
                    s1 += s[p + 1] * w;
                    s2 += s[p + 2] * w;
                    s3 += s[p + 3] * w;
                }
                int at = (yy * src.Width + x) * 4;
                o[at] = Clip8(s0);
                o[at + 1] = Clip8(s1);
                o[at + 2] = Clip8(s2);
                o[at + 3] = Clip8(s3);
            }
        }
        return output;
    }

    private static RgbaImage Premultiply(RgbaImage src)
    {
        var output = src.Clone();
        var p = output.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            for (int c = 0; c < 3; c++)
            {
                int t = p[i + c] * a + 128;
                p[i + c] = (byte)(((t >> 8) + t) >> 8);
            }
        }
        return output;
    }

    private static RgbaImage Unpremultiply(RgbaImage image)
    {
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a == 0 || a == 255)
                continue;
            for (int c = 0; c < 3; c++)
                p[i + c] = (byte)Math.Min(255, 255 * p[i + c] / a);
        }
        return image;
    }
}
