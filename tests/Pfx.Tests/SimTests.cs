using System.Globalization;
using System.Numerics;
using Pfx.Sim;
using Xunit.Abstractions;

namespace Pfx.Tests;

// One test per verified fact of prompt 2.3 / 2.12 and of the P3 game probe (journal P3).
public class SimTests
{
    private readonly ITestOutputHelper _out;

    public SimTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static readonly SimOptions Opt = new();

    private static readonly EmitterParams Base = new()
    {
        Name = "e", Shape = ShapeKind.Point, MaxNum = 5000, BirthRate = 50, LifeTime = 2, SizeMultiplier = 1,
        StretchMultiplier = 1, EffectTime = 10, Repeat = true,
    };

    private static EmitterParams Jet(Vector3 cone) => Base with { ConeAngle = cone, Velocity = 1 };

    private static List<RenderParticle> Run(EmitterParams p, float seconds, SourceMotion? motion = null, SimOptions? o = null)
    {
        var sim = new EffectSim(new[] { p }, o ?? Opt, motion);
        sim.RunTo(seconds);
        return sim.Render(0);
    }

    private static int CountAt(EmitterParams p, float seconds)
    {
        var sim = new EffectSim(new[] { p }, Opt);
        sim.RunTo(seconds);
        return sim.Count;
    }

    private static Vector3 Dir(RenderParticle p) => Vector3.Normalize(p.Velocity);

    private static float PolarDeg(Vector3 d) => MathF.Acos(Math.Clamp(d.Y, -1, 1)) * 180 / MathF.PI;

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Graph_equals_the_W0_fit_on_the_undershoot_keys()
    {
        var g = Graph.FromNumbers(new double[] { 0, 0.1, 0.25, 0.1, 0.5, 1, 1, 1 }, 1);
        var rows = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Golden", "spline_undershoot.csv")).Skip(1)
            .Select(l => l.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()).ToList();
        double maxFit = 0, maxDrawn = 0, sumDrawn = 0;
        foreach (var r in rows)
        {
            double v = g.Evaluate((float)r[0]);
            maxFit = Math.Max(maxFit, Math.Abs(v - r[1]));
            maxDrawn = Math.Max(maxDrawn, Math.Abs(v - r[2]));
            sumDrawn += Math.Abs(v - r[2]);
        }
        _out.WriteLine($"{rows.Count} columns: max |sim - fit| {maxFit:0.000000}, vs the drawn Workbench curve mean " +
                       $"{sumDrawn / rows.Count:0.0000} max {maxDrawn:0.0000}");
        Assert.True(rows.Count > 500);
        Assert.True(maxFit < 1e-4);
        Assert.True(maxDrawn < 0.014, "W0 measured 0.0129 against a ~0.01 wide drawn line");
        Assert.True(g.Unclamped(0.125f) < 0.1f);
    }

    [Fact]
    public void Two_key_graph_is_a_straight_line()
    {
        var g = Graph.FromNumbers(new double[] { 0, 0, 1, 1 }, 1);
        foreach (float t in new[] { 0.1f, 0.25f, 0.5f, 0.8f })
            Assert.InRange(g.Evaluate(t), t - 1e-6f, t + 1e-6f);
        // Births follow the game's 32-sample read (CG7): the line is read as u * 31/32, so 400 * 31/64 = 193.75 births in
        // all and 400 * 0.125 * 31/32 = 48.4 by half time.
        var ramp = Base with { BirthRate = 100, LifeTime = 100, EffectTime = 4, Repeat = false, BRateMast = g };
        Assert.InRange(CountAt(ramp, 2), 47, 50);
        Assert.InRange(CountAt(ramp, 5), 192, 196);
    }

    // CG7 probe shape_t30.ptc (EffectTime 30, BirthRate 1500, LifeTime 60): game births per emitter after 31.5 s. The
    // simulator steps at the probe's frame time (median 11 ms): births use the rate at the start of a step, which
    // undercounts a rising rate by half a step's rise (~12 births on the 1 s ramps at 1/60 s).
    [Theory]
    [InlineData("0 0 0.875 0 0.90625 1 1 1", 3633)]
    [InlineData("0 0 0.9 0 0.901 1 0.93 1 0.931 0 1 0", 1451)]
    [InlineData("0 0 0.9 0 0.95 1 1 1", 2010)]
    [InlineData("0 0 0.9 0 1 1", 969)]
    [InlineData("0 0 0.95 0 0.951 1 0.98 1 0.981 0 1 0", 731)]
    [InlineData("0 0 0.97 0 0.971 1 1 1", 0)]
    [InlineData("0 0 0.9 0 0.901 1 0.902 1 0.903 0 1 0", 0)]
    public void Births_follow_the_games_32_sample_master_read(string keys, int game)
    {
        var g = Graph.FromNumbers(keys.Split(' ').Select(k => double.Parse(k, CultureInfo.InvariantCulture)).ToArray(), 1);
        var p = Base with { BirthRate = 1500, LifeTime = 60, MaxNum = 100000, EffectTime = 30, Repeat = false, BRateMast = g };
        var run = new EffectSim(new[] { p }, new SimOptions { Step = 0.011f });
        run.RunTo(31.5f);
        int sim = run.Count;
        _out.WriteLine($"{keys}: sim {sim}, game {game}");
        Assert.InRange(sim, game * 0.99 - 1, game * 1.01 + 1);
    }

