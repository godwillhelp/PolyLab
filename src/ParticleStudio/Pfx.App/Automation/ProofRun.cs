using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows.Threading;
using Pfx.App.Recipes;
using Pfx.App.Views;
using Pfx.Core.Export;
using Pfx.Core.Model;
using Pfx.Render;

namespace Pfx.App.Automation;

// --proof <dir> [--to <mod folder>] [--prefix <P>] [--proof-set p12b|p12c|p12d|cg] [--proof-sound <ogg>]: the game check of the
// recipes. A project is built in <dir> with one state per recipe; each recipe is added through the shell's own path
// (AddRecipe with the panel's selected state, as a click on "Add" does) to plain vanilla-material parts; the project is
// saved and exported through the DayZ mod profile into the mod folder; then every state is played in the viewport and
// photographed with the camera the game probe uses (shots.txt lists the probe's commands for the same views). Set p12b
// holds the recipes marked "in game" in P12b, set p12c the ones that were not checked and the template generators,
// set p12d what P12c could not see (shots_night.txt: the shots for a server started at night).
// Logs to <dir>\proof.txt.
public sealed class ProofRun
{
    private const string Dust = "graphics/particles/materials/smoke/smoke_dust_04.emat";
    private const string Cloud = "graphics/particles/materials/smoke/smoke_cloud_01_b.emat";
    private const string Flame = "Graphics/Particles/materials/fire/fire_flame_01.emat";
    private const string Sparks = "Graphics/Particles/materials/fire/sparks1.emat";

    // Where the probe puts the effect: 20 m in front of the player's spawn (the probe's Local frame: x right, z ahead).
    public const float Ahead = 20;

    // The game's static camera has a vertical field of view of 1 rad (P12b probe log); the viewport takes the same.
    private const float GameFov = 180 / MathF.PI;

    // One view: the state, the effect's age when photographed, eye and target relative to the effect origin; probe
    // commands run while the state plays (after the shot) and after it is stopped.
    private sealed record Shot(string State, float Age, Vector3 Eye, Vector3 Target, string Name, string[]? During = null, string[]? After = null, bool Night = false);

    // A proof project: its parts (file id and text), states, the state each part plays in, the recipes, the views and the
    // viewport field of view and whether each photo shows only its state's parts (P12b kept the defaults; P12c matches
    // the game camera and the probe's one-state playing).
    private sealed record Set(string Project, (string Id, string Text)[] Files, string[] States, string[] PartState, Action Recipes, List<Shot> Shots, float Fov, bool Solo);

    private static readonly Vector3 LowEye = new(0, 3, -18), LowTarget = new(0, 3, 0);

    private readonly MainWindow _w;
    private readonly Pfx.App.Core.Launch _launch;
    private readonly string _dir;
    private readonly StringBuilder _log = new();
    private TaskCompletionSource? _frame;

