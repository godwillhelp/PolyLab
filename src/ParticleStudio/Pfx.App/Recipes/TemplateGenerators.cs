using System.Globalization;
using System.Numerics;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.App.Recipes;

// The generators of spec 3.10 after the tornado: each starts from emitters of an effect the game ships (the template,
// a parameter: any effect under the data roots works) and scales them by its values, so every number of the result
// comes from a working effect and the user's own settings; then it adds what the effect needs outside the .ptc (a light,
// hazes, a flash). All seen in game: burst, flash, fog and glow in P12c (2026-10-15); in P12d (2026-10-16) sparks
// (scale 3, 1.5 m up), embers (scale 3, rate 4), arc (scale 2, rate 20), the heat column's hazes (frame change 7× while
// on), which were too small to see at scale 1, and the vortex as two emitters (pair_vortex of pairs3).
public abstract class TemplateGenerator : GeneratorRecipe
{

    // The template effect and the emitters taken from it (all when empty).
    protected abstract string Template { get; }
    protected virtual string[] TemplateEmitters => Array.Empty<string>();

    protected virtual IEnumerable<RecipeParam> Own => Array.Empty<RecipeParam>();

    public override IReadOnlyList<RecipeParam> Params => new[]
    {
        new RecipeParam("name", ParamKind.Text, Id[4..]), RecipeParam.Material("template", Template), RecipeParam.Num("scale", 1, 0.01, 100),
        RecipeParam.Num("rate", 1, 0.01, 100), RecipeParam.Num("life", 1, 0.05, 20), new RecipeParam("tint", ParamKind.Text, ""),
    }.Concat(Own).Append(RecipeParam.State()).ToList();

    // Emitter texts of the template that the generator keeps.
    protected List<string> TemplateTexts(RecipeContext c)
    {
        string? disk = c.Scene.Resolve(c.Text("template"));
        if (disk == null)
            throw new InvalidDataException("Template not found: " + c.Text("template"));
        var doc = PtcDocument.Load(disk);
        var keep = TemplateEmitters;
        return doc.Emitters.Where(e => keep.Length == 0 || keep.Contains(e.Name)).Select(e => RecipeScene.Text(e.Block)).ToList();
    }

    // The part's bytes: the kept emitters in a new effect, scaled. Size and distance keys by `scale` (the shape keeps its
    // proportions in space and, with velocities scaled too, in time), births and limits by `rate`, lives by `life`, the
    // colour graph multiplied by `tint` (r g b, empty = as the template).
    protected byte[] Scaled(RecipeContext c, IEnumerable<string> emitters, Action<PtcDocument>? more = null, double[]? tintOverride = null)
    {
        string text = "EffectDef {\r\n Emitors {" + string.Concat(emitters.Select(t => t.Replace("\r\n", "\n").Replace("\n", "\r\n"))) + "\r\n }\r\n}\r\n";
        var doc = PtcDocument.From(EnfDocument.Parse(text));
        double k = c.Number("scale"), r = c.Number("rate"), l = c.Number("life");
        var tint = tintOverride ?? Tint(c);
        var stack = new UndoStack();
        foreach (var e in doc.Emitters)
        {
            void Mul(string key, double f)
            {
                if (e.Block.FindLine(key) != null && e.Numbers(key) is { } v)
                    stack.Do(e.Set(key, v.Select(x => Math.Round(x * f, 4)).ToArray()));
            }
            foreach (var key in new[] { "SizeMultiplier", "SizeRND", "ShapeSize", "Offset", "Velocity", "VelocityRND" })
                Mul(key, k);
            Mul("BirthRate", r);
            Mul("BirthRateRND", r);
            if (e.Block.FindLine("MaxNum") != null && e.Number("MaxNum") is { } max)
                stack.Do(e.Set("MaxNum", Math.Ceiling(max * r * l)));
            Mul("LifeTime", l);
            Mul("LifeTimeRND", l);
            if (tint is { } t && e.Block.FindBlock("Color") != null && e.Numbers("Color") is { } col)
                stack.Do(e.SetGraph("Color", col.Select((x, i) => i % 4 == 0 ? x : Math.Round(Math.Min(1, x * t[i % 4 - 1]), 4)).ToList()));
        }
        more?.Invoke(doc);
        return doc.ToBytes();
    }