    [Fact]
    public void Master_read_holds_the_last_sample_and_skips_the_tail()
    {
        var late = Graph.FromNumbers(new double[] { 0, 0, 0.97, 0, 0.971, 1, 1, 1 }, 1);
        Assert.Equal(1f, late.Evaluate(0.99f));
        Assert.Equal(0f, late.EvaluateCpu(1f));
        var flat = Graph.Constant(0.4f);
        Assert.Equal(0.4f, flat.EvaluateCpu(0.37f), 6);
    }

    private static Graph Keys(string keys) =>
        Graph.FromNumbers(keys.Split(' ').Select(k => double.Parse(k, CultureInfo.InvariantCulture)).ToArray(), 1);

    // CG8 RenderDoc dump of texsParamsTexture (probe gpu.ptc, DayZ 1.30): the texels the game baked for three graphs.
    [Fact]
    public void Gpu_texels_are_the_games_bytes()
    {
        var line = Keys("0 0 1 1");
        for (int i = 0; i < Graph.Texels; i++)
            Assert.Equal(i, line.Texel(i));
        var step = Keys("0 0.25 0.5 0.25 0.501 1 1 1");
        Assert.Equal(63, step.Texel(0));
        Assert.Equal(49, Enumerable.Range(0, Graph.Texels).Min(i => step.Texel(i)));
        Assert.Equal(255, step.Texel(128));
        var gap = Keys("0 0.25 0.508 0.25 0.509 1 0.522 1 0.523 0.25 1 0.25");
        Assert.Equal(new byte[] { 63, 255, 255, 255, 255, 63 }, Enumerable.Range(129, 6).Select(i => gap.Texel(i)).ToArray());
        // The nearest texel: u = 0.5 reads texel 128, just below it texel 127.
        Assert.Equal(1f, step.EvaluateGpu(0.5f));
        Assert.Equal(step.Texel(127) / 255f, step.EvaluateGpu(127.4f / 256));
    }

    // CG8 birth probe (DayZ 1.30, LifeTime 60, per-frame counts): every emitter had its first particle in the spawn
    // frame, then one per 1 / rate up to EffectTime; a master that starts at 0 gives its first birth where it rises.
    [Theory]
    [InlineData(0.003f, 31f, false, null, 1)]
    [InlineData(1f, 31f, false, null, 31)]
    [InlineData(16f, 31f, false, null, 496)]
    [InlineData(0.3f, 31f, false, "0 0 0.15625 0 0.1875 1 0.21875 0 0.46875 0 0.5 1 0.53125 0 1 0", 1)]
    [InlineData(0.2f, 2f, true, null, 7)]
    public void Birth_accumulator_starts_full(float rate, float effect, bool repeat, string? master, int game)
    {
        var p = Base with { BirthRate = rate, LifeTime = 100, EffectTime = effect, Repeat = repeat };
        if (master != null)
            p = p with { BRateMast = Keys(master) };
        Assert.Equal(master == null ? 1 : 0, CountAt(p, 0.02f));
        Assert.Equal(game, CountAt(p, 33.5f));
    }

    [Fact]
    public void First_birth_comes_where_the_master_rises()
    {
        var p = Base with
        {
            BirthRate = 0.3f, LifeTime = 100, EffectTime = 31, Repeat = false,
            BRateMast = Keys("0 0 0.15625 0 0.1875 1 0.21875 0 0.46875 0 0.5 1 0.53125 0 1 0"),
        };
        Assert.Equal(0, CountAt(p, 4.95f));
        Assert.Equal(1, CountAt(p, 5.1f));
    }

