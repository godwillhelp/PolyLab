using System.Buffers.Binary;
using System.Numerics;

namespace Pfx.Textures;

// DXT1 (BC1), DXT5 (BC3), BC4, BC5 and BC7 decoding on the CPU. Interpolation uses integer division like Pillow's BcnDecode
// (the decoder behind hires.py), so decodes compare exactly with the Python reference. BC4 lands in red, BC5 in red
// and green; the other channels stay 0 and alpha 255. BC7 (vanilla 1.30 particle textures, DXGI 98 / 99) decodes to
// the same bytes as texconv; sRGB or not, the stored bytes are returned unconverted.
public static class BlockCodec
{
    // BC7 tables of the BPTC specification (ARB_texture_compression_bptc), in the layout of Pillow's BcnDecode.c.
    private static readonly (int Subsets, int PartitionBits, int RotationBits, int SelectorBits, int ColorBits, int AlphaBits,
        int EndpointP, int SharedP, int IndexBits, int Index2Bits)[] Bc7Modes =
    {
        (3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        (2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        (3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        (2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        (1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        (1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        (1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        (2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    };

    // Subset of each texel per partition: one bit per texel for two subsets, two bits for three.
    private static readonly ushort[] Bc7Partitions2 =
    {
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec,
        0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000, 0xf710, 0x008e, 0x7100, 0x08ce,
        0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0,
        0x718e, 0x399c, 0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
        0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660, 0x0272, 0x04e4,
        0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718,
        0xccf0, 0x0fcc, 0x7744, 0xee22,
    };

    private static readonly uint[] Bc7Partitions3 =
    {
        0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0,
        0x5a5a5050, 0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4,
        0xa9a59450, 0x2a0a4250, 0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454,
        0x6a6a4040, 0xa4a45000, 0x1a1a0500, 0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400,
        0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200, 0xa9a58000, 0x5090a0a8, 0xa8a09050,
        0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50, 0x500aa550, 0xaaaa4444,
        0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600, 0xaa444444,
        0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
        0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44,
        0x2a4a5254,
    };

    // Anchor texels (their index has one bit less) of subset 1 for two subsets, of subsets 1 and 2 for three.
    private static readonly byte[] Bc7Anchor2 =
    {
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    };

    private static readonly byte[] Bc7Anchor3A =
    {
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    };

    private static readonly byte[] Bc7Anchor3B =
    {
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    };

    // Interpolation weights by index bit count.
    private static readonly int[][] Bc7Weights =
    {
        [], [], [0, 21, 43, 64], [0, 9, 18, 27, 37, 46, 55, 64], [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64],
    };

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
                    case BlockFormat.Bc7:
                        Bc7(src, block);
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

    // The mode is the lowest set bit of the first byte; a block without one is reserved and decodes to 0 in every
    // channel, as texconv does (Pillow gives opaque black instead).
    private static void Bc7(ReadOnlySpan<byte> src, byte[] block)
    {
        if (src[0] == 0)
        {
            Array.Clear(block);
            return;
        }
        int mode = BitOperations.TrailingZeroCount(src[0]);
        var m = Bc7Modes[mode];
        var bits = BinaryPrimitives.ReadUInt128LittleEndian(src);
        int at = mode + 1;
        int Read(int count)
        {
            int value = (int)((bits >> at) & (UInt128)((1 << count) - 1));
            at += count;
            return value;
        }

        int partition = Read(m.PartitionBits), rotation = Read(m.RotationBits), selector = Read(m.SelectorBits);
        int ends = m.Subsets * 2;
        Span<int> e = stackalloc int[24];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < ends; i++)
                e[i * 4 + c] = Read(m.ColorBits);
        for (int i = 0; i < ends; i++)
            e[i * 4 + 3] = m.AlphaBits > 0 ? Read(m.AlphaBits) : 255;

        int colorBits = m.ColorBits, alphaBits = m.AlphaBits;
        if (m.EndpointP + m.SharedP > 0)
        {
            colorBits++;
            if (alphaBits > 0)
                alphaBits++;
            int p = 0;
            for (int i = 0; i < ends; i++)
            {
                if (m.EndpointP > 0 || i % 2 == 0)
                    p = Read(1);
                for (int c = 0; c < (alphaBits > 0 ? 4 : 3); c++)
                    e[i * 4 + c] = e[i * 4 + c] << 1 | p;
            }
        }
        for (int i = 0; i < ends; i++)
        {
            for (int c = 0; c < 3; c++)
                e[i * 4 + c] = Unquantize(e[i * 4 + c], colorBits);
            if (alphaBits > 0)
                e[i * 4 + 3] = Unquantize(e[i * 4 + 3], alphaBits);
        }

        int colorAt = at, alphaAt = at + 16 * m.IndexBits - m.Subsets;
        var cw = Bc7Weights[m.IndexBits];
        var aw = Bc7Weights[m.Index2Bits > 0 ? m.Index2Bits : m.IndexBits];
        for (int i = 0; i < 16; i++)
        {
            int s = m.Subsets switch
            {
                2 => Bc7Partitions2[partition] >> i & 1,
                3 => (int)(Bc7Partitions3[partition] >> 2 * i & 3),
                _ => 0,
            };
            bool anchor = i == 0 || m.Subsets == 2 && i == Bc7Anchor2[partition]
                          || m.Subsets == 3 && (i == Bc7Anchor3A[partition] || i == Bc7Anchor3B[partition]);
            at = colorAt;
            int i0 = Read(anchor ? m.IndexBits - 1 : m.IndexBits);
            colorAt = at;
            int wc = cw[i0], wa = wc;
            if (m.Index2Bits > 0)
            {
                at = alphaAt;
                int i1 = Read(i == 0 ? m.Index2Bits - 1 : m.Index2Bits);
                alphaAt = at;
                (wc, wa) = selector == 0 ? (cw[i0], aw[i1]) : (aw[i1], cw[i0]);
            }
            for (int c = 0; c < 4; c++)
            {
                int w = c == 3 ? wa : wc;
                block[i * 4 + c] = (byte)(((64 - w) * e[s * 8 + c] + w * e[s * 8 + 4 + c] + 32) >> 6);
            }
            if (rotation > 0)
                (block[i * 4 + 3], block[i * 4 + rotation - 1]) = (block[i * 4 + rotation - 1], block[i * 4 + 3]);
        }
    }

    // Top bits repeated below: n bits to 8.
    private static int Unquantize(int v, int bits)
    {
        v <<= 8 - bits;
        return v | v >> bits;
    }

    private static void Store(RgbaImage image, int bx, int by, byte[] block)
    {
        int w = Math.Min(4, image.Width - bx), h = Math.Min(4, image.Height - by);
        for (int y = 0; y < h; y++)
            Array.Copy(block, y * 16, image.Pixels, ((by + y) * image.Width + bx) * 4, w * 4);
    }
}