    protected static double[]? Tint(RecipeContext c)
    {
        var parts = c.Text("tint").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return null;
        var v = new double[3];
        for (int i = 0; i < 3; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                return null;
        return v;
    }

    // One part named after the node in particles\, in the state, plus the generator's own items.
    protected (ProjectFile, IReadOnlyList<byte[]>, IReadOnlyList<string>) One(RecipeContext c, byte[] bytes, Action<ProjectFile, string>? items = null)
    {
        string name = ProjectNames.Id(c.Text("name"));
        var made = new ProjectFile();
        var part = new PartEntry { Id = name, Path = Pfx.Core.Generators.TornadoGenerator.PartsFolder + "/" + name + ".ptc" };
        made.Parts.Add(part);
        var state = new StateEntry { Name = "made" };
        state.Items.Add(new StateItem { Ref = part.Id });
        made.States.Add(state);
        items?.Invoke(made, part.Id);
        return (made, new[] { bytes }, Array.Empty<string>());
    }
}

// Sparks: vanilla electro_sparks' spark emitter.
public sealed class GenSparks : TemplateGenerator
{
    public override string Id => "gen_sparks";
    protected override string Template => "graphics/particles/electro_sparks.ptc";
    protected override string[] TemplateEmitters => new[] { "sparksrandom2" };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) => One(c, Scaled(c, TemplateTexts(c)));
}

// Embers: the bonfire's small sparks rising from the fire.
public sealed class GenEmbers : TemplateGenerator
{
    public override string Id => "gen_embers";
    protected override string Template => "graphics/particles/fire_bonfire.ptc";
    protected override string[] TemplateEmitters => new[] { "sparks_small" };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) => One(c, Scaled(c, TemplateTexts(c)));
}

// Ground fog: the vanilla spooky mist layer, spread over a wider area by `scale`.
public sealed class GenFog : TemplateGenerator
{
    public override string Id => "gen_fog";
    protected override string Template => "graphics/particles/spooky_mist.ptc";

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) => One(c, Scaled(c, TemplateTexts(c)));
}

// Burst ring: the m67 grenade's fast dust ring (a cone of 100-150° burst once).
public sealed class GenBurstRing : TemplateGenerator
{
    public override string Id => "gen_burst";
    protected override string Template => "graphics/particles/explosion_m67_01.ptc";
    protected override string[] TemplateEmitters => new[] { "explo_dust_01_small_ring_fast" };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) => One(c, Scaled(c, TemplateTexts(c)));
}

// Flash and afterglow: the m84 flash grenade's fire flash and sparks, with the blinding flash (CG_AnomFlash, the
// vanilla grenade's rule) and its short white light (0.2 s as the tornado strikes). The blinding casts a ray from the
// flash to the player's head; the flash stands at `height` like the flash recipe's (1.5 m, seen in P12b), not at the
// grenade's 0.5 m, from where no white-out came in P12d (shot pair_flashgen).
public sealed class GenFlash : TemplateGenerator
{
    public override string Id => "gen_flash";
    protected override string Template => "graphics/particles/explosion_m84_01.ptc";
    protected override string[] TemplateEmitters => new[] { "explo_fire_01", "explo_fire_02", "explo_debris_03_sparks" };
    public override IReadOnlyList<string> Needs => new[] { "script", "light" };
    protected override IEnumerable<RecipeParam> Own => new[] { RecipeParam.Num("full", 3, 0, 100), RecipeParam.Num("max", 30, 0.1, 500), RecipeParam.Num("height", 1.5, -50, 200), RecipeParam.Num("glow", 0.2, 0, 10) };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) =>
        One(c, Scaled(c, TemplateTexts(c)), (made, part) =>
        {
            float y = (float)c.Number("height");
            made.Flashes.Add(new FlashEntry { Id = part + "_flash", At = new[] { 0, y, 0 }, Full = (float)c.Number("full"), Max = (float)Math.Max(c.Number("max"), c.Number("full")) });
            made.States[0].Items.Add(new StateItem { Ref = part + "_flash" });
            if (c.Number("glow") > 0)
            {
                made.Lights.Add(new LightEntry { Id = part + "_light", Offset = new[] { 0, y, 0 }, Color = new[] { 1f, 1f, 1f }, Radius = (float)c.Number("max"), Life = (float)c.Number("glow"), FadeOut = 0 });
                made.States[0].Items.Add(new StateItem { Ref = part + "_light" });
            }
        });
}