    // CG9 probe sf.ptc (DayZ 1.30, 1000 births/s, LifeTime 60): the logged frame times from the spawn frame on and the
    // game's counts after each of them. Births lag one frame, and a frame that reaches EffectTime ends them. The model
    // runs 1-2 above the game (its first lagged frame is 1-2 ms shorter than the script's whole-ms timeslice).
    [Theory]
    [InlineData("11 113 11 11 10 10", 0.05f, "1 1 1 1 1 1")]
    [InlineData("11 113 11 11 10 10", 0.2f, "1 11 124 134 145 155")]
    [InlineData("110 14 11 10 10 10", 0.1f, "0 0 0 0 0 0")]
    [InlineData("110 14 11 10 10 10", 0.2f, "1 110 123 134 144 154")]
    [InlineData("9 300 23 20 23 20", 0.2f, "1 1 1 1 1 1")]
    [InlineData("9 300 23 20 23 20", 0.5f, "1 8 309 331 351 374")]
    public void Births_lag_one_frame_and_end_with_the_frame_that_reaches_EffectTime(string frames, float effect, string game)
    {
        var e = new EmitterSim(Base with { BirthRate = 1000, LifeTime = 60, MaxNum = 100000, EffectTime = effect, Repeat = false }, new SimOptions(), 0);
        var want = game.Split(' ').Select(int.Parse).ToArray();
        var dts = frames.Split(' ').Select(f => int.Parse(f) / 1000f).ToArray();
        for (int i = 0; i < dts.Length; i++)
        {
            e.Step(dts[i], Vector3.Zero, 0, Vector3.Zero);
            Assert.InRange(e.Count, want[i], want[i] + 2);
        }
    }

    // The first spawn of damage_cv_tear_00 on a client: a 137 ms frame after the spawn frame left its 0.1 s blood burst
    // at the one particle of the spawn frame; a second spawn gave 87 at its 9.7 ms frames (the count moves by a frame's
    // births with where the frames fall against EffectTime). Frames are clamped at 0.3 s.
    [Fact]
    public void A_long_first_frame_ends_a_short_burst()
    {
        var blood = Base with { BirthRate = 1000, LifeTime = 6.5f, MaxNum = 100, EffectTime = 0.1f, Repeat = false };
        int Run(EmitterParams p, float first, float until)
        {
            var sim = new EffectSim(new[] { p }, new SimOptions { Step = 0.0097f, FirstFrame = first });
            sim.RunTo(until);
            return sim.Count;
        }
        Assert.Equal(1, Run(blood, 0.137f, 1));
        Assert.InRange(Run(blood, 0, 1), 87, 90);
        var longer = blood with { EffectTime = 2, MaxNum = 100000, LifeTime = 60 };
        // A long frame inside a longer effect only delays births (CG9 mid_300: the full count, 300 in one frame).
        Assert.Equal(Run(longer, 0.3f, 3), Run(longer, 1f, 3));
        Assert.InRange(Run(longer, 0.3f, 3), Run(longer, 0, 3) - 2, Run(longer, 0, 3) + 2);
    }

    // CG8 probe cpu_am.ptc: Velocity 3, AirResistance 3, AirResistanceMast 0.25 stepping to 1 at half the effect. The
    // particle born at spawn rose 4 m (= 3 / (3 · 0.25)): the master is read once, at birth.
    [Fact]
    public void Air_resistance_master_is_read_at_birth()
    {
        var p = Base with
        {
            BirthRate = 1, MaxNum = 1, LifeTime = 60, EffectTime = 1 / 0.515f, Repeat = false, Velocity = 3, AirResistance = 3,
            AirResistanceMast = Keys("0 0.25 0.5 0.25 0.501 1 1 1"),
        };
        // 1 ms steps: the explicit drag step stops short by v / k · k·dt / 2 (2.5 cm at 1/60 s).
        var first = Run(p, 20, null, new SimOptions { Step = 0.001f })[0];
        Assert.InRange(first.Position.Y, 3.99f, 4.01f);
    }

    // CG8 probe cpu_rot.ptc: RotationSpeed 0.25 stepping to 1 at half a 10 s life, RotMultiplier 60. The game turned the
    // bar 110.7° by age 6 s, the 32-sample read 110.8°, the spline 125.4°.
    [Fact]
    public void Rotation_over_life_follows_the_32_sample_read()
    {
        var p = Base with
        {
            BirthRate = 1, MaxNum = 1, LifeTime = 10, EffectTime = 0.5f, Repeat = false, RotMultiplier = 60,
            RotationSpeed = Keys("0 0.25 0.5 0.25 0.501 1 1 1"),
        };
        var o = new SimOptions { Step = 0.001f };
        float degrees = Run(p, 6.001f, null, o)[0].Angle * 180 / MathF.PI;
        Assert.InRange(degrees, 109.7f, 111.7f);
    }

