using System.Windows;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.Core.Model;
using Pfx.Textures;

namespace Pfx.App.Automation;

// P7 checks: the slicer, the pivot / scale handles and fields, the grid, the texture panel and the atlas builder, run
// through the methods their controls call (no input reaches the desktop).
public sealed partial class ShellChecks
{
    private async Task<bool> Until(Func<bool> done, int frames = 900)
    {
        for (int i = 0; i < frames && !done(); i++)
            await Frame();
        return done();
    }

    // The first emitter whose material names a texture that resolves, a flipbook if there is one.
    private (EffectDoc Doc, EmitterView Emitter)? TexturedEmitter()
    {
        (EffectDoc, EmitterView)? any = null;
        foreach (var doc in _w.Docs)
            foreach (var e in doc.Ptc.Emitters)
            {
                if (e.Text("Material") is not { } mat || !doc.Materials.TryGetValue(mat, out var m)
                    || m.Emat.Block.FindLine("AlbedoMap")?.Values.FirstOrDefault()?.Value is not { } map || _w.Resolve(map) == null)
                    continue;
                if ((m.Emat.Number("TileU") ?? 1) * (m.Emat.Number("TileV") ?? 1) > 1)
                    return (doc, e);
                any ??= (doc, e);
            }
        return any;
    }

    private async Task TextureChecks()
    {
        Log("texture workspace:");
        if (TexturedEmitter() is not var (doc, emitter))
        {
            Problem("no opened emitter has a texture that resolves");
            return;
        }
        _w.Select(doc, new[] { emitter.Block }, false);
        _w.ShowPane("slicer");
        _w.ShowPane("texture");
        var s = _w.Slicer;
        await Until(() => s.Levels != null || s.LoadError != null);
        await Settled();
        Expect(s.Levels != null && s.Summary != null, $"slicer reads {Path.GetFileName(s.TexturePath)} of {emitter.Name}: {s.Summary?.Width}x{s.Summary?.Height} {s.Summary?.Format}, grid {s.TileU}x{s.TileV}");
        if (s.Levels == null || s.Summary == null)
            return;
        if (s.Summary.Kind == "EDDS")
        {
            long stored = EddsFile.Read(File.ReadAllBytes(s.TexturePath!)).Levels.Sum(l => (long)l.Data.Length);
            Expect(stored == s.Summary.GpuBytes, $"video memory readout {s.Summary.GpuBytes} bytes = the stored mips {stored}");
        }
        await Frame();
        await Frame();
        int idx = doc.Ptc.Emitters.ToList().FindIndex(x => x.Block == emitter.Block);
        int alive = _w.Pipeline.PreviewOf(doc)?.Sims[0].Emitters[idx].Count ?? -1;
        Expect(s.Live.Sum() == s.LiveTotal && s.LiveTotal > 0, $"live tiles: {string.Join(" ", s.Live)} = {s.Live.Sum()} particles, the emitter has {s.LiveTotal} (preview {alive})");
        var samples = s.SampleParticles();
        Expect(samples.All(x => x.Frame == Pfx.Sim.Flipbook.FrameAt(s.Params!, x.Start, x.Age)), "sample particles show the simulator's frame for their age");
        Expect(_w.TextureView.Rows.Count > 8 && _w.TextureView.Rows.Any(r => r.Label == Loc.T("Tex.Gpu")), $"texture panel: {_w.TextureView.Rows.Count} rows");

        // Pivot: press, two moves, release on the sprite view = one undo step, the inspector shows the same value.
        var sprite = _w.SlicerView.SpritePanel;
        Expect(sprite.ActualWidth >= 80 && sprite.ActualHeight >= 80, $"sprite view {sprite.ActualWidth:0}x{sprite.ActualHeight:0} px at the default dock height");
        var before = doc.Ptc.ToBytes();
        string? label = doc.UndoLabel;
        var p = sprite.Pivot(s);
        sprite.Press(new Point(p.X + 6, p.Y - 4));
        sprite.Drag(new Point(p.X + 12, p.Y - 9));
        sprite.Drag(new Point(p.X + 20, p.Y - 14));
        sprite.Release();
        await Built();
        await Settled();
        var cx = _w.EmitterInspector.AllFields.OfType<NumberField>().FirstOrDefault(f => f.Key == "CenterX");
        var cy = _w.EmitterInspector.AllFields.OfType<NumberField>().FirstOrDefault(f => f.Key == "CenterY");
        double dragX = s.CenterX, dragY = s.CenterY;
        var np = sprite.Pivot(s);
        Expect(dragX != 0 && cx?.Value == dragX && cy?.Value == dragY && Math.Abs(np.X - (p.X + 20)) <= 2 && Math.Abs(np.Y - (p.Y - 14)) <= 2,
            $"pivot drag: CenterX {dragX} CenterY {dragY}, inspector {cx?.Value} {cy?.Value}, cross under the pointer ({np.X - p.X:0.#}, {np.Y - p.Y:0.#} px)");
        doc.UndoStep();
        await Built();
        Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(before) && doc.UndoLabel == label, "the whole pivot drag undoes as one step");

