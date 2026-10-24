using System.Buffers.Binary;

namespace Pfx.Textures;

public sealed record PaaLevel(int Width, int Height, bool Lzo, byte[] Data);

// Bohemia PAA (DXT1 / DXT5 only, the types found in the corpus): u16 type, "GGAT" tags (name reversed, u32 length,
// data), u16 palette count + 3 bytes each, then levels largest first: u16 width (bit 0x8000 = LZO-compressed data),
// u16 height, u24 size, data; a level of width 0 and height 0 ends the list. A "ZIWS" (SWIZ) tag says how the stored
// channels map back to the picture: one byte per target channel in the order A, R, G, B; 0-3 = stored A, R, G, B,
// 4-7 = the same inverted, 8 = full. Seen in the corpus: 05040203 on DXT5 normal maps, 08080203 / 08080208 on DXT1
// _smdi / _as maps; with it applied, decodes match ImageToPAA.exe within its DXT rounding.
public sealed class PaaFile
{
    private const ushort TypeDxt1 = 0xFF01, TypeDxt5 = 0xFF05;
    private const int LzoFlag = 0x8000;

    private PaaFile(BlockFormat format, List<string> tags, byte[]? swizzle, List<PaaLevel> levels, int tail)
    {
        Format = format;
        Swizzle = swizzle;
        Tags = tags;
        Levels = levels;
        TailBytes = tail;
    }

    public BlockFormat Format { get; }
    public IReadOnlyList<string> Tags { get; }
    public byte[]? Swizzle { get; }
    public IReadOnlyList<PaaLevel> Levels { get; }
    public int TailBytes { get; }

    public static PaaFile Read(byte[] data)
    {
        var span = data.AsSpan();
        if (span.Length < 2)
            throw new InvalidDataException("PAA shorter than its type field");
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var format = type switch
        {
            TypeDxt1 => BlockFormat.Dxt1,
            TypeDxt5 => BlockFormat.Dxt5,
            _ => throw new NotSupportedException($"PAA type 0x{type:X4}"),
        };
        int at = 2;
        var tags = new List<string>();
        byte[]? swizzle = null;
        while (at + 12 <= span.Length && span.Slice(at, 4).SequenceEqual("GGAT"u8))
        {
            string name = System.Text.Encoding.Latin1.GetString(data, at + 4, 4);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 8)..]));
            if (at + 12 + length > span.Length)
                throw new InvalidDataException($"PAA tag {name} runs past the end of the file");
            if (name == "ZIWS")
            {
                if (length != 4 || span.Slice(at + 12, 4).ToArray().Any(c => c > 8))
                    throw new NotSupportedException($"PAA swizzle {Convert.ToHexString(span.Slice(at + 12, length))}");
                swizzle = span.Slice(at + 12, 4).ToArray();
            }
            tags.Add(name);
            at += 12 + length;
        }
        if (at + 2 > span.Length)
            throw new InvalidDataException("PAA palette field missing");
        at += 2 + 3 * BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
        var levels = new List<PaaLevel>();
        while (true)
        {
            if (at + 4 > span.Length)
                throw new InvalidDataException("PAA level list has no end marker");
            int rawWidth = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(span[(at + 2)..]);
            if (rawWidth == 0 && height == 0)
            {
                at += 4;
                break;
            }
            if (at + 7 > span.Length)
                throw new InvalidDataException("PAA level header cut off");
            int size = span[at + 4] | span[at + 5] << 8 | span[at + 6] << 16;
            at += 7;
            if (at + size > span.Length)
                throw new InvalidDataException($"PAA level {levels.Count} runs past the end of the file");
            bool lzo = (rawWidth & LzoFlag) != 0;
            int width = rawWidth & ~LzoFlag;
            int expected = BlockCodec.Bytes(format, width, height);
            var stored = span.Slice(at, size);
            byte[] payload;
            if (lzo)
            {
                payload = Lzo1x.Decompress(stored, expected, out int used);
                if (used != size)
                    throw new InvalidDataException($"PAA level {levels.Count}: LZO used {used} of {size} bytes");
            }
            else if (size == expected)
                payload = stored.ToArray();
            else
                throw new InvalidDataException($"PAA level {levels.Count} {width}x{height} has {size} bytes, expected {expected}");
            levels.Add(new PaaLevel(width, height, lzo, payload));
            at += size;
        }
        return new PaaFile(format, tags, swizzle, levels, span.Length - at);
    }

    public RgbaImage Decode(int level = 0)
    {
        var image = BlockCodec.Decode(Format, Levels[level].Width, Levels[level].Height, Levels[level].Data);
        if (Swizzle != null)
            Unswizzle(image.Pixels, Swizzle);
        return image;
    }

    private static void Unswizzle(byte[] p, byte[] swizzle)
    {
        Span<int> source = stackalloc int[4];
        Span<byte> stored = stackalloc byte[4];
        // Tag order A, R, G, B; pixel order R, G, B, A.
        for (int t = 0; t < 4; t++)
            source[(t + 3) % 4] = swizzle[t];
        for (int i = 0; i < p.Length; i += 4)
        {
            stored[0] = p[i + 3];
            stored[1] = p[i];
            stored[2] = p[i + 1];
            stored[3] = p[i + 2];
            for (int c = 0; c < 4; c++)
            {
                int code = source[c];
                p[i + c] = code == 8 ? (byte)255 : code >= 4 ? (byte)(255 - stored[code - 4]) : stored[code];
            }
        }
    }
}