    [Fact]
    public void Graph_values_are_clamped_to_zero_one()
    {
        Assert.Equal(1f, Graph.FromNumbers(new double[] { 0, 2, 1, 2 }, 1).Evaluate(0.5f));
        var under = Graph.FromNumbers(new double[] { 0, 0, 0.3, 0, 0.6, 1, 1, 1 }, 1);
        Assert.True(under.Unclamped(0.15f) < 0);
        Assert.Equal(0f, under.Evaluate(0.15f));
    }

    [Fact]
    public void Cone_band_is_half_of_the_apertures_from_plus_Y()
    {
        foreach (var p in Run(Jet(new Vector3(0, 90, 90)), 1))
        {
            Assert.InRange(PolarDeg(Dir(p)), 44.9f, 45.1f);
            Near(Vector3.Normalize(new Vector3(-1, 1, 0)), Dir(p));
        }
        var band = Run(Jet(new Vector3(360, 80, 100)), 1);
        Assert.True(band.Count > 40);
        Assert.All(band, p => Assert.InRange(PolarDeg(Dir(p)), 39.99f, 50.01f));
        Assert.All(Run(Jet(new Vector3(0, 180, 180)), 1), p => Near(-Vector3.UnitX, Dir(p)));
    }

    [Fact]
    public void Azimuth_range_fans_from_minus_X_towards_minus_Z()
    {
        var fan = Run(Jet(new Vector3(90, 180, 180)), 2);
        Assert.True(fan.Count > 80);
        Assert.All(fan, p => Assert.True(Dir(p).X <= 1e-4f && Dir(p).Z <= 1e-4f, $"{Dir(p)}"));
        Assert.Contains(fan, p => Dir(p).X < -0.98f);
        Assert.Contains(fan, p => Dir(p).Z < -0.98f);
    }

