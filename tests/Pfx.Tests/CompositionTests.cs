using System.Numerics;
using System.Text;
using Pfx.Core.Model;
using Pfx.Sim;

namespace Pfx.Tests;

// P10: the project file (composition), its state cycle and the simulator's run windows.
public class CompositionTests
{
    private static ProjectFile Zharka() => new()
    {
        Name = "zharka",
        Parts =
        {
            new PartEntry { Id = "flames", Path = "particles/flames.ptc" },
            new PartEntry { Id = "column", Path = "particles/column.ptc", Stop = 2.5f },
        },
        Lights = { new LightEntry { Id = "glow", Radius = 6, Brightness = 2 }, new LightEntry { Id = "flash", Life = 0.5f, FadeOut = 0.3f } },
        Hazes = { new HazeEntry { Id = "heat" } },
        Sounds = { new SoundEntry { Id = "burn", File = "sounds/fire_idle.ogg", Loop = true } },
        States =
        {
            new StateEntry { Name = "idle", Items = { new() { Ref = "flames" }, new() { Ref = "glow" }, new() { Ref = "heat" }, new() { Ref = "burn" } } },
            new StateEntry { Name = "active", Duration = 4, OverIdle = true, Items = { new() { Ref = "column", Delay = 0.2f }, new() { Ref = "flash" } } },
            new StateEntry { Name = "linger", Duration = 3, Items = { new() { Ref = "flames" } } },
        },
        Anomaly = { Archetype = "burn", Tick = 0.5f, Shells = { ["hit"] = new Shell { R = 2, YMax = 3 } } },
    };

    [Fact]
    public void A_version_1_project_opens_with_its_parts_as_entries()
    {
        var file = Composition.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"parts\":[\"particles/Fire Big.ptc\"],\"materials\":[]}"));
        var part = Assert.Single(file.Parts);
        Assert.Equal("particles/Fire Big.ptc", part.Path);
        Assert.Equal("fire_big", part.Id);
        Assert.Equal(ProjectFile.CurrentVersion, file.Version);
    }

    [Fact]
    public void A_project_round_trips_every_value()
    {
        var file = Zharka();
        file.Parts[0].PathKeys = new PathKeys { Times = new[] { 0f, 1f }, Points = new[] { new[] { 0f, 0, 0 }, new[] { 1f, 0, 2 } }, YawAlong = true };
        file.Lights[0].Anim = new LightAnim { Name = "pulse", Fps = 15, Frames = { new[] { 1f, 0.5f, 0.2f } } };
        file.Lights[0].Curve.Add(new[] { 0.5f, 0.25f });
        byte[] once = Composition.Bytes(file);
        byte[] twice = Composition.Bytes(Composition.Parse(once));
        Assert.Equal(Encoding.UTF8.GetString(once), Encoding.UTF8.GetString(twice));
        Assert.Contains("\"stop\": 2.5", Encoding.UTF8.GetString(once));
    }

    [Fact]
    public void Ids_are_safe_names()
    {
        Assert.Equal("fire_big_2", ProjectNames.Id("Fire Big (2)"));
        Assert.Equal("x3d", ProjectNames.Id("3D"));
        Assert.Equal("item", ProjectNames.Id("!!"));
        Assert.Equal("glow3", ProjectNames.Unique("glow", new[] { "glow", "glow2" }));
    }

    [Fact]
    public void The_cycle_places_states_and_item_runs()
    {
        var file = Zharka();
        var spans = StateCycle.Spans(file, 5);
        Assert.Equal(new[] { (0.0, 5.0), (5.0, 9.0), (9.0, 12.0) }, spans.Select(s => (s.From, s.To)));
        var runs = StateCycle.Runs(file, 5);
        // The idle items run on under the over-idle active state.
        Assert.Equal(9.0, runs.Single(r => r.Ref == "flames" && r.State == 0).End);
        var column = runs.Single(r => r.Ref == "column");
        Assert.Equal(5.2, column.Start, 5);
        Assert.Equal(5.2 + 2.5, column.HardStop!.Value, 5);
        // A light with a lifetime ends after life + fade-out.
        Assert.Equal(5.8, runs.Single(r => r.Ref == "flash").End, 5);
        Assert.Equal(12.0, runs.Single(r => r.Ref == "flames" && r.State == 2).End);
        var timing = StateCycle.Timing(file);
        Assert.Equal(4, timing["active"]);
        Assert.Equal(3, timing["linger"]);
        Assert.Equal(0.5f, timing["tick"]);
    }

    private static readonly EmitterParams Steady = new()
    {
        Name = "e", Shape = ShapeKind.Point, MaxNum = 5000, BirthRate = 50, LifeTime = 1, SizeMultiplier = 1, StretchMultiplier = 1,
        EffectTime = 10, Repeat = true,
    };

    [Fact]
    public void A_window_starts_late_soft_stops_and_hard_stops()
    {
        var o = new SimOptions { Window = new SimWindow(1, 2, null) };
        var sim = new EffectSim(new[] { Steady }, o);
        sim.RunTo(0.99f);
        Assert.Equal(0, sim.Count);
        sim.RunTo(1.99f);
        Assert.InRange(sim.Count, 40, 51);
        // Emitter clock started with the window.
        Assert.InRange(sim.Emitters[0].Time, 0.97f, 1.0f);
        long born = sim.Emitters[0].Born;
        sim.RunTo(2.5f);
        Assert.Equal(born, sim.Emitters[0].Born);
        Assert.InRange(sim.Count, 15, 35);
        sim.RunTo(3.1f);
        Assert.Equal(0, sim.Count);

        var hard = new EffectSim(new[] { Steady }, new SimOptions { Window = new SimWindow(0, null, 1.5f) });
        hard.RunTo(1.49f);
        Assert.True(hard.Count > 40);
        hard.RunTo(1.6f);
        Assert.Equal(0, hard.Count);
        hard.RunTo(4);
        Assert.Equal(0, hard.Count);
    }

    [Fact]
    public void A_windowed_simulation_is_exact_after_a_fast_forward()
    {
        var o = new SimOptions { Window = new SimWindow(0.5f, 3, 3.4f) };
        var full = new EffectSim(new[] { Steady with { Velocity = 2, ConeAngle = new Vector3(360, 0, 90) } }, o);
        var fast = new EffectSim(new[] { Steady with { Velocity = 2, ConeAngle = new Vector3(360, 0, 90) } }, o);
        full.RunTo(3.2f);
        fast.RunTo(3.2f, exactAt: 3.2f);
        var a = full.Render(0);
        var b = fast.Render(0);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i].Position, b[i].Position);
    }
}
