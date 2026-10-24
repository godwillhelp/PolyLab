using System.Text;
using Pfx.App.Lint;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Textures;

namespace Pfx.Tests;

// P12: every lint rule fires on a crafted bad file, and a plain emitter fires none.
public sealed class LintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfx_lint_" + Guid.NewGuid().ToString("N"));

    private const string Good = "MaxNum 100\n   BirthRate 5\n   LifeTime 2\n   SizeMultiplier 0.5\n   Material \"m/alpha.emat\"";

    public LintTests()
    {
        Write("m/alpha.emat", "ParticleSprite {\n Softness 2\n}\n");
        Write("m/dust.emat", "ParticleSprite {\n Softness 2\n}\n");
        Write("m/glow.emat", "ParticleSprite {\n BlendMode Additive_AlphaModulate\n Softness 2\n}\n");
        Write("m/smoke_add.emat", "ParticleSprite {\n BlendMode Additive_AlphaModulate\n Softness 2\n}\n");
        Write("m/streak.emat", "ParticleStreak {\n Softness 2\n}\n");
        Write("m/hard.emat", "ParticleSprite {\n ZEnable 0\n}\n");
        Write("m/tex_missing.emat", "ParticleSprite {\n AlbedoMap \"t/none.edds\"\n Softness 2\n}\n");
        Write("m/npot.emat", "ParticleSprite {\n AlbedoMap \"t/npot.dds\"\n Softness 2\n}\n");
        Write("m/plain.emat", "ParticleSprite {\n AlbedoMap \"t/plain.edds\"\n Softness 2\n}\n");
        Write("m/big.emat", "ParticleSprite {\n AlbedoMap \"t/big.dds\"\n Softness 2\n}\n");
        Write("m/lit.emat", "ParticleSprite {\n NormalMap \"t/normal.dds\"\n Softness 2\n}\n");
        WriteBytes("t/npot.dds", DxtHeader(100, 64));
        WriteBytes("t/big.dds", DxtHeader(4096, 4096));
        WriteBytes("t/normal.dds", DxtHeader(64, 64));
        WriteBytes("t/plain.edds", DdsFile.WritePlainBgra(new RgbaImage(4, 4, new byte[64])));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private void Write(string rel, string text) => WriteBytes(rel, Encoding.Latin1.GetBytes(text));

    private void WriteBytes(string rel, byte[] bytes)
    {
        string path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // A plain DDS header of a DXT1 texture (no ENF1 mark, no pixels): all the summary reads.
    private static byte[] DxtHeader(int width, int height)
    {
        var bytes = DdsHeader.ForEdds(EddsFormat.Dxt1, width, height, 1, 8).Bytes.ToArray();
        Array.Clear(bytes, 36, 4);
        return bytes;
    }

    private static string Emitter(string name, string keys) => $"  EmitorDef {name} {{\n   {keys}\n  }}\n";

    private static string Ptc(params string[] emitters) => "EffectDef {\n Emitors {\n" + string.Concat(emitters) + " }\n}\n";

    private LintPart Part(string file, string text, PartEntry? entry = null) =>
        new(Path.Combine(_dir, file), file, Encoding.Latin1.GetBytes(text), entry);

    private LintReport Lint(IEnumerable<LintPart> parts, ProjectFile? project = null, string sky = "dusk") =>
        Linter.Run(new LintInput(parts.ToList(), new ExportSnapshot(Array.Empty<EffectDoc>(), new[] { _dir }), project, project != null ? _dir : null, sky));

    private LintReport Lint(string keys, PartEntry? entry = null, string sky = "dusk") =>
        Lint(new[] { Part("a.ptc", Ptc(Emitter("a", keys)), entry) }, sky: sky);

    private static void Fires(LintRule rule, LintReport report, string? key = null)
    {
        var hit = report.Issues.Where(i => i.Rule == rule).ToList();
        Assert.True(hit.Count > 0, $"{rule.Id} did not fire; got {string.Join(", ", report.Issues.Select(i => i.Rule.Id))}");
        if (key != null)
            Assert.Equal(key, hit[0].Key);
    }

    private static PartEntry Spinning(float spin = 30) => new() { Id = "a", Path = "a.ptc", Spin = spin };

    [Fact]
    public void A_plain_emitter_is_clean() => Assert.Empty(Lint(Good).Issues);

    [Fact]
    public void Every_rule_has_a_test_of_its_name()
    {
        var tests = typeof(LintTests).GetMethods().Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0).Select(m => m.Name).ToHashSet();
        Assert.Equal(Linter.Rules.Count, Linter.Rules.Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain(Linter.Rules, r => !tests.Contains(r.Id));
    }

    [Fact] public void MaterialMissing() => Fires(Linter.MaterialMissing, Lint(Good.Replace("m/alpha.emat", "m/none.emat")), "Material");
    [Fact] public void TextureMissing() => Fires(Linter.TextureMissing, Lint(Good.Replace("alpha", "tex_missing")), "Material");
    [Fact] public void NpotDxt() => Fires(Linter.NpotDxt, Lint(Good.Replace("alpha", "npot")));
    [Fact] public void NoEnf1() => Fires(Linter.NoEnf1, Lint(Good.Replace("alpha", "plain")));
    [Fact] public void TextureBig() => Fires(Linter.TextureBig, Lint(Good.Replace("alpha", "big")));
    [Fact] public void MaxNumLow() => Fires(Linter.MaxNumLow, Lint(Good.Replace("MaxNum 100", "MaxNum 5")), "MaxNum");
    [Fact] public void NeverEmits() => Fires(Linter.NeverEmits, Lint(Good.Replace("BirthRate 5", "BirthRate 0")), "BirthRate");
    [Fact] public void FarOffset() => Fires(Linter.FarOffset, Lint(Good + "\n   Offset 20 0 0\n   LocalTransform 0", Spinning()), "Offset");
    [Fact] public void GraphAboveOne() => Fires(Linter.GraphAboveOne, Lint(Good + "\n   Size {\n    0 2 1 1\n   }"), "Size");
    [Fact] public void GraphUndershoot() => Fires(Linter.GraphUndershoot, Lint(Good + "\n   Size {\n    0 0.1 0.25 0.1 0.5 1 1 1\n   }"), "Size");
    [Fact] public void Streak() => Fires(Linter.Streak, Lint(Good.Replace("alpha", "streak")));
    [Fact] public void LitMoving() => Fires(Linter.LitMoving, Lint(Good.Replace("alpha", "lit") + "\n   Offset 3 0 0", Spinning()));
    [Fact] public void SortOnSpin() => Fires(Linter.SortOnSpin, Lint(Good + "\n   Sort 1", Spinning()), "Sort");
    [Fact] public void ZEnableHard() => Fires(Linter.ZEnableHard, Lint(Good.Replace("alpha", "hard")), "ZEnable");
    [Fact] public void DenseNoSort() => Fires(Linter.DenseNoSort, Lint("MaxNum 1000\n   BirthRate 200\n   LifeTime 5\n   SizeMultiplier 3\n   Velocity 0\n   Material \"m/alpha.emat\""), "Sort");
    [Fact] public void BigCard() => Fires(Linter.BigCard, Lint(Good.Replace("SizeMultiplier 0.5", "SizeMultiplier 6")), "SizeMultiplier");
    [Fact] public void EmissiveSmoke() => Fires(Linter.EmissiveSmoke, Lint(Good.Replace("alpha", "smoke_add")));
    [Fact] public void Restitution() => Fires(Linter.Restitution, Lint(Good + "\n   Restitution 0.5"), "Restitution");
    [Fact] public void SizedShape() => Fires(Linter.SizedShape, Lint(Good + "\n   ShapeType Cone\n   ShapeSize 1 1 1"), "ShapeSize");
    [Fact] public void BirthRateRnd() => Fires(Linter.BirthRateRnd, Lint(Good + "\n   BirthRateRND 4"), "BirthRateRND");
    [Fact] public void Wind() => Fires(Linter.Wind, Lint(Good + "\n   Wind 1"), "Wind");
    [Fact] public void Spring() => Fires(Linter.Spring, Lint(Good + "\n   Spring 0.1"), "Spring");
    [Fact] public void SpringLocal() => Fires(Linter.SpringLocal, Lint(Good + "\n   Spring 0.1\n   LocalTransform 1"), "LocalTransform");
    [Fact] public void ManyDecimals() => Fires(Linter.ManyDecimals, Lint(Good + "\n   Alpha {\n    0 1 1 0.12345\n   }"), "Alpha");
    [Fact] public void FarShimmer() => Fires(Linter.FarShimmer, Lint(Good.Replace("SizeMultiplier 0.5", "SizeMultiplier 0.3"), Spinning()), "SizeMultiplier");
    [Fact] public void BirthGate() => Fires(Linter.BirthGate, Lint(Good + "\n   EffectTime 10\n   BRateMast {\n    0 0 0.7 0 0.71 1 1 1\n   }"), "BRateMast");
    [Fact] public void MasterTail() => Fires(Linter.MasterTail, Lint(Good + "\n   EffectTime 10\n   Repeat 0\n   BRateMast {\n    0 0 0.97 0 0.971 1 1 1\n   }"), "BRateMast");

    // A turn-rate window between two of the 32 samples: the game's read (CG8) keeps a fraction of it.
    [Fact] public void SampledGraph() => Fires(Linter.SampledGraph, Lint(Good + "\n   RotMultiplier 60\n   RotationSpeed {\n    0 0 0.5 0 0.505 1 0.52 1 0.525 0 1 0\n   }"), "RotationSpeed");

    // CG9: the blood burst of damage_cv_tear_00 (1000 births/s for 0.1 s, LifeTime 6.5): 99 drawn, about 70 at 60 fps,
    // 1 after the 137 ms first frame.
    private const string Burst = "MaxNum 100\n   BirthRate 1000\n   LifeTime 6.5\n   SizeMultiplier 0.5\n   Material \"m/alpha.emat\"\n   EffectTime 0.1\n   Repeat 0";

    [Fact] public void FrameLoss() => Fires(Linter.FrameLoss, Lint(Burst), "EffectTime");
    [Fact] public void ColdCut() => Fires(Linter.ColdCut, Lint(Burst), "EffectTime");

    // Two seconds at 5 births/s lose at most a birth; the burst held by MaxNum with the effect time 0.367 s longer
    // (a clamped 0.3 s frame plus two frames at 30 fps) keeps its 100 even after the load frame.
    [Fact]
    public void Frame_loss_is_quiet_on_long_and_held_emitters()
    {
        var issues = Lint(Good + "\n   EffectTime 2\n   Repeat 0").Issues.Concat(Lint(Burst.Replace("EffectTime 0.1", "EffectTime 0.467")).Issues);
        Assert.DoesNotContain(issues, i => i.Rule == Linter.FrameLoss || i.Rule == Linter.ColdCut);
    }

    // CG10: electra_ib_active's lightning1 (BirthRate 12 + RND 48 drawn per step, open 0.5 s) gave 20 at 1 ms and 15
    // at 60 fps only because the two runs drew different rates; over 400 seeds it gives 20 and 19.
    [Fact]
    public void Frame_loss_compares_frames_not_random_draws() =>
        Assert.DoesNotContain(Lint("MaxNum 32\n   BirthRate 12\n   BirthRateRND 48\n   LifeTime 1\n   SizeMultiplier 0.5\n   Material \"m/alpha.emat\"\n   EffectTime 3\n   Repeat 0\n   BRateMast {\n    0 1 0.16115 1 0.17254 0 1 0\n   }").Issues,
            i => i.Rule == Linter.FrameLoss);

    // CG10: voronka_ar_blowout's gravity_leaves_00 (cap 73 refilled as lives of 0.038 + 0.132 s end) loses 3 % at 60 fps
    // over 400 seeds; one shared mean life puts every death into the same frame and showed 7 %.
    [Fact]
    public void Frame_loss_keeps_random_lives_of_a_refilled_cap() =>
        Assert.DoesNotContain(Lint("ShapeType Sphere\n   ShapeSize 0.6 0.6 0.6\n   ConeAngle 360 0 44.571\n   MaxNum 73\n   BirthRate 1000\n   SizeMultiplier 0.141\n   SizeRND 0.058\n   Velocity -37.045\n   VelocityRND 34.083\n   AirResistance 3.7\n   GravityMultiply 1.152\n   LifeTime 0.038\n   LifeTimeRND 0.132\n   Material \"m/alpha.emat\"\n   EffectTime 2.583\n   Repeat 0\n   BRateMast {\n    0 0 0.84375 0 0.875 1 0.90625 1 0.9375 0.4 0.96875 0 1 0\n   }").Issues,
            i => i.Rule == Linter.FrameLoss);

    [Fact]
    public void Sampled_graph_is_quiet_without_rotation() =>
        Assert.DoesNotContain(Lint(Good + "\n   RotationSpeed {\n    0 0 0.5 0 0.505 1 0.52 1 0.525 0 1 0\n   }").Issues, i => i.Rule == Linter.SampledGraph);

    [Fact]
    public void Master_tail_is_quiet_on_an_early_window() =>
        Assert.DoesNotContain(Lint(Good + "\n   EffectTime 10\n   Repeat 0\n   BRateMast {\n    0 0 0.3 0 0.4 1 1 1\n   }").Issues, i => i.Rule == Linter.MasterTail);

    [Fact]
    public void Far_shimmer_is_quiet_on_a_near_only_part() =>
        Assert.DoesNotContain(Lint(Good.Replace("SizeMultiplier 0.5", "SizeMultiplier 0.3"), new PartEntry { Spin = 30, Near = 150 }).Issues, i => i.Rule == Linter.FarShimmer);

    [Fact]
    public void Undershoot_is_quiet_on_a_two_key_line() =>
        Assert.DoesNotContain(Lint(Good + "\n   Size {\n    0 0 1 1\n   }").Issues, i => i.Rule == Linter.GraphUndershoot);

    // The game clamps the curve to 0-1: a dip below a key of 0 draws the same 0 (seen on the tornado's bolt colours).
    [Fact]
    public void Undershoot_is_quiet_below_a_key_of_zero() =>
        Assert.DoesNotContain(Lint(Good + "\n   Alpha {\n    0 0 0.1 0 0.2 1 1 1\n   }").Issues, i => i.Rule == Linter.GraphUndershoot);

    // 2.11: sorting a spinning cloud made the blinking worse, so a dense cloud on a turning part is not told to sort.
    [Fact]
    public void Dense_cloud_on_a_spinning_part_is_not_told_to_sort() =>
        Assert.DoesNotContain(Lint("MaxNum 1000\n   BirthRate 200\n   LifeTime 5\n   SizeMultiplier 3\n   Velocity 0\n   Material \"m/alpha.emat\"", Spinning()).Issues,
            i => i.Rule == Linter.DenseNoSort);

    // CG1: around dust plays under the moving camera, so it counts as a moving source (no sort advice), and it is never
    // far away (no shimmer warning).
    [Fact]
    public void Around_part_moves_and_is_near()
    {
        var issues = Lint("MaxNum 1000\n   BirthRate 200\n   LifeTime 5\n   SizeMultiplier 0.3\n   Velocity 0\n   Material \"m/alpha.emat\"",
            new PartEntry { Id = "a", Path = "a.ptc", Around = 100 }).Issues;
        Assert.DoesNotContain(issues, i => i.Rule == Linter.DenseNoSort);
        Assert.DoesNotContain(issues, i => i.Rule == Linter.FarShimmer);
    }

    [Fact]
    public void NameKeyword() => Fires(Linter.NameKeyword, Lint(new[] { Part("a.ptc", Ptc(Emitter("ref", Good))) }));

    [Fact]
    public void NameChars() => Fires(Linter.NameChars, Lint(new[] { Part("a.ptc", Ptc(Emitter("a-b", Good))) }));

    [Fact]
    public void NameTaken() => Fires(Linter.NameTaken, Lint(new[] { Part("a.ptc", Ptc(Emitter("a", Good), Emitter("a", Good + "\n   Offset 0 5 0"))) }));

    [Fact]
    public void Nameless() => Fires(Linter.Nameless, Lint(new[] { Part("a.ptc", "EffectDef {\n Emitors {\n  EmitorDef {\n   " + Good + "\n  }\n }\n}\n") }));

    [Fact]
    public void CloseAlpha() => Fires(Linter.CloseAlpha, Lint(new[] { Part("a.ptc", Ptc(Emitter("a", Good))), Part("b.ptc", Ptc(Emitter("b", Good + "\n   Offset 1 0 0"))) }), "Offset");

    [Fact]
    public void StackedAxis() => Fires(Linter.StackedAxis,
        Lint(new[] { Part("a.ptc", Ptc(Emitter("low", Good), Emitter("high", Good + "\n   Offset 0 6 0")), Spinning()) }), "Offset");

    [Fact]
    public void MixedStops() => Fires(Linter.MixedStops, Lint(new[]
    {
        Part("a.ptc", Ptc(Emitter("once", Good + "\n   Repeat 0\n   EffectTime 2"), Emitter("loop", Good + "\n   Repeat 1\n   Offset 9 0 0")),
            new PartEntry { Id = "a", Path = "a.ptc", Stop = 3 }),
    }));

    [Fact]
    public void GlowAtNoon()
    {
        var parts = new[] { Part("a.ptc", Ptc(Emitter("glow", Good.Replace("alpha", "glow")), Emitter("dust", Good.Replace("alpha", "dust") + "\n   Offset 9 0 0"))) };
        Fires(Linter.GlowAtNoon, Lint(parts, sky: "noon"), "Material");
        Assert.DoesNotContain(Lint(parts, sky: "night").Issues, i => i.Rule == Linter.GlowAtNoon);
    }

    [Fact]
    public void TurnRates()
    {
        var project = new ProjectFile { Name = "t" };
        project.Parts.Add(new PartEntry { Id = "a", Path = "a.ptc", Spin = 10 });
        project.Parts.Add(new PartEntry { Id = "b", Path = "b.ptc", Spin = 20 });
        project.States.Add(new StateEntry { Name = "idle", Items = { new StateItem { Ref = "a" }, new StateItem { Ref = "b" } } });
        Fires(Linter.TurnRates, Lint(new[] { Part("a.ptc", Ptc(Emitter("a", Good))) }, project));
    }

    // A part played only as strikes lives for one burst: 5/s for 0.2 s needs 2 particles, not the steady 10.
    [Fact]
    public void A_strike_part_needs_one_burst_of_particles()
    {
        var project = new ProjectFile { Name = "t" };
        project.Parts.Add(new PartEntry { Id = "a", Path = "a.ptc" });
        project.Strikes.Add(new StrikeEntry { Id = "k", Part = "a", Duration = 0.2f });
        project.States.Add(new StateEntry { Name = "idle", Items = { new StateItem { Ref = "k" } } });
        var part = Part("a.ptc", Ptc(Emitter("a", Good.Replace("MaxNum 100", "MaxNum 5"))), project.Parts[0]);
        Assert.DoesNotContain(Lint(new[] { part }, project).Issues, i => i.Rule == Linter.MaxNumLow);
        project.States[0].Items.Add(new StateItem { Ref = "a" });
        Fires(Linter.MaxNumLow, Lint(new[] { part }, project));
    }

    [Fact]
    public void UnusedMaterial()
    {
        var project = new ProjectFile { Name = "t", Materials = { "m/glow.emat" } };
        Fires(Linter.UnusedMaterial, Lint(new[] { Part("a.ptc", Ptc(Emitter("a", Good))) }, project));
    }

    [Fact]
    public void DonorPath() => Fires(Linter.DonorPath, Lint(Good.Replace("m/alpha.emat", "{0123456789ABCDEF}cg_xray/ar/materials/x.emat")), "Material");

    [Fact]
    public void DonorTag()
    {
        var project = new ProjectFile { Name = "t", Export = { Profile = "cg" } };
        Fires(Linter.DonorTag, Lint(new[] { Part("electra_ar_idle.ptc", Ptc(Emitter("a", Good))) }, project));
    }
}
