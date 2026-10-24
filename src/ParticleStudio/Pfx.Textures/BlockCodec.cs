namespace Pfx.Textures;

// DXT1 (BC1), DXT5 (BC3), BC4 and BC5 decoding on the CPU. Interpolation uses integer division like Pillow's BcnDecode
// (the decoder behind hires.py), so decodes compare exactly with the Python reference. BC4 lands in red, BC5 in red
// and green; the other channels stay 0 and alpha 255.
public static class BlockCodec
{
    public static int BlockBytes(BlockFormat format) => format is BlockFormat.Dxt1 or BlockFormat.Bc4 ? 8 : 16;

    public static int Bytes(BlockFormat format, int width, int height) =>
        checked(((width + 3) / 4) * ((height + 3) / 4) * BlockBytes(format));

    public static RgbaImage Decode(BlockFormat format, int width, int height, ReadOnlySpan<byte> data)
    {
        if (data.Length != Bytes(format, width, height))
            throw new InvalidDataException($"{format} {width}x{height} needs {Bytes(format, width, height)} bytes, got {data.Length}");
        var image = new RgbaImage(width, height);
        var block = new byte[64];
        int size = BlockBytes(format), at = 0;
        for (int by = 0; by < height; by += 4)
        {
            for (int bx = 0; bx < width; bx += 4, at += size)
            {
                var src = data.Slice(at, size);
                switch (format)
                {
                    case BlockFormat.Dxt1:
                        Color(src, block, false);
                        break;
                    case BlockFormat.Dxt5:
                        Color(src[8..], block, true);
                        Alpha(src, block, 3);
                        break;
                    case BlockFormat.Bc4:
                        Grey(block);
                        Alpha(src, block, 0);
                        break;
                    case BlockFormat.Bc5:
                        Grey(block);
                        Alpha(src, block, 0);
                        Alpha(src[8..], block, 1);
                        break;
                }
                Store(image, bx, by, block);
            }
        }
        return image;
    }

    private static void Grey(byte[] block)
    {
        Array.Clear(block);
        for (int i = 3; i < 64; i += 4)
            block[i] = 255;
    }

    // DXT5 colour blocks always use the four-colour mode, whatever the order of c0 and c1.
    private static void Color(ReadOnlySpan<byte> src, byte[] block, bool fourColours)
    {
        int c0 = src[0] | src[1] << 8, c1 = src[2] | src[3] << 8;
        Span<int> p = stackalloc int[16];
        Expand(c0, p[..4]);
        Expand(c1, p.Slice(4, 4));
        if (c0 > c1 || fourColours)
        {
            for (int c = 0; c < 3; c++)
            {
                p[8 + c] = (2 * p[c] + p[4 + c]) / 3;
                p[12 + c] = (p[c] + 2 * p[4 + c]) / 3;
            }
            p[11] = p[15] = 255;
        }
        else
        {
            for (int c = 0; c < 3; c++)
                p[8 + c] = (p[c] + p[4 + c]) / 2;
            p[11] = 255;
            p[12] = p[13] = p[14] = p[15] = 0;
        }
        uint bits = (uint)(src[4] | src[5] << 8 | src[6] << 16 | src[7] << 24);
        for (int i = 0; i < 16; i++, bits >>= 2)
        {
            int k = (int)(bits & 3) * 4;
            for (int c = 0; c < 4; c++)
                block[i * 4 + c] = (byte)p[k + c];
        }
    }

    private static void Expand(int c, Span<int> rgba)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgba[0] = r << 3 | r >> 2;
        rgba[1] = g << 2 | g >> 4;
        rgba[2] = b << 3 | b >> 2;
        rgba[3] = 255;
    }

    private static void Alpha(ReadOnlySpan<byte> src, byte[] block, int channel)
    {
        int a0 = src[0], a1 = src[1];
        Span<int> a = stackalloc int[8];
        a[0] = a0;
        a[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
                a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        }
        else
        {
            for (int i = 1; i < 5; i++)
                a[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            a[6] = 0;
            a[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++, bits >>= 3)
            block[i * 4 + channel] = (byte)a[(int)(bits & 7)];
    }

    private static void Store(RgbaImage image, int bx, int by, byte[] block)
    {
        int w = Math.Min(4, image.Width - bx), h = Math.Min(4, image.Height - by);
        for (int y = 0; y < h; y++)
            Array.Copy(block, y * 16, image.Pixels, ((by + y) * image.Width + bx) * 4, w * 4);
    }
}
