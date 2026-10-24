using System.Globalization;
using System.Numerics;
using Pfx.App.Lint;
using Pfx.Core.Editing;
using Pfx.Core.Generators;
using Pfx.Core.Model;
using Pfx.Sim;
using Pfx.XRay;

namespace Pfx.App.Recipes;

// The recipes of spec 3.10 (first set and the R0 set). Each class says where its numbers come from; statuses follow
// what the CG anomaly journal recorded in game (research of 2026-10-15 for P12b).
public static class RecipeCatalog
{
    public static IReadOnlyList<Recipe> All { get; } = new Recipe[]
    {
        new Swirl(), new OrbitTurn(), new Strands(), new Funnel(), new AddLight(), new HeatHaze(), new Flash(), new Tint(),
        new AroundDust(), new StopSplit(), new SoftGround(), new PaletteMatch(), new GroundGlow(), new Spawner(), new AddSound(),
        new NearOnly(), new SingleColumn(), new OneBody(), new Strikes(), new StripGate(), new MakeLit(), new MakeVolumetric(),
        new FixOrder(), new SortInside(),
        new GenTornado(), new GenSparks(), new GenEmbers(), new GenFog(), new GenBurstRing(), new GenFlash(), new GenArc(), new GenVortex(), new GenHeat(),
        new GenGlow(),
    };

    public static Recipe? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    // Lint rows whose fix is a recipe (spec 3.10: "lint rows that name a recipe get a button that applies it").
    public static IReadOnlyDictionary<string, string> ForLint { get; } = new Dictionary<string, string>
    {
        ["FarShimmer"] = "near_only", ["StackedAxis"] = "single_column", ["TurnRates"] = "one_body", ["BirthGate"] = "strip_gate",
        ["DenseNoSort"] = "sort_inside", ["CloseAlpha"] = "fix_order", ["EmissiveSmoke"] = "make_lit", ["BigCard"] = "make_volumetric",
        ["ZEnableHard"] = "soft_ground",
    };

    internal static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    internal static EmitterParams Params(RecipeContext c, EmitterView e) => EmitterParams.From(e, c.Scene.Tiles);

    // Sprite size at its largest (the size graphs are multipliers of SizeMultiplier).
    internal static float Size(EmitterParams p) => p.SizeMultiplier * Linter.Peak(p.Size) * Linter.Peak(p.SizeMast);

    internal static bool Moves(PartEntry p) => p.Spin != 0 || p.PathKeys is { Points.Length: > 0 };

    // A copy of an emitter's material with some keys changed, written beside the part (materials\<stem>_<suffix>.emat,
    // reused when the same bytes are already there): vanilla and donor files are never edited in place. Returns the game
    // path the emitter should then use, or null (material not found; noted).
    internal static string? CopyMaterial(RecipeContext c, Target t, string suffix, Func<EmatDocument, IEnumerable<IEdit>> change)
    {
        string? game = t.Emitter!.Text("Material");
        if (game != null && CopyFrom(c, t, game, suffix, change) is { } copy)
            return copy;
        c.Note("Rcp.Note.NoMaterial", t.Emitter.Name, game ?? "—");
        return null;
    }

    // ---------------------------------------------------------------- motion

    // Tornado v4 (seen 2026-10-13): LocalTransform 1 emitters on a script-turned source. Rankine profile (seen the same
    // day): parts at radius r outside the core turn at Γ/r², Γ = core rate × core radius² (own_tornado RING_SPIN).
    public sealed class Swirl : Recipe
    {
        public override string Id => "swirl";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("rate", -150, -3600, 3600), RecipeParam.Flag("rankine", false), RecipeParam.Num("core", 10, 0.1, 1000),
        };

