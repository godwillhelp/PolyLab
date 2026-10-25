using System.Text;
using System.Text.RegularExpressions;
using Pfx.App.Core;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.App.Preview;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Schema;
using Pfx.Core.Text;
using Pfx.Render;

namespace Pfx.Tests;

public class AppTests
{
    private const string Ptc =
        "EffectDef {\n" +
        " Emitors {\n" +
        "  EmitorDef sparks {\n" +
        "   MaxNum 50\n" +
        "   BirthRate 10\n" +
        "   Material \"fx/spark.emat\"\n" +
        "   Velocity 3\n" +
        "   LifeTime 0.6\n" +
        "   LifeTimeRND 0.4\n" +
        "   FutureKey 7\n" +
        "   EffectTime 2\n" +
        "   Alpha {\n" +
        "    0 0 0.1 1 1 0\n" +
        "   }\n" +
        "  }\n" +
        " }\n" +
        "}";

    private const string Emat = "Particle {\n AlbedoMap \"fx/spark.edds\" alpha\n Emissive 1 1 1 0\n BlendMode Additive_AlphaModulate\n}";

    private static string Temp(string name, string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfx_app_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "fx"));
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
        return path;
    }

    private static string LangDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "ParticleStudio", "Pfx.App", "Lang"));

    private static Dictionary<string, string> Lang(string lang) =>
        Regex.Matches(File.ReadAllText(Path.Combine(LangDir(), lang + ".xaml")), "x:Key=\"([^\"]+)\">([^<]*)<")
            .ToDictionary(m => m.Groups[1].Value, m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));

    [Fact]
    public void The_three_languages_have_the_same_keys_and_placeholders()
    {
        var ru = Lang("ru");
        var en = Lang("en");
        var uk = Lang("uk");
        Assert.True(ru.Count > 500);
        Assert.Equal(ru.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        Assert.Equal(ru.Keys.OrderBy(k => k), uk.Keys.OrderBy(k => k));
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
        foreach (var k in ru.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[k]), k);
            Assert.False(string.IsNullOrWhiteSpace(uk[k]), k);
            Assert.True(Holes(ru[k]) == Holes(en[k]) && Holes(ru[k]) == Holes(uk[k]), k);
        }
    }

    [Theory]
    [InlineData("uk-UA", "uk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-US", "en")]
    public void New_settings_take_the_display_language(string culture, string lang)
    {
        var was = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
            Assert.Equal(lang, new AppSettings().Lang);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = was; }
    }

    [Fact]
    public void Every_schema_key_has_a_plain_name_in_every_language()
    {
        var schema = PfxSchema.Shared;
        foreach (var lang in new[] { "ru", "en", "uk" })
        {
            var d = Lang(lang);
            foreach (var f in schema.Effect.Concat(schema.Emitor))
                Assert.True(d.ContainsKey("F." + f.Key), lang + " F." + f.Key);
            foreach (var f in schema.Material)
                Assert.True(d.ContainsKey("M." + f.Key), lang + " M." + f.Key);
        }
    }

    [Fact]
    public void An_untouched_document_is_not_rewritten_and_save_as_is_byte_identical()
    {
        string path = Temp("a.ptc", Ptc.Replace("\n", "\r\n"));
        var stamp = File.GetLastWriteTimeUtc(path);
        var doc = EffectDoc.Open(path);
        Assert.False(doc.Dirty);
        Assert.Equal(0, doc.Save());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        string copy = Path.Combine(Path.GetDirectoryName(path)!, "b.ptc");
        doc.Save(copy);
        Assert.Equal(Encoding.Latin1.GetBytes(Ptc.Replace("\n", "\r\n")), File.ReadAllBytes(copy));
    }

    [Fact]
    public void Edits_undo_back_to_clean_and_labels_follow_the_history()
    {
        var doc = EffectDoc.Open(Temp("a.ptc", Ptc));
        var e = doc.Ptc.Emitters[0];
        doc.Do(e.Set("Velocity", 4), () => "v4", "drag");
        doc.Do(e.Set("Velocity", 5), () => "v5", "drag");
        doc.Seal();
        doc.Do(e.Set("MaxNum", 60), () => "max");
        Assert.True(doc.Dirty);
        Assert.Equal("max", doc.UndoLabel);
        doc.UndoStep();
        Assert.Equal("v5", doc.UndoLabel);
        doc.UndoStep();
        Assert.False(doc.Dirty);
        Assert.Null(doc.UndoLabel);
        doc.RedoStep();
        Assert.Equal(5, doc.Ptc.Emitters[0].Number("Velocity"));
        // After an undo a drag with the same key must start a new step, not merge into the step below.
        doc.UndoStep();
        doc.Do(e.Set("Velocity", 9), () => "v9", "drag");
        doc.UndoStep();
        Assert.False(doc.Dirty);
    }

    // Without a WPF application a key is its own text, so the key here is the format; `name` stands for a text in
    // the interface language.
    [Fact]
    public void Undo_labels_are_read_when_shown_with_the_values_of_the_edit()
    {
        var doc = EffectDoc.Open(Temp("a.ptc", Ptc));
        string name = "speed", value = "4";
        Func<string> field = () => name;
        doc.Do(doc.Ptc.Emitters[0].Set("Velocity", 4), Loc.Later("{0} = {1}", field, value));
        name = "Speed";
        value = "5";
        Assert.Equal("Speed = 4", doc.UndoLabel);
        doc.UndoStep();
        doc.RedoStep();
        name = "SPEED";
        Assert.Equal("SPEED = 4", doc.UndoLabel);
    }

    [Fact]
    public void Material_edits_count_as_changes_and_are_saved_with_the_effect()
    {
        string path = Temp("a.ptc", Ptc);
        string dir = Path.GetDirectoryName(path)!;
        File.WriteAllBytes(Path.Combine(dir, "fx", "spark.emat"), Encoding.Latin1.GetBytes(Emat));
        var doc = EffectDoc.Open(path);
        doc.LoadMaterials(new DataRoots(new[] { dir }));
        var m = doc.Materials["fx/spark.emat"];
        doc.Do(m.Emat.Set("Softness", 1), () => "soft");
        Assert.True(doc.Dirty);
        Assert.Equal(1, doc.Save());
        Assert.Contains("Softness 1", File.ReadAllText(Path.Combine(dir, "fx", "spark.emat")));
        Assert.False(doc.Dirty);
    }

    // Every key the engine registers gets a row (spec 1.1 rule 1); the shell checks the same on the built controls.
    [Fact]
    public void The_inspector_has_a_row_for_every_schema_key()
    {
        string path = Temp("a.ptc", Ptc);
        string dir = Path.GetDirectoryName(path)!;
        File.WriteAllBytes(Path.Combine(dir, "fx", "spark.emat"), Encoding.Latin1.GetBytes(Emat));
        var doc = EffectDoc.Open(path);
        doc.LoadMaterials(new DataRoots(new[] { dir }));
        var schema = PfxSchema.Shared;
        var e = doc.Ptc.Emitters[0];
        var cases = new (FieldScope Scope, KeyedBlock Block, IReadOnlyList<SchemaField> Fields)[]
        {
            (FieldScope.Effect, doc.Ptc, schema.Effect),
            (FieldScope.Emitter, e, schema.Emitor),
            (FieldScope.Material, doc.Materials["fx/spark.emat"].Emat, schema.Material),
        };
        foreach (var (scope, block, fields) in cases)
        {
            var vm = new InspectorVm(scope);
            vm.Show(doc, new[] { new Target(block, doc.OriginalOf(block.Block)) }, "t", "s");
            var keys = vm.AllFields.SelectMany(f => f.Keys).ToHashSet();
            var missing = fields.Select(f => f.Key).Where(k => !keys.Contains(k)).ToList();
            Assert.True(missing.Count == 0, scope + ": " + string.Join(", ", missing));
        }
        var emitterVm = new InspectorVm(FieldScope.Emitter);
        emitterVm.Show(doc, new[] { new Target(e, doc.OriginalOf(e.Block)) }, "t", "s");
        Assert.Contains(emitterVm.AllFields, f => f.Key == "FutureKey" && f is TextField { IsUnknown: true });
    }

    [Fact]
    public void A_typed_value_is_clamped_to_the_engine_range_and_noted()
    {
        var doc = EffectDoc.Open(Temp("a.ptc", Ptc));
        var e = doc.Ptc.Emitters[0];
        var vm = new InspectorVm(FieldScope.Emitter);
        vm.Show(doc, new[] { new Target(e, doc.OriginalOf(e.Block)) }, "t", "s");
        var restitution = vm.AllFields.OfType<NumberField>().First(f => f.Key == "Restitution");
        restitution.Commit("99");
        Assert.Equal(10, doc.Ptc.Emitters[0].Number("Restitution"));
        Assert.NotNull(restitution.Note);
        var pair = vm.AllFields.OfType<PairField>().First(f => f.Key == "Velocity");
        pair.Base.Commit("+=0.5");
        Assert.Equal(3.5, doc.Ptc.Emitters[0].Number("Velocity"));
        pair.Base.Commit("abc");
        Assert.Equal(3.5, doc.Ptc.Emitters[0].Number("Velocity"));
    }

    [Theory]
    [InlineData("2", 1, 2)]
    [InlineData("0.5*2", 1, 1)]
    [InlineData("(1+2)/4", 0, 0.75)]
    [InlineData("+=0.1", 1, 1.1)]
    [InlineData("-=1", 3, 2)]
    [InlineData("*=2", 3, 6)]
    [InlineData("*1.5", 2, 3)]
    [InlineData("+10%", 2, 2.2)]
    [InlineData("-25%", 4, 3)]
    [InlineData("1,5", 0, 1.5)]
    [InlineData("-5", 0, -5)]
    [InlineData("1e-3", 0, 0.001)]
    public void Number_fields_take_expressions(string text, double current, double expected)
    {
        Assert.True(Expr.TryApply(text, current, out var r));
        Assert.Equal(expected, r, 9);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1+")]
    [InlineData("")]
    [InlineData("2**3")]
    public void Bad_input_is_rejected(string text) => Assert.False(Expr.TryApply(text, 1, out _));

    [Fact]
    public void Emitter_names_follow_the_workbench_rules()
    {
        Assert.Null(Names.Problem("sparks_2", new[] { "glow" }));
        Assert.Equal("Name.Keyword", Names.Problem("ref", Array.Empty<string>()));
        Assert.Equal("Name.Chars", Names.Problem("two words", Array.Empty<string>()));
        Assert.Equal("Name.Chars", Names.Problem("spark!", Array.Empty<string>()));
        Assert.Equal("Name.Taken", Names.Problem("glow", new[] { "glow" }));
        Assert.Equal("emitor2", Names.Unique("emitor1", new[] { "emitor1" }));
        Assert.Equal("emitor3", Names.Unique("emitor1", new[] { "emitor1", "emitor2" }));
        Assert.Equal("sparks_cloned2", Names.Unique("sparks_cloned", new[] { "sparks_cloned" }));
    }

    private static byte[] Instances(EffectPreview p)
    {
        var bytes = new List<byte>();
        foreach (var sim in p.Sims)
            for (int e = 0; e < sim.Emitters.Count; e++)
            {
                var into = new Pfx.Sim.ParticleInstance[sim.Emitters[e].Count];
                sim.WriteInstances(e, into);
                bytes.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(into.AsSpan()).ToArray());
            }
        return bytes.ToArray();
    }

    // An edit rebuilds the preview at the playback time itself: emitters the edit did not touch are copied from the old
    // preview's checkpoint, the edited one is simulated, and the result equals a run from 0 of the edited file.
    [Fact]
    public void A_rebuilt_preview_is_exact_at_the_playback_time()
    {
        string two = Ptc.Replace("  EmitorDef sparks {", "  EmitorDef smoke {\n   BirthRate 30\n   MaxNum 200\n   LifeTime 3\n   Velocity 1\n   VelocityRND 2\n  }\n  EmitorDef sparks {");
        string path = Temp("a.ptc", two);
        var roots = new DataRoots(Array.Empty<string>());
        var settings = new PreviewSettings { Copies = 3, Motion = MotionKind.Circle };
        var old = new EffectPreview(PtcDocument.Load(path), path, roots, settings);
        old.Seek(4.25f);
        old.AddCheckpoint(old.Capture());
        old.Seek(7.3f);
        string edited = two.Replace("   Velocity 3\n", "   Velocity 5\n");
        var rebuilt = new EffectPreview(PtcDocument.From(EnfDocument.Parse(Encoding.Latin1.GetBytes(edited))), path, roots, settings);
        Assert.Equal(1, rebuilt.StartAt(7.3f, old.ForReuse()));
        var fresh = new EffectPreview(PtcDocument.From(EnfDocument.Parse(Encoding.Latin1.GetBytes(edited))), path, roots, settings);
        fresh.Seek(7.3f);
        Assert.Equal(fresh.ShownSteps, rebuilt.ShownSteps);
        Assert.Equal(Instances(fresh), Instances(rebuilt));
        Assert.True(fresh.AliveTotal > 100);
        // Other settings (seed): nothing is taken over, the result is still exact.
        var reseeded = new EffectPreview(PtcDocument.Load(path), path, roots, settings with { Seed = 9 });
        Assert.Equal(0, reseeded.StartAt(7.3f, old.ForReuse()));
        var reseededFresh = new EffectPreview(PtcDocument.Load(path), path, roots, settings with { Seed = 9 });
        reseededFresh.Seek(7.3f);
        Assert.Equal(Instances(reseededFresh), Instances(reseeded));
    }

    // Spec 7 P6: an edited effect saved and opened again keeps every key: other keys as written, the edited graph's other
    // numbers as written (no 3-decimal rounding, W0), the new numbers as typed. Runs on the corpus when PFX_CORPUS is set.
    [Fact]
    public void Curve_edits_save_and_reopen_with_every_key_and_no_rounding()
    {
        var files = new List<string> { Temp("a.ptc", Ptc.Replace("    0 0 0.1 1 1 0\n", "    0 0 0.10 1.0 0.6518 0.03239 1 0\n")) };
        var roots = Environment.GetEnvironmentVariable("PFX_CORPUS");
        if (!string.IsNullOrEmpty(roots))
            files.AddRange(roots.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(r => Directory.EnumerateFiles(r, "*.ptc", SearchOption.AllDirectories)));
        string dir = Path.Combine(Path.GetTempPath(), "pfx_curve_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int emitters = 0;
        try
        {
            foreach (var file in files)
            {
                EffectDoc doc;
                PtcDocument original;
                try
                {
                    doc = EffectDoc.Open(file);
                    original = PtcDocument.Load(file);
                }
                catch (EnfSyntaxException)
                {
                    continue;
                }
                var editor = new Pfx.App.Curves.CurveEditor();
                foreach (var e in doc.Ptc.Emitters)
                {
                    editor.Show(doc, e, 1);
                    editor.Focus("Alpha");
                    editor.AddKeyAt(0.4321, 0.3333);
                    int at = editor.Keys(editor.Active).FindIndex(k => k.Time == 0.4321);
                    if (at >= 0)
                        editor.SetValue(at, "0.123456789");
                }
                string saved = Path.Combine(dir, emitters + ".ptc");
                doc.Save(saved);
                var reopened = PtcDocument.Load(saved);
                Assert.Equal(original.Emitters.Count, reopened.Emitters.Count);
                for (int i = 0; i < original.Emitters.Count; i++)
                {
                    var o = original.Emitters[i].Block;
                    var r = reopened.Emitters[i].Block;
                    foreach (var key in o.Items.Select(EnfEdits.KeyOf).Where(k => k.Length > 0 && k != "Alpha"))
                        Assert.Equal(Raw.Of(o, key), Raw.Of(r, key));
                    var before = (Raw.Of(o, "Alpha") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var after = (Raw.Of(r, "Alpha") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    int k = after.IndexOf("0.4321");
                    if (k < 0)
                        continue;
                    Assert.Equal("0.123456789", after[k + 1]);
                    after.RemoveRange(k, 2);
                    if (before.Length > 0)
                        Assert.Equal(before, after);
                    emitters++;
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        Assert.True(emitters > 0);
    }

    // Open -> save through the document model keeps every corpus file byte for byte.
    [Fact]
    public void Corpus_files_save_byte_identical_through_the_document_model()
    {
        var roots = Environment.GetEnvironmentVariable("PFX_CORPUS");
        if (string.IsNullOrEmpty(roots))
            return;
        int count = 0;
        string dir = Path.Combine(Path.GetTempPath(), "pfx_app_corpus_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var root in roots.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.ptc", SearchOption.AllDirectories))
                {
                    byte[] original = File.ReadAllBytes(file);
                    EffectDoc doc;
                    try
                    {
                        doc = EffectDoc.Open(file);
                    }
                    catch (EnfSyntaxException)
                    {
                        continue;
                    }
                    string copy = Path.Combine(dir, count + ".ptc");
                    doc.Save(copy);
                    Assert.True(File.ReadAllBytes(copy).AsSpan().SequenceEqual(original), file);
                    count++;
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        Assert.True(count > 600, $"{count} files");
    }
}
