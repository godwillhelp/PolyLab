using System.Buffers.Binary;
using System.Text;

namespace Pfx.Textures;

public enum BlockFormat
{
    None,
    Dxt1,
    Dxt5,
    Bc4,
    Bc5,
}

// The formats the EDDS writer produces: what vanilla particle textures use.
public enum EddsFormat
{
    Dxt1,
    Dxt5,
    Rgba8,
}

// DDS header (128 bytes, 148 with a DX10 extension). The original bytes are kept so an unchanged file is written back
// as it was, including fields this class does not model.
public sealed class DdsHeader
{
    public const int BaseSize = 128;
    private const int Dx10Size = 20;
    private const uint PfAlphaPixels = 0x1, PfAlpha = 0x2, PfFourCc = 0x4, PfRgb = 0x40, PfLuminance = 0x20000;
    private const int DxgiBc4Unorm = 80, DxgiBc5Unorm = 83;
    private const uint FlagDepth = 0x800000;

    // Values of every vanilla EDDS of that format (P:\graphics, 2026-10-11 and 2026-10-14 surveys, memory
    // dayz-ptc-emitter-schema): DXT flags 0xA1007, RGBA8 flags 0x2100F, caps 0x401008.
    private const uint EddsBlockFlags = 0xA1007, EddsRgbaFlags = 0x2100F, EddsCaps = 0x401008;

    private DdsHeader(byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }
    public int Size => Bytes.Length;

    public uint Flags => U32(8);
    public int Height => (int)U32(12);
    public int Width => (int)U32(16);
    public uint Pitch => U32(20);
    public int MipCount => Math.Max(1, (int)U32(28));
    public bool IsEnf1 => Bytes.AsSpan(36, 4).SequenceEqual("ENF1"u8);
    public uint PfFlags => U32(80);
    public string FourCc => Encoding.Latin1.GetString(Bytes, 84, 4);
    public int BitCount => (int)U32(88);
    public uint RMask => U32(92);
    public uint GMask => U32(96);
    public uint BMask => U32(100);
    public uint AMask => U32(104);
    public uint Caps => U32(108);
    public int DxgiFormat => Size > BaseSize ? (int)U32(128) : 0;

    public BlockFormat Block
    {
        get
        {
            if ((PfFlags & PfFourCc) == 0)
                return BlockFormat.None;
            return FourCc switch
            {
                "DXT1" => BlockFormat.Dxt1,
                "DXT5" => BlockFormat.Dxt5,
                "DX10" when DxgiFormat == DxgiBc4Unorm => BlockFormat.Bc4,
                "DX10" when DxgiFormat == DxgiBc5Unorm => BlockFormat.Bc5,
                "DX10" => throw new NotSupportedException($"DX10 DXGI format {DxgiFormat}"),
                _ => throw new NotSupportedException($"FourCC '{FourCc.TrimEnd('\0')}'"),
            };
        }
    }

    public string FormatName => Block switch
    {
        BlockFormat.None => $"{BitCount}-bit masks {RMask:x}/{GMask:x}/{BMask:x}/{AMask:x}",
        var b => b.ToString().ToUpperInvariant(),
    };

    public int LevelWidth(int level) => Math.Max(1, Width >> level);
    public int LevelHeight(int level) => Math.Max(1, Height >> level);

    // Volume textures (flag DDSD_DEPTH; vanilla has two 3D colour LUTs) store each level as depth slices one after
    // another; the slice count halves with each level like the sides.
    public int Depth => (Flags & FlagDepth) != 0 ? Math.Max(1, (int)U32(24)) : 1;
    public int LevelDepth(int level) => Math.Max(1, Depth >> level);

    public int LevelBytes(int level) => checked(SliceBytes(level) * LevelDepth(level));

    private int SliceBytes(int level) => Block == BlockFormat.None
        ? checked(LevelWidth(level) * LevelHeight(level) * (BitCount / 8))
        : BlockCodec.Bytes(Block, LevelWidth(level), LevelHeight(level));