// Electric arc / lightning column: the vanilla short-circuit spark emitter repeated up a column (one emitter every
// `step` metres), each the template at its own height.
public sealed class GenArc : TemplateGenerator
{
    public override string Id => "gen_arc";
    protected override string Template => "graphics/particles/electro_shortc2.ptc";
    protected override IEnumerable<RecipeParam> Own => new[] { RecipeParam.Num("height", 6, 0.1, 200), RecipeParam.Num("step", 1, 0.1, 50) };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c)
    {
        var one = TemplateTexts(c);
        int n = Math.Max(1, (int)Math.Floor(c.Number("height") / c.Number("step")) + 1);
        var texts = new List<string>();
        for (int i = 0; i < n; i++)
            foreach (var t in one)
            {
                var block = (EnfBlock)RecipeScene.Item(t);
                block.Header[^1] = new EnfToken(EnfTokenKind.Word, block.Header[^1].Leading, (block.Name ?? "arc") + "_" + i);
                texts.Add(RecipeScene.Text(block));
            }
        double step = c.Number("step");
        return One(c, Scaled(c, texts, doc =>
        {
            var stack = new UndoStack();
            var list = doc.Emitters;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var o = e.Numbers("Offset") ?? new double[] { 0, 0, 0 };
                stack.Do(e.Set("Offset", o[0], Math.Round(o[1] + i / Math.Max(1, one.Count) * step, 4), o[2]));
            }
        }));
    }
}

// Pull vortex: dust on a ring, turned by script and pulled to the centre by the engine's spring; the dust material and
// colours are the template's (the vanilla gas ground layer). The spring is first order at SpringRate · Spring per second
// (P3), so `pull`, the share of its distance a particle covers in its mean life, gives Spring = −ln(1 − pull) /
// (SpringRate · life). In game a spring on a LocalTransform 1 emitter throws its particles away (P12d, pair_gasspring), so
// each template emitter becomes two with half its births: one turning with the source (LocalTransform 1, no spring) and
// one pulled in place (`_pull`, LocalTransform 0, the spring: pair_gasspring0 matched the viewport).
public sealed class GenVortex : TemplateGenerator
{
    public override string Id => "gen_vortex";
    protected override string Template => "graphics/particles/contaminated_area_gas_ground.ptc";
    public override IReadOnlyList<string> Needs => new[] { "script" };
    protected override IEnumerable<RecipeParam> Own => new[] { RecipeParam.Num("radius", 6, 0.1, 200), RecipeParam.Num("pull", 0.5, 0, 0.99), RecipeParam.Num("spin", -60, -3600, 3600) };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c)
    {
        double r = c.Number("radius"), pull = c.Number("pull");
        double rate = new Pfx.Sim.SimOptions().SpringRate;
        var (made, bytes, notes) = One(c, Scaled(c, TemplateTexts(c), doc =>
        {
            var stack = new UndoStack();
            foreach (var e in doc.Emitters.ToList())
            {
                stack.Do(e.SetWord("ShapeType", "Ellipse"));
                stack.Do(e.Set("ShapeSize", 2 * r, 0, 2 * r));
                stack.Do(e.Set("LocalTransform", 1));
                if (e.Reset("Spring") is { } noSpring)
                    stack.Do(noSpring);
                if (pull <= 0)
                    continue;
                foreach (var key in new[] { "BirthRate", "BirthRateRND" })
                    if (e.Block.FindLine(key) != null && e.Number(key) is { } v)
                        stack.Do(e.Set(key, Math.Round(v / 2, 4)));
                if (e.Block.FindLine("MaxNum") != null && e.Number("MaxNum") is { } max)
                    stack.Do(e.Set("MaxNum", Math.Ceiling(max / 2)));
                string name = e.Name + "_pull";
                stack.Do(doc.CloneEmitter(e, name));
                var pulled = doc.Emitter(name)!;
                double life = Math.Max((pulled.Number("LifeTime") ?? 1) + (pulled.Number("LifeTimeRND") ?? 0) / 2, 0.01);
                stack.Do(pulled.Set("LocalTransform", 0));
                stack.Do(pulled.Set("Spring", Math.Round(-Math.Log(1 - pull) / (rate * life), 6)));
            }
        }));
        made.Parts[0].Spin = (float)c.Number("spin");
        return (made, bytes, notes);
    }
}

