using System.Numerics;
using System.Text;
using Pfx.App.Model;
using Pfx.App.Textures;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;
using Pfx.Sim;
using Pfx.Textures;
using Xunit.Abstractions;

namespace Pfx.Tests;

// P7: the frame slicer, pivot and scale, the atlas builder and the video-memory readout.
public class TextureWorkspaceTests
{
    private readonly ITestOutputHelper _output;

    public TextureWorkspaceTests(ITestOutputHelper output) => _output = output;

    private static string Ptc(string anim) =>
        "EffectDef {\n Emitors {\n  EmitorDef flip {\n   MaxNum 400\n   BirthRate 40\n   Material \"fx/flip.emat\"\n   LifeTime 2\n   LifeTimeRND 1\n"
        + anim + "   EffectTime 4\n   Repeat 1\n  }\n }\n}\n";

    private const string Emat = "Particle {\n AlbedoMap \"fx/none.edds\"\n TileU 4\n TileV 3\n}\n";

    private static EffectDoc Open(string anim)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfx_p7_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "fx"));
        File.WriteAllBytes(Path.Combine(dir, "fx", "flip.emat"), Encoding.Latin1.GetBytes(Emat));
        string path = Path.Combine(dir, "flip.ptc");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(Ptc(anim)));
        var doc = EffectDoc.Open(path);
        doc.LoadMaterials(new DataRoots(new[] { dir }));
        return doc;
    }

    // The texture is not resolved (null), so the slicer never starts a decode: no UI thread in the tests.
    private static SlicerModel Slicer(EffectDoc doc)
    {
        var s = new SlicerModel(_ => null, new TextureCache());
        s.Show(doc, doc.Ptc.Emitters[0], 1);
        return s;
    }

    private static List<RenderParticle> Simulate(EmitterParams p, float seconds)
    {
        var sim = new EmitterSim(p, new SimOptions(), 0);
        for (float t = 0; t < seconds; t += 1f / 60)
            sim.Step(1f / 60, Vector3.Zero, 0, Vector3.Zero);
        var list = new List<RenderParticle>();
        sim.Render(list, Vector3.Zero, 0);
        return list;
    }

    [Theory]
    [InlineData("   AnimFPS 5\n")]
    [InlineData("   AnimFPS 5\n   AnimOnce 1\n")]
    [InlineData("   AnimFPS 3\n   RandomFrame 1\n")]
    [InlineData("   AnimFPS 3\n   RandomFrame 1\n   AnimOnce 1\n")]
    [InlineData("   AnimFPS 6\n   LifetimeByAnim 1\n")]
    [InlineData("   AnimFPS 0\n")]
    [InlineData("")]
    public void The_slicer_shows_the_frame_the_simulator_draws_at_the_same_age(string anim)
    {
        var doc = Open(anim);
        var slicer = Slicer(doc);
        Assert.Equal(12, slicer.Frames);
        var p = slicer.Params!;
        Assert.Equal(12, p.Frames);
        var particles = Simulate(p, 3.5f);
        Assert.True(particles.Count > 50);
        var starts = new HashSet<int>();
        foreach (var r in particles)
        {
            // In game (P7) AnimFPS 0, or no key, steps one tile per second.
            int steps = (int)(r.Age * (p.AnimFps > 0 ? p.AnimFps : 1));
            int start;
            if (!p.RandomFrame)
                start = 0;
            else if (p.AnimOnce && r.Frame == 11)
                continue;
            else
                start = ((r.Frame - steps) % 12 + 12) % 12;
            starts.Add(start);
            // The slicer's highlight at this age and start equals the simulator's frame...
            Assert.Equal(r.Frame, slicer.FrameAt(start, r.Age));
            // ...and both follow the in-game rule (P0): row-major from the start tile, AnimOnce holds the last tile.
            int expected = p.AnimOnce ? Math.Min(start + steps, 11) : (start + steps) % 12;
            Assert.Equal(expected, r.Frame);
        }
        if (p.RandomFrame)
            Assert.True(starts.Count >= 10, $"random starts cover the sheet: {starts.Count} of 12");
        else
            Assert.Equal(new[] { 0 }, starts.ToArray());
        if (p.LifetimeByAnim)
            Assert.All(particles, r => Assert.True(r.Age < 12f / 6 + 1e-3f, "LifetimeByAnim: life = tiles / fps = 2 s"));
        var sim = new EmitterSim(p, new SimOptions(), 0);
        for (int i = 0; i < 120; i++)
            sim.Step(1f / 60, Vector3.Zero, 0, Vector3.Zero);
        var counts = new int[12];
        sim.CountFrames(counts);
        var render = new List<RenderParticle>();
        sim.Render(render, Vector3.Zero, 0);
        for (int f = 0; f < 12; f++)
            Assert.Equal(render.Count(r => r.Frame == f), counts[f]);
    }

    [Fact]
    public void Samples_follow_the_rule_and_AnimOnce_holds_the_last_tile()
    {
        var slicer = Slicer(Open("   AnimFPS 4\n   AnimOnce 1\n"));
        Assert.Equal(11, slicer.FrameAt(0, 2.9f));
        Assert.Equal(11, slicer.FrameAt(0, 2.75f));
        Assert.Equal(10, slicer.FrameAt(0, 2.74f));
        foreach (var s in slicer.SampleParticles())
            Assert.Equal(s.Frame, Flipbook.FrameAt(slicer.Params!, s.Start, s.Age));
    }

    [Fact]
    public void Pivot_and_scale_edits_write_the_emitter_and_one_drag_is_one_undo_step()
    {
        var doc = Open("   AnimFPS 4\n");
        var slicer = Slicer(doc);
        var before = doc.Ptc.ToBytes();
        slicer.SetPivot(0.104, -0.3, true);
        slicer.SetPivot(0.2, -0.4, true);
        slicer.SetPivot(0.31, -0.52, true);
        slicer.EndDrag();
        var e = doc.Ptc.Emitters[0];
        Assert.Equal(0.31, e.Number("CenterX"));
        Assert.Equal(-0.52, e.Number("CenterY"));
        Assert.Equal(0.31, slicer.CenterX);
        slicer.SetScale(0.5, 2, true);
        slicer.SetScale(0.55, 0.8, true);
        slicer.EndDrag();
        Assert.Equal(0.55, e.Number("ScaleX"));
        Assert.Equal(0.8, e.Number("ScaleY"));
        slicer.SetPivot(3, 0, false);
        Assert.Equal(1, e.Number("CenterX"));
        doc.UndoStep();
        Assert.Equal(0.31, e.Number("CenterX"));
        doc.UndoStep();
        Assert.Null(doc.Ptc.Emitters[0].Block.FindLine("ScaleX"));
        doc.UndoStep();
        Assert.Equal(before, doc.Ptc.ToBytes());
        Assert.False(doc.Undo.CanUndo);
    }

    [Fact]
    public void Grid_edits_write_the_material_in_one_step_and_reach_the_flipbook_rule()
    {
        var doc = Open("   AnimFPS 4\n");
        var slicer = Slicer(doc);
        slicer.SetTiles(2, 8);
        var m = doc.Materials.Values.Single();
        Assert.Equal(2, m.Emat.Number("TileU"));
        Assert.Equal(8, m.Emat.Number("TileV"));
        slicer.Refresh();
        Assert.Equal(16, slicer.Params!.Frames);
        doc.UndoStep();
        Assert.Equal(4, m.Emat.Number("TileU"));
        Assert.Equal(3, m.Emat.Number("TileV"));
        Assert.False(m.Dirty);
    }

    [CorpusFact]
    public void Video_memory_readout_equals_the_stored_mip_bytes()
    {
        int files = 0;
        foreach (var root in Environment.GetEnvironmentVariable("PFX_CORPUS")!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.edds", SearchOption.AllDirectories))
            {
                var edds = EddsFile.Read(File.ReadAllBytes(path));
                if (edds.Header.BitCount == 24)
                    continue;
                long stored = edds.Levels.Sum(l => (long)l.Data.Length);
                Assert.Equal(stored, TextureSummary.Read(path).GpuBytes);
                files++;
            }
        }
        _output.WriteLine($"{files} EDDS files: readout = bytes of the stored mips");
        Assert.True(files > 100);
    }

    [Fact]
    public void Video_memory_of_a_written_edds_equals_its_levels()
    {
        var atlas = AtlasPacker.Pack(Numbered(6, 48, 40), TextureRules.DefaultMaxSide);
        var edds = EddsFile.Build(atlas.Image, EddsFormat.Rgba8, atlas.TileU, atlas.TileV, TextureRules.DefaultMaxSide, null);
        Assert.Equal(edds.Levels.Sum(l => (long)l.Data.Length), TextureMemory.Edds(EddsFormat.Rgba8, atlas.Image.Width, atlas.Image.Height));
        Assert.Equal(TextureMemory.FullChain(atlas.Image.Width, atlas.Image.Height), edds.Levels.Count);
    }

    // Frames whose every pixel carries its index, to see where each frame lands in the atlas.
    private static List<RgbaImage> Numbered(int count, int w, int h) => Enumerable.Range(0, count).Select(i =>
    {
        var f = new RgbaImage(w, h);
        for (int p = 0; p < f.Pixels.Length; p += 4)
        {
            f.Pixels[p] = (byte)(i * 15);
            f.Pixels[p + 1] = (byte)(255 - i * 15);
            f.Pixels[p + 2] = 128;
            f.Pixels[p + 3] = 255;
        }
        return f;
    }).ToList();

    [Fact]
    public void Atlas_frames_land_in_game_order_without_bleeding_across_tiles()
    {
        var frames = Numbered(16, 100, 100);
        var atlas = AtlasPacker.Pack(frames, TextureRules.DefaultMaxSide);
        Assert.Equal((4, 4), (atlas.TileU, atlas.TileV));
        Assert.Equal((512, 512), (atlas.Image.Width, atlas.Image.Height));
        var tiles = TextureTools.Split(atlas.Image, 4, 4);
        for (int i = 0; i < 16; i++)
            for (int p = 0; p < tiles[i].Pixels.Length; p += 4)
                Assert.Equal(i * 15, tiles[i].Pixels[p]);
        var sized = AtlasPacker.Pack(frames, 1024, (1024, 256));
        Assert.Equal((1024, 256), (sized.Image.Width, sized.Image.Height));
        Assert.Throws<ArgumentException>(() => AtlasPacker.Pack(frames, 512, (1024, 512)));
    }

    [Fact]
    public void Premultiply_check_counts_semi_transparent_pixels_darker_than_alpha()
    {
        var img = new RgbaImage(2, 2, new byte[] { 50, 40, 30, 100, 200, 10, 10, 100, 0, 0, 0, 0, 255, 255, 255, 255 });
        Assert.Equal((2, 1), TextureTools.PremultiplyCheck(img));
    }

    [Fact]
    public void Upscale_recombination_keeps_the_source_colour_and_mean()
    {
        var src = new RgbaImage(8, 8);
        for (int i = 0; i < src.Pixels.Length; i += 4)
        {
            src.Pixels[i] = 200;
            src.Pixels[i + 1] = 120;
            src.Pixels[i + 2] = 40;
            src.Pixels[i + 3] = (byte)(i / 4 % 8 * 30 + 15);
        }
        var bled = TextureTools.Bleed(src);
        var up = Resample.Lanczos(bled, 32, 32);
        var alpha = Resample.Lanczos(src, 32, 32);
        var grey = new RgbaImage(32, 32);
        for (int i = 0; i < grey.Pixels.Length; i += 4)
        {
            grey.Pixels[i] = grey.Pixels[i + 1] = grey.Pixels[i + 2] = alpha.Pixels[i + 3];
            grey.Pixels[i + 3] = 255;
        }
        var outFrame = CpuUpscaler.Combine(src, bled, up, grey, 8, 8);
        double Mean(RgbaImage im, int c) => Enumerable.Range(0, im.Pixels.Length / 4).Average(i => (double)im.Pixels[i * 4 + c]);
        for (int c = 0; c < 4; c++)
            Assert.InRange(Math.Abs(Mean(outFrame, c) - Mean(src, c)), 0, 3);
        var (exe, args) = CpuUpscaler.SplitCommand("py -3.12");
        Assert.Equal("py", exe);
        Assert.Equal(new[] { "-3.12" }, args);
        Assert.Equal(@"C:\Program Files\Python\python.exe", CpuUpscaler.SplitCommand("\"C:\\Program Files\\Python\\python.exe\"").Exe);
    }

    [Fact]
    public void Frame_files_sort_with_numbers_as_numbers()
    {
        var names = new[] { "f_10.png", "f_2.png", "f_1.png", "F_3.png" };
        Assert.Equal(new[] { "f_1.png", "f_2.png", "F_3.png", "f_10.png" }, names.OrderBy(n => n, NaturalOrder.Instance).ToArray());
    }

    [Fact]
    public void A_rewritten_texture_is_decoded_again_after_forget()
    {
        string path = Path.Combine(Path.GetTempPath(), "pfx_p7_forget_" + Guid.NewGuid().ToString("N") + ".png");
        var a = new RgbaImage(2, 2);
        a.Pixels[0] = 10;
        ImageFiles.Save(path, a);
        var cache = new TextureCache();
        Assert.Equal(10, cache.Decoded(path)![0].Pixels[0]);
        a.Pixels[0] = 99;
        ImageFiles.Save(path, a);
        Assert.Equal(10, cache.Decoded(path)![0].Pixels[0]);
        cache.Forget(path);
        Assert.Equal(99, cache.Decoded(path)![0].Pixels[0]);
        File.Delete(path);
    }

    [TexconvFact]
    public void A_dxt5_flipbook_from_a_sequence_writes_a_vanilla_edds()
    {
        var texconv = new Texconv(Environment.GetEnvironmentVariable("PFX_TEXCONV")!);
        var atlas = AtlasPacker.Pack(Numbered(16, 64, 64), TextureRules.DefaultMaxSide);
        var edds = EddsFile.Build(atlas.Image, EddsFormat.Dxt5, atlas.TileU, atlas.TileV, TextureRules.DefaultMaxSide, texconv, TextureTools.PackIntensity);
        var back = EddsFile.Read(edds.ToBytes());
        Assert.True(back.Header.IsEnf1);
        Assert.Equal("DXT5", back.Header.FormatName);
        Assert.Equal(TextureMemory.Edds(EddsFormat.Dxt5, 256, 256), back.Levels.Sum(l => (long)l.Data.Length));
        var top = back.Decode();
        var tiles = TextureTools.Split(top, 4, 4);
        for (int i = 0; i < 16; i++)
            Assert.InRange(Math.Abs(tiles[i].Pixels[4 * 32 * 64 + 4 * 32 + 3] - Math.Max(i * 15, 255 - i * 15)), 0, 8);
    }
}
