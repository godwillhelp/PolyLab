using System.Globalization;
using Pfx.Core.Generators;
using Pfx.Core.Model;

namespace Pfx.App.Recipes;

// Generators as nodes (spec 3.10): the node holds the generator's settings and the parts it wrote; editing a setting
// writes every part again (the parts are the generator's output: edits made inside them are replaced, the panel says
// so). Parts go to fixed files under the project folder, so a regenerated tornado keeps its file names.
public abstract class GeneratorRecipe : Recipe
{
    public override RecipeScope Scope => RecipeScope.Project;

    // The parts (path relative to the project folder, bytes, script data), the other items and the order they join the
    // state in, plus language keys of notes.
    protected abstract (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c);

    public override string? Why(RecipeContext c)
    {
        if (c.Scene.Folder == null)
            return "Rcp.Why.SaveFirst";
        var (made, _, _) = Build(c);
        var own = c.Previous.Where(o => o.Op == RecipeOp.ItemAdd && o.After != null).Select(o => o.After!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in made.Parts)
        {
            if (own.Contains(p.Path))
                continue;
            if (c.Project.Parts.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(c.Scene.Folder, p.Path)))
                return "Rcp.Why.FileTaken";
        }
        return null;
    }

    public override void Apply(RecipeContext c)
    {
        var (made, bytes, notes) = Build(c);
        string state = c.State() ?? c.NewState("idle");
        var ids = new Dictionary<string, string>();
        for (int i = 0; i < made.Parts.Count; i++)
        {
            var p = made.Parts[i];
            string id = c.NewId(p.Id);
            ids[p.Id] = id;
            c.AddPart(new PartEntry { Id = id, Path = p.Path, Spin = p.Spin, Near = p.Near, Stop = p.Stop });
            c.WritePart(id, bytes[i]);
        }
        foreach (var k in made.Strikes)
        {
            string id = c.NewId(k.Id);
            ids[k.Id] = id;
            k.Id = id;
            k.Part = ids[k.Part];
            c.AddItem(k);
        }
        foreach (var item in made.Items().Where(i => i is not (PartEntry or StrikeEntry)))
        {
            string id = c.NewId(item.Id);
            ids[item.Id] = id;
            item.Id = id;
            c.AddItem(item);
        }
        foreach (var it in made.States.SelectMany(s => s.Items))
            c.AddToState(state, ids[it.Ref], it.Delay);
        foreach (var n in notes)
            c.Note(n);
    }
}

// own_tornado.py as a node: every module constant is a setting (defaults = the script's), the eleven files are written
// as the script writes them (golden test byte for byte).
public sealed class GenTornado : GeneratorRecipe
{
    private static readonly TornadoSettings D = new();

    public override string Id => "gen_tornado";
    public override IReadOnlyList<string> Needs => new[] { "script", "light" };

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Vec(IEnumerable<double> v) => string.Join(" ", v.Select(R));

    public override IReadOnlyList<RecipeParam> Params { get; } = new[]
    {
        new RecipeParam("name", ParamKind.Text, D.Name),
        RecipeParam.Num("height", D.FunnelHeight, 1, 500), RecipeParam.Num("base", D.FunnelBase, 0.1, 200), RecipeParam.Num("top", D.FunnelTop, 0.1, 400),
        RecipeParam.Num("spin", D.FunnelSpin, -3600, 3600), RecipeParam.Num("puffs", D.ColumnPuffs, 1, 20000), RecipeParam.Num("strands", D.Strands, 1, 64),
        RecipeParam.Num("strand_speed", D.StrandSpeed, 0.1, 100), RecipeParam.Num("jet_heading", D.JetHeading, -360, 720),
        RecipeParam.Num("cap_spin", D.CapSpin, -3600, 3600), RecipeParam.Num("zone", D.ZoneRadius, 1, 5000), RecipeParam.Num("around_life", D.AroundLife, 0.1, 60),
        RecipeParam.Num("debris_near", D.DebrisNear, 0, 5000),
        new RecipeParam("rings", ParamKind.Text, string.Join("; ", D.Rings.Select(r => Vec(new[] { r.Radius, r.Blob, r.Count, r.Rate })))),
        new RecipeParam("dark", ParamKind.Text, Vec(D.Dark)), new RecipeParam("brown", ParamKind.Text, Vec(D.Brown)),
        RecipeParam.Material("dust", D.Dust), RecipeParam.Material("stones", D.Stones), RecipeParam.Path("donor"),
        RecipeParam.Num("gap_min", D.StrikeGap[0], 0.05, 3600), RecipeParam.Num("gap_max", D.StrikeGap[1], 0.05, 3600),
        RecipeParam.Num("burst", D.StrikeBurst, 0.01, 60), RecipeParam.Num("brightness", D.StrikeBrightness, 0, 100000),
        RecipeParam.State(),
    };

    // The settings the node's values give; a malformed vector or ring text falls back to the script's value.
    public static TornadoSettings Settings(RecipeContext c)
    {
        double[] Nums(string key, double[] fallback, int n)
        {
            var parts = c.Text(key).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return fallback;
            return v.Length == n ? v : fallback;
        }
        var rings = new List<TornadoRing>();
        foreach (var ring in c.Text("rings").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = ring.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 4 || !p.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                rings = D.Rings.ToList();
                break;
            }
            var n = p.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            rings.Add(new TornadoRing(n[0], n[1], (int)n[2], n[3]));
        }
        string donor = c.Text("donor");
        return new TornadoSettings
        {
            Name = ProjectNames.Id(c.Text("name")), FunnelHeight = c.Number("height"), FunnelBase = c.Number("base"), FunnelTop = c.Number("top"),
            FunnelSpin = c.Number("spin"), ColumnPuffs = (int)c.Number("puffs"), Strands = (int)c.Number("strands"), StrandSpeed = c.Number("strand_speed"),
            JetHeading = c.Number("jet_heading"), CapSpin = c.Number("cap_spin"), ZoneRadius = c.Number("zone"), AroundLife = c.Number("around_life"),
            DebrisNear = c.Number("debris_near"), Rings = rings.ToArray(), Dark = Nums("dark", D.Dark, 3), Brown = Nums("brown", D.Brown, 3),
            Dust = c.Text("dust"), Stones = c.Text("stones"), BoltDonor = donor.Length > 0 ? donor : null,
            StrikeGap = new[] { c.Number("gap_min"), Math.Max(c.Number("gap_max"), c.Number("gap_min")) }, StrikeBurst = c.Number("burst"),
            StrikeBrightness = c.Number("brightness"),
        };
    }

    protected override (ProjectFile Made, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Notes) Build(RecipeContext c)
    {
        var s = Settings(c);
        var r = TornadoGenerator.Generate(s);
        var made = TornadoGenerator.Project(s, r);
        return (made, r.Parts.Select(p => p.Bytes).ToList(), r.Notes);
    }
}
