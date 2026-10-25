using System.Text;
using System.Text.Json;
using Pfx.App.Recipes;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.Tests;

// P12b: every recipe makes its change, and taking its node back leaves the project and every part byte for byte as
// before (spec 3.10 acceptance: "removing a recipe node removes everything it created", one test per recipe).
public sealed class RecipeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfx_rcp_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, PtcDocument> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProjectFile _project = new() { Name = "test", States = { new StateEntry { Name = "idle" }, new StateEntry { Name = "hit", Duration = 2 } } };

    public RecipeTests()
    {
        Write("m/dust.emat", "ParticleSprite {\n Color 0.3922 0.3922 0.3922 1\n Softness 2\n}\n");
        Write("m/cloud.emat", "Particle {\n Diffuse 0.7216 0.7216 0.7216 1\n Softness 2\n}\n");
        Write("m/lit.emat", "Particle {\n NormalMap \"t/n.edds\"\n Softness 2\n}\n");
        Write("m/glow.emat", "ParticleSprite {\n Emissive 1 1 1 1\n}\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private void Write(string rel, string text)
    {
        string path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
    }

    private static string Emitter(string name, string keys) => $"  EmitorDef {name} {{\n   {keys.Replace("\n", "\n   ")}\n  }}\n";

    private const string Puff = "BirthRate 5\nLifeTime 4\nMaxNum 30\nSizeMultiplier 2\nVelocity 1\nMaterial \"m/cloud.emat\"\nColor {\n 0 1 0.5 0.25 1 1 0.5 0.25\n}";

    // A part with the given emitters, in the first state, its file written (CRLF like the game's files).
    private PartEntry Part(string id, float spin, params string[] emitters)
    {
        string text = ("EffectDef {\n Emitors {\n" + string.Concat(emitters) + " }\n}\n").Replace("\n", "\r\n");
        Write("parts/" + id + ".ptc", text);
        var entry = new PartEntry { Id = id, Path = "parts/" + id + ".ptc", Spin = spin };
        _project.Parts.Add(entry);
        _project.States[0].Items.Add(new StateItem { Ref = id });
        _docs[entry.Path] = PtcDocument.From(EnfDocument.Parse(Encoding.Latin1.GetBytes(text)));
        return entry;
    }

    private RecipeScene Scene() => new(_project,
        p => _docs.GetValueOrDefault(p.Path),
        p => _docs.TryGetValue(p.Path, out var d) ? d : _docs[p.Path] = PtcDocument.From(EnfDocument.Parse("EffectDef {\r\n Emitors {\r\n }\r\n}\r\n")),
        game => File.Exists(Path.Combine(_dir, game)) ? Path.Combine(_dir, game) : null,
        _ => null,
        p => Path.Combine(_dir, p.Path),
        folder => Path.GetRelativePath(_dir, folder).Replace('\\', '/'),
        _dir);

    private string State() => JsonSerializer.Serialize(_project, Composition.Json) + "\n"
        + string.Join("\n", _docs.OrderBy(d => d.Key).Select(d => d.Key + "=" + d.Value.Doc.Write()));

    private (RecipeNode Node, RecipeScene Scene) Apply(string kind, IEnumerable<RecipeTarget> targets, params (string Key, string Value)[] values)
    {
        var node = new RecipeNode { Id = kind + "1", Kind = kind, Params = RecipeCatalog.Find(kind)!.Defaults(), Targets = targets.ToList() };
        foreach (var (k, v) in values)
            node.Params[k] = v;
        _project.Recipes.Add(node);
        var scene = Scene();
        Assert.Null(RecipeRunner.Apply(scene, node));
        return (node, scene);
    }

    private static RecipeTarget[] Of(string part, params string[] emitters) =>
        emitters.Length == 0 ? new[] { new RecipeTarget { Part = part } } : emitters.Select(e => new RecipeTarget { Part = part, Emitter = e }).ToArray();

    private PtcDocument Doc(string id) => _docs[_project.Parts.First(p => p.Id == id).Path];

    // Applies, checks, takes the node back: the project, every part (new parts empty again) and the folder as before.
    private void RoundTrip(string kind, IEnumerable<RecipeTarget> targets, Action<RecipeNode> check, params (string, string)[] values)
    {
        string project = JsonSerializer.Serialize(_project, Composition.Json);
        var parts = _docs.ToDictionary(d => d.Key, d => d.Value.Doc.Write());
        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Order().ToList();
        string before = State();
        var (node, _) = Apply(kind, targets, values);
        Assert.NotEqual(before, State());
        Assert.NotEmpty(node.Log);
        check(node);
        var scene = Scene();
        RecipeUndo.Revert(node, scene);
        _project.Recipes.Remove(node);
        Assert.Empty(scene.Notes);
        Assert.Equal(project, JsonSerializer.Serialize(_project, Composition.Json));
        foreach (var d in _docs)
        {
            if (parts.TryGetValue(d.Key, out var text))
                Assert.Equal(text, d.Value.Doc.Write());
            else
                Assert.Empty(d.Value.Emitters);
        }
        Assert.Equal(files, Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public void Swirl_turns_the_part_and_its_emitters_follow()
    {
        Part("body", 0, Emitter("a", Puff), Emitter("b", Puff + "\nLocalTransform 0"));
        Part("ring", 0, Emitter("r", Puff + "\nOffset 30 0 0"));
        RoundTrip("swirl", Of("body").Concat(Of("ring")), _ =>
        {
            Assert.Equal(-150f, _project.Parts[0].Spin);
            Assert.All(Doc("body").Emitters, e => Assert.Equal(1, e.Number("LocalTransform")));
            Assert.Equal(-16.67f, _project.Parts[1].Spin);
        }, ("rankine", "1"));
    }

    [Fact]
    public void Orbit_turn_spins_by_the_angle_over_the_longest_life()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("orbit_turn", Of("p"), _ => Assert.Equal(22.5f, _project.Parts[0].Spin));
    }

    [Fact]
    public void Strands_add_the_tornado_jets()
    {
        Part("p", -150, Emitter("a", Puff));
        RoundTrip("strands", Of("p"), _ => Assert.Equal(new[] { "a", "strand_0", "strand_1", "strand_2", "strand_3", "strand_4" }, Doc("p").Emitters.Select(e => e.Name)));
    }

    [Fact]
    public void Funnel_adds_column_and_billow()
    {
        Part("p", -150, Emitter("a", Puff));
        RoundTrip("funnel", Of("p"), _ =>
        {
            var names = Doc("p").Emitters.Select(e => e.Name).ToList();
            Assert.Equal(new[] { "a", "column", "ground_billow" }, names);
            Assert.Equal("\r\n", Doc("p").Doc.NewLine);
            Assert.DoesNotContain("\n  EmitorDef column", Doc("p").Doc.Write().Replace("\r\n", "\r"));
        });
    }

    [Fact]
    public void Add_light_takes_colour_and_reach_from_the_emitters()
    {
        Part("p", 0, Emitter("a", Puff + "\nOffset 0 2 0"));
        RoundTrip("add_light", Of("p", "a"), _ =>
        {
            var l = Assert.Single(_project.Lights);
            Assert.Equal(new[] { 1f, 0.5f, 0.25f }, l.Color);
            Assert.Equal(2f, l.Offset[1]);
            Assert.InRange(l.Radius, 4, 12);
            Assert.Contains(_project.States[0].Items, i => i.Ref == l.Id);
        });
    }

    [Fact]
    public void Haze_tint_flash_and_sound_go_into_the_chosen_state()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("haze", Array.Empty<RecipeTarget>(), _ => Assert.Equal("haze", _project.States[1].Items.Single().Ref), ("state", "hit"));
        RoundTrip("tint", Array.Empty<RecipeTarget>(), _ => Assert.Equal(new[] { 1f, 0.5f, 0.2f }, _project.Screens.Single().Color));
        RoundTrip("flash", Array.Empty<RecipeTarget>(), _ =>
        {
            Assert.Equal(3f, _project.Flashes.Single().Full);
            Assert.Equal(new[] { "flash", "flash_light" }, _project.States[1].Items.Select(i => i.Ref));
            Assert.Equal(0.2f, _project.Lights.Single().Life);
        }, ("state", "hit"));
        Write("s/hum.ogg", "OggS");
        RoundTrip("add_sound", Array.Empty<RecipeTarget>(), _ => Assert.Equal("s/hum.ogg", _project.Sounds.Single().File), ("file", "s/hum.ogg"));
    }

    [Fact]
    public void Around_dust_gets_the_swirl_of_the_core()
    {
        Part("dust", 0, Emitter("a", Puff));
        RoundTrip("around", Of("dust"), _ =>
        {
            Assert.Equal(100f, _project.Parts[0].Around);
            Assert.Equal(-261.8f, _project.Parts[0].Swirl);
            Assert.Equal(10f, _project.Parts[0].SwirlMin);
        });
    }

    [Fact]
    public void Stop_split_and_near_only_move_emitters_into_a_new_part()
    {
        Part("p", -30, Emitter("a", Puff), Emitter("b", Puff));
        RoundTrip("stop_split", Of("p", "b"), _ =>
        {
            var split = _project.Parts.Single(x => x.Id == "p_s");
            Assert.Equal(2f, split.Stop);
            Assert.Equal(-30f, split.Spin);
            Assert.Equal("b", Doc("p_s").Emitters.Single().Name);
            Assert.Equal("a", Doc("p").Emitters.Single().Name);
            Assert.Contains(_project.States[0].Items, i => i.Ref == "p_s");
        });
        RoundTrip("near_only", Of("p", "a"), _ => Assert.Equal(150f, _project.Parts.Single(x => x.Id == "p_near").Near));
    }

    [Fact]
    public void Soft_ground_points_at_a_softened_copy()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("soft_ground", Of("p", "a"), _ =>
        {
            Assert.Equal("parts/materials/cloud_soft.emat", Doc("p").Emitter("a")!.Text("Material"));
            var m = EmatDocument.Load(Path.Combine(_dir, "parts", "materials", "cloud_soft.emat"));
            Assert.Equal(1, m.Number("Softness"));
            Assert.Equal(1, m.Number("CameraBlendFar"));
        });
    }

    [Fact]
    public void Palette_match_scales_colour_by_the_material_ratio()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("palette_match", Of("p", "a"), _ => Assert.Equal(new[] { 0, 0.3922, 0.1961, 0.0981, 1, 0.3922, 0.1961, 0.0981 }, Doc("p").Emitter("a")!.Numbers("Color")),
            ("reference", "m/dust.emat"));
    }

    [Fact]
    public void Ground_glow_lies_flat_under_the_effect()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("ground_glow", Of("p"), _ =>
        {
            var g = Doc("p").Emitter("ground_glow")!;
            Assert.Equal("None", g.Text("BillboardingType"));
            Assert.Equal(new double[] { 0, 90, 0 }, g.Numbers("Angles"));
            Assert.Equal("m/glow.emat", g.Text("Material"));
            Assert.Null(g.Block.FindLine("RandomAngle"));
        }, ("material", "m/glow.emat"));
    }

    [Fact]
    public void Ground_glow_drops_the_softness_of_its_material()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("ground_glow", Of("p"), _ =>
        {
            Assert.Equal("parts/materials/dust_flat.emat", Doc("p").Emitter("ground_glow")!.Text("Material"));
            var m = EmatDocument.Load(Path.Combine(_dir, "parts", "materials", "dust_flat.emat"));
            Assert.Null(m.Block.FindLine("Softness"));
            Assert.Equal(0.3922, m.Number("Color"));
        }, ("material", "m/dust.emat"));
    }

    [Fact]
    public void Spawner_parent_becomes_scaled_children()
    {
        Part("p", 0, Emitter("parent", "BirthRate 2\nLifeTime 3\nMaxNum 10\nOffset 1 2 3"), Emitter("child", "BirthRate 4\nMaxNum 5\nOffset 0 1 0"));
        RoundTrip("spawner", Of("p", "parent", "child"), _ =>
        {
            var c = Assert.Single(Doc("p").Emitters);
            Assert.Equal(24, c.Number("BirthRate"));
            Assert.Equal(50, c.Number("MaxNum"));
            Assert.Equal(new double[] { 1, 3, 3 }, c.Numbers("Offset"));
        });
    }

    [Fact]
    public void Single_column_replaces_a_stack_with_one_base_emitter()
    {
        Part("p", -150, Emitter("low", Puff + "\nOffset 0 0 0\nShapeSize 10 0 10"), Emitter("mid", Puff + "\nOffset 0 10 0\nShapeSize 15 0 15"),
            Emitter("high", Puff.Replace("SizeMultiplier 2", "SizeMultiplier 6") + "\nOffset 0 20 0\nShapeSize 20 0 20"));
        RoundTrip("single_column", Of("p", "low", "mid", "high"), _ =>
        {
            var c = Assert.Single(Doc("p").Emitters);
            Assert.Equal("column", c.Name);
            Assert.Equal(15, c.Number("BirthRate"));
            Assert.Equal(6, c.Number("SizeMultiplier"));
            Assert.Equal(new double[] { 0, 0.333, 1, 1 }, c.Numbers("Size"));
            Assert.Equal(5.154, c.Number("Velocity")!.Value, 3);
        });
    }

    [Fact]
    public void One_body_turn_merges_parts_at_one_rate()
    {
        Part("body", -150, Emitter("a", Puff));
        Part("strands", 0, Emitter("s", Puff + "\nLocalTransform 0"));
        RoundTrip("one_body", Of("body").Concat(Of("strands")), _ =>
        {
            Assert.Single(_project.Parts);
            Assert.Equal(new[] { "a", "s" }, Doc("body").Emitters.Select(e => e.Name));
            Assert.All(Doc("body").Emitters, e => Assert.Equal(1, e.Number("LocalTransform")));
        });
    }

    [Fact]
    public void Strikes_play_the_part_only_in_bursts()
    {
        Part("bolts", 0, Emitter("a", Puff));
        RoundTrip("strikes", Of("bolts"), _ =>
        {
            Assert.Equal("bolts", _project.Strikes.Single().Part);
            Assert.Equal(new[] { "bolts_strikes" }, _project.States[0].Items.Select(i => i.Ref));
        });
    }

    [Fact]
    public void Strip_gate_removes_the_birth_master()
    {
        Part("p", 0, Emitter("a", Puff + "\nBRateMast {\n 0 0 0.9 0 1 1\n}"));
        RoundTrip("strip_gate", Of("p", "a"), _ => Assert.Null(Doc("p").Emitter("a")!.Block.FindBlock("BRateMast")));
    }

    [Fact]
    public void Make_lit_uses_a_lit_copy_and_refuses_moving_sources()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("make_lit", Of("p", "a"), _ => Assert.Equal("parts/materials/lit_lit.emat", Doc("p").Emitter("a")!.Text("Material")), ("material", "m/lit.emat"));
        _project.Parts[0].Spin = 10;
        var node = new RecipeNode { Id = "x", Kind = "make_lit", Params = RecipeCatalog.Find("make_lit")!.Defaults(), Targets = Of("p", "a").ToList() };
        Assert.Equal("Rcp.Why.Moving", RecipeRunner.Apply(Scene(), node));
    }

    [Fact]
    public void Make_volumetric_splits_cards_on_still_sources()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("make_volumetric", Of("p", "a"), _ =>
        {
            var e = Doc("p").Emitter("a")!;
            Assert.Equal(1, e.Number("SizeMultiplier"));
            Assert.Equal(20, e.Number("BirthRate"));
            Assert.Equal(1, e.Number("Sort"));
        });
        Assert.Equal(RecipeStatus.Rejected, RecipeCatalog.Find("make_volumetric")!.Status);
    }

    [Fact]
    public void Fix_order_splits_a_cloud_along_its_long_side()
    {
        Part("p", 0, Emitter("a", Puff + "\nShapeType Box\nShapeSize 30 4 6"));
        RoundTrip("fix_order", Of("p", "a"), _ =>
        {
            Assert.Equal(new[] { "a_1", "a_2", "a_3" }, Doc("p").Emitters.Select(e => e.Name));
            Assert.Equal(new double[] { -10, 0, 0 }, Doc("p").Emitter("a_1")!.Numbers("Offset"));
            Assert.Equal(new double[] { 10, 4, 6 }, Doc("p").Emitter("a_3")!.Numbers("ShapeSize"));
        });
    }

    [Fact]
    public void Sort_inside_switches_sorting_on_still_sources_only()
    {
        Part("p", 0, Emitter("a", Puff));
        RoundTrip("sort_inside", Of("p", "a"), _ => Assert.Equal(1, Doc("p").Emitter("a")!.Number("Sort")));
    }

    [Fact]
    public void Every_recipe_has_a_test()
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RecipeTests.cs"));
        foreach (var r in RecipeCatalog.All)
            Assert.True(source.Contains("\"" + r.Id + "\"", StringComparison.Ordinal), r.Id + " has no test");
    }

    // Composed text keys (Rcp.<id>, its line, its sources, each parameter and its tip) exist in the three languages.
    [Fact]
    public void Every_recipe_has_its_texts()
    {
        string lang = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ParticleStudio", "Pfx.App", "Lang");
        foreach (var file in new[] { "ru.xaml", "en.xaml", "uk.xaml" })
        {
            string text = File.ReadAllText(Path.Combine(lang, file));
            bool Has(string key) => text.Contains("x:Key=\"" + key + "\"", StringComparison.Ordinal);
            foreach (var r in RecipeCatalog.All)
            {
                foreach (var key in new[] { "Rcp." + r.Id, "Rcp." + r.Id + ".What", "Rcp." + r.Id + ".Source" })
                    Assert.True(Has(key), file + ": " + key);
                foreach (var p in r.Params.Where(p => p.Kind != ParamKind.State))
                {
                    Assert.True(Has("Rcp." + r.Id + "." + p.Key), file + ": Rcp." + r.Id + "." + p.Key);
                    Assert.True(Has("Tip.Rcp." + r.Id + "." + p.Key), file + ": Tip.Rcp." + r.Id + "." + p.Key);
                }
            }
            string source = string.Concat(Directory.GetFiles(Path.Combine(lang, "..", "Recipes"), "*.cs").Select(File.ReadAllText));
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, "\"(Rcp\\.(Why|Note)\\.[A-Za-z]+)\""))
                Assert.True(Has(m.Groups[1].Value), file + ": " + m.Groups[1].Value);
        }
    }

    [Fact]
    public void A_key_changed_after_the_recipe_is_left_as_the_user_made_it()
    {
        Part("p", 0, Emitter("a", Puff));
        var (node, _) = Apply("sort_inside", Of("p", "a"));
        new Pfx.Core.Editing.UndoStack().Do(Doc("p").Emitter("a")!.Set("Sort", 0));
        var scene = Scene();
        RecipeUndo.Revert(node, scene);
        Assert.Equal("Rcp.Note.KeyChanged", Assert.Single(scene.Notes).Key);
        Assert.Equal(0, Doc("p").Emitter("a")!.Number("Sort"));
    }

    [Fact]
    public void Reapply_with_new_values_replaces_the_old_result()
    {
        Part("p", 0, Emitter("a", Puff));
        var (node, _) = Apply("haze", Array.Empty<RecipeTarget>());
        node.Params["radius"] = "3";
        Assert.Null(RecipeRunner.Reapply(Scene(), node));
        Assert.Equal(3f, Assert.Single(_project.Hazes).Radius);
        node.On = false;
        Assert.Null(RecipeRunner.Reapply(Scene(), node));
        Assert.Empty(_project.Hazes);
        Assert.Empty(node.Log);
    }

    [Fact]
    public void Tornado_node_writes_the_generator_parts_and_regenerates()
    {
        var (node, _) = Apply("gen_tornado", Array.Empty<RecipeTarget>());
        Assert.Equal(9, _project.Parts.Count);
        Assert.Equal("particles/tornado_cg_t1.ptc", _project.Parts[0].Path);
        var defaults = Pfx.Core.Generators.TornadoGenerator.Generate(new Pfx.Core.Generators.TornadoSettings());
        for (int i = 0; i < defaults.Parts.Count; i++)
            Assert.True(defaults.Parts[i].Bytes.AsSpan().SequenceEqual(Doc(_project.Parts[i].Id).ToBytes()), _project.Parts[i].Id);
        node.Params["rings"] = "30 12 10 3";
        Assert.Null(RecipeRunner.Reapply(Scene(), node));
        Assert.Equal(7, _project.Parts.Count);
        Assert.Equal("particles/tornado_cg_t1.ptc", _project.Parts[0].Path);
        Assert.Equal(-150f, _project.Parts[0].Spin);
    }

    [Fact]
    public void Tornado_node_with_the_golden_donor_equals_own_tornado_py()
    {
        string? dir = Environment.GetEnvironmentVariable("PFX_TORNADO_GOLDEN");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "golden.json")));
        string donor = golden.RootElement.GetProperty("donor").GetString()!;
        var (node, scene) = Apply("gen_tornado", Array.Empty<RecipeTarget>(), ("donor", donor));
        Assert.Empty(scene.Notes);
        var files = Directory.GetFiles(Path.Combine(dir, "default"), "*.ptc");
        Assert.Equal(files.Length, _project.Parts.Count);
        foreach (var f in files)
        {
            var part = _project.Parts.Single(p => p.Path == "particles/" + Path.GetFileName(f));
            Assert.True(File.ReadAllBytes(f).AsSpan().SequenceEqual(Doc(part.Id).ToBytes()), Path.GetFileName(f));
        }
        Assert.Single(_project.Strikes);

        // The node's values edited to golden.py's variant: regeneration writes what own_tornado.py writes with them.
        var map = new Dictionary<string, string>
        {
            ["FUNNEL_HEIGHT"] = "height", ["FUNNEL_TOP"] = "top", ["FUNNEL_SPIN"] = "spin", ["STRANDS"] = "strands", ["STRAND_SPEED"] = "strand_speed",
            ["CAP_SPIN"] = "cap_spin", ["AROUND_LIFE"] = "around_life", ["STRIKE_BURST"] = "burst", ["RINGS"] = "rings", ["DARK"] = "dark",
        };
        foreach (var o in golden.RootElement.GetProperty("variant").GetProperty("overrides").EnumerateObject())
        {
            string v = o.Value.GetString()!;
            node.Params[map[o.Name]] = o.Name switch
            {
                "RINGS" => string.Join("; ", System.Text.RegularExpressions.Regex.Matches(v, "\\(([^)]*)\\)").Select(m => string.Join(" ", m.Groups[1].Value.Split(',').Select(x => x.Trim())))),
                "DARK" => string.Join(" ", v.Trim('(', ')').Split(',').Select(x => x.Trim())),
                _ => v,
            };
        }
        Assert.Null(RecipeRunner.Reapply(Scene(), node));
        var variant = Directory.GetFiles(Path.Combine(dir, "variant"), "*.ptc");
        Assert.Equal(variant.Length, _project.Parts.Count);
        foreach (var f in variant)
        {
            var part = _project.Parts.Single(p => p.Path == "particles/" + Path.GetFileName(f));
            Assert.True(File.ReadAllBytes(f).AsSpan().SequenceEqual(Doc(part.Id).ToBytes()), "variant " + Path.GetFileName(f));
        }
    }

    // Every template generator ("gen_sparks", "gen_embers", "gen_fog", "gen_burst", "gen_flash", "gen_arc", "gen_vortex",
    // "gen_heat", "gen_glow") on one crafted template that has the emitters each one takes: one new part, sizes and births
    // scaled, its own items, and nothing left after taking the node back.
    [Theory]
    [InlineData("gen_sparks")]
    [InlineData("gen_embers")]
    [InlineData("gen_fog")]
    [InlineData("gen_burst")]
    [InlineData("gen_flash")]
    [InlineData("gen_arc")]
    [InlineData("gen_vortex")]
    [InlineData("gen_heat")]
    [InlineData("gen_glow")]
    public void Template_generator_scales_its_template(string kind)
    {
        string keys = "BirthRate 4\nMaxNum 20\nSizeMultiplier 0.5\nLifeTime 2\nMaterial \"m/glow.emat\"\nColor {\n 0 1 1 1 1 1 1 1\n}";
        Write("t/template.ptc", "EffectDef {\r\n Emitors {\r\n" + string.Concat(new[] { "sparksrandom2", "sparks_small", "explo_dust_01_small_ring_fast", "explo_fire_01",
            "explo_fire_02", "explo_debris_03_sparks", "light" }.Select(n => Emitter(n, keys).Replace("\n", "\r\n"))) + " }\r\n}\r\n");
        Part("p", 0, Emitter("a", Puff));
        var recipe = RecipeCatalog.Find(kind)!;
        RoundTrip(kind, Array.Empty<RecipeTarget>(), _ =>
        {
            var made = _project.Parts.Single(p => p.Id != "p");
            Assert.Equal("particles/" + kind[4..] + ".ptc", made.Path);
            var doc = Doc(made.Id);
            Assert.NotEmpty(doc.Emitters);
            Assert.All(doc.Emitters, e => Assert.Equal(1, e.Number("SizeMultiplier")));
            Assert.All(doc.Emitters, e => Assert.Equal(kind == "gen_vortex" ? 6 : 12, e.Number("BirthRate")));
            Assert.Contains(_project.States[0].Items, i => i.Ref == made.Id);
            if (kind == "gen_flash")
                Assert.Single(_project.Flashes);
            if (kind == "gen_heat")
                Assert.Equal(2, _project.Hazes.Count);
            if (kind == "gen_glow")
                Assert.Single(_project.Lights);
            if (kind == "gen_vortex")
            {
                Assert.Equal(14, doc.Emitters.Count);
                foreach (var e in doc.Emitters)
                {
                    bool pulled = e.Name.EndsWith("_pull");
                    Assert.Equal(pulled ? 0 : 1, e.Number("LocalTransform"));
                    if (pulled)
                        Assert.Equal(0.00559, e.Number("Spring"));
                    else
                        Assert.Null(e.Block.FindLine("Spring"));
                }
            }
        }, ("template", "t/template.ptc"), ("scale", "2"), ("rate", "3"));
        // Seen in game in P12c (2026-10-15) and P12d (2026-10-16).
        Assert.Equal(RecipeStatus.Proven, recipe.Status);
    }

    [Fact]
    public void Own_recipe_adds_its_emitters_with_exposed_values()
    {
        Part("p", 0, Emitter("a", Puff));
        var own = new OwnRecipe
        {
            Name = "sparks", Emitters = { "\n  EmitorDef spark {\n   BirthRate 9\n   SizeMultiplier 0.1\n  }" },
            Lights = { new LightEntry { Id = "glow" } }, Exposed = { new OwnParam { Emitter = "spark", Key = "BirthRate", Value = "9" } },
        };
        string before = State();
        var node = new RecipeNode { Id = "own1", Kind = OwnRecipeRun.Kind, Own = own, Targets = Of("p").ToList() };
        node.Params["p0"] = "20";
        _project.Recipes.Add(node);
        Assert.Null(RecipeRunner.Apply(Scene(), node));
        Assert.Equal(20, Doc("p").Emitter("spark")!.Number("BirthRate"));
        Assert.Equal("glow", _project.Lights.Single().Id);
        RecipeUndo.Revert(node, Scene());
        _project.Recipes.Remove(node);
        Assert.Equal(before, State());
    }
}

// P12b packaging: the link mod's PBO and the upscale bridge ride in the tool's assembly and are written beside it.
public sealed class PayloadTests
{
    [Fact]
    public void Embedded_files_are_written_beside_the_tool()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        string? written = Pfx.App.Core.Payload.Ensure(Pfx.App.Core.Payload.LinkPbo);
        Assert.Equal(Path.Combine(Pfx.App.Core.Payload.LinkMod, "addons", "PolyLab_PfxLink.pbo"), written);
        byte[] pbo = File.ReadAllBytes(written!);
        Assert.True(pbo.AsSpan().IndexOf("prefix\0PolyLab_PfxLink\0"u8) > 0);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "link", "PolyLab_PfxLink"), "*", SearchOption.AllDirectories))
            Assert.True(pbo.AsSpan().IndexOf(File.ReadAllBytes(file)) > 0, file);
        string? script = Pfx.App.Core.Payload.Ensure(Pfx.App.Core.Payload.Upscale);
        Assert.True(File.ReadAllBytes(Path.Combine(root, "src", "ParticleStudio", "Pfx.App", "Tools", "pfx_upscale.py")).AsSpan().SequenceEqual(File.ReadAllBytes(script!)));
    }
}
