using System.Text;
using K4os.Compression.LZ4;
using Pfx.Textures;

namespace Pfx.Tests;

// PFX_TEXCONV = path of texconv.exe (DirectXTex), the encoder the DXT tests call.
public sealed class TexconvFactAttribute : FactAttribute
{
    public TexconvFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PFX_TEXCONV")))
            Skip = "PFX_TEXCONV is not set";
    }
}

// Golden files come from tools/ParticleStudio/textures/make_golden.py (hires.py, the Python reference that made the approved
// anomaly textures).
public class TextureTests
{
    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    private static RgbaImage Png(string name) => ImageFiles.Load(Golden(name));

    private static int MaxDiff(RgbaImage a, RgbaImage b, int channels = 4)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        int max = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
            if (i % 4 < channels)
                max = Math.Max(max, Math.Abs(a.Pixels[i] - b.Pixels[i]));
        return max;
    }

    [Fact]
    public void Lanczos_matches_pillow_exactly()
    {
        var a = Png("in_a.png");
        foreach (var (w, h) in new[] { (24, 20), (17, 9), (96, 40), (48, 13), (5, 77) })
            Assert.Equal(0, MaxDiff(Png($"lanczos_a_{w}x{h}.png"), Resample.Lanczos(a, w, h)));
    }

    [Fact]
    public void Mip_chain_matches_hires_for_a_flipbook_and_a_whole_image()
    {
        var flip = TextureTools.MipChain(Png("in_flip_4x2.png"), 4, 2);
        Assert.Equal(8, flip.Count);
        for (int i = 0; i < flip.Count; i++)
            Assert.Equal(0, MaxDiff(Png($"mip_flip_4x2_{i:D2}.png"), flip[i]));
        var whole = TextureTools.MipChain(Png("in_a.png"), 1, 1);
        Assert.Equal(6, whole.Count);
        for (int i = 0; i < whole.Count; i++)
            Assert.Equal(0, MaxDiff(Png($"mip_a_{i:D2}.png"), whole[i]));
    }

    [Fact]
    public void Texture_tools_match_hires()
    {
        var a = Png("in_a.png");
        var b = Png("in_b.png");
        var bled = TextureTools.Bleed(a);
        Assert.Equal(0, MaxDiff(Png("bleed_a.png"), bled, 3));
        Assert.Equal(0, MaxDiffAlphaOnly(a, bled));
        Assert.Equal(0, MaxDiff(Png("contrast_a_1.6.png"), TextureTools.Contrast(a, 1.6)));
        Assert.Equal(0, MaxDiff(Png("pack_a.png"), TextureTools.PackIntensity(a)));
        Assert.True(MaxDiff(Png("match_b_to_a.png"), TextureTools.MatchMean(b, a)) <= 1);
    }

    private static int MaxDiffAlphaOnly(RgbaImage a, RgbaImage b)
    {
        int max = 0;
        for (int i = 3; i < a.Pixels.Length; i += 4)
            max = Math.Max(max, Math.Abs(a.Pixels[i] - b.Pixels[i]));
        return max;
    }

    [Fact]
    public void Bleed_keeps_covered_pixels()
    {
        var a = Png("in_a.png");
        var bled = TextureTools.Bleed(a);
        for (int i = 0; i < a.Pixels.Length; i += 4)
            if (a.Pixels[i + 3] > 0)
                Assert.True(a.Pixels.AsSpan(i, 4).SequenceEqual(bled.Pixels.AsSpan(i, 4)));
    }

    [TexconvFact]
    public void Texconv_bridge_writes_the_same_blocks_as_hires()
    {
        var texconv = new Texconv(Environment.GetEnvironmentVariable("PFX_TEXCONV")!);
        var levels = TextureTools.MipChain(Png("in_sq.png"), 1, 1);
        foreach (var format in new[] { EddsFormat.Dxt1, EddsFormat.Dxt5 })
        {
            var ours = texconv.Encode(levels, format).SelectMany(b => b).ToArray();
            var want = File.ReadAllBytes(Golden($"texconv_sq_{format.ToString().ToUpperInvariant()}.bin"));
            Assert.Equal(want, ours);
        }
    }

    [TexconvFact]
    public void Built_dxt_edds_reads_back_with_vanilla_header_values()
    {
        var texconv = new Texconv(Environment.GetEnvironmentVariable("PFX_TEXCONV")!);
        var edds = EddsFile.Build(Png("in_sq.png"), EddsFormat.Dxt5, 1, 1, TextureRules.DefaultMaxSide, texconv);
        var back = EddsFile.Read(edds.ToBytes());
        Assert.True(back.Header.IsEnf1);
        Assert.Equal(0xA1007u, back.Header.Flags);
        Assert.Equal(0x401008u, back.Header.Caps);
        Assert.Equal((uint)BlockCodec.Bytes(BlockFormat.Dxt5, 64, 64), back.Header.Pitch);
        Assert.Equal(7, back.Levels.Count);
        Assert.Equal(File.ReadAllBytes(Golden("texconv_sq_DXT5.bin")), back.Levels.SelectMany(l => l.Data).ToArray());
    }

    [Fact]
    public void Rgba8_edds_round_trips_pixels_and_allows_npot()
    {
        var a = Png("in_a.png");
        var edds = EddsFile.Build(a, EddsFormat.Rgba8, 1, 1, TextureRules.DefaultMaxSide, null);
        var back = EddsFile.Read(edds.ToBytes());
        Assert.Equal(0x2100Fu, back.Header.Flags);
        Assert.Equal((uint)48 * 4, back.Header.Pitch);
        Assert.Equal(0, MaxDiff(a, back.Decode()));
        Assert.Equal(edds.ToBytes(), back.ToBytes());
    }

    [Fact]
    public void Npot_dxt_and_oversized_textures_are_refused()
    {
        Assert.Throws<ArgumentException>(() => EddsFile.Create(EddsFormat.Dxt1, 96, 64, new[] { new byte[BlockCodec.Bytes(BlockFormat.Dxt1, 96, 64)] }));
        Assert.Throws<ArgumentException>(() => TextureRules.Check(EddsFormat.Rgba8, 4096, 16, TextureRules.DefaultMaxSide));
        TextureRules.Check(EddsFormat.Rgba8, 96, 64, TextureRules.DefaultMaxSide);
    }

    [Fact]
    public void Lz4_chunks_chain_blocks_and_follow_the_80_percent_rule()
    {
        var rng = new Random(5);
        var noise = new byte[70000];
        rng.NextBytes(noise);
        // Period 30000: the second 64 KB block can only compress by matching into the first one.
        var repeated = new byte[200000];
        for (int i = 0; i < repeated.Length; i++)
            repeated[i] = noise[i % 30000];
        var chunk = Lz4Chunk.Encode(repeated, LZ4Level.L03_HC);
        Assert.NotNull(chunk);
        Assert.Equal(repeated, Lz4Chunk.Decode(chunk));
        Assert.True(chunk!.Length < repeated.Length / 2);
        Assert.Null(Lz4Chunk.Encode(noise, LZ4Level.L03_HC));
        Assert.Null(Lz4Chunk.Encode(Array.Empty<byte>(), LZ4Level.L03_HC));
    }

    [Fact]
    public void Dxt_blocks_decode_with_integer_interpolation()
    {
        // c0 = pure red (0xF800) > c1 = pure blue (0x001F): indices 0,1,2,3 in the first row.
        byte[] four = { 0x00, 0xF8, 0x1F, 0x00, 0b11100100, 0, 0, 0 };
        var img = BlockCodec.Decode(BlockFormat.Dxt1, 4, 4, four);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255, 170, 0, 85, 255, 85, 0, 170, 255 }, img.Pixels[..16]);
        // c0 <= c1: index 2 = average, index 3 = transparent black.
        byte[] three = { 0x1F, 0x00, 0x00, 0xF8, 0b11100100, 0, 0, 0 };
        img = BlockCodec.Decode(BlockFormat.Dxt1, 4, 4, three);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255, 127, 0, 127, 255, 0, 0, 0, 0 }, img.Pixels[..16]);
        // DXT5 alpha a0 = 255 > a1 = 0: index 2 = (6*255)/7 = 218; first texel uses index 2.
        byte[] dxt5 = { 255, 0, 2, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };
        img = BlockCodec.Decode(BlockFormat.Dxt5, 4, 4, dxt5);
        Assert.Equal(218, img.Pixels[3]);
        Assert.Equal(255, img.Pixels[7]);
    }

    [Fact]
    public void Lzo_literals_matches_overlaps_and_end_marker()
    {
        var abcd = Encoding.ASCII.GetBytes("abcd");
        byte[] stream = { 21, abcd[0], abcd[1], abcd[2], abcd[3], 0x6C, 0x00, 0x11, 0x00, 0x00 };
        Assert.Equal("abcdabcd", Encoding.ASCII.GetString(Lzo1x.Decompress(stream, 8, out int used)));
        Assert.Equal(stream.Length, used);
        byte[] run = { 18, (byte)'a', 0xC0, 0x00, 0x11, 0x00, 0x00 };
        Assert.Equal("aaaaaaaa", Encoding.ASCII.GetString(Lzo1x.Decompress(run, 8, out used)));
        Assert.Throws<InvalidDataException>(() => Lzo1x.Decompress(run, 9, out _));
    }

    [Fact]
    public void Paa_swizzle_maps_stored_channels_back()
    {
        // DXT1 4x4, all texels colour 0 = pure red; swizzle A = inverted R (5), R = full (8), G = G (2), B = B (3).
        using var s = new MemoryStream();
        s.Write(new byte[] { 0x01, 0xFF });
        s.Write("GGATZIWS"u8);
        s.Write(BitConverter.GetBytes(4));
        s.Write(new byte[] { 5, 8, 2, 3 });
        s.Write(new byte[] { 0, 0 });
        s.Write(new byte[] { 4, 0, 4, 0, 8, 0, 0 });
        s.Write(new byte[] { 0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0 });
        s.Write(new byte[] { 0, 0, 0, 0, 0, 0 });
        var paa = PaaFile.Read(s.ToArray());
        Assert.Equal(2, paa.TailBytes);
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, paa.Decode().Pixels[..4]);
    }

    [Fact]
    public void Tga_and_png_round_trip()
    {
        var a = Png("in_a.png");
        Assert.Equal(0, MaxDiff(a, ImageFiles.ReadTga(ImageFiles.WriteTga(a))));
        Assert.Equal(0, MaxDiff(a, ImageFiles.ReadPng(ImageFiles.WritePng(a))));
    }

    [Fact]
    public void Atlas_packer_keeps_every_frame_and_returns_tiles()
    {
        var frames = Enumerable.Range(0, 8).Select(i => Png("in_a.png").Crop(0, 0, 32, 32)).ToList();
        var atlas = AtlasPacker.Pack(frames, TextureRules.DefaultMaxSide);
        Assert.Equal((4, 2, 128, 64), (atlas.TileU, atlas.TileV, atlas.Image.Width, atlas.Image.Height));
        Assert.Equal(0, MaxDiff(frames[5], atlas.Image.Crop(32, 32, 32, 32)));
        var six = AtlasPacker.Pack(frames.Take(6).ToList(), TextureRules.DefaultMaxSide);
        Assert.Equal((3, 2, 128, 64), (six.TileU, six.TileV, six.Image.Width, six.Image.Height));
        Assert.Equal((2, 1), AtlasPacker.Grid(2, 32, 32));
        Assert.Equal((7, 1), AtlasPacker.Grid(7, 32, 32));
        Assert.Equal((2, 8), AtlasPacker.Grid(16, 128, 32));
    }
}
