using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Pfx.Textures;

public sealed class EddsLevel
{
    public const string Copy = "COPY", Lz4 = "LZ4 ";

    internal EddsLevel(string tag, byte[] stored, byte[] data)
    {
        Tag = tag;
        Stored = stored;
        Data = data;
    }

    public string Tag { get; }

    // The chunk as it sits in the file; written back unchanged, so an untouched file stays byte-identical.
    public byte[] Stored { get; }

    public byte[] Data { get; }
}

// Enfusion DDS: a DDS header with "ENF1" at offset 36, a table of (tag, size) per mip from the smallest to the
// largest, then the chunks in table order. Levels here are listed largest first, like DDS mips.
public sealed class EddsFile
{
    private EddsFile(DdsHeader header, List<EddsLevel> levels)
    {
        Header = header;
        Levels = levels;
    }

    public DdsHeader Header { get; }
    public IReadOnlyList<EddsLevel> Levels { get; }

    public static bool IsEdds(ReadOnlySpan<byte> data) =>
        data.Length >= DdsHeader.BaseSize && data[..4].SequenceEqual("DDS "u8) && data.Slice(36, 4).SequenceEqual("ENF1"u8);

    public static EddsFile Read(byte[] data)
    {
        var header = DdsHeader.Parse(data);
        if (!header.IsEnf1)
            throw new InvalidDataException("No ENF1 at offset 36: a plain DDS, not an EDDS");
        int count = header.MipCount;
        int at = header.Size + count * 8;
        if (at > data.Length)
            throw new InvalidDataException("EDDS mip table cut off");
        var levels = new EddsLevel[count];
        for (int i = 0; i < count; i++)
        {
            int entry = header.Size + i * 8;
            string tag = System.Text.Encoding.Latin1.GetString(data, entry, 4);
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4)));
            if (at + size > data.Length)
                throw new InvalidDataException($"EDDS chunk {i} runs past the end of the file");
            var stored = data.AsSpan(at, size).ToArray();
            at += size;
            int level = count - 1 - i;
            byte[] raw = tag switch
            {
                EddsLevel.Copy => stored,
                EddsLevel.Lz4 => Lz4Chunk.Decode(stored),
                _ => throw new InvalidDataException($"EDDS chunk {i} has unknown tag '{tag}'"),
            };
            if (raw.Length != header.LevelBytes(level))
                throw new InvalidDataException($"EDDS level {level} has {raw.Length} bytes, {header.FormatName} " +
                                               $"{header.LevelWidth(level)}x{header.LevelHeight(level)} needs {header.LevelBytes(level)}");
            levels[level] = new EddsLevel(tag, stored, raw);
        }
        if (at != data.Length)
            throw new InvalidDataException($"EDDS has {data.Length - at} bytes after its last chunk");
        return new EddsFile(header, levels.ToList());
    }

    public byte[] ToBytes()
    {
        using var output = new MemoryStream();
        output.Write(Header.Bytes);
        Span<byte> entry = stackalloc byte[8];
        for (int i = Levels.Count - 1; i >= 0; i--)
        {
            System.Text.Encoding.Latin1.GetBytes(Levels[i].Tag, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)Levels[i].Stored.Length);
            output.Write(entry);
        }
        for (int i = Levels.Count - 1; i >= 0; i--)
            output.Write(Levels[i].Stored);
        return output.ToArray();
    }

    public RgbaImage Decode(int level = 0) => Header.Decode(level, Levels[level].Data);

    // Closest to vanilla of the levels tried on P:\graphics (P2 journal): same COPY/LZ4 choice on 2128 of 2136 chunks,
    // LZ4 chunks 98 % of vanilla's size. Vanilla's exact bytes come from another LZ4 build and are not reproduced.
    public const LZ4Level DefaultLz4 = LZ4Level.L03_HC;

    public static EddsFile Create(EddsFormat format, int width, int height, IReadOnlyList<byte[]> levels, LZ4Level lz4 = DefaultLz4)
    {
        if (levels.Count == 0)
            throw new ArgumentException("No levels");
        TextureRules.RequireWritable(format, width, height);
        var header = DdsHeader.ForEdds(format, width, height, levels.Count, levels[0].Length);
        var list = new List<EddsLevel>(levels.Count);
        for (int i = 0; i < levels.Count; i++)
        {
            if (levels[i].Length != header.LevelBytes(i))
                throw new ArgumentException($"Level {i} has {levels[i].Length} bytes, {format} {header.LevelWidth(i)}x{header.LevelHeight(i)} needs {header.LevelBytes(i)}");
            var packed = Lz4Chunk.Encode(levels[i], lz4);
            list.Add(packed == null
                ? new EddsLevel(EddsLevel.Copy, levels[i], levels[i])
                : new EddsLevel(EddsLevel.Lz4, packed, levels[i]));
        }
        return new EddsFile(header, list);
    }

    // Top image → mip chain (flipbook-aware) → DXT through texconv or BGRA → EDDS. DXT1 drops alpha first, as hires.py.
    // A per-level step runs on every level after the chain is made (hires.py packs brightness into alpha there).
    public static EddsFile Build(RgbaImage top, EddsFormat format, int tileU, int tileV, int maxSide, Texconv? texconv,
        Func<RgbaImage, RgbaImage>? perLevel = null)
    {
        TextureRules.Check(format, top.Width, top.Height, maxSide);
        var levels = TextureTools.MipChain(top, tileU, tileV);
        if (perLevel != null)
            levels = levels.Select(perLevel).ToList();
        IReadOnlyList<byte[]> raws;
        if (format == EddsFormat.Rgba8)
            raws = levels.Select(ImageFiles.ToBgra).ToList();
        else
        {
            if (texconv == null)
                throw new ArgumentNullException(nameof(texconv), $"{format} needs texconv");
            raws = texconv.Encode(levels, format);
        }
        return Create(format, top.Width, top.Height, raws);
    }
}
