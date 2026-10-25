using System.Text;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Core.Text;

namespace Pfx.Tests;

public class EditTests
{
    private const string Ptc =
        "EffectDef {\n" +
        " Emitors {\n" +
        "  EmitorDef sparks {\n" +
        "   ShapeType Point\n" +
        "   MaxNum 50\n" +
        "   Material \"fx/spark.emat\"\n" +
        "   Velocity 3\n" +
        "   LifeTime 0.6\n" +
        "   Alpha {\n" +
        "    0 0 0.1 1 1 0\n" +
        "   }\n" +
        "  }\n" +
        "  EmitorDef glow {\n" +
        "   SizeMultiplier 2\n" +
        "  }\n" +
        " }\n" +
        "}";

    private static string Crlf(string text) => text.Replace("\n", "\r\n");

    private static (PtcDocument Doc, byte[] Original) Load(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        return (PtcDocument.From(EnfDocument.Parse(bytes)), bytes);
    }

    private static string Text(PtcDocument doc) => Encoding.Latin1.GetString(doc.ToBytes());

    // After the edit the file must parse again and give the same values; after undo it must be the original bytes.
    private static PtcDocument Reparse(PtcDocument doc) => PtcDocument.From(EnfDocument.Parse(doc.ToBytes()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setting_an_existing_key_changes_only_its_value(bool crlf)
    {
        string src = crlf ? Crlf(Ptc) : Ptc;
        var (doc, original) = Load(src);
        var undo = new UndoStack();
        undo.Do(doc.Emitter("sparks")!.Set("Velocity", 4.25));
        Assert.Equal(src.Replace("Velocity 3", "Velocity 4.25"), Text(doc));
        Assert.Equal(4.25, Reparse(doc).Emitter("sparks")!.Number("Velocity"));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_key_is_inserted_in_engine_order_with_the_files_line_break(bool crlf)
    {
        string src = crlf ? Crlf(Ptc) : Ptc;
        string nl = crlf ? "\r\n" : "\n";
        var (doc, original) = Load(src);
        var undo = new UndoStack();
        undo.Do(doc.Emitter("sparks")!.Set("BirthRate", 40));
        Assert.Equal(src.Replace("   MaxNum 50" + nl, "   MaxNum 50" + nl + "   BirthRate 40" + nl), Text(doc));
        Assert.Equal(40, Reparse(doc).Emitter("sparks")!.Number("BirthRate"));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void A_key_that_is_not_in_the_file_reads_as_the_engine_default()
    {
        var (doc, _) = Load(Ptc);
        var glow = doc.Emitter("glow")!;
        Assert.False(glow.Has("Velocity"));
        Assert.Equal(2, glow.Number("Velocity"));
        Assert.Equal(new double[] { 360, 0, 20 }, glow.Numbers("ConeAngle"));
    }

    [Fact]
    public void Reset_removes_the_key_and_the_default_applies()
    {
        var (doc, original) = Load(Ptc);
        var undo = new UndoStack();
        undo.Do(doc.Emitter("sparks")!.Reset("MaxNum")!);
        Assert.DoesNotContain("MaxNum", Text(doc));
        Assert.Equal(10, Reparse(doc).Emitter("sparks")!.Number("MaxNum"));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void Graphs_are_replaced_or_created()
    {
        var (doc, original) = Load(Ptc);
        var undo = new UndoStack();
        var sparks = doc.Emitter("sparks")!;
        undo.Do(sparks.SetGraph("Alpha", new double[] { 0, 1, 1, 0 }));
        undo.Do(sparks.SetGraph("Size", new double[] { 0, 0.5, 1, 1 }));
        string text = Text(doc);
        Assert.Contains("   Alpha {\n    0 1 1 0\n   }\n", text);
        Assert.Contains("   Size {\n    0 0.5 1 1\n   }\n", text);
        var back = Reparse(doc).Emitter("sparks")!;
        Assert.Equal(new double[] { 0, 1, 1, 0 }, back.Numbers("Alpha"));
        Assert.Equal(new double[] { 0, 0.5, 1, 1 }, back.Numbers("Size"));
        undo.Undo();
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void Emitters_can_be_added_renamed_moved_and_removed()
    {
        var (doc, original) = Load(Ptc);
        var undo = new UndoStack();
        undo.Do(doc.AddEmitter("smoke"));
        Assert.Equal(new[] { "sparks", "glow", "smoke" }, doc.Emitters.Select(e => e.Name));
        undo.Do(doc.MoveEmitter(doc.Emitter("smoke")!, 0));
        Assert.Equal(new[] { "smoke", "sparks", "glow" }, doc.Emitters.Select(e => e.Name));
        undo.Do(doc.Emitter("glow")!.Rename("halo"));
        undo.Do(doc.RemoveEmitter(doc.Emitter("sparks")!));
        Assert.Equal(new[] { "smoke", "halo" }, Reparse(doc).Emitters.Select(e => e.Name));
        undo.Do(doc.Emitter("smoke")!.Set("Velocity", 1));
        Assert.Equal(1, Reparse(doc).Emitter("smoke")!.Number("Velocity"));
        while (undo.CanUndo)
            undo.Undo();
        Assert.Equal(original, doc.ToBytes());
        while (undo.CanRedo)
            undo.Redo();
        Assert.Equal(new[] { "smoke", "halo" }, Reparse(doc).Emitters.Select(e => e.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_clone_copies_the_emitter_text_after_its_source(bool crlf)
    {
        string text = crlf ? Crlf(Ptc) : Ptc;
        var (doc, original) = Load(text);
        var undo = new UndoStack();
        undo.Do(doc.CloneEmitter(doc.Emitter("sparks")!, "sparks_cloned"));
        string nl = crlf ? "\r\n" : "\n";
        string block = text.Substring(text.IndexOf("  EmitorDef sparks", StringComparison.Ordinal), text.IndexOf(nl + "  EmitorDef glow", StringComparison.Ordinal) - text.IndexOf("  EmitorDef sparks", StringComparison.Ordinal));
        Assert.Contains(nl + block + nl + block.Replace("EmitorDef sparks", "EmitorDef sparks_cloned") + nl + "  EmitorDef glow", Text(doc));
        var again = Reparse(doc);
        Assert.Equal(new[] { "sparks", "sparks_cloned", "glow" }, again.Emitters.Select(e => e.Name));
        Assert.Equal(again.Emitter("sparks")!.Numbers("Alpha"), again.Emitter("sparks_cloned")!.Numbers("Alpha"));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void An_effect_without_emitors_gets_the_block_created()
    {
        var (doc, original) = Load("EffectDef {\n}\n");
        var undo = new UndoStack();
        undo.Do(doc.AddEmitter("first"));
        Assert.Equal("EffectDef {\n Emitors {\n  EmitorDef first {\n  }\n }\n}\n", Text(doc));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void Drag_steps_with_one_merge_key_undo_as_one()
    {
        var (doc, original) = Load(Ptc);
        var undo = new UndoStack();
        var sparks = doc.Emitter("sparks")!;
        foreach (var v in new[] { 3.1, 3.2, 3.3 })
            undo.Do(sparks.Set("Velocity", v), "drag:sparks.Velocity");
        undo.Seal();
        undo.Do(sparks.Set("Velocity", 9), "drag:sparks.Velocity");
        undo.Undo();
        Assert.Equal(3.3, sparks.Number("Velocity"));
        undo.Undo();
        Assert.Equal(original, doc.ToBytes());
    }

    [Fact]
    public void An_old_style_material_keeps_its_quoted_vectors()
    {
        const string src = "material \"Graphics/Particles/materials/flash/electric.emat\": Particle\r\n{\r\n" +
                           "\tSort translucent\r\n\tColor \"0.000000 0.000000 0.000000 1.000000\"\r\n}\r\n";
        var bytes = Encoding.Latin1.GetBytes(src);
        var mat = EmatDocument.From(EnfDocument.Parse(bytes));
        Assert.Equal("Particle", mat.ClassName);
        Assert.Equal(new double[] { 0, 0, 0, 1 }, mat.Numbers("Color"));
        var undo = new UndoStack();
        undo.Do(mat.Set("Color", 1, 0.5, 0, 1));
        Assert.Contains("\tColor \"1 0.5 0 1\"\r\n", Encoding.Latin1.GetString(mat.ToBytes()));
        undo.Do(mat.SetWord("BlendMode", "Additive_AlphaModulate"));
        Assert.Contains("\r\n\tBlendMode Additive_AlphaModulate\r\n}", Encoding.Latin1.GetString(mat.ToBytes()));
        undo.Undo();
        undo.Undo();
        Assert.Equal(bytes, mat.ToBytes());
    }

    [Fact]
    public void A_project_file_saves_and_opens_with_relative_paths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pfx_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "particles"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "particles", "a.ptc"), Ptc, Encoding.Latin1);
            var comp = new Composition(Path.Combine(dir, "a.plpfx"), new ProjectFile());
            comp.File.Parts.Add(new PartEntry { Id = "a", Path = comp.Relative(Path.Combine(dir, "particles", "a.ptc")) });
            comp.Save();
            comp.Save();
            var back = Composition.Open(Path.Combine(dir, "a.plpfx"));
            Assert.Equal(new[] { "particles/a.ptc" }, back.File.Parts.Select(p => p.Path));
            Assert.Equal(2, back.LoadParts().Single().Doc.Emitters.Count);
            Assert.False(File.Exists(Path.Combine(dir, "a.plpfx.tmp")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Projects_saved_before_the_rename_still_count_as_projects()
    {
        Assert.True(ProjectFile.IsProject(@"D:\fx\a.plpfx"));
        Assert.True(ProjectFile.IsProject(@"D:\fx\old.NDZPFX"));
        Assert.False(ProjectFile.IsProject(@"D:\fx\a.ptc"));
    }
}
