using System.Text.Json;
using Pfx.Core.Export;
using Pfx.Core.Model;
using Pfx.Render;

namespace Pfx.App.Automation;

// P10 checks, through the methods the panels call: a project made from the open parts, one undo history over the
// project and a part, the states played by the preview (a part runs only in its state's window, a light glows only
// while its state runs), the timeline's project tracks, and the export panel's plan and write into a folder of the
// check run (written twice: the second plan is all unchanged; a hand-added file stays).
public sealed partial class ShellChecks
{
    private async Task CompositionChecks()
    {
        Log("Composition:");
        _w.StatesPlaying = false;
        _w.NewProject();
        var p = _w.Project!;
        Expect(p.File.Parts.Count == _w.Docs.Count && p.File.States.Count == 1 && p.File.States[0].Items.Count == _w.Docs.Count,
            $"new project from {_w.Docs.Count} open parts: {p.File.Parts.Count} parts in state {p.File.States[0].Name}");
        string first = p.File.Parts[0].Id, second = p.File.Parts[^1].Id;
        p.Do(f =>
        {
            f.Name = "check";
            f.States[0].Items.RemoveAll(i => i.Ref == second);
            f.Lights.Add(new LightEntry { Id = "glow", Radius = 6, Brightness = 2 });
            f.States[0].Items.Add(new StateItem { Ref = "glow" });
            f.Sounds.Add(new SoundEntry { Id = "roar", GameSet = "HeavyFire_SoundSet" });
            f.States.Add(new StateEntry { Name = "active", Duration = 2, Items = { new() { Ref = second, Delay = 0.5f }, new() { Ref = "roar" } } });
        }, () => "check: states");
        _w.ComposeTab.SelectItem("glow");
        await Settled();

        // One history: a project edit, then a part edit; Ctrl+Z takes back the part edit first.
        p.Do(f => f.Lights[0].Radius = 8, () => "check: radius");
        var doc = _w.Docs[0];
        var e = doc.Ptc.Emitters[0];
        double? before = e.Number("Velocity");
        doc.Do(e.Set("Velocity", (before ?? 0) + 1), () => "check: velocity");
        _w.UndoActive();
        bool partFirst = e.Number("Velocity") == before && p.File.Lights[0].Radius == 8;
        _w.UndoActive();
        bool thenProject = p.File.Lights[0].Radius == 6;
        _w.RedoActive();
        _w.RedoActive();
        Expect(partFirst && thenProject && p.File.Lights[0].Radius == 8 && e.Number("Velocity") == (before ?? 0) + 1,
            "one undo history: Ctrl+Z took back the part edit, then the project edit; Ctrl+Y redid both");
        _w.UndoActive();
        _w.UndoActive();

        // States in the preview.
        _w.StatesPlaying = true;
        var timeline = _w.ProjectTimeline;
        Expect(timeline is { States.Count: 2 } && timeline.Runs.Any(r => r.Ref == second && r.State == 1),
            $"states played: {string.Join(", ", timeline?.States.Select(s => $"{s.Name} {s.From:0.0}-{s.To:0.0}") ?? Array.Empty<string>())}");
        while (_w.Pipeline.AnyBusy)
            await Frame();
        var span = timeline!.States[1];
        var secondDoc = _w.Docs.First(d => _w.PartOf(d) == second);
        _w.Pipeline.Playing = false;
        foreach (var (t, wanted) in new[] { (span.From * 0.5, false), (span.From + 1.0, true) })
        {
            _w.Pipeline.Seek((float)t);
            while (_w.Pipeline.AnySeeking || _w.Pipeline.AnyBusy)
                await Frame();
            await Frame();
            int alive = _w.Pipeline.PreviewOf(secondDoc)?.AliveTotal ?? -1;
            Expect(wanted ? alive > 0 : alive == 0, $"part {second} at {t:0.00} s ({(wanted ? "its state" : "before its state")}): {alive} alive");
        }
        var batch = new SpriteBatch();
        _w.Pipeline.Seek((float)(span.From * 0.5));
        while (_w.Pipeline.AnySeeking)
            await Frame();
        _w.ComposeProjectSprites(batch);
        int idleGlow = batch.Vertices.Count;
        _w.Pipeline.Seek((float)(span.From + 1.0));
        while (_w.Pipeline.AnySeeking)
            await Frame();
        batch.Clear();
        _w.ComposeProjectSprites(batch);
        Expect(idleGlow > 0 && batch.Vertices.Count == 0, $"the idle light glows in idle ({idleGlow} vertices) and not in active ({batch.Vertices.Count})");
        TimelinePanelCheck();

        // Export into the check folder through the export panel.
        string mod = Path.Combine(_dir, "export_mod");
        Directory.CreateDirectory(mod);
        p.Do(f =>
        {
            f.Export.Profile = "dayz";
            f.Export.Dayz.ModFolder = mod;
            f.Export.Dayz.Prefix = "CHK";
        }, () => "check: export");
        var plan = await ExportPlanNow();
        Expect(plan is { CanWrite: true } && plan.Files.Count > 3 && plan.Files.All(f => f.State == FileState.New),
            $"export plan: {plan?.Files.Count} files, all new, errors {string.Join("; ", plan?.Errors.Select(x => x.Key) ?? Array.Empty<string>())}");
        _w.ExportTab.Write();
        File.WriteAllText(Path.Combine(mod, "fx", "mine.txt"), "a file of the user");
        var again = await ExportPlanNow();
        Expect(again is { CanWrite: true } && again.Files.All(f => f.State == FileState.Same) && again.Remove.Count == 0
               && File.Exists(Path.Combine(mod, "fx", "mine.txt")), "written; planned again: every file unchanged, nothing to remove, the user's file stays");
        Log("  example shown: " + (again?.Example?.Split('\n').FirstOrDefault() ?? "none"));
    }

    private void TimelinePanelCheck()
    {
        _w.TimelineView.Rebuild();
        var tracks = _w.TimelineView.Tracks;
        Expect(tracks.Any(t => t.Kind == Timeline.TrackKind.State && t.Spans.Count == 2) && tracks.Any(t => t.Kind == Timeline.TrackKind.Script && t.Spans.Any(s => s.Label == "glow")),
            $"timeline: project state track and script tracks ({string.Join(", ", tracks.Where(t => t.Kind == Timeline.TrackKind.Script).Select(t => t.Name + " " + t.Spans.Count))})");
    }

    private Task<ExportPlan?> ExportPlanNow()
    {
        var done = new TaskCompletionSource<ExportPlan?>();
        void Handler()
        {
            _w.ExportTab.Planned -= Handler;
            done.TrySetResult(_w.ExportTab.Plan);
        }
        _w.ExportTab.Planned += Handler;
        _w.ExportTab.Replan();
        return done.Task;
    }
}