    [Fact]
    public void Positive_yaw_turns_clockwise_from_above_for_source_and_emitter()
    {
        var jet = Jet(new Vector3(0, 180, 180));
        Assert.All(Run(jet, 1, SourceMotion.Still(yaw: 90)), p => Near(Vector3.UnitZ, Dir(p)));
        Assert.All(Run(jet with { Angles = new(90, 0, 0) }, 1), p => Near(Vector3.UnitZ, Dir(p)));
        Near(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, Orientation.Yaw(MathF.PI / 2)), 1e-6f);
    }

    [Fact]
    public void Pitch_tilts_up_towards_minus_Z_and_roll_towards_plus_X()
    {
        var up = Jet(Vector3.Zero);
        float s = MathF.Sin(MathF.PI / 6), c = MathF.Cos(MathF.PI / 6);
        Assert.All(Run(up with { Angles = new(0, 30, 0) }, 1), p => Near(new Vector3(0, c, -s), Dir(p)));
        Assert.All(Run(up with { Angles = new(0, 0, 30) }, 1), p => Near(new Vector3(s, c, 0), Dir(p)));
    }

    [Fact]
    public void Box_ellipse_and_sphere_sizes_are_full_extents_and_sphere_is_round_by_x()
    {
        var dots = Base with { MaxNum = 5000, BirthRate = 50000, LifeTime = 10, ShapeSize = new(2, 1, 0.5f) };
        var box = Run(dots with { Shape = ShapeKind.Box }, 0.1f);
        Assert.All(box, p => Assert.True(MathF.Abs(p.Position.X) <= 1 && MathF.Abs(p.Position.Y) <= 0.5f && MathF.Abs(p.Position.Z) <= 0.25f));
        var ellipse = Run(dots with { Shape = ShapeKind.Ellipse }, 0.1f);
        Assert.All(ellipse, p => Assert.True(new Vector3(p.Position.X / 1, p.Position.Y / 0.5f, p.Position.Z / 0.25f).Length() <= 1.0001f));
        var sphere = Run(dots with { Shape = ShapeKind.Sphere }, 0.1f).Select(p => p.Position).ToList();
        Assert.All(sphere, p => Assert.True(p.Length() <= 1.0001f));
        Assert.True(sphere.Max(p => p.Y) > 0.9f && sphere.Max(p => p.Z) > 0.9f, "round, diameter = ShapeSize.x");
        var r = sphere.Select(p => p.Length()).OrderBy(x => x).ToList();
        _out.WriteLine($"sphere {r.Count} points, median radius {r[r.Count / 2]:0.000} of 1");
        Assert.InRange(r[r.Count / 2], 0.4f, 0.6f);
    }

    [Fact]
    public void Missing_ShapeType_is_Point()
    {
        Assert.Equal(ShapeKind.Point, EmitterParams.ParseShape(null));
        Assert.Equal(ShapeKind.Point, EmitterParams.ParseShape("2"));
        Assert.Equal(ShapeKind.OnSphere, EmitterParams.ParseShape("OnSphere"));
    }

    [Fact]
    public void LocalTransform_0_leaves_a_ring_on_a_turning_source_and_1_orbits_with_it()
    {
        var turn = new SourceMotion.Turning(Vector3.Zero, 0, 90);
        var marker = Base with { Offset = new(2, 0, 0), MaxNum = 100, BirthRate = 10, LifeTime = 3 };
        var world = Run(marker, 2, turn);
        var headings = world.Select(p => MathF.Atan2(p.Position.Z, p.Position.X) * 180 / MathF.PI).ToList();
        Assert.All(world, p => Assert.InRange(new Vector2(p.Position.X, p.Position.Z).Length(), 1.999f, 2.001f));
        Assert.True(headings.Max() - headings.Min() > 150, "ring of birth points");
        var expected = Vector3.Transform(new Vector3(2, 0, 0), Orientation.Yaw(MathF.PI));
        Assert.All(Run(marker with { LocalTransform = true }, 2, turn), p => Near(expected, p.Position, 1e-3f));
    }

    [Fact]
    public void Flipbook_plays_row_major_holds_the_last_tile_and_sets_lifetime_by_anim()
    {
        var flip = Base with { MaxNum = 1, BirthRate = 100, LifeTime = 100, EffectTime = 100, AnimFps = 2, TileU = 4, TileV = 4 };
        Assert.Equal(1, Run(flip, 0.75f)[0].Frame);
        Assert.Equal(15, Run(flip, 7.9f)[0].Frame);
        Assert.Equal(0, Run(flip, 8.4f)[0].Frame);
        Assert.Equal(15, Run(flip with { AnimOnce = true }, 30f)[0].Frame);
        var byAnim = flip with { AnimOnce = true, LifetimeByAnim = true, BirthRate = 1, MaxNum = 100 };
        Assert.Equal(8f, byAnim.BaseLifeTime);
        Assert.Equal(8, CountAt(byAnim, 20));
    }

    [Fact]
    public void Random_part_is_added_as_value_plus_uniform_times_rnd()
    {
        var e = Base with { BirthRate = 10, LifeTime = 2, LifeTimeRnd = 2 };
        double mean = Enumerable.Range(0, 40).Select(k => (double)CountAt(e, 6 + k * 0.1f)).Average();
        _out.WriteLine($"LifeTime 2 + RND 2 at 10/s: mean alive {mean:0.0} (game 29-34)");
        Assert.InRange(mean, 27, 33);
    }

    [Fact]
    public void EffectTime_0_without_repeat_stops_births_after_one_second()
    {
        var e = Base with { BirthRate = 10, LifeTime = 2, EffectTime = 0, Repeat = false };
        Assert.InRange(CountAt(e, 1.5f), 10, 11);
        Assert.Equal(0, CountAt(e, 3.1f));
        Assert.InRange(CountAt(e with { Repeat = true }, 5), 20, 21);
    }

    [Fact]
    public void BirthRateVel_adds_births_per_metre_per_second_of_source_speed()
    {
        var e = Base with { BirthRate = 2, BirthRateVel = 20, LifeTime = 2 };
        Assert.InRange(CountAt(e, 5), 3, 5);
        Assert.InRange(Run(e, 5, new SourceMotion.Line(Vector3.Zero, new Vector3(2.5f, 0, 0), 0)).Count, 102, 106);
        Assert.InRange(Run(e, 5, new SourceMotion.Line(Vector3.Zero, new Vector3(5, 0, 0), 0)).Count, 202, 206);
        var none = e with { BirthRateVel = 0 };
        Assert.Equal(CountAt(none, 5), Run(none, 5, new SourceMotion.Line(Vector3.Zero, new Vector3(5, 0, 0), 0)).Count);
    }

    [Fact]
    public void VelAffect_1_carries_the_source_velocity_and_0_leaves_a_still_trail()
    {
        var moving = new SourceMotion.Line(Vector3.Zero, new Vector3(5, 0, 0), 0);
        var e = Base with { MaxNum = 100, BirthRate = 10, LifeTime = 3, VelAffect = 1 };
        Assert.All(Run(e, 2, moving), p => Near(new Vector3(5, 0, 0), p.Velocity));
        var trail = Run(e with { VelAffect = 0 }, 2, moving);
        Assert.All(trail, p => Assert.Equal(Vector3.Zero, p.Velocity));
        Assert.True(trail.Max(p => p.Position.X) - trail.Min(p => p.Position.X) > 9);
    }

    [Fact]
    public void Gravity_and_linear_air_resistance_follow_the_measured_trails()
    {
        var grav = Jet(new Vector3(0, 180, 180)) with { Velocity = 3, GravityMultiply = 0.25f, MaxNum = 1, BirthRate = 100, LifeTime = 10 };
        var g = Run(grav, 1.2f, o: Opt with { Step = 0.001f })[0];
        double fall = 0.5 * 9.81 * 0.25 * g.Age * g.Age;
        Assert.InRange(-g.Position.Y, fall - 0.005, fall + 0.005);
        var drag = Jet(new Vector3(0, 180, 180)) with { Velocity = 3, AirResistance = 1, MaxNum = 1, BirthRate = 100, LifeTime = 10 };
        var d = Run(drag, 4, o: Opt with { Step = 0.001f })[0];
        Assert.InRange(-d.Position.X, 2.94f, 2.95f);
    }

    [Fact]
    public void Spring_pulls_to_the_emitter_and_holds_from_half_up()
    {
        var jet = Jet(Vector3.Zero) with { Velocity = 3, MaxNum = 1, BirthRate = 100, LifeTime = 10 };
        float c = Opt.SpringRate * 0.01f;
        float expected = 3 / c * (1 - MathF.Exp(-c * 3));
        float h = Run(jet with { Spring = 0.01f }, 3, o: Opt with { Step = 0.001f })[0].Position.Y;
        _out.WriteLine($"Spring 0.01 after 3 s: {h:0.000} m (formula {expected:0.000}, game 4.17)");
        Assert.InRange(h, expected - 0.02f, expected + 0.02f);
        foreach (float spring in new[] { 0.5f, 2f, 8f })
            Assert.All(Run(jet with { Spring = spring, MaxNum = 100, BirthRate = 10 }, 3), p => Assert.InRange(p.Position.Y, 0, 0.1f));
    }

    [Fact]
    public void Restitution_above_zero_keeps_particles_at_their_birth_point()
    {
        var e = Jet(new Vector3(0, 180, 180)) with { Velocity = 1, GravityMultiply = 1, Restitution = 0.5f, BirthRate = 10, LifeTime = 4 };
        var frozen = Run(e, 3);
        Assert.True(frozen.Count > 20);
        Assert.All(frozen, p => Assert.Equal(Vector3.Zero, p.Position));
        Assert.Contains(Run(e with { Restitution = -1 }, 3), p => p.Position.Y < -1);
    }

    [Fact]
    public void Stretch_adds_S_minus_1_seconds_of_travel_ahead_of_the_sprite()
    {
        var e = Base with { MaxNum = 10, BirthRate = 10, Velocity = 4, VelAngle = true, StretchMultiplier = 3, SizeMultiplier = 0.25f };
        Assert.All(Run(e, 1), p => Assert.InRange(p.StretchLength, 7.999f, 8.001f));
        // Size 0.25 is texel 63 of the baked graph (CG8: floor(255 · v)), so 8 m · 63/255.
        var graphSized = e with { SizeMultiplier = 1, Size = Graph.Constant(0.25f) };
        Assert.All(Run(graphSized, 1), p => Assert.InRange(p.StretchLength, 8 * 63 / 255f - 1e-3f, 8 * 63 / 255f + 1e-3f));
        Assert.All(Run(e with { StretchMultiplier = 1 }, 1), p => Assert.Equal(0f, p.StretchLength));
        Assert.All(Run(e with { StretchMultiplier = 0 }, 1), p => Assert.Equal(0f, p.StretchLength));
    }

    [Fact]
    public void MaxNum_caps_the_live_count()
    {
        var e = Base with { MaxNum = 7, BirthRate = 10, LifeTime = 2 };
        Assert.Equal(7, CountAt(e, 3));
        Assert.InRange(CountAt(e with { MaxNum = 1000 }, 3), 20, 21);
    }

    [CorpusFact]
    public void Every_corpus_effect_simulates_ten_seconds_without_errors_or_NaN()
    {
        int files = 0, emitters = 0, bad = 0;
        long particles = 0, peak = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var root in Environment.GetEnvironmentVariable("PFX_CORPUS")!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.ptc", SearchOption.AllDirectories))
            {
                var sim = EffectSim.From(Pfx.Core.Model.PtcDocument.Load(path), Opt);
                files++;
                emitters += sim.Emitters.Count;
                for (int s = 1; s <= 10; s++)
                {
                    sim.RunTo(s);
                    peak = Math.Max(peak, sim.Count);
                    for (int e = 0; e < sim.Emitters.Count; e++)
                    {
                        foreach (var p in sim.Render(e))
                        {
                            particles++;
                            if (!float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z + p.Size + p.Angle + p.StretchLength
                                                + p.Color.X + p.Color.Y + p.Color.Z + p.Color.W))
                            {
                                bad++;
                                _out.WriteLine($"not finite: {path} {sim.Emitters[e].Params.Name} at {s} s");
                            }
                        }
                    }
                }
            }
        }
        _out.WriteLine($"{files} effects, {emitters} emitters, {particles} particle samples at 1..10 s, peak {peak} alive " +
                       $"in one effect, {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(files > 0);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void Same_seed_and_step_give_identical_particles()
    {
        var e = Base with
        {
            Shape = ShapeKind.Sphere, ShapeSize = new(2, 2, 2), ConeAngle = new(360, 0, 120), MaxNum = 500, BirthRate = 100,
            BirthRateRnd = 50, LifeTimeRnd = 1, Velocity = 2, VelocityRnd = 1, GravityMultiply = 0.3f,
            GravityMultiplyRnd = 0.2f, AirResistance = 0.5f, AirResistanceRnd = 0.5f, SizeRnd = 0.5f, RandomAngle = true,
            RandomRotDir = true, RotMultiplier = 90, RandomFrame = true, TileU = 4, TileV = 2, AnimFps = 5, Spring = 0.02f,
            EffectTime = 3,
        };
        var motion = new SourceMotion.Circle(Vector3.Zero, 2, 4, CircleAxis.Y);
        var a = Run(e, 4, motion, Opt with { Seed = 7, Step = 0.02f });
        var b = Run(e, 4, motion, Opt with { Seed = 7, Step = 0.02f });
        var c = Run(e, 4, motion, Opt with { Seed = 8, Step = 0.02f });
        Assert.True(a.Count > 100);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ---- baking authored curves into engine keys (P6) ----

    // The error of baked keys against the authored curve, measured again on a 4x finer grid offset from the
    // fitter's: engine spline (Graph, clamped) against the clamped authored value.
    private static double Measured(GraphBake.Result r, Func<double, int, double> f, int channels)
    {
        var g = Graph.FromNumbers(r.Numbers, channels);
        double max = 0;
        int n = GraphBake.Samples * 4;
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5) / n;
            for (int c = 0; c < channels; c++)
                max = Math.Max(max, Math.Abs(g.Evaluate((float)t, c) - Math.Clamp(f(t, c), 0, 1)));
        }
        return max;
    }

    [Fact]
    public void Baked_keys_stay_within_the_error_of_the_authored_curve()
    {
        var rnd = new SimRandom(5);
        var stroke = Enumerable.Range(0, 40).Select(i => (i / 39.0, 0.5 + 0.4 * Math.Sin(i * 0.4) + 0.05 * rnd.Signed())).ToList();
        var curves = new List<(string Name, Func<double, int, double> F, int Channels, double[] Corners)>();
        foreach (var name in CurvePresets.Names)
        {
            var (curve, corners) = CurvePresets.Get(name);
            curves.Add((name, (t, _) => curve(t), 1, corners));
        }
        var drawn = GraphBake.Polyline(stroke);
        curves.Add(("drawn stroke", (t, _) => drawn(t), 1, stroke.Select(p => p.Item1).ToArray()));
        curves.Add(("colour, three channels", (t, c) => c switch { 0 => 1 - t * t, 1 => 0.5 + 0.5 * Math.Sin(3 * t), _ => t }, 3, Array.Empty<double>()));
        curves.Add(("above 1 is clamped", (t, _) => 1.4 * Math.Sin(Math.PI * t), 1, Array.Empty<double>()));
        foreach (double limit in new[] { 0.02, 0.01, 0.005 })
            foreach (var (name, f, channels, corners) in curves)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = GraphBake.Fit(f, channels, limit, corners);
                double ms = watch.Elapsed.TotalMilliseconds;
                double measured = Measured(r, f, channels);
                _out.WriteLine($"{name}, limit {limit}: {r.Keys} keys, error {r.Error:0.00000}, finer grid {measured:0.00000}, {ms:0} ms");
                Assert.True(r.Keys <= GraphBake.MaxKeys);
                if (!r.WithinLimit)
                {
                    // Not reachable with MaxKeys keys: the reached error is said, and it is what the finer grid sees.
                    Assert.True(r.Error > limit && measured <= r.Error + 0.001, $"{name}: reported {r.Error}, measured {measured}");
                    continue;
                }
                Assert.True(measured <= limit, $"{name}: {measured} > {limit} on the finer grid");
            }
    }

    [Fact]
    public void A_curve_the_engine_spline_already_draws_bakes_to_its_own_keys()
    {
        var line = GraphBake.Fit((t, _) => 0.2 + 0.6 * t, 1, 0.001);
        Assert.Equal(new double[] { 0, 0.2, 1, 0.8 }, line.Numbers);
        var flat = GraphBake.Fit((_, _) => 0.7, 1, 0.001);
        Assert.Equal(2, flat.Keys);
    }

    // ---- checkpoints and exact seeks (P6) ----

    // Emitters that use every per-particle path of a step: shapes, spread, drag, gravity, spring, rotation, flipbook,
    // masters, a non-repeating end, LocalTransform on a turning source, a MaxNum cap and long lives.
    private static readonly EmitterParams[] Mixed =
    {
        Base with
        {
            Shape = ShapeKind.Sphere, ShapeSize = new(2, 2, 2), ConeAngle = new(360, 0, 120), MaxNum = 300, BirthRate = 120,
            BirthRateRnd = 40, LifeTimeRnd = 1.5f, Velocity = 2, VelocityRnd = 1, GravityMultiply = 0.3f, AirResistance = 0.5f,
            AirResistanceRnd = 0.5f, SizeRnd = 0.5f, RandomAngle = true, RandomRotDir = true, RotMultiplier = 90,
            RandomFrame = true, TileU = 4, TileV = 2, AnimFps = 5, Spring = 0.02f, EffectTime = 3,
            BRateMast = Graph.FromNumbers(new double[] { 0, 0.2, 0.5, 1, 1, 0.3 }, 1),
            VelocityMast = Graph.FromNumbers(new double[] { 0, 1, 1, 0.4 }, 1),
        },
        Jet(new(360, 0, 40)) with { LocalTransform = true, LifeTime = 0.7f, BirthRate = 200, EffectTime = 2.5f, Repeat = false },
        Base with { Shape = ShapeKind.Box, ShapeSize = new(3, 1, 3), LifeTime = 40, LifeTimeRnd = 30, BirthRate = 3, Velocity = 0.2f },
    };

    private static byte[] Snapshot(EffectSim sim)
    {
        var bytes = new List<byte>();
        for (int e = 0; e < sim.Emitters.Count; e++)
        {
            var into = new ParticleInstance[sim.Emitters[e].Count];
            sim.WriteInstances(e, into);
            bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan()).ToArray());
            bytes.AddRange(BitConverter.GetBytes(sim.Emitters[e].Born));
        }
        return bytes.ToArray();
    }

    private static readonly SourceMotion Turning = new SourceMotion.Turning(new Vector3(1, 0, 2), 15, 40);

    [Fact]
    public void Saved_state_continues_exactly_like_the_run_it_came_from()
    {
        var a = new EffectSim(Mixed, Opt, Turning);
        a.RunTo(3.2f);
        var saved = a.Save();
        a.RunTo(7);
        var b = new EffectSim(Mixed, Opt, Turning);
        b.RunTo(1);
        b.Load(saved);
        Assert.Equal(3.2f, b.Time, 3);
        b.RunTo(7);
        Assert.True(a.Count > 100);
        Assert.Equal(Snapshot(a), Snapshot(b));
        _out.WriteLine($"{a.Count} alive at 7 s, state at 3.2 s {saved.Bytes} bytes");
    }

    [Fact]
    public void Fast_forward_is_exact_at_the_target()
    {
        foreach (float t in new[] { 0.5f, 4f, 12f, 25f })
        {
            var full = new EffectSim(Mixed, Opt, Turning);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            full.RunTo(t);
            double fullMs = watch.Elapsed.TotalMilliseconds;
            var fast = new EffectSim(Mixed, Opt, Turning);
            watch.Restart();
            fast.RunTo(t, exactAt: t);
            double fastMs = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(Snapshot(full), Snapshot(fast));
            _out.WriteLine($"t {t} s: {full.Count} alive, full {fullMs:0.0} ms, fast-forward {fastMs:0.0} ms");
        }
    }

    [Fact]
    public void Partial_rebuild_from_a_saved_state_equals_a_full_run()
    {
        var old = new EffectSim(Mixed, Opt, Turning);
        old.RunTo(6);
        var saved = old.Save();
        var changed = Mixed.ToArray();
        changed[1] = changed[1] with { Velocity = 3 };
        var full = new EffectSim(changed, Opt, Turning);
        full.RunTo(9);
        var rebuilt = new EffectSim(changed, Opt, Turning);
        rebuilt.RunTo(6, exactAt: 6, only: new[] { 1 });
        rebuilt.Load(saved, only: new[] { 0, 2 });
        rebuilt.RunTo(9);
        Assert.Equal(Snapshot(full), Snapshot(rebuilt));
        Assert.Throws<ArgumentException>(() => new EffectSim(changed, Opt, Turning).Load(saved, only: new[] { 0 }));
    }
}