    public static DdsHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BaseSize || !data[..4].SequenceEqual("DDS "u8))
            throw new InvalidDataException("Not a DDS file");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124)
            throw new InvalidDataException("DDS header size is not 124");
        bool dx10 = data.Slice(84, 4).SequenceEqual("DX10"u8);
        int size = dx10 ? BaseSize + Dx10Size : BaseSize;
        if (data.Length < size)
            throw new InvalidDataException("DDS DX10 header cut off");
        var header = new DdsHeader(data[..size].ToArray());
        if (header.Block == BlockFormat.None && (header.BitCount is not (8 or 16 or 24 or 32) || header.ChannelMasks().All(m => m == 0)))
            throw new NotSupportedException($"Uncompressed DDS with {header.BitCount} bits and no channel masks");
        return header;
    }

    public static DdsHeader ForEdds(EddsFormat format, int width, int height, int mipCount, int topLevelBytes)
    {
        var b = new byte[BaseSize];
        "DDS "u8.CopyTo(b);
        Put(b, 4, 124);
        bool rgba = format == EddsFormat.Rgba8;
        Put(b, 8, rgba ? EddsRgbaFlags : EddsBlockFlags);
        Put(b, 12, (uint)height);
        Put(b, 16, (uint)width);
        Put(b, 20, rgba ? (uint)width * 4 : (uint)topLevelBytes);
        Put(b, 28, (uint)mipCount);
        "ENF1"u8.CopyTo(b.AsSpan(36));
        Put(b, 76, 32);
        if (rgba)
        {
            Put(b, 80, PfRgb | PfAlphaPixels);
            Put(b, 88, 32);
            Put(b, 92, 0x00FF0000);
            Put(b, 96, 0x0000FF00);
            Put(b, 100, 0x000000FF);
            Put(b, 104, 0xFF000000);
        }
        else
        {
            Put(b, 80, PfFourCc);
            Encoding.Latin1.GetBytes(format == EddsFormat.Dxt1 ? "DXT1" : "DXT5").CopyTo(b, 84);
        }
        Put(b, 108, EddsCaps);
        return new DdsHeader(b);
    }

    // Plain uncompressed BGRA DDS, one level: the input handed to texconv.
    public static DdsHeader ForPlainBgra(int width, int height)
    {
        var b = new byte[BaseSize];
        "DDS "u8.CopyTo(b);
        Put(b, 4, 124);
        Put(b, 8, 0x100F);
        Put(b, 12, (uint)height);
        Put(b, 16, (uint)width);
        Put(b, 20, (uint)width * 4);
        Put(b, 76, 32);
        Put(b, 80, PfRgb | PfAlphaPixels);
        Put(b, 88, 32);
        Put(b, 92, 0x00FF0000);
        Put(b, 96, 0x0000FF00);
        Put(b, 100, 0x000000FF);
        Put(b, 104, 0xFF000000);
        Put(b, 108, 0x1000);
        return new DdsHeader(b);
    }

    // A volume level comes back as its slices stacked top to bottom.
    public RgbaImage Decode(int level, ReadOnlySpan<byte> data)
    {
        int w = LevelWidth(level), h = LevelHeight(level), depth = LevelDepth(level), slice = SliceBytes(level);
        if (data.Length != LevelBytes(level))
            throw new InvalidDataException($"Level {level} ({w}x{h}x{depth} {FormatName}) has {data.Length} bytes, expected {LevelBytes(level)}");
        var image = new RgbaImage(w, h * depth);
        for (int z = 0; z < depth; z++)
        {
            var part = data.Slice(z * slice, slice);
            image.Paste(Block == BlockFormat.None ? DecodeMasked(w, h, part) : BlockCodec.Decode(Block, w, h, part), 0, z * h);
        }
        return image;
    }

    private uint[] ChannelMasks() => new[] { RMask, GMask, BMask, AMask };

    private RgbaImage DecodeMasked(int w, int h, ReadOnlySpan<byte> data)
    {
        int bytes = BitCount / 8;
        bool luminance = (PfFlags & PfLuminance) != 0;
        bool alphaOnly = (PfFlags & (PfAlpha | PfRgb | PfLuminance)) == PfAlpha;
        var masks = ChannelMasks();
        if (alphaOnly)
            masks = new uint[] { 0, 0, 0, masks[3] != 0 ? masks[3] : masks[0] };
        var image = new RgbaImage(w, h);
        var px = image.Pixels;
        for (int i = 0, o = 0; i < data.Length; i += bytes, o += 4)
        {
            uint v = 0;
            for (int k = 0; k < bytes; k++)
                v |= (uint)data[i + k] << (8 * k);
            for (int c = 0; c < 4; c++)
                px[o + c] = masks[c] == 0 ? (byte)(c == 3 ? 255 : 0) : Channel(v, masks[c]);
            if (luminance)
                px[o + 1] = px[o + 2] = px[o];
        }
        return image;
    }

    private static byte Channel(uint value, uint mask)
    {
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint v = (value & mask) >> shift;
        return max == 255 ? (byte)v : (byte)((v * 255 + max / 2) / max);
    }

    private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at));

    private static void Put(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
}