// Heat column: hazes stacked up a column (CG haze values: radius 2, power 0.01-0.05) over the vanilla hot vapour.
public sealed class GenHeat : TemplateGenerator
{
    public override string Id => "gen_heat";
    protected override string Template => "graphics/particles/item_hot_vapor.ptc";
    public override IReadOnlyList<string> Needs => new[] { "script", "light" };
    protected override IEnumerable<RecipeParam> Own => new[] { RecipeParam.Num("height", 6, 0.5, 100), RecipeParam.Num("radius", 2, 0.05, 50), RecipeParam.Num("power", 0.02, 0, 0.2) };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c) =>
        One(c, Scaled(c, TemplateTexts(c)), (made, part) =>
        {
            double radius = c.Number("radius");
            int n = Math.Max(1, (int)Math.Ceiling(c.Number("height") / (2 * radius)));
            for (int i = 0; i < n; i++)
            {
                string id = part + "_haze" + i;
                made.Hazes.Add(new HazeEntry { Id = id, At = new[] { 0, (float)(radius + i * 2 * radius), 0 }, Radius = (float)radius, Power = (float)c.Number("power") });
                made.States[0].Items.Add(new StateItem { Ref = id });
            }
        });
}

// Artifact element glow: the vanilla flare's light sprite and a point light in the element's colour, taken from the
// flare of that colour (electro = blue flare, fire = red flare, acid = green flare, gravi = the purple smoke's colour).
public sealed class GenGlow : TemplateGenerator
{
    private static readonly Dictionary<string, (string Effect, string Emitter)> Elements = new()
    {
        ["electro"] = ("graphics/particles/fire_small_flare_blue_01.ptc", "light"),
        ["fire"] = ("graphics/particles/fire_small_flare_red_01.ptc", "light"),
        ["acid"] = ("graphics/particles/fire_small_flare_green_01.ptc", "light"),
        ["gravi"] = ("graphics/particles/smoke_m18_purple_01.ptc", "smoke_fast_right"),
    };

    public override string Id => "gen_glow";
    protected override string Template => "graphics/particles/fire_small_flare_blue_01.ptc";
    protected override string[] TemplateEmitters => new[] { "light" };
    public override IReadOnlyList<string> Needs => new[] { "script", "light" };
    protected override IEnumerable<RecipeParam> Own => new[] { RecipeParam.Pick("element", "electro", Elements.Keys.ToArray()), RecipeParam.Num("radius", 3, 0.1, 100) };

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c)
    {
        var (effect, emitter) = Elements.GetValueOrDefault(c.Text("element"), Elements["electro"]);
        string? disk = c.Scene.Resolve(effect);
        var colour = Vector3.One;
        if (disk != null && PtcDocument.Load(disk).Emitter(emitter) is { } source)
        {
            var p = RecipeCatalog.Params(c, source);
            colour = new Vector3(Lint.Linter.Peak(p.Color, 0), Lint.Linter.Peak(p.Color, 1), Lint.Linter.Peak(p.Color, 2));
            colour /= Math.Max(Math.Max(colour.X, colour.Y), Math.Max(colour.Z, 1e-3f));
        }
        // Without a tint of its own the glow sprite takes the element's colour.
        var tint = Tint(c) ?? new double[] { colour.X, colour.Y, colour.Z };
        return One(c, Scaled(c, TemplateTexts(c), null, tint), (made, part) =>
        {
            made.Lights.Add(new LightEntry { Id = part + "_light", Offset = new[] { 0f, 0.3f, 0f }, Color = new[] { colour.X, colour.Y, colour.Z }, Radius = (float)c.Number("radius") });
            made.States[0].Items.Add(new StateItem { Ref = part + "_light" });
        });
    }
}
