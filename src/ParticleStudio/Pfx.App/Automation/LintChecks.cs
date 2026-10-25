using Pfx.App.Lint;
using Pfx.Render;

namespace Pfx.App.Automation;

// P12 checks through the shell's own paths: an inspector edit that breaks a rule shows up in the lint after the worker
// run, a click on its row selects the emitter and finds the field, undo clears it; the layers and overdraw views draw
// something different from the normal look and the draw-order list matches the renderer's draws.
public sealed partial class ShellChecks
{
    private async Task Linted()
    {
        await Frame();
        while (_w.LintBusy)
            await Frame();
        await Settled();
    }

    private async Task LintChecks()
    {
        Log("Checks (lint, draw order, budget):");
        var doc = _w.Docs[0];
        var e = doc.Ptc.Emitters[0];
        await Linted();
        int before = _w.Lint.Issues.Count(i => i.Rule == Linter.Restitution);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        doc.Do(e.Set("Restitution", 0.5), () => "check: restitution");
        await Linted();
        var hit = _w.Lint.Issues.FirstOrDefault(i => i.Rule == Linter.Restitution && i.Emitter == 0 && _w.Lint.Parts[i.Part] == doc.Title);
        Expect(hit != null && _w.Lint.Issues.Count(i => i.Rule == Linter.Restitution) == before + 1,
            $"Restitution 0.5 on {e.Name} listed after {clock.Elapsed.TotalMilliseconds:0} ms (worker {_w.LastLintMs:0.0} ms)");
        if (hit != null)
        {
            _w.ShowPane("checks");
            _w.Jump(hit);
            await Settled();
            Expect(_w.SearchText == "Restitution" && _w.SelectedEmitters.Select(v => v.Block).SequenceEqual(new[] { e.Block }), "a click on the row selects the emitter and finds the field");
            _w.SearchText = "";
        }
        doc.UndoStep();
        await Linted();
        Expect(_w.Lint.Issues.Count(i => i.Rule == Linter.Restitution) == before, "undo removes the row");

        _w.ShowPane("checks");
        await Settled();
        await Frame();
        while (_w.BudgetBusy)
            await Frame();
        await Settled();
        var b = _w.Budget;
        Expect(b != null && b.Peak > 0 && b.Parts.Count == _w.Docs.Count,
            $"budget: peak {b?.Peak} at {b?.PeakAt:0.0} s, {b?.Parts.Count} parts, textures {b?.TextureBytes / 1048576.0:0.00} MB, worker {_w.LastBudgetMs:0} ms");

        var hashes = new Dictionary<DrawView, int>();
        foreach (var mode in Enum.GetValues<DrawView>())
        {
            _w.DrawMode = mode;
            await Frame();
            await Frame();
            var image = _w.Viewport.CaptureFrame(true);
            hashes[mode] = image == null ? 0 : image.Pixels.Aggregate(17, (h, p) => h * 31 + p);
        }
        _w.DrawMode = DrawView.Normal;
        Expect(hashes.Values.Distinct().Count() == hashes.Count, "normal, layers and overdraw views draw three different frames");
        var rows = _w.DrawOrder(out double fill);
        int draws = _w.Viewport.Draws.Count(d => d.Copy == 0);
        Expect(rows.Count == draws && rows.Select(r => r.Layer).SequenceEqual(Enumerable.Range(0, rows.Count)),
            $"draw order lists the renderer's {draws} layers back to front, screen fill x{fill:0.00}");
    }
}
