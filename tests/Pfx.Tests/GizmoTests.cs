using System.Numerics;
using Pfx.App.Viewport;
using Pfx.Core.Model;
using Pfx.Core.Text;
using Pfx.Render;
using Pfx.Sim;
using Xunit.Abstractions;

namespace Pfx.Tests;

// P8 acceptance in tests: the cone gizmo against the probed facts of prompt 2.3 and against emission directions sampled
// from the simulator, the shape gizmo against the simulator's spawn volume, and the path fits.
public class GizmoTests
{
    private readonly ITestOutputHelper _out;

    public GizmoTests(ITestOutputHelper output) => _out = output;

    private const float Rad = MathF.PI / 180f;

    private static EmitterView View(string keys) =>
        PtcDocument.From(EnfDocument.Parse("EffectDef {\n Emitors {\n  EmitorDef e {\n" + keys + "\n  }\n }\n}\n")).Emitters[0];

    private static string Vec(string key, Vector3 v) => FormattableString.Invariant($"   {key} {v.X} {v.Y} {v.Z}\n");

    private static EmitterFrame Frame(Vector3 cone, Vector3 angles, float sourceYaw = 0, Vector3 source = default)
    {
        var view = View(Vec("ConeAngle", cone) + Vec("Angles", angles));
        return new EmitterFrame(view, EmitterParams.From(view), source, sourceYaw);
    }

    // The gizmo's drawn direction for a cone value pair (unit, from the emitter origin).
    private static Vector3 Drawn(EmitterFrame f, float polar, float azimuth) =>
        Vector3.Normalize(GizmoTool.ConePoint(f, polar, azimuth, 1) - f.Origin);

    private static void Near(Vector3 want, Vector3 got, float eps = 1e-4f) =>
        Assert.True(Vector3.Distance(want, got) < eps, $"want {want}, got {got}");

    [Fact]
    public void The_cone_gizmo_draws_the_probed_directions()
    {
        float s = MathF.Sin(30 * Rad), c = MathF.Cos(30 * Rad);
        // 0 180 180 is horizontal along -X; emitter yaw 90 and source yaw 90 both turn it to +Z (clockwise from above).
        Near(-Vector3.UnitX, Drawn(Frame(new(0, 180, 180), default), 90, 0));
        Near(Vector3.UnitZ, Drawn(Frame(new(0, 180, 180), new(90, 0, 0)), 90, 0));
        Near(Vector3.UnitZ, Drawn(Frame(new(0, 180, 180), default, 90), 90, 0));
        // 0 90 90 = a jet 45 degrees above the horizon towards -X.
        Near(Vector3.Normalize(new Vector3(-1, 1, 0)), Drawn(Frame(new(0, 90, 90), default), 45, 0));
        // Pitch +30 tilts +Y towards -Z, roll +30 towards +X.
        Near(new Vector3(0, c, -s), Drawn(Frame(default, new(0, 30, 0)), 0, 0));
        Near(new Vector3(s, c, 0), Drawn(Frame(default, new(0, 0, 30)), 0, 0));
        // The azimuth range [0, A] runs from -X towards -Z.
        Near(-Vector3.UnitZ, Drawn(Frame(new(90, 180, 180), default), 90, 90));
    }

    // The band as the gizmo draws it: polar from its drawn axis, azimuth from its drawn azimuth-0 ray towards its
    // drawn azimuth-90 ray (not from Orientation directly, so a wrong drawing fails here).
    private static (float Polar, float Azimuth) InDrawnBand(EmitterFrame f, Vector3 d)
    {
        var y = Drawn(f, 0, 0);
        var x = Drawn(f, 90, 0);
        var z = Drawn(f, 90, 90);
        float polar = MathF.Acos(Math.Clamp(Vector3.Dot(d, y), -1, 1)) / Rad;
        float az = MathF.Atan2(Vector3.Dot(d, z), Vector3.Dot(d, x)) / Rad;
        return (polar, az < -1e-3f ? az + 360 : az);
    }

    public static IEnumerable<object[]> Bands() => new[]
    {
        new object[] { new Vector3(360, 0, 40), new Vector3(0, 0, 0), 0f },
        new object[] { new Vector3(90, 60, 120), new Vector3(0, 0, 0), 0f },
        new object[] { new Vector3(120, 80, 100), new Vector3(45, 0, 0), 0f },
        new object[] { new Vector3(60, 30, 90), new Vector3(0, 30, 0), 0f },
        new object[] { new Vector3(60, 30, 90), new Vector3(0, 0, -40), 0f },
        new object[] { new Vector3(200, 100, 160), new Vector3(30, 20, 10), 0f },
        new object[] { new Vector3(45, 90, 90), new Vector3(-60, 0, 0), 135f },
    };

