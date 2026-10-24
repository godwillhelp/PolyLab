namespace Pfx.Textures;

// Video memory of a texture as the game keeps it: block formats stay compressed (R0 saw BC1 / BC3 albedo views),
// uncompressed ones keep their bytes per pixel; every stored mip level counts. Worked out from the header (format,
// sides, mip count), not from the stored chunks, so a test can hold it against the bytes of written levels.
public static class TextureMemory
{
    public static long Level(BlockFormat block, int bitsPerPixel, int width, int height) => block == BlockFormat.None
        ? (long)width * height * Math.Max(1, bitsPerPixel / 8)
        : (long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * BlockCodec.BlockBytes(block);

    public static long Chain(BlockFormat block, int bitsPerPixel, int width, int height, int mips)
    {
        long sum = 0;
        for (int i = 0; i < mips; i++)
            sum += Level(block, bitsPerPixel, Math.Max(1, width >> i), Math.Max(1, height >> i));
        return sum;
    }

    // What an EDDS written by EddsFile.Build takes: the full chain down to 1 x 1.
    public static long Edds(EddsFormat format, int width, int height) =>
        Chain(Block(format), format == EddsFormat.Rgba8 ? 32 : 0, width, height, FullChain(width, height));

    public static int FullChain(int width, int height) => 1 + (int)Math.Log2(Math.Max(width, height));

    public static BlockFormat Block(EddsFormat format) => format switch
    {
        EddsFormat.Dxt1 => BlockFormat.Dxt1,
        EddsFormat.Dxt5 => BlockFormat.Dxt5,
        _ => BlockFormat.None,
    };
}

// What the texture panel shows about a file: kind, format, sides, mips (and how EDDS stores them), sizes on disk and in
// video memory. Reads the file once; decoding pixels is left to the caller.
public sealed record TextureSummary
{
    public required string Kind { get; init; }
    public required string Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Mips { get; init; }
    public bool Enf1 { get; init; }
    public bool Compressed { get; init; }
    public int Lz4Levels { get; init; }
    public int CopyLevels { get; init; }
    public long DiskBytes { get; init; }
    public long GpuBytes { get; init; }
    // 24-bit layouts have no D3D11 format: the game must widen them; how is not measured (taken as 32 bits).
    public bool GpuGuessed { get; init; }

    public bool PowerOfTwo => TextureRules.IsPowerOfTwo(Width) && TextureRules.IsPowerOfTwo(Height);

    public static TextureSummary Read(string path)
    {
        var data = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".edds" or ".dds")
        {
            var h = DdsHeader.Parse(data);
            int lz4 = 0, copy = 0;
            if (h.IsEnf1)
            {
                var edds = EddsFile.Read(data);
                lz4 = edds.Levels.Count(l => l.Tag == EddsLevel.Lz4);
                copy = edds.Levels.Count - lz4;
            }
            int bits = h.Block == BlockFormat.None ? h.BitCount : 0;
            bool widened = bits == 24;
            return new TextureSummary
            {
                Kind = h.IsEnf1 ? "EDDS" : "DDS",
                Format = h.Block == BlockFormat.None ? MaskName(h) : h.FormatName,
                Width = h.Width,
                Height = h.Height,
                Mips = h.MipCount,
                Enf1 = h.IsEnf1,
                Compressed = h.Block != BlockFormat.None,
                Lz4Levels = lz4,
                CopyLevels = copy,
                DiskBytes = data.Length,
                // Volume LUTs (two vanilla .dds) halve their depth per level as well: their header's own sizes count.
                GpuBytes = h.Depth > 1
                    ? Enumerable.Range(0, h.MipCount).Sum(i => (long)h.LevelBytes(i))
                    : TextureMemory.Chain(h.Block, widened ? 32 : bits, h.Width, h.Height, h.MipCount),
                GpuGuessed = widened,
            };
        }
        if (ext == ".paa")
        {
            var paa = PaaFile.Read(data);
            return new TextureSummary
            {
                Kind = "PAA",
                Format = paa.Format.ToString().ToUpperInvariant(),
                Width = paa.Levels[0].Width,
                Height = paa.Levels[0].Height,
                Mips = paa.Levels.Count,
                Compressed = true,
                DiskBytes = data.Length,
                GpuBytes = paa.Levels.Sum(l => TextureMemory.Level(paa.Format, 0, l.Width, l.Height)),
            };
        }
        var image = ImageFiles.Load(path);
        return new TextureSummary
        {
            Kind = ext.TrimStart('.').ToUpperInvariant(),
            Format = "RGBA8",
            Width = image.Width,
            Height = image.Height,
            Mips = 1,
            DiskBytes = data.Length,
            GpuBytes = TextureMemory.Level(BlockFormat.None, 32, image.Width, image.Height),
        };
    }

    private static string MaskName(DdsHeader h) => (h.BitCount, h.AMask != 0) switch
    {
        (32, true) => "RGBA8",
        (32, false) => "RGBX8",
        (24, _) => "RGB8",
        (8, true) => "A8",
        (8, false) => "L8",
        var (bits, _) => $"{bits}-bit",
    };
}