        var right = sprite.HandleRight(s);
        sprite.Press(right);
        sprite.Drag(new Point(right.X - 15, right.Y));
        sprite.Release();
        await Built();
        await Settled();
        var sx = _w.EmitterInspector.AllFields.OfType<NumberField>().FirstOrDefault(f => f.Key == "ScaleX");
        Expect(s.ScaleX < 1 && sx?.Value == s.ScaleX, $"right handle: ScaleX {s.ScaleX}, inspector {sx?.Value}");
        cx?.Commit("0.25");
        await Built();
        await Settled();
        Expect(s.CenterX == 0.25 && sprite.Pivot(s).X < right.X, $"inspector CenterX 0.25 → slicer {s.CenterX}");
        while (!doc.Ptc.ToBytes().AsSpan().SequenceEqual(before) && doc.Undo.CanUndo)
        {
            doc.UndoStep();
            await Built();
        }
        Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(before), "undo returns the emitter byte for byte");

        // Grid and frame rate through the slicer's fields.
        var view = _w.SlicerView;
        var material = s.Material!;
        int u0 = s.TileU;
        view.TileUField.Text = (u0 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        view.Commit(view.TileUField);
        await Built();
        var preview = _w.Pipeline.PreviewOf(doc)!;
        Expect(material.Emat.Number("TileU") == u0 + 1 && preview.Params[idx].TileU == u0 + 1 && s.Frames == (u0 + 1) * s.TileV,
            $"grid field: material TileU {material.Emat.Number("TileU")}, preview grid {preview.Params[idx].TileU}x{preview.Params[idx].TileV}");
        double fps0 = emitter.Number("AnimFPS") ?? 0;
        view.FpsField.Text = "+=12";
        view.Commit(view.FpsField);
        await Built();
        Expect(emitter.Number("AnimFPS") == fps0 + 12, $"frame rate field '+=12': AnimFPS {fps0} → {emitter.Number("AnimFPS")}");
        doc.UndoStep();
        await Built();
        doc.UndoStep();
        await Built();
        Expect(!doc.Dirty, "undo returns emitter and material unchanged");

        // Atlas builder: the slicer's sheet cut into frames, built again, written as EDDS.
        _w.ShowPane("atlas");
        var builder = _w.AtlasView.Builder;
        builder.Clear();
        _w.OpenInBuilder();
        bool whole = s.Summary.Width % s.TileU == 0 && s.Summary.Height % s.TileV == 0;
        if (whole)
        {
            await Until(() => !builder.Building && builder.Result != null);
            builder.Format = EddsFormat.Rgba8;
            await Until(() => !builder.Building && builder.Result?.Format == EddsFormat.Rgba8);
            var r = builder.Result!;
            Expect(builder.Frames.Count == s.Frames && r.TileU * r.TileV == s.Frames && r.GpuBytes == TextureMemory.Edds(EddsFormat.Rgba8, r.Levels[0].Width, r.Levels[0].Height),
                $"atlas from the sheet: {builder.Frames.Count} frames → {r.TileU}x{r.TileV} in {r.Levels[0].Width}x{r.Levels[0].Height}, {r.Levels.Length} mips, {r.GpuBytes} bytes");
            string outPath = Path.Combine(_dir, "atlas_check.edds");
            _w.AtlasView.ExportTo(outPath);
            await Until(() => builder.LastExport == outPath);
            var written = File.Exists(outPath) ? TextureSummary.Read(outPath) : null;
            Expect(written != null && written.Enf1 && written.GpuBytes == r.GpuBytes && written.Mips == r.Levels.Length,
                $"export: {Path.GetFileName(outPath)} ENF1 {written?.Enf1}, {written?.Mips} mips, video memory {written?.GpuBytes} bytes");
        }
        else
        {
            Log($"  (the sheet {s.Summary.Width}x{s.Summary.Height} does not split into {s.TileU}x{s.TileV}: atlas round trip skipped)");
        }
        await Settled();
    }
}