    [Theory]
    [MemberData(nameof(Bands))]
    public void Simulated_emission_directions_lie_in_the_drawn_band_and_fill_it(Vector3 cone, Vector3 angles, float sourceYaw)
    {
        var f = Frame(cone, angles, sourceYaw);
        var p = f.Params with { Shape = ShapeKind.Point, MaxNum = 5000, BirthRate = 2000, LifeTime = 5, Velocity = 1, EffectTime = 10, Repeat = true };
        var sim = new EffectSim(new[] { p }, new SimOptions(), SourceMotion.Still(default, sourceYaw));
        sim.RunTo(1);
        var dirs = sim.Render(0).Select(r => Vector3.Normalize(r.Velocity)).ToList();
        Assert.True(dirs.Count > 1500);
        float minP = 999, maxP = -1, minA = 999, maxA = -1;
        foreach (var d in dirs)
        {
            var (polar, az) = InDrawnBand(f, d);
            minP = Math.Min(minP, polar);
            maxP = Math.Max(maxP, polar);
            if (polar > 0.5f && polar < 179.5f)
            {
                minA = Math.Min(minA, az);
                maxA = Math.Max(maxA, az);
            }
        }
        _out.WriteLine($"cone {cone} angles {angles} source yaw {sourceYaw}: polar {minP:0.00}..{maxP:0.00} (band {cone.Y / 2}..{cone.Z / 2}), azimuth {minA:0.00}..{maxA:0.00} (band 0..{cone.X})");
        // Inside: float rounding only. Filled: 2000 uniform draws leave less than 0.5 % of the range at either end.
        Assert.InRange(minP, cone.Y / 2 - 0.01f, cone.Y / 2 + Math.Max(0.6f, (cone.Z - cone.Y) / 2 * 0.005f));
        Assert.InRange(maxP, cone.Z / 2 - Math.Max(0.6f, (cone.Z - cone.Y) / 2 * 0.005f), cone.Z / 2 + 0.01f);
        if (cone.X < 360)
        {
            Assert.InRange(minA, -0.01f, cone.X * 0.005f + 0.01f);
            Assert.InRange(maxA, cone.X * 0.995f - 0.01f, cone.X + 0.01f);
        }
    }

    [Theory]
    [InlineData(ShapeKind.Box, 2f, 1f, 0.5f)]
    [InlineData(ShapeKind.Ellipse, 2f, 1f, 0.5f)]
    [InlineData(ShapeKind.Sphere, 2f, 1f, 0.5f)]
    [InlineData(ShapeKind.OnSphere, 2f, 1f, 0.5f)]
    public void The_shape_gizmo_spans_the_simulated_spawn_volume(ShapeKind shape, float x, float y, float z)
    {
        var f = Frame(new(0, 0, 0), new(30, 20, 0));
        var p = f.Params with { Shape = shape, ShapeSize = new(x, y, z), MaxNum = 5000, BirthRate = 3000, LifeTime = 5, Velocity = 0 };
        var sim = new EffectSim(new[] { p }, new SimOptions());
        sim.RunTo(1);
        var frame = f with { Params = p };
        Matrix4x4.Invert(frame.Axes, out var toLocal);
        var local = sim.Render(0).Select(r => Vector3.TransformNormal(r.Position - frame.Origin, toLocal)).ToList();
        var lines = new List<LineVertex>();
        SceneGeometry.Shape(lines, frame.Origin, frame.Axes, p, Vector4.One);
        var drawn = lines.Select(l => Vector3.TransformNormal(l.Position - frame.Origin, toLocal)).ToList();
        for (int k = 0; k < 3; k++)
        {
            float spawned = local.Max(v => MathF.Abs(v[k]));
            float wire = drawn.Max(v => MathF.Abs(v[k]));
            float reach = p.SpawnHalfExtent[k];
            _out.WriteLine($"{shape} axis {k}: simulated {spawned:0.0000}, wireframe {wire:0.0000}, handle {reach:0.0000}");
            // Box fills its corners uniformly; radial shapes reach the rim along an axis rarely (P(r |u| > 0.9) = 0.5 %,
            // ~15 of 3000 draws), so they are checked at 90 %.
            Assert.InRange(spawned, reach * (shape == ShapeKind.Box ? 0.97f : 0.9f), reach + 1e-4f);
            Assert.InRange(wire, reach - 1e-4f, reach + 1e-4f);
        }
    }