    public ProofRun(MainWindow window, Pfx.App.Core.Launch launch)
    {
        _w = window;
        _launch = launch;
        _dir = Path.GetFullPath(launch.Proof!);
    }

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        _w.Viewport.Rendered += () => _frame?.TrySetResult();
        _ = Run();
    }

    private Task Frame()
    {
        _frame = new TaskCompletionSource();
        return _frame.Task;
    }

    private async Task Settle()
    {
        await Frame();
        while (_w.Pipeline.AnyBusy || _w.Pipeline.AnySeeking || _w.Viewport.Previews.Count < _w.Docs.Count)
            await Frame();
        await _w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Frame();
        await Frame();
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        File.WriteAllText(Path.Combine(_dir, "proof.txt"), _log.ToString());
    }

    private static string E(string name, params string[] keys) =>
        "\r\n  EmitorDef " + name + " {\r\n" + string.Concat(keys.Select(k => "   " + k.Replace("\n", "\r\n") + "\r\n")) + "  }";

    private static string Ptc(params string[] emitters) => "EffectDef {\r\n Emitors {" + string.Concat(emitters) + "\r\n }\r\n}\r\n";

    private static string Puff(string material, float size, float rate, float life, float speed, string extra = "") =>
        $"MaxNum {Math.Ceiling(rate * life * 1.5)}\nBirthRate {N(rate)}\nMaterial \"{material}\"\nSizeMultiplier {N(size)}\nSizeRND {N(size * 0.25f)}\nRandomAngle 1\nRandomRotDir 1\nRotMultiplier 20\n" +
        $"Velocity {N(speed)}\nVelocityRND {N(speed * 0.25f)}\nGravityMultiply 0\nLifeTime {N(life)}\nLifeTimeRND {N(life * 0.1f)}\nAlpha {{\n 0 0 0.2 0.8 0.8 0.8 1 0\n}}\nEffectTime 10\nRepeat 1" + extra;

    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void Add(string recipe, string state, IReadOnlyList<RecipeTarget> targets, params (string, string)[] values)
    {
        _w.ComposeTab.SelectState(_w.Project!.File.States.FindIndex(s => s.Name == state));
        int before = _w.Project.File.Recipes.Count;
        _w.AddRecipe(RecipeCatalog.Find(recipe)!, null, targets, values.ToDictionary(v => v.Item1, v => v.Item2));
        var node = _w.Project.File.Recipes.Count > before ? _w.Project.File.Recipes[^1] : null;
        Log($"{recipe} in {state}: {(node == null ? "REFUSED" : node.Log.Count + " changes")}");
    }

    private static RecipeTarget[] Part(params string[] ids) => ids.Select(i => new RecipeTarget { Part = i }).ToArray();

    private static RecipeTarget[] Emit(string part, params string[] names) => names.Select(n => new RecipeTarget { Part = part, Emitter = n }).ToArray();

    private static readonly RecipeTarget[] None = Array.Empty<RecipeTarget>();

    private Set P12b()
    {
        var files = new (string Id, string Text)[]
        {
            ("swirl", Ptc(E("puff_a", Puff(Dust, 2, 6, 4, 0.6f), "Offset 3 1 0"), E("puff_b", Puff(Dust, 2, 6, 4, 0.6f), "Offset -3 3 0"), E("puff_c", Puff(Dust, 2, 6, 4, 0.6f), "Offset 0 5 3"))),
            ("strands", Ptc(E("base", Puff(Dust, 2, 4, 3, 0.5f)))),
            ("funnel", Ptc()),
            ("light", Ptc(E("fire", Puff(Flame, 1, 20, 0.6f, 1.5f), "Color {\n 0 1 0.6 0.25 1 1 0.4 0.1\n}"))),
            ("around", Ptc(E("drift", Puff(Dust, 6, 40, 5, 1), "ShapeType Box", "ShapeSize 30 4 30", "ConeAngle 20 170 190", "Offset 0 2 0"))),
            ("soft", Ptc(E("big", Puff(Dust, 8, 3, 6, 0.2f), "Offset 0 0.5 0"))),
            ("palette", Ptc(E("lit", Puff(Cloud, 3, 4, 5, 0.3f), "Offset -3 2 0", "Color {\n 0 1 1 1 1 1 1 1\n}"), E("dust", Puff(Dust, 3, 4, 5, 0.3f), "Offset 3 2 0", "Color {\n 0 1 1 1 1 1 1 1\n}"))),
            ("near", Ptc(E("debris", Puff(Sparks, 0.15f, 60, 2, 2), "ShapeType Box", "ShapeSize 6 3 6", "Offset 0 1.5 0"))),
            ("column", Ptc(E("low", Puff(Dust, 2, 5, 3, 0.3f), "ShapeType Ellipse", "ShapeSize 3 0 3", "Offset 0 0.5 0"), E("mid", Puff(Dust, 3, 5, 3, 0.3f), "ShapeType Ellipse", "ShapeSize 4 0 4", "Offset 0 4 0"),
                E("high", Puff(Dust, 4, 5, 3, 0.3f), "ShapeType Ellipse", "ShapeSize 5 0 5", "Offset 0 8 0"))),
            ("body", Ptc(E("core", Puff(Dust, 2, 8, 4, 1.5f), "ShapeType Ellipse", "ShapeSize 2 0 2", "ConeAngle 360 10 10"))),
            ("jets", Ptc(E("jet_a", Puff(Dust, 1.5f, 6, 3, 1.5f), "Offset 3 0.5 0"), E("jet_b", Puff(Dust, 1.5f, 6, 3, 1.5f), "Offset -3 0.5 0"))),
            ("bolts", Ptc(E("bolt", Puff(Sparks, 0.3f, 400, 0.3f, 6), "ShapeType Box", "ShapeSize 0.5 3 0.5", "Offset 0 1.5 0"))),
        };
        string[] states = { "swirl", "strands", "funnel", "light", "haze", "flash", "tint", "around", "soft", "palette", "near", "column", "onebody", "strikes" };
        string[] partState = { "swirl", "strands", "funnel", "light", "around", "soft", "palette", "near", "column", "onebody", "onebody", "strikes" };
        void Recipes()
        {
            Add("swirl", "swirl", Part("swirl"), ("rate", "-90"));
            Add("strands", "strands", Part("strands"), ("height", "10"), ("base", "2"), ("top", "4"), ("speed", "3"), ("count", "5"), ("puffs", "60"));
            Add("funnel", "funnel", Part("funnel"), ("height", "12"), ("base", "1.5"), ("top", "4"), ("speed", "3"), ("puffs", "120"), ("cap", "0"));
            Add("add_light", "light", Emit("light", "fire"), ("brightness", "3"));
            Add("haze", "haze", None, ("radius", "2"), ("power", "0.05"), ("height", "1"));
            Add("flash", "flash", None, ("full", "3"), ("max", "30"), ("height", "1.5"), ("glow", "0.2"));
            Add("tint", "tint", None, ("radius", "60"));
            Add("around", "around", Part("around"), ("radius", "60"));
            Add("soft_ground", "soft", Emit("soft", "big"));
            Add("palette_match", "palette", Emit("palette", "lit"), ("reference", Dust));
            Add("near_only", "near", Emit("near", "debris"), ("near", "40"));
            Add("single_column", "column", Emit("column", "low", "mid", "high"));
            Add("one_body", "onebody", Part("body", "jets"), ("rate", "-60"));
            Add("strikes", "strikes", Part("bolts"), ("height", "6"), ("low", "1"), ("high", "2"), ("gap_min", "1"), ("gap_max", "2"), ("burst", "0.3"), ("light", "10"));
        }
        var shots = states.Select(s => new Shot(s, s == "flash" ? 0.1f : 5, LowEye, LowTarget, s)).ToList();
        shots.Add(new Shot("near", 5, new Vector3(0, 3, -80), LowTarget, "near_far"));
        return new Set("rcpproof", files, states, partState, Recipes, shots, Camera.DefaultFov, false);
    }

    // The P12c set. Every part is built so its recipe's effect shows in one frame: the orbit turn moves an offset puff
    // around the axis, the stop split leaves only the other emitter after the stop, the strip gate shows births before
    // the gated twin starts (BRateMast 0 until 31 % of the 10 s effect time), the spawner's children appear at the
    // parent's height. Haze is photographed as a series and compared with the same series after it stops (moving pixels
    // inside the haze against the frame's rest); the sound check lists the sounds the game is playing.
    private Set P12c()
    {
        const string gate = "BRateMast {\n 0 0 0.3 0 0.31 1 1 1\n}";
        var files = new (string Id, string Text)[]
        {
            ("orbit", Ptc(E("puff", Puff(Dust, 1.5f, 8, 4, 0.3f), "Offset 4 2 0"))),
            ("split", Ptc(E("long", Puff(Dust, 2, 6, 4, 0.4f), "Offset -3 2 0"), E("short", Puff(Dust, 2, 6, 4, 0.4f), "Offset 3 2 0"))),
            ("glow", Ptc()),
            ("spawn", Ptc(E("parent", Puff(Dust, 1, 2, 2, 0), "Offset 0 3 0"), E("child", Puff(Dust, 1, 3, 3, 0.5f)))),
            ("sound", Ptc(E("flame", Puff(Flame, 0.5f, 10, 0.6f, 1)))),
            ("gate", Ptc(E("shut", Puff(Dust, 2, 6, 4, 0.4f), "Offset -3 2 0", gate), E("open", Puff(Dust, 2, 6, 4, 0.4f), "Offset 3 2 0", gate))),
            ("lit", Ptc(E("cloud", Puff(Dust, 4, 3, 6, 0.2f), "Offset 0 2 0"))),
            ("order", Ptc(E("bank", Puff(Cloud, 3, 12, 6, 0.1f), "ShapeType Box", "ShapeSize 14 2 2", "Offset 0 2 0"))),
            ("sort", Ptc(E("dense", Puff(Dust, 2, 30, 4, 0.5f), "ShapeType Box", "ShapeSize 3 3 3", "Offset 0 2 0", "Color {\n 0 0.6 0.4 0.3 1 1 0.9 0.8\n}"))),
        };
        string[] states = { "orbit", "split", "glow", "spawner", "sound", "gate", "lit", "order", "sort", "haze", "sparks", "embers", "fog", "burst", "flashgen", "arc", "vortex", "heat", "glowgen" };
        string[] partState = { "orbit", "split", "glow", "spawner", "sound", "gate", "lit", "order", "sort" };
        void Recipes()
        {
            Add("orbit_turn", "orbit", Part("orbit"), ("angle", "90"));
            Add("stop_split", "split", Emit("split", "short"), ("stop", "2"));
            Add("ground_glow", "glow", Part("glow"));
            Add("spawner", "spawner", Emit("spawn", "parent", "child"));
            if (_launch.ProofSound is { } ogg)
            {
                Directory.CreateDirectory(Path.Combine(_dir, "sounds"));
                File.Copy(ogg, Path.Combine(_dir, "sounds", "proof.ogg"), true);
                Add("add_sound", "sound", None, ("file", "sounds/proof.ogg"), ("loop", "1"));
            }
            else
            {
                Log("add_sound skipped: no --proof-sound");
            }
            Add("strip_gate", "gate", Emit("gate", "open"));
            Add("make_lit", "lit", Emit("lit", "cloud"));
            Add("fix_order", "order", Emit("order", "bank"), ("pieces", "3"));
            Add("sort_inside", "sort", Emit("sort", "dense"));
            Add("haze", "haze", None, ("radius", "2"), ("power", "0.2"), ("height", "1"));
            Add("gen_sparks", "sparks", None);
            Add("gen_embers", "embers", None);
            Add("gen_fog", "fog", None);
            Add("gen_burst", "burst", None);
            Add("gen_flash", "flashgen", None, ("max", "10"));
            Add("gen_arc", "arc", None);
            Add("gen_vortex", "vortex", None);
            Add("gen_heat", "heat", None);
            Add("gen_glow", "glowgen", None);
        }
        var hazeEye = new Vector3(0, 1.2f, -5);
        var hazeAt = new Vector3(0, 1, 0);
        var shots = new List<Shot>
        {
            new("orbit", 5, new Vector3(0, 16, -12), Vector3.Zero, "orbit"),
            new("split", 4, LowEye, LowTarget, "split"),
            new("glow", 3, new Vector3(0, 6, -10), Vector3.Zero, "glow"),
            new("spawner", 5, LowEye, LowTarget, "spawner"),
            new("sound", 3, LowEye, LowTarget, "sound", During: new[] { "sounds" }),
            new("gate", 2, LowEye, LowTarget, "gate"),
            new("gate", 5, LowEye, LowTarget, "gate_late"),
            new("lit", 5, LowEye, LowTarget, "lit"),
            new("order", 5, new Vector3(0, 3, -24), LowTarget, "order"),
            new("sort", 5, new Vector3(0, 3, -10), new Vector3(0, 2, 0), "sort"),
            new("haze", 3, hazeEye, hazeAt, "haze", During: new[] { "shots game_hazeseq 12 100", "wait 1600" },
                After: new[] { "wait 1000", "shots game_hazectl 12 100", "wait 1600" }),
            new("sparks", 0.25f, new Vector3(0, 3, -4), new Vector3(0, 0.3f, 0), "sparks"),
            new("embers", 3, new Vector3(0, 4, -6), new Vector3(0, 1, 0), "embers"),
            new("fog", 8, new Vector3(0, 4, -20), Vector3.Zero, "fog"),
            new("burst", 0.3f, new Vector3(0, 3, -15), new Vector3(0, 1, 0), "burst"),
            new("flashgen", 0.15f, LowEye, LowTarget, "flashgen"),
            new("flashgen", 0.15f, new Vector3(0, 1.6f, -4), new Vector3(0, 1.2f, 0), "flashgen_near"),
            new("arc", 0.3f, new Vector3(0, 4, -10), LowTarget, "arc"),
            new("vortex", 6, new Vector3(0, 10, -14), Vector3.Zero, "vortex"),
            new("heat", 3, new Vector3(0, 2, -3), new Vector3(0, 0.3f, 0), "heat"),
            new("glowgen", 3, new Vector3(0, 1.5f, -5), new Vector3(0, 0.3f, 0), "glowgen"),
        };
        return new Set("lateproof", files, states, partState, Recipes, shots, GameFov, true);
    }

    // The P12d set. The vortex is taken apart: the vanilla gas layer alone, the P12c generator's dust without its spring,
    // with its Spring 0.05 (LocalTransform 1 as generated and 0), the generator as it is now, and Spring 0.000001 at
    // LocalTransform 1 and 0 (in game the LocalTransform 1 particles vanished: does a weak spring move them away?). The template
    // generators are built at the scale a user would pick to see them (P12c's scale 1 showed nothing); sparks and the
    // arc's short flashes are also shot as a series, the heat column's hazes as a series while on and after (P12c haze). The ground glow plays in both runs: shots_night.txt is for the server started at
    // night. The flash generator's max reaches the player standing Ahead metres away.
    private Set P12d()
    {
        string vanilla = File.ReadAllText(_w.Resolve("graphics/particles/contaminated_area_gas_ground.ptc")
            ?? throw new FileNotFoundException("contaminated_area_gas_ground.ptc under the data roots"), Encoding.Latin1);
        string Gas(string? spring, int local)
        {
            string nl = vanilla.Contains("\r\n") ? "\r\n" : "\n";
            string text = vanilla.Replace("ShapeSize 50 0 50", "ShapeSize 12 0 12").Replace("LocalTransform 0", "LocalTransform " + local)
                .Replace("GravityMultiplyRND 0" + nl, spring != null ? "GravityMultiplyRND 0" + nl + "   Spring " + spring + nl : "GravityMultiplyRND 0" + nl);
            if (!text.Contains("ShapeSize 12 0 12") || !text.Contains("LocalTransform " + local) || (spring != null) != text.Contains("Spring "))
                throw new InvalidDataException("contaminated_area_gas_ground.ptc is not the 1.29 file the P12d set edits");
            return text;
        }
        var files = new (string Id, string Text)[]
        {
            ("gas", vanilla), ("gasfree", Gas(null, 1)), ("gasspring", Gas("0.05", 1)), ("gasspring0", Gas("0.05", 0)), ("glow", Ptc()),
            ("gasdrift", Gas("0.000001", 1)), ("gasdrift0", Gas("0.000001", 0)),
        };
        string[] states = { "gas", "gasfree", "gasspring", "gasspring0", "vortex", "glow", "sparks", "embers", "arc", "heat", "flashgen", "tornado", "gasdrift", "gasdrift0" };
        string[] partState = { "gas", "gasfree", "gasspring", "gasspring0", "glow", "gasdrift", "gasdrift0" };
        void Recipes()
        {
            Add("gen_vortex", "vortex", None);
            Add("ground_glow", "glow", Part("glow"));
            Add("gen_sparks", "sparks", None, ("scale", "3"));
            Add("gen_embers", "embers", None, ("scale", "3"), ("rate", "4"));
            Add("gen_arc", "arc", None, ("scale", "2"), ("rate", "20"));
            Add("gen_heat", "heat", None, ("scale", "4"));
            Add("gen_flash", "flashgen", None, ("max", "30"));
            Add("gen_tornado", "tornado", None);
        }
        var gasEye = new Vector3(0, 10, -14);
        var shots = new List<Shot>();
        foreach (var s in new[] { "gas", "gasfree", "gasspring", "gasspring0", "vortex", "gasdrift", "gasdrift0" })
        {
            shots.Add(new(s, 6, gasEye, Vector3.Zero, s));
            shots.Add(new(s, 15, gasEye, Vector3.Zero, s + "_late"));
        }
        shots.AddRange(new Shot[]
        {
            new("glow", 3, new Vector3(0, 6, -10), Vector3.Zero, "glow", Night: true),
            new("sparks", 0.25f, new Vector3(0, 3, -8), new Vector3(0, 1.5f, 0), "sparks", During: new[] { "shots game_sparksseq 8 50", "wait 500" }),
            new("embers", 3, new Vector3(0, 8, -14), new Vector3(0, 12, 0), "embers"),
            new("arc", 0.3f, new Vector3(0, 3, -10), new Vector3(0, 3, 0), "arc", During: new[] { "shots game_arcseq 8 50", "wait 500" }),
            new("heat", 3, new Vector3(0, 2, -6), new Vector3(0, 2, 0), "heat", During: new[] { "shots game_heatseq 12 100", "wait 1600" },
                After: new[] { "wait 1000", "shots game_heatctl 12 100", "wait 1600" }),
            new("flashgen", 0.15f, LowEye, LowTarget, "flashgen"),
            new("tornado", 20, new Vector3(0, 30, -110), new Vector3(0, 30, 0), "tornado"),
        });
        return new Set("p12dproof", files, states, partState, Recipes, shots, GameFov, true);
    }

    // The probe plays one state and stops it at once before the next; the preview's cycle lets the previous state's
    // particles finish their lives. For the photo the viewport hides (view state only, as the eye toggle) every part
    // that is not in the state or carried by one of its strikes.
    private void ShowOnly(string state)
    {
        var file = _w.Project!.File;
        var refs = file.States.First(s => s.Name == state).Items.Select(i => i.Ref).ToHashSet();
        foreach (var k in file.Strikes.Where(k => refs.Contains(k.Id)))
            refs.Add(k.Part);
        foreach (var d in _w.Docs)
        {
            d.Hidden.Clear();
            if (_w.PartOf(d) is not { } part || !refs.Contains(part))
                foreach (var e in d.Ptc.Emitters)
                    d.Hidden.Add(e.Block);
            _w.Pipeline.Request(d);
        }
    }

    // The CG profile check: a fire core always on, and an active state with the blinding flash and dust around the
    // camera (the project is exported afterwards with --export --profile cg; the CG showcase plays it in game).
    private Set Cg()
    {
        var files = new (string Id, string Text)[]
        {
            ("core", Ptc(E("fire", Puff(Flame, 1, 20, 0.6f, 1.5f)))),
            ("dust", Ptc(E("drift", Puff(Dust, 6, 40, 5, 1), "ShapeType Box", "ShapeSize 30 4 30", "ConeAngle 20 170 190", "Offset 0 2 0"))),
        };
        string[] states = { "idle", "active" };
        string[] partState = { "idle", "active" };
        void Recipes()
        {
            Add("around", "active", Part("dust"), ("radius", "60"));
            Add("flash", "active", None, ("full", "3"), ("max", "30"), ("height", "1.5"), ("glow", "0.2"));
        }
        var shots = new List<Shot> { new("idle", 3, LowEye, LowTarget, "idle"), new("active", 3, LowEye, LowTarget, "active") };
        return new Set("pfxcg", files, states, partState, Recipes, shots, GameFov, true);
    }

    // Seconds until nothing of a stopped state is left in the game: the runtime soft-stops its parts (live particles
    // finish their lives) and fades its lights and hazes out, as the preview does.
    private float ClearTime(string state)
    {
        var file = _w.Project!.File;
        var refs = file.States.First(s => s.Name == state).Items.Select(i => i.Ref).ToHashSet();
        foreach (var k in file.Strikes.Where(k => refs.Contains(k.Id)))
            refs.Add(k.Part);
        float life = _w.Docs.Where(d => _w.PartOf(d) is { } part && refs.Contains(part)).SelectMany(d => _w.ParamsOf(d)).Select(p => p.MaxLife).DefaultIfEmpty(0).Max();
        float fade = file.Lights.Where(l => refs.Contains(l.Id)).Select(l => l.FadeOut).Concat(file.Hazes.Where(h => refs.Contains(h.Id)).Select(h => h.Fade)).DefaultIfEmpty(0).Max();
        return Math.Max(life, fade);
    }

    private async Task Run()
    {
        try
        {
            _w.WindowState = System.Windows.WindowState.Maximized;
            await Frame();
            var set = _launch.ProofSet switch
            {
                null or "p12b" => P12b(),
                "p12c" => P12c(),
                "p12d" => P12d(),
                "cg" => Cg(),
                _ => throw new ArgumentException("Unknown proof set " + _launch.ProofSet),
            };
            string parts = Path.Combine(_dir, "parts");
            Directory.CreateDirectory(parts);
            foreach (var (id, text) in set.Files)
                File.WriteAllText(Path.Combine(parts, id + ".ptc"), text, Encoding.Latin1);
            var file = new ProjectFile { Name = set.Project };
            foreach (var s in set.States)
                file.States.Add(new StateEntry { Name = s });
            for (int i = 0; i < set.Files.Length; i++)
            {
                file.Parts.Add(new PartEntry { Id = set.Files[i].Id, Path = "parts/" + set.Files[i].Id + ".ptc" });
                file.States.First(s => s.Name == set.PartState[i]).Items.Add(new StateItem { Ref = set.Files[i].Id });
            }
            string project = Path.Combine(_dir, set.Project + ".plpfx");
            SafeFile.Write(project, Composition.Bytes(file));
            _w.OpenProject(project);
            await Settle();
            set.Recipes();
            _w.SaveProjectTo(null);
            await Settle();

            if (_launch.ExportTo is { } mod)
            {
                var p = _w.Project!.Snapshot();
                p.Export.Profile = "dayz";
                p.Export.Dayz = new DayzModProfile { ModFolder = mod, Prefix = _launch.ExportPrefix ?? "RCP" };
                var plan = DayzModExport.Plan(p, _w.Project.Folder!, _w.TakeSnapshot());
                foreach (var e in plan.Errors)
                    Log("export error " + e.Key + " " + string.Join(", ", e.Args));
                if (plan.CanWrite)
                {
                    plan.Write();
                    Log($"exported {plan.Files.Count} files to {mod}; notes: {string.Join("; ", plan.Notes.Select(n => n.Key))}");
                }
            }

            _w.StatesPlaying = true;
            await Settle();
            var cmds = new StringBuilder();
            var night = new StringBuilder();
            foreach (var shot in set.Shots)
            {
                var span = _w.ProjectTimeline!.States.First(x => x.Name == shot.State);
                if (set.Solo)
                    ShowOnly(shot.State);
                _w.Pipeline.Seek((float)(span.From + shot.Age));
                _w.Pipeline.Playing = false;
                _w.Viewport.Camera.Fov = set.Fov;
                _w.Viewport.Camera.LookAt(shot.Eye, shot.Target);
                await Settle();
                if (_w.Viewport.CaptureFrame(false) is { } image)
                    Pfx.Textures.ImageFiles.Save(Path.Combine(_dir, "tool_" + shot.Name + ".png"), MainWindow.Opaque(image));
                Log($"tool shot {shot.Name}: state {shot.State} at {shot.Age} s, {_w.Viewport.Previews.Sum(v => v.AliveTotal)} particles");
                // The probe's frame: x right, y up from the ground, z ahead; the effect stands Ahead metres in front.
                string V(Vector3 v) => string.Join(" ", new[] { v.X, v.Y, v.Z + Ahead }.Select(N));
                void Commands(StringBuilder to, string name)
                {
                    to.AppendLine($"fx {shot.State} 0 0 {N(Ahead)}");
                    to.AppendLine($"cam {V(shot.Eye)} {V(shot.Target)}");
                    to.AppendLine($"wait {(int)(shot.Age * 1000)}");
                    to.AppendLine($"shot game_{name}");
                    foreach (var line in shot.During ?? Array.Empty<string>())
                        to.AppendLine(line);
                    to.AppendLine("fxstop");
                    foreach (var line in shot.After ?? Array.Empty<string>())
                        to.AppendLine(line);
                    if (set.Solo)
                        to.AppendLine($"wait {(int)Math.Ceiling(ClearTime(shot.State) * 1000)}");
                }
                Commands(cmds, shot.Name);
                if (shot.Night)
                    Commands(night, shot.Name + "_night");
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), cmds.ToString());
            if (night.Length > 0)
                File.WriteAllText(Path.Combine(_dir, "shots_night.txt"), night.ToString());
            Log("RESULT: done");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
        }
        _w.Close();
    }
}