        public override void Apply(RecipeContext c)
        {
            var parts = c.Parts();
            double rate = c.Number("rate"), core = c.Number("core");
            double gamma = rate * core * core;
            for (int i = 0; i < parts.Count; i++)
            {
                var t = parts[i];
                double r = parts[i].Doc.Emitters.Select(e => Params(c, e).Offset).Select(o => Math.Sqrt(o.X * o.X + o.Z * o.Z)).DefaultIfEmpty(0).Average();
                double own = i > 0 && c.Flag("rankine") && r > core ? Math.Round(gamma / (r * r), 2) : rate;
                c.SetField(t.Part.Id, "spin", (float)own);
                foreach (var e in t.Doc.Emitters)
                    c.Set(t with { Emitter = e }, "LocalTransform", 1);
            }
        }
    }

    // xray.py's attractor compensation: a part whose particles an X-Ray orbit action would bend turns instead, by the
    // wanted angle over the longest particle life. Seen in game 2026-10-15 (P12c pair_orbit).
    public sealed class OrbitTurn : Recipe
    {
        public override string Id => "orbit_turn";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("angle", 90, -3600, 3600) };

        public override void Apply(RecipeContext c)
        {
            foreach (var t in c.Parts())
            {
                float life = t.Doc.Emitters.Select(e => Params(c, e)).Select(p => p.LifeTime + p.LifeTimeRnd).DefaultIfEmpty(1).Max();
                c.SetField(t.Part.Id, "spin", (float)Math.Round(c.Number("angle") / Math.Max(life, 0.01f), 2));
                foreach (var e in t.Doc.Emitters)
                    c.Set(t with { Emitter = e }, "LocalTransform", 1);
            }
        }
    }

    internal static TornadoSettings Tornado(RecipeContext c) => new()
    {
        FunnelHeight = c.Number("height"), FunnelBase = c.Number("base"), FunnelTop = c.Number("top"), StrandSpeed = c.Number("speed"),
        Strands = (int)c.Number("count"), ColumnPuffs = (int)c.Number("puffs"), Dust = c.Text("material"),
    };

    internal static readonly RecipeParam[] FunnelParams =
    {
        RecipeParam.Num("height", 65, 1, 500), RecipeParam.Num("base", 10, 0.1, 200), RecipeParam.Num("top", 22, 0.1, 400),
        RecipeParam.Num("speed", 6, 0.1, 100), RecipeParam.Material("material", "graphics/particles/materials/smoke/smoke_dust_04.emat"),
    };

    // own_tornado.py STRANDS (seen in tornado v4 2026-10-13, merged into the turning body 2026-10-14): jets on a ring
    // of the base radius rising along the wall slope, emitter yaw = heading − 270.
    public sealed class Strands : Recipe
    {
        public override string Id => "strands";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<RecipeParam> Params => FunnelParams.Concat(new[] { RecipeParam.Num("count", 5, 1, 32), RecipeParam.Num("puffs", 540, 1, 10000) }).ToArray();

        public override void Apply(RecipeContext c)
        {
            var part = c.Parts()[0];
            foreach (var text in TornadoGenerator.Group(Tornado(c), "strands"))
                c.AddEmitter(part.Part.Id, text);
        }
    }

    // own_tornado.py funnel (seen 2026-10-13 16:20 and 17:35): one column emitter whose puffs rise along the wall slope
    // and grow from the base radius to the top, a ground billow, and optionally the spreading cap.
    public sealed class Funnel : Recipe
    {
        public override string Id => "funnel";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<RecipeParam> Params => FunnelParams.Concat(new[]
        {
            RecipeParam.Num("puffs", 540, 1, 10000), RecipeParam.Num("count", 5, 1, 32), RecipeParam.Flag("billow", true), RecipeParam.Flag("cap", false),
        }).ToArray();

        public override void Apply(RecipeContext c)
        {
            var part = c.Parts()[0];
            var s = Tornado(c);
            var groups = new List<string> { "column" };
            if (c.Flag("billow"))
                groups.Add("billow");
            if (c.Flag("cap"))
                groups.Add("cap");
            foreach (var g in groups)
                foreach (var text in TornadoGenerator.Group(s, g))
                    c.AddEmitter(part.Part.Id, text);
        }
    }

    // ---------------------------------------------------------------- light, haze, flash, screen

    // Showcase point lights (V8-V13, seen): colour from the emitters' colour graph at its brightest, radius from where
    // the particles actually go (the simulator's farthest particle over one cycle), at the emitters' mean offset.
    public sealed class AddLight : Recipe
    {
        public override string Id => "add_light";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<string> Needs => new[] { "script", "light" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("brightness", 1, 0, 100000), RecipeParam.Num("flicker", 0, 0, 1), RecipeParam.Num("radius", 0, 0, 1000),
        };

        public override void Apply(RecipeContext c)
        {
            var targets = c.Emitters();
            var ps = targets.Select(t => Params(c, t.Emitter!)).ToList();
            var col = new Vector3();
            foreach (var p in ps)
                col += new Vector3(Linter.Peak(p.Color, 0), Linter.Peak(p.Color, 1), Linter.Peak(p.Color, 2));
            float top = Math.Max(Math.Max(col.X, col.Y), Math.Max(col.Z, 1e-3f));
            var at = ps.Select(p => p.Offset).Aggregate(Vector3.Zero, (a, b) => a + b) / ps.Count;
            double radius = c.Number("radius") > 0 ? c.Number("radius") : Reach(ps);
            var part = targets[0].Part;
            var light = c.AddItem(new LightEntry
            {
                Id = c.NewId("light"), Offset = new[] { at.X, at.Y, at.Z }, Color = new[] { col.X / top, col.Y / top, col.Z / top },
                Brightness = (float)c.Number("brightness"), Radius = (float)Math.Round(Math.Max(radius, 1), 1), FlickerAmp = (float)c.Number("flicker"),
                FlickerSpeed = c.Number("flicker") > 0 ? 10 : 0,
            });
            c.AddToStatesOf(part.Id, light.Id);
        }

        // The farthest a particle gets from the source while the emitters play one cycle (still source, 30 steps per
        // second: positions move less than a metre per step at the speeds of particle effects).
        internal static double Reach(IReadOnlyList<EmitterParams> ps)
        {
            var sim = new EffectSim(ps, new SimOptions { Step = 1f / 30 });
            float end = Math.Min(ps.Max(p => (p.EffectTime > 0 ? p.EffectTime : 10) + p.LifeTime + p.LifeTimeRnd), 60);
            double far = 0;
            while (sim.Time < end)
            {
                sim.Step();
                for (int i = 0; i < sim.Emitters.Count; i++)
                    foreach (var r in sim.Render(i))
                        far = Math.Max(far, r.Position.Length() + r.Size / 2);
            }
            return far;
        }
    }

    // CG_AnomShowLightSpec.Haze: a near-black light with heat haze; radius 2 and power 0.01-0.05 are the values the
    // showcase uses (tramplin haze seen by the owner 2026-10-11).
    public sealed class HeatHaze : Recipe
    {
        public override string Id => "haze";
        public override RecipeScope Scope => RecipeScope.Project;
        public override IReadOnlyList<string> Needs => new[] { "script", "light" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("radius", 2, 0.05, 50), RecipeParam.Num("power", 0.02, 0, 0.2), RecipeParam.Num("height", 0.5, -50, 200), RecipeParam.State(),
        };

        public override void Apply(RecipeContext c)
        {
            var h = c.AddItem(new HazeEntry { Id = c.NewId("haze"), At = new[] { 0, (float)c.Number("height"), 0 }, Radius = (float)c.Number("radius"), Power = (float)c.Number("power") });
            if (c.State() is { } state)
                c.AddToState(state, h.Id);
        }
    }

    // CG_AnomFlash (D14, seen 2026-10-13): the vanilla flash grenade's blinding for a player whose head sees the point,
    // full within 3 m and fading to 30 m (the showcase values), with the strike's short white light (0.2 s, tornado).
    public sealed class Flash : Recipe
    {
        public override string Id => "flash";
        public override RecipeScope Scope => RecipeScope.Project;
        public override IReadOnlyList<string> Needs => new[] { "script", "light" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("full", 3, 0, 100), RecipeParam.Num("max", 30, 0.1, 500), RecipeParam.Num("height", 1.5, -50, 200),
            RecipeParam.Num("glow", 0.2, 0, 10), RecipeParam.State(),
        };

        public override void Apply(RecipeContext c)
        {
            float y = (float)c.Number("height");
            var f = c.AddItem(new FlashEntry { Id = c.NewId("flash"), At = new[] { 0, y, 0 }, Full = (float)c.Number("full"), Max = (float)Math.Max(c.Number("max"), c.Number("full")) });
            string? state = c.State();
            if (state == null)
                return;
            c.AddToState(state, f.Id);
            if (c.Number("glow") > 0)
            {
                var l = c.AddItem(new LightEntry
                {
                    Id = c.NewId("flash_light"), Offset = new[] { 0, y, 0 }, Color = new[] { 1f, 1f, 1f }, Radius = (float)c.Number("max"),
                    Life = (float)c.Number("glow"), FadeOut = 0,
                });
                c.AddToState(state, l.Id);
            }
        }
    }

    // CG_AnomTint (tornado, seen 2026-10-13 16:20): the screen multiplied by 1 : 0.5 : 0.2 (G_CUT 0.5, B_CUT 0.8) while
    // the camera is inside the radius, faded in and out over 3 s (FADE_TIME).
    public sealed class Tint : Recipe
    {
        public override string Id => "tint";
        public override RecipeScope Scope => RecipeScope.Project;
        public override IReadOnlyList<string> Needs => new[] { "script", "ppe" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("radius", 60, 0.5, 5000), RecipeParam.Num("g", 0.5, 0, 1), RecipeParam.Num("b", 0.2, 0, 1), RecipeParam.Num("fade", 3, 0, 60), RecipeParam.State(),
        };

        public override void Apply(RecipeContext c)
        {
            var s = c.AddItem(new ScreenEntry { Id = c.NewId("tint"), Color = new[] { 1f, (float)c.Number("g"), (float)c.Number("b") }, Radius = (float)c.Number("radius"), Fade = (float)c.Number("fade") });
            if (c.State() is { } state)
                c.AddToState(state, s.Id);
        }
    }

    // tornado_cg_around (seen 2026-10-13, visibility 4.9-6.0 m measured): the part plays at the ground under the camera
    // within the radius, turned along the swirl Γ = core rate × core radius² (own_tornado SWIRL) with speed Γ / r.
    public sealed class AroundDust : Recipe
    {
        public override string Id => "around";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("radius", 100, 1, 5000), RecipeParam.Num("rate", -150, -3600, 3600), RecipeParam.Num("core", 10, 0.1, 1000),
        };

        public override void Apply(RecipeContext c)
        {
            double gamma = c.Number("rate") * Math.PI / 180 * c.Number("core") * c.Number("core");
            foreach (var t in c.Parts())
            {
                c.SetField(t.Part.Id, "around", (float)c.Number("radius"));
                c.SetField(t.Part.Id, "swirl", (float)Math.Round(gamma, 1));
                c.SetField(t.Part.Id, "swirl_min", (float)c.Number("core"));
            }
        }
    }

    // ---------------------------------------------------------------- splitting and parts

    // V11 (xray.py split by stop time): emitters that must end at another time go to their own part with an IMMEDIATE
    // stop. Built in CG 2026-10-12, seen in game 2026-10-15 (P12c pair_split).
    public sealed class StopSplit : Recipe
    {
        public override string Id => "stop_split";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("stop", 2, 0.01, 3600) };

        public override void Apply(RecipeContext c) => MoveToNewPart(c, "_s", "stop", (float)c.Number("stop"));
    }

    // Moves the targets into a new part beside their part (same turn, states and delays) and sets one script value.
    internal static void MoveToNewPart(RecipeContext c, string suffix, string field, float value)
    {
        var targets = c.Emitters();
        var from = targets[0].Part;
        var part = c.NewPart(from.Id + suffix, from);
        foreach (var t in targets.Where(t => t.Part.Id == from.Id))
            c.MoveEmitter(from.Id, t.Emitter!.Name, part.Id);
        c.SetField(part.Id, field, value);
        c.AddToStatesOf(from.Id, part.Id);
    }

    // Tornado 2026-10-14 (seen, 40 crossings measured): small or fast emitters on their own source whose births switch
    // on and off at the near radius, so they never shimmer far away.
    public sealed class NearOnly : Recipe
    {
        public override string Id => "near_only";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("near", 150, 1, 5000) };

        public override void Apply(RecipeContext c) => MoveToNewPart(c, "_near", "near", (float)c.Number("near"));
    }

    // Tornado 2026-10-14 13:15 (measured): every part of a turning body in one LocalTransform 1 part at one rate; the
    // emptied parts leave the project.
    public sealed class OneBody : Recipe
    {
        public override string Id => "one_body";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "script" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("rate", 0, -3600, 3600) };

        public override string? Why(RecipeContext c) => c.Parts().Count < 2 ? "Rcp.Why.TwoParts" : base.Why(c);

        public override void Apply(RecipeContext c)
        {
            var parts = c.Parts();
            var body = parts[0];
            float rate = c.Number("rate") != 0 ? (float)c.Number("rate") : body.Part.Spin;
            foreach (var t in parts.Skip(1))
            {
                foreach (var e in t.Doc.Emitters.ToList())
                    c.MoveEmitter(t.Part.Id, e.Name, body.Part.Id);
                c.RemoveItem(t.Part.Id);
            }
            c.SetField(body.Part.Id, "spin", rate);
            foreach (var e in body.Doc.Emitters)
                c.Set(body with { Emitter = e }, "LocalTransform", 1);
        }
    }

    // Tornado 2026-10-14 13:15 (seen, kept by the owner): the part plays only as random bursts (every 3-8 s, 0.2 s, at a
    // random height on the camera-facing side) with a short white light, instead of all the time.
    public sealed class Strikes : Recipe
    {
        public override string Id => "strikes";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "script", "light" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("height", 10, 0, 1000), RecipeParam.Num("low", 2, 0, 1000), RecipeParam.Num("high", 4, 0, 1000),
            RecipeParam.Num("gap_min", 3, 0.05, 3600), RecipeParam.Num("gap_max", 8, 0.05, 3600), RecipeParam.Num("burst", 0.2, 0.01, 60),
            RecipeParam.Num("light", 30, 0, 1000), RecipeParam.Num("brightness", 1, 0, 100000),
        };

        public override void Apply(RecipeContext c)
        {
            var part = c.Parts()[0].Part;
            var states = c.Project.States.Where(s => s.Items.Any(x => x.Ref == part.Id)).Select(s => s.Name).ToList();
            if (states.Count == 0 && c.Project.States.Count > 0)
                states.Add(c.Project.States[0].Name);
            c.RemoveFromStates(part.Id);
            var k = c.AddItem(new StrikeEntry
            {
                Id = c.NewId(part.Id + "_strikes"), Part = part.Id, Height = (float)c.Number("height"), RadiusLow = (float)c.Number("low"),
                RadiusHigh = (float)c.Number("high"), GapMin = (float)c.Number("gap_min"), GapMax = (float)Math.Max(c.Number("gap_max"), c.Number("gap_min")),
                Duration = (float)c.Number("burst"), LightRadius = (float)c.Number("light"), LightBrightness = (float)c.Number("brightness"),
            });
            foreach (var s in states)
                c.AddToState(s, k.Id);
        }
    }

    // ---------------------------------------------------------------- materials and colour

    // The global X-Ray soft rule (seen with the flash 2026-10-13 and voronka 17:20): Softness 1 and CameraBlendFar =
    // half the sprite size, so a billboard fades where it cuts the ground and near the camera.
    public sealed class SoftGround : Recipe
    {
        public override string Id => "soft_ground";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<string> Needs => new[] { "file" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("softness", 1, 0, 10) };

        public override void Apply(RecipeContext c)
        {
            foreach (var t in c.Emitters())
            {
                float half = Size(Params(c, t.Emitter!)) / 2;
                if (CopyMaterial(c, t, "soft", m => new[] { m.Set("Softness", c.Number("softness")), m.Set("CameraBlendFar", Math.Round(half, 3)) }) is { } game)
                    c.SetString(t, "Material", game);
            }
        }
    }

    // Tornado white parts (seen 2026-10-13 17:20, later replaced by one material): lit materials differ in brightness by
    // their emat Color (P4: Color, not Diffuse); the targets' colour graphs are scaled by reference ÷ own.
    public sealed class PaletteMatch : Recipe
    {
        public override string Id => "palette_match";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Material("reference", "graphics/particles/materials/smoke/smoke_dust_04.emat") };

        public override void Apply(RecipeContext c)
        {
            double Brightness(string? game)
            {
                string? disk = game != null ? c.Scene.Resolve(game) : null;
                if (disk == null)
                    return double.NaN;
                var m = EmatDocument.Load(disk);
                var v = m.Numbers("Color") ?? new double[] { 1, 1, 1 };
                return v.Take(3).Average();
            }
            double reference = Brightness(c.Text("reference"));
            if (double.IsNaN(reference))
            {
                c.Note("Rcp.Note.NoMaterial", "—", c.Text("reference"));
                return;
            }
            foreach (var t in c.Emitters())
            {
                double own = Brightness(t.Emitter!.Text("Material"));
                if (double.IsNaN(own) || own <= 0 || Math.Abs(own - reference) < 1e-6)
                    continue;
                double k = reference / own;
                var nums = t.Emitter.Numbers("Color") ?? new double[] { 0, 1, 1, 1 };
                var scaled = nums.Select((v, i) => i % 4 == 0 ? v : Math.Round(Math.Min(1, v * k), 4)).ToList();
                c.SetGraph(t, "Color", scaled);
            }
        }
    }

    // A flat sprite lying on the ground (BillboardingType None at Angles 0 90 0, the vanilla decal pattern) with a glow
    // material, under the effect. A soft material fades a sprite over Softness × half its size of depth gap (R0/P4), and
    // a sprite lying 5 cm over the ground has almost none: with glow1.emat (Softness 2) the glow showed neither in the
    // viewport nor in game (P12c pair_glow). The sprite uses a copy of the material without Softness. A None sheet turns
    // about the emitter Y with its particle angle (R0), which lies flat after the pitch: a random angle stood the glow on
    // edge, so the sprite keeps angle 0 like the vanilla flat sprites (puddles, the grenade's flat flash). Seen in game
    // at noon 2026-10-16 (P12d pair_glow).
    public sealed class GroundGlow : Recipe
    {
        public override string Id => "ground_glow";
        public override RecipeScope Scope => RecipeScope.Parts;
        public override IReadOnlyList<string> Needs => new[] { "file" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Num("size", 4, 0.1, 100), RecipeParam.Material("material", "graphics/particles/materials/glow/glow1.emat"), RecipeParam.Num("life", 2, 0.05, 60),
        };

        public override void Apply(RecipeContext c)
        {
            double life = c.Number("life");
            var part = c.Parts()[0];
            string material = c.Text("material");
            if (c.Scene.Resolve(material) is not { } disk)
                c.Note("Rcp.Note.NoMaterial", "ground_glow", material);
            else if (EmatDocument.Load(disk).Block.FindLine("Softness") != null)
                material = CopyFrom(c, part, material, "flat", m => new[] { m.Reset("Softness")! }) ?? material;
            string text = string.Join("\n", "", "  EmitorDef ground_glow {", "   ShapeType Point", "   MaxNum " + (int)Math.Ceiling(2 * life * 1.2), "   BirthRate 2",
                "   Offset 0 0.05 0", "   Angles 0 90 0", "   Material \"" + material + "\"", "   SizeMultiplier " + F(c.Number("size")),
                "   BillboardingType None", "   Velocity 0", "   GravityMultiply 0", "   LifeTime " + F(life), "   Alpha {", "    0 0 0.3 1 0.7 1 1 0", "   }",
                "   EffectTime 10", "   Repeat 1", "  }");
            c.AddEmitter(part.Part.Id, text);
        }
    }

    // xray.py child() for an "on play" child: an undrawn parent only spawns; each child becomes a sibling at the
    // parent's offset whose rate and cap are multiplied by the parent's live count and cap. The first target is the
    // parent (removed), the rest are its children. Built for X-Ray conversions 2026-10-12, seen in game 2026-10-15
    // (P12c pair_spawner).
    public sealed class Spawner : Recipe
    {
        public override string Id => "spawner";
        public override RecipeScope Scope => RecipeScope.Emitters;

        public override string? Why(RecipeContext c) => c.Emitters().Count < 2 ? "Rcp.Why.ParentChild" : base.Why(c);

        public override void Apply(RecipeContext c)
        {
            var targets = c.Emitters();
            var parent = Params(c, targets[0].Emitter!);
            double live = Math.Min(Math.Max(parent.MaxNum, 1), Linter.Rate(parent) * Math.Max(parent.LifeTime, 1e-3));
            foreach (var t in targets.Skip(1))
            {
                var p = Params(c, t.Emitter!);
                c.Set(t, "BirthRate", Math.Round(p.BirthRate * live, 4));
                c.Set(t, "MaxNum", (double)Math.Max(p.MaxNum, 1) * Math.Max(parent.MaxNum, 1));
                c.Set(t, "Offset", parent.Offset.X + p.Offset.X, parent.Offset.Y + p.Offset.Y, parent.Offset.Z + p.Offset.Z);
            }
            c.RemoveEmitter(targets[0].Part.Id, targets[0].Emitter!.Name);
        }
    }

    // V9 sounds: a sound shader and set from an .ogg; an X-Ray .ogg brings its range and volume from its Vorbis comment
    // (xray.py ogg_params). The exported sound set played in game 2026-10-15 (P12c probe log: "playing true").
    public sealed class AddSound : Recipe
    {
        public override string Id => "add_sound";
        public override RecipeScope Scope => RecipeScope.Project;
        public override IReadOnlyList<string> Needs => new[] { "script", "sound" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Path("file"), RecipeParam.Flag("loop", true), RecipeParam.Num("delay", 0, 0, 3600), RecipeParam.State() };

        public override string? Why(RecipeContext c) => c.Text("file").Length == 0 ? "Rcp.Why.NoFile" : null;

        public override void Apply(RecipeContext c)
        {
            string file = c.Text("file");
            var s = new SoundEntry { Id = c.NewId(Path.GetFileNameWithoutExtension(file)), File = file, Loop = c.Flag("loop") };
            string disk = c.Scene.Folder != null && !Path.IsPathRooted(file) ? Path.Combine(c.Scene.Folder, file) : file;
            if (File.Exists(disk))
            {
                using var f = File.OpenRead(disk);
                var head = new byte[(int)Math.Min(f.Length, XrRecognize.HeadBytes)];
                f.ReadExactly(head);
                if (XrSoundParams.Read(head) is { } x)
                {
                    if (x.Range is double range)
                        s.Range = (float)range;
                    if (x.Volume is double volume)
                        s.Volume = (float)volume;
                }
            }
            c.AddItem(s);
            if (c.State() is { } state)
                c.AddToState(state, s.Id, (float)c.Number("delay"));
        }
    }

    // Tornado 2026-10-14 11:20 (measured in game): emitters stacked on one axis swap order under a turn; one emitter at
    // the base whose puffs rise to the top along the wall slope and grow to the top size keeps the order. Same live
    // puff count; rate = count / life, speed = height / (life · cos slope) as own_tornado's column.
    public sealed class SingleColumn : Recipe
    {
        public override string Id => "single_column";
        public override RecipeScope Scope => RecipeScope.Emitters;

        public override string? Why(RecipeContext c)
        {
            var t = c.Emitters();
            if (t.Count < 2)
                return "Rcp.Why.TwoEmitters";
            var ys = t.Select(x => Params(c, x.Emitter!).Offset.Y).ToList();
            return ys.Max() - ys.Min() < 0.01f ? "Rcp.Why.NotStacked" : null;
        }

        public override void Apply(RecipeContext c)
        {
            var targets = c.Emitters().Where(t => t.Part.Id == c.Emitters()[0].Part.Id).ToList();
            var ps = targets.Select(t => (T: t, P: Params(c, t.Emitter!))).OrderBy(x => x.P.Offset.Y).ToList();
            var low = ps[0];
            var high = ps[^1];
            double h = high.P.Offset.Y - low.P.Offset.Y;
            double rLow = low.P.ShapeSize.X / 2, rHigh = high.P.ShapeSize.X / 2;
            double slope = Math.Atan((rHigh - rLow) / h) * 180 / Math.PI;
            double life = ps.Max(x => x.P.LifeTime);
            double live = ps.Sum(x => Linter.Rate(x.P) * x.P.LifeTime);
            double rate = live / life;
            double speed = h / (life * Math.Cos(slope * Math.PI / 180));
            double sizeLow = Size(low.P), sizeHigh = Size(high.P);
            string part = low.T.Part.Id;
            string name = c.AddEmitter(part, RecipeScene.Text(low.T.Emitter!.Block), "column");
            var column = new Target(low.T.Part, low.T.Doc, low.T.Doc.Emitter(name));
            c.SetWord(column, "ShapeType", "Ellipse");
            c.Set(column, "ShapeSize", 2 * rLow, 0, 2 * rLow);
            c.Set(column, "ConeAngle", 360, Math.Round(2 * Math.Abs(slope), 3), Math.Round(2 * Math.Abs(slope), 3));
            c.Set(column, "BirthRate", Math.Round(rate, 3));
            c.Set(column, "BirthRateRND", 0);
            c.Set(column, "MaxNum", Math.Ceiling(rate * life * 1.2));
            c.Set(column, "Velocity", Math.Round(speed, 3));
            c.Set(column, "VelocityRND", Math.Round(speed * 0.25, 3));
            c.Set(column, "GravityMultiply", 0);
            c.Set(column, "LifeTime", Math.Round(life, 3));
            c.Set(column, "SizeMultiplier", Math.Round(sizeHigh, 3));
            c.SetGraph(column, "Size", new[] { 0, Math.Round(sizeLow / Math.Max(sizeHigh, 1e-3), 3), 1, 1 });
            foreach (var x in ps)
                c.RemoveEmitter(part, x.T.Emitter!.Name);
        }
    }

    // X-Ray CV effects keep births off at the start with BRateMast (2.11); removing the graph fires them at once. On the
    // tornado the stripped CV bolts stayed invisible at noon for another reason. Births at once seen in game 2026-10-15
    // (P12c pair_gate / pair_gate_late).
    public sealed class StripGate : Recipe
    {
        public override string Id => "strip_gate";
        public override RecipeScope Scope => RecipeScope.Emitters;

        public override void Apply(RecipeContext c)
        {
            foreach (var t in c.Emitters().Where(t => t.Emitter!.Block.FindBlock("BRateMast") != null))
                c.RemoveKey(t, "BRateMast");
        }
    }

    // R0: volume comes only from per-pixel lighting with a normal map. The targets use a copy of a lit material (the
    // vanilla lit cloud by default) with BidirLightScale 0.7 (R0's 0.6-0.8). Seen in game on a still source 2026-10-15
    // (P12c pair_lit); on moving sources the lit tornado blinked (2.11), so moving parts are refused.
    public sealed class MakeLit : Recipe
    {
        public override string Id => "make_lit";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<string> Needs => new[] { "file" };
        public override IReadOnlyList<RecipeParam> Params => new[]
        {
            RecipeParam.Material("material", "graphics/particles/materials/smoke/smoke_cloud_01_b.emat"), RecipeParam.Num("bidir", 0.7, 0, 1),
        };

        public override string? Why(RecipeContext c) => c.Emitters().Any(t => Moves(t.Part)) ? "Rcp.Why.Moving" : base.Why(c);

        public override void Apply(RecipeContext c)
        {
            foreach (var t in c.Emitters())
            {
                if (CopyFrom(c, t, c.Text("material"), "lit", m => new[] { m.Set("BidirLightScale", c.Number("bidir")) }) is { } game)
                    c.SetString(t, "Material", game);
                else
                    c.Note("Rcp.Note.NoMaterial", t.Emitter!.Name, c.Text("material"));
            }
        }
    }

    internal static string? CopyFrom(RecipeContext c, Target t, string material, string suffix, Func<EmatDocument, IEnumerable<IEdit>> change)
    {
        string? disk = c.Scene.Resolve(material);
        if (disk == null)
            return null;
        var emat = EmatDocument.Load(disk);
        foreach (var e in change(emat))
            e.Apply();
        byte[] bytes = emat.ToBytes();
        string folder = Path.Combine(Path.GetDirectoryName(c.Scene.PartDisk(t.Part))!, "materials");
        string stem = ProjectNames.Id(Path.GetFileNameWithoutExtension(disk)) + "_" + suffix;
        string name = stem + ".emat";
        for (int n = 2; File.Exists(Path.Combine(folder, name)) && !File.ReadAllBytes(Path.Combine(folder, name)).AsSpan().SequenceEqual(bytes); n++)
            name = stem + n + ".emat";
        string path = Path.Combine(folder, name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, bytes);
            c.MadeFile(path, bytes);
        }
        return (c.Scene.GamePath(folder) ?? "materials") + "/" + name;
    }

    // R0 "make it volumetric": one huge card becomes k² smaller puffs (size ÷ k, births × k²) with random rotation,
    // sorted, on a material copy with Softness 2. Rejected by the owner on the turning tornado (v5/v6, 2026-10-14):
    // refused for moving sources.
    public sealed class MakeVolumetric : Recipe
    {
        public override string Id => "make_volumetric";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override RecipeStatus Status => RecipeStatus.Rejected;
        public override IReadOnlyList<string> Needs => new[] { "file" };
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("split", 2, 1, 8) };

        public override string? Why(RecipeContext c) => c.Emitters().Any(t => Moves(t.Part)) ? "Rcp.Why.Moving" : base.Why(c);

        public override void Apply(RecipeContext c)
        {
            double k = Math.Round(c.Number("split"));
            foreach (var t in c.Emitters())
            {
                var p = Params(c, t.Emitter!);
                c.Set(t, "SizeMultiplier", Math.Round(p.SizeMultiplier / k, 4));
                c.Set(t, "SizeRND", Math.Round(p.SizeRnd / k, 4));
                c.Set(t, "BirthRate", Math.Round(p.BirthRate * k * k, 4));
                c.Set(t, "MaxNum", Math.Ceiling(Math.Max(p.MaxNum, 1) * k * k));
                c.Set(t, "RandomAngle", 1);
                c.Set(t, "RandomRotDir", 1);
                c.Set(t, "Sort", 1);
                if (CopyMaterial(c, t, "vol", m => new[] { m.Set("Softness", 2) }) is { } game)
                    c.SetString(t, "Material", game);
            }
        }
    }

    // R0: the game sorts whole emitters by one key, unstable when keys are close; a big alpha cloud split into emitters
    // at their real places along its longest side sorts by those places. Seen in game 2026-10-15 (P12c pair_order).
    public sealed class FixOrder : Recipe
    {
        public override string Id => "fix_order";
        public override RecipeScope Scope => RecipeScope.Emitters;
        public override IReadOnlyList<RecipeParam> Params => new[] { RecipeParam.Num("pieces", 3, 2, 16) };

        public override void Apply(RecipeContext c)
        {
            int n = (int)Math.Round(c.Number("pieces"));
            foreach (var t in c.Emitters())
            {
                var p = Params(c, t.Emitter!);
                var size = p.ShapeSize;
                int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
                float span = axis == 0 ? size.X : axis == 1 ? size.Y : size.Z;
                string text = RecipeScene.Text(t.Emitter!.Block);
                for (int i = 0; i < n; i++)
                {
                    string name = c.AddEmitter(t.Part.Id, text, t.Emitter.Name + "_" + (i + 1));
                    var piece = new Target(t.Part, t.Doc, t.Doc.Emitter(name));
                    var off = p.Offset;
                    float shift = (i + 0.5f) / n * span - span / 2;
                    var s = size;
                    if (axis == 0) { off.X += shift; s.X /= n; } else if (axis == 1) { off.Y += shift; s.Y /= n; } else { off.Z += shift; s.Z /= n; }
                    c.Set(piece, "Offset", Math.Round(off.X, 4), Math.Round(off.Y, 4), Math.Round(off.Z, 4));
                    c.Set(piece, "ShapeSize", Math.Round(s.X, 4), Math.Round(s.Y, 4), Math.Round(s.Z, 4));
                    c.Set(piece, "BirthRate", Math.Round(p.BirthRate / n, 4));
                    c.Set(piece, "MaxNum", Math.Ceiling(Math.Max(p.MaxNum, 1) / (double)n));
                }
                c.RemoveEmitter(t.Part.Id, t.Emitter.Name);
            }
        }
    }

    // R0: particles are depth-sorted inside an emitter only with Sort 1. Refused on moving sources: on the spinning
    // tornado it made the blinking worse (2.11). Seen in game on a still source 2026-10-15 (P12c pair_sort).
    public sealed class SortInside : Recipe
    {
        public override string Id => "sort_inside";
        public override RecipeScope Scope => RecipeScope.Emitters;

        public override string? Why(RecipeContext c) => c.Emitters().Any(t => Moves(t.Part)) ? "Rcp.Why.Moving" : base.Why(c);

        public override void Apply(RecipeContext c)
        {
            foreach (var t in c.Emitters())
                c.Set(t, "Sort", 1);
        }
    }
}