    // P8 in game (p8_order.ptc, top and front views): yaw 90 + pitch 30 leaned west, yaw 90 + roll 30 leaned south,
    // so yaw is applied after the other two.
    [Fact]
    public void Yaw_comes_after_pitch_and_roll_as_in_game()
    {
        float s = MathF.Sin(30 * Rad), c = MathF.Cos(30 * Rad);
        Near(new Vector3(-s, c, 0), Vector3.TransformNormal(Vector3.UnitY, Orientation.YawPitchRoll(new(90, 30, 0))));
        Near(new Vector3(0, c, -s), Vector3.TransformNormal(Vector3.UnitY, Orientation.YawPitchRoll(new(90, 0, 30))));
        Assert.Equal(KnownDifference.None, new EmitterParams { Angles = new(90, 30, 0) }.Differences & KnownDifference.AngleOrder);
        Assert.Equal(KnownDifference.AngleOrder, new EmitterParams { Angles = new(0, 30, 30) }.Differences & KnownDifference.AngleOrder);
    }

    [Fact]
    public void Aim_turns_the_band_onto_a_direction_with_yaw_and_cone_only()
    {
        foreach (var d in new[] { new Vector3(1, 1, 0), new Vector3(0, 0.3f, 1), new Vector3(-1, -0.5f, -1), new Vector3(0.2f, 1, -0.4f) })
        {
            var (yaw, cone) = Orientation.Aim(d, 0);
            var dirs = new EffectSim(new[] { new EmitterParams { Angles = new(yaw, 0, 0), ConeAngle = cone, Velocity = 1, BirthRate = 50, MaxNum = 100, LifeTime = 1, EffectTime = 10, Repeat = true } },
                new SimOptions());
            dirs.RunTo(0.5f);
            foreach (var r in dirs.Render(0))
                Near(Vector3.Normalize(d), Vector3.Normalize(r.Velocity), 1e-3f);
        }
        var (_, wide) = Orientation.Aim(new Vector3(1, 1, 0), 20);
        Assert.Equal(new Vector3(20 / MathF.Sin(45 * Rad), 70, 110).X, wide.X, 2);
        Assert.Equal(70, wide.Y, 3);
        Assert.Equal(110, wide.Z, 3);
    }

    [Theory]
    [InlineData(4f, 6f, 0f, 1f, 0f, 2.0f)]
    [InlineData(6f, 3f, 2f, 0.6f, 0.4f, 3.0f)]
    [InlineData(2f, 8f, -1f, 1.5f, 1.2f, 1.5f)]
    public void A_ballistic_flight_is_fitted_back_from_its_own_path(float vx, float vy, float vz, float g, float k, float life)
    {
        var o = new SimOptions();
        var start = new Vector3(1, 2, 3);
        var truth = Ballistics.Path(start, new Vector3(vx, vy, vz), g, k, life, o);
        // A drawn stroke: every fifth simulated point plus the end, wobbled by up to 1 cm (a hand at ~1 px).
        var rnd = new Random(7);
        var drawn = truth.Where((_, i) => i % 5 == 0).Append(truth[^1])
            .Select(p => p + new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * 0.02f).ToList();
        var fit = PathFit.Ballistic(drawn, start, life, o, 0.0005f);
        _out.WriteLine($"v {fit.Velocity} (true {vx} {vy} {vz}), g {fit.GravityMultiply:0.000} ({g}), k {fit.AirResistance:0.000} ({k}), rms {fit.Rms:0.0000} m, max {fit.Max:0.0000} m, rounds {fit.Iterations}, converged {fit.Converged}");
        // The flight is what is fitted: the fitted path stays within the wobble (up to 1.7 cm) of the true one. With strong drag on a
        // short life, speed and drag trade against each other inside that wobble, so the values are checked loosely.
        float off = truth.Max(p => PathFit.Nearest(p, fit.Path).Distance);
        _out.WriteLine($"true path to fitted path: max {off:0.0000} m");
        Assert.True(fit.Converged);
        Assert.True(fit.Max < 0.03f, $"max error {fit.Max}");
        Assert.True(off < 0.03f, $"fitted path {off} m from the true one");
        Assert.True(Vector3.Distance(fit.Velocity, new Vector3(vx, vy, vz)) < 0.05f * new Vector3(vx, vy, vz).Length());
        Assert.InRange(fit.GravityMultiply, g * 0.95f - 0.01f, g * 1.05f + 0.01f);
        Assert.InRange(fit.AirResistance, k * 0.8f - 0.05f, k * 1.2f + 0.05f);
    }

    // The shell check's flight (roadflare red_smoke, mean life 0.85 s) drawn on screen: every third step, snapped to a
    // 2.17 cm pixel grid in the drawing plane.
    [Fact]
    public void A_short_flight_drawn_in_pixels_is_fitted_within_a_pixel()
    {
        var o = new SimOptions();
        const float px = 0.0217f;
        var start = new Vector3(0, 0.1f, 0);
        var truth = Ballistics.Path(start, new Vector3(0, 5, 3), 1, 0.4f, 0.85f, o);
        var drawn = truth.Where((_, i) => i % 3 == 0).Append(truth[^1])
            .Select(p => new Vector3(p.X, MathF.Round(p.Y / px) * px, MathF.Round(p.Z / px) * px)).ToList();
        var fit = PathFit.Ballistic(drawn, start, 0.85f, o, px * 0.1f);
        float off = truth.Max(p => PathFit.Nearest(p, fit.Path).Distance);
        _out.WriteLine($"v {fit.Velocity}, g {fit.GravityMultiply:0.000}, k {fit.AirResistance:0.000}, rms {fit.Rms:0.0000}, max {fit.Max:0.0000}, true path {off:0.0000} m, rounds {fit.Iterations}");
        // Snapping moves a drawn point up to half a pixel diagonal (1.5 cm) off the flight; the fitted path itself
        // stays within a pixel of the true one.
        Assert.True(fit.Max < 2 * px, $"max error {fit.Max}");
        Assert.True(off < px, $"fitted path {off} m from the true one");
    }

    [Fact]
    public void A_drawn_helix_gives_radius_turn_rate_and_rise()
    {
        var c = new Vector3(1, 0, -2);
        var pts = new List<Vector3>();
        var times = new List<float>();
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 30f, h = 40 + 180 * t;
            pts.Add(c + new Vector3(3 * MathF.Sin(h * Rad), 0.5f + 0.01f * h, 3 * MathF.Cos(h * Rad)));
            times.Add(t);
        }
        var fit = PathFit.Orbit(pts, times, c);
        Assert.Equal(3, fit.Radius, 3);
        Assert.Equal(180, fit.DegreesPerSecond, 2);
        Assert.Equal(0.01f, fit.RisePerDegree, 4);
        Assert.Equal(40, fit.StartHeading, 2);
        Assert.True(fit.Max < 1e-4f);
    }

    [Fact]
    public void A_keyframed_source_follows_its_keys_and_loops()
    {
        var m = new SourceMotion.Keyframed(new[] { 0f, 1f, 3f }, new[] { Vector3.Zero, new Vector3(0, 0, 2), new Vector3(4, 0, 2) }, true, 0);
        var (p, yaw, v) = m.At(0.5f);
        Near(new Vector3(0, 0, 1), p);
        Assert.Equal(0, yaw, 3);
        Near(new Vector3(0, 0, 2), v);
        (p, yaw, _) = m.At(2f);
        Near(new Vector3(2, 0, 2), p);
        Assert.Equal(90, yaw, 3);
        (p, _, _) = m.At(3.5f);
        Near(new Vector3(0, 0, 1), p);
    }

    [Fact]
    public void Path_windows_fire_the_chain_in_turn()
    {
        const int n = 5;
        for (int i = 0; i < n; i++)
        {
            var g = Graph.FromNumbers(PathTool.Window(i, n), 1);
            float peak = (i + 0.5f) / n;
            Assert.Equal(1, g.Evaluate(peak), 4);
            // Away from its own window the emitter is off (the spline's dips below 0 clamp to 0).
            for (float t = 0; t <= 1; t += 0.01f)
                if (MathF.Abs(t - peak) >= 1f / n)
                    Assert.Equal(0, g.Evaluate(t), 4);
        }
    }

    [Fact]
    public void Rounding_follows_what_a_pixel_moves()
    {
        Assert.Equal(0.001, ToolEdits.Precision(0.0042), 12);
        Assert.Equal(0.01, ToolEdits.Precision(0.01), 12);
        Assert.Equal(0.3, ToolEdits.Round(0.1 + 0.2, 0.001));
        Assert.Equal("0.3", Pfx.Core.Editing.EnfEdits.Number(ToolEdits.Round(0.1 + 0.2, 0.001)));
    }
}
