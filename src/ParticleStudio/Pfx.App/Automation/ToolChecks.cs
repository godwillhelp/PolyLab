using System.Numerics;
using Pfx.App.Core;
using Pfx.App.Inspector;
using Pfx.App.Model;
using Pfx.App.Viewport;
using Pfx.Core.Model;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Automation;

// P8 checks: the viewport tools driven through the methods the viewport's mouse handler calls (Press / Drag / Release
// with pixel positions worked out from the handles on screen; no input is sent to the desktop). Handle and field are
// one value (1.1 rule 4), every drag is one undo step, presets and the five path uses write plain keys in one step.
public sealed partial class ShellChecks
{
    private async Task Settle()
    {
        while (_w.Pipeline.AnyBusy)
            await Frame();
        await Frame();
    }

    private Vector2 Px(Vector3 world) => _w.Gizmo.Screen(world) ?? new Vector2(float.NaN);

    private static string RawOf(EmitterView v, string key) => Raw.Of(v.Block, key) ?? "";

    // A drag of a handle by pixels, in a few mouse moves like a hand makes.
    private async Task DragHandle(Handle h, Vector2 by, int moves = 4)
    {
        var at = h.Points.Length == 1 ? Px(h.Points[0]) : Px(h.Points[^1]);
        var g = _w.Gizmo;
        bool taken = g.Press((int)MathF.Round(at.X), (int)MathF.Round(at.Y), false, false);
        Expect(taken, $"a press on the {h.Kind} handle {h.Axis} takes it (not the camera)");
        for (int i = 1; i <= moves; i++)
        {
            var p = at + by * i / moves;
            g.Drag((int)MathF.Round(p.X), (int)MathF.Round(p.Y), false, false);
        }
        var end = at + by;
        g.Release((int)MathF.Round(end.X), (int)MathF.Round(end.Y));
        await Settle();
    }

    private Handle HandleOf(HandleKind kind, int axis = 0, int side = 0)
    {
        var f = _w.Gizmo.ActiveFrame()!;
        return _w.Gizmo.Build(f).First(h => h.Kind == kind && (kind is HandleKind.MoveFree or HandleKind.ScaleUniform or HandleKind.ConeA
            or HandleKind.ConeB or HandleKind.ConeC || h.Axis == axis) && (side == 0 || h.Side == side));
    }

    private Vector2 ScreenAxis(Vector3 from, Vector3 to)
    {
        var d = Px(to) - Px(from);
        return d.LengthSquared() > 0 ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    private async Task ToolChecks()
    {
        Log("viewport tools (P8):");
        var doc = _w.Docs[0];
        var first = doc.Ptc.Emitters[0];
        _w.Select(doc, new[] { first.Block }, false);
        _w.ViewFrom(ViewPreset.Free);
        _w.Pipeline.Playing = false;
        await Settle();
        await Settled();
        var original = doc.Ptc.ToBytes();
        int undo0 = doc.UndoCount;
        var vector = (string key) => _w.EmitterInspector.AllFields.OfType<VectorField>().First(f => f.Key == key);

        // Move along X: one undo step; Y and Z untouched; the inspector shows the same numbers; the tip stays under the cursor.
        _w.SetTool("select");
        var f0 = _w.Gizmo.ActiveFrame()!;
        var hx = HandleOf(HandleKind.MoveAxis, 0);
        var dir = ScreenAxis(hx.Points[0], hx.Points[^1]);
        var cursor = Px(hx.Points[^1]) + dir * 60;
        var offsetBefore = ToolEdits.Vec(first, "Offset");
        await DragHandle(hx, dir * 60);
        var offset = ToolEdits.Vec(first, "Offset");
        // The point taken (the arrow tip at the press) moves with the emitter; the arrow itself keeps its length on screen.
        var grabbed = _w.Gizmo.ActiveFrame()!.Origin + (hx.Points[^1] - f0.Origin);
        float miss = Vector2.Distance(Px(grabbed), cursor);
        Expect(offset[0] != offsetBefore[0] && offset[1] == offsetBefore[1] && offset[2] == offsetBefore[2],
            $"move X by 60 px: Offset {string.Join(" ", offsetBefore)} -> {string.Join(" ", offset)} ({RawOf(first, "Offset")})");
        Expect(miss < 1.5f, $"the point taken on the X arrow ends {miss:0.00} px from the cursor");
        Expect(doc.UndoCount == undo0 + 1 && doc.UndoLabel == Loc.T("Undo.GizmoMove"), "the drag (4 mouse moves) is one undo step");
        var field = vector("Offset");
        Expect(field.Values.SequenceEqual(offset), $"the inspector's Offset shows the same numbers ({string.Join(" ", field.Values)})");
        field.Commit(0, "+=0.5");
        await Settle();
        var f1 = _w.Gizmo.ActiveFrame()!;
        Expect(MathF.Abs(f1.Origin.X - (float)(offset[0] + 0.5) - f0.SourcePosition.X) < 1e-4f,
            $"typing +=0.5 into Offset X moves the gizmo to x {f1.Origin.X:0.###}");
        var free = HandleOf(HandleKind.MoveFree);
        await DragHandle(free, new Vector2(-30, 20));
        Expect(doc.UndoLabel == Loc.T("Undo.GizmoMove") && !ToolEdits.Vec(first, "Offset").SequenceEqual(ToolEdits.Vec(first, "Offset").Select(v => 0.0)),
            $"the centre square moves in the screen plane: Offset {RawOf(first, "Offset")}");

        // Rotate: the yaw ring changes only the first Angles value.
        _w.SetTool("rotate");
        var anglesBefore = ToolEdits.Vec(first, "Angles");
        var ring = HandleOf(HandleKind.Ring, 0);
        var grab = ring.Points[16];
        var to = ring.Points[24];
        int count = doc.UndoCount;
        var g = _w.Gizmo;
        var gp = Px(grab);
        g.Press((int)gp.X, (int)gp.Y, false, false);
        for (int i = 17; i <= 24; i++)
        {
            var p = Px(ring.Points[i]);
            g.Drag((int)p.X, (int)p.Y, false, false);
        }
        g.Release((int)Px(to).X, (int)Px(to).Y);
        await Settle();
        var angles = ToolEdits.Vec(first, "Angles");
        Expect(angles[0] != anglesBefore[0] && angles[1] == anglesBefore[1] && angles[2] == anglesBefore[2] && doc.UndoCount == count + 1,
            $"the yaw ring dragged an eighth of the way round: Angles {string.Join(" ", anglesBefore)} -> {RawOf(first, "Angles")} (one undo step)");
        Expect(vector("Angles").Values.SequenceEqual(angles), "the inspector's Angles shows the same numbers");
        _w.Gizmo.Snap = true;
        g.Press((int)gp.X, (int)gp.Y, false, false);
        g.Drag((int)Px(ring.Points[20]).X, (int)Px(ring.Points[20]).Y, false, false);
        g.Release(0, 0);
        await Settle();
        double snapped = ToolEdits.Vec(first, "Angles")[0] - angles[0];
        Expect(Math.Abs(snapped / _w.Gizmo.StepDegrees - Math.Round(snapped / _w.Gizmo.StepDegrees)) < 1e-9,
            $"with snapping the yaw changes in {Loc.Num(_w.Gizmo.StepDegrees)} degree steps ({Loc.Num(snapped)})");
        _w.Gizmo.Snap = false;

        // Shape and cone handles (an ellipse 1 x 1 x 1 for the check).
        doc.Do(first.SetWord("ShapeType", "Ellipse"), () => "check");
        doc.Do(first.Set("ShapeSize", 1, 1, 1), () => "check");
        doc.Seal();
        await Settle();
        _w.SetTool("shape");
        var face = HandleOf(HandleKind.ShapeFace, 0, 1);
        var fdir = ScreenAxis(_w.Gizmo.ActiveFrame()!.Origin, face.Points[0]);
        var fcursor = Px(face.Points[0]) + fdir * 30;
        await DragHandle(face, fdir * 30);
        var size = ToolEdits.Vec(first, "ShapeSize");
        float fmiss = Vector2.Distance(Px(HandleOf(HandleKind.ShapeFace, 0, 1).Points[0]), fcursor);
        Expect(size[0] > 1 && size[1] == 1 && size[2] == 1 && fmiss < 1.5f,
            $"the +X face handle dragged 30 px out: ShapeSize {RawOf(first, "ShapeSize")}, handle {fmiss:0.00} px from the cursor");
        var coneBefore = ToolEdits.Vec(first, "ConeAngle");
        await DragHandle(HandleOf(HandleKind.ConeC), new Vector2(25, 15));
        var cone = ToolEdits.Vec(first, "ConeAngle");
        Expect(cone[2] != coneBefore[2] && cone[0] == coneBefore[0] && cone[1] == coneBefore[1],
            $"the outer cone handle (C) dragged: ConeAngle {string.Join(" ", coneBefore)} -> {RawOf(first, "ConeAngle")}; the drawn band's C rim now at polar {cone[2] / 2}");
        _w.SetTool("scale");
        var all = HandleOf(HandleKind.ScaleUniform);
        await DragHandle(all, new Vector2(40, 0));
        var scaled = ToolEdits.Vec(first, "ShapeSize");
        Expect(Math.Abs(scaled[1] / scaled[2] - 1) < 1e-6 && scaled[1] != 1, $"the centre scales every axis by one factor: ShapeSize {RawOf(first, "ShapeSize")}");
        var axisHandle = HandleOf(HandleKind.ScaleAxis, 1);
        await DragHandle(axisHandle, ScreenAxis(axisHandle.Points[0], axisHandle.Points[^1]) * 20);
        var scaledY = ToolEdits.Vec(first, "ShapeSize");
        Expect(scaledY[1] != scaled[1] && scaledY[0] == scaled[0] && scaledY[2] == scaled[2], $"the Y arrow scales only Y: ShapeSize {RawOf(first, "ShapeSize")}");

        while (doc.UndoCount > undo0)
            doc.UndoStep();
        await Settle();
        Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(original), "undoing every handle drag gives the file back byte for byte");

        await PresetChecks(doc);
        await PathChecks(doc);
        _w.SetTool("select");
        _w.Pipeline.Playing = true;
    }

    private async Task PresetChecks(EffectDoc doc)
    {
        var first = doc.Ptc.Emitters[0];
        var original = doc.Ptc.ToBytes();
        int n = doc.Ptc.Emitters.Count;
        foreach (var (kind, key, want) in new[]
        {
            ("flat", "Angles", "0 90 0"), ("wall", "BillboardingType", "None"), ("beam", "BillboardingType", "LockedAxis"), ("ring", "ConeAngle", "360 180 180"),
        })
        {
            _w.Select(doc, new[] { first.Block }, false);
            int count = doc.UndoCount;
            _w.ApplyPreset(kind);
            await Settle();
            Expect(RawOf(first, key) == want && doc.UndoCount == count + 1, $"preset {kind}: {key} {RawOf(first, key)}, one undo step");
            doc.UndoStep();
            await Settle();
        }
        _w.Select(doc, new[] { first.Block }, false);
        _w.ApplyPreset("cross");
        await Settle();
        string yaws = string.Join(" / ", doc.Ptc.Emitters.Take(3).Select(e => RawOf(e, "Angles")));
        Expect(doc.Ptc.Emitters.Count == n + 2 && _w.SelectedEmitters.Count == 3, $"preset cross: 3 planes, all selected, Angles {yaws}");
        doc.UndoStep();
        await Settle();
        Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(original), "one undo takes the whole cross back");
    }

    // A stroke in pixels from world points (projected through the viewport camera), one sample per 1/60 s of drawing.
    private (List<Vector2> Px, List<float> T) Stroke(IEnumerable<Vector3> world)
    {
        var px = world.Select(Px).ToList();
        return (px, px.Select((_, i) => i / 60f).ToList());
    }

    private async Task<PathResult?> Draw(List<Vector2> px, List<float> t)
    {
        var done = new TaskCompletionSource<PathResult?>();
        void On(PathResult? r) => done.TrySetResult(r);
        _w.PathResultReady += On;
        _w.PathDrawing.Draw(px, t);
        var r = await done.Task;
        _w.PathResultReady -= On;
        await Settle();
        return r;
    }

    private async Task<PathResult?> Use(PathUse use)
    {
        var done = new TaskCompletionSource<PathResult?>();
        void On(PathResult? r) => done.TrySetResult(r);
        _w.PathResultReady += On;
        _w.SetPathUse(use);
        var r = await done.Task;
        _w.PathResultReady -= On;
        await Settle();
        return r;
    }

    private async Task PathChecks(EffectDoc doc)
    {
        var first = doc.Ptc.Emitters[0];
        _w.Select(doc, new[] { first.Block }, false);
        var original = doc.Ptc.ToBytes();
        int undo0 = doc.UndoCount;
        _w.SetTool("path");
        _w.SetPathPlane(PathPlane.Wall);
        _w.ViewFrom(ViewPreset.Side);
        await Settle();
        var f = _w.Gizmo.ActiveFrame()!;
        var p = f.Params;
        float life = p.LifeTime + p.LifeTimeRnd * 0.5f;
        // A flight the simulator can make (5 m/s up, 3 m/s north, gravity 1, drag 1; red_smoke's spreads of 0.4 m/s
        // and drag 1 keep their means), drawn as its projection.
        var truth = Ballistics.Path(f.Origin, new Vector3(0, 5, 3), 1, 1, life, new SimOptions { Step = _w.PreviewSettings.SimStep });
        var (px, t) = Stroke(truth.Where((_, i) => i % 3 == 0).Append(truth[^1]));
        _w.SetPathUse(PathUse.Flight);
        var flight = await Draw(px, t);
        float mpp = _w.Gizmo.MetresPerPixel(f.Origin);
        Expect(flight != null && flight.Max < 2 * mpp && doc.UndoCount == undo0 + 1,
            $"use 2, flight: {flight?.Readout} (a pixel here is {mpp:0.0000} m; {px.Count} points)");
 Expect(Math.Abs((first.Number("GravityMultiply") ?? 0) + p.GravityMultiplyRnd * 0.5 - 1) < 0.05 && Math.Abs((first.Number("AirResistance") ?? 0) + p.AirResistanceRnd * 0.5 - 1) < 0.1,
            $"the fit finds the drawn flight's gravity 1 and drag 1 (means with the spreads): GravityMultiply {RawOf(first, "GravityMultiply")}, AirResistance {RawOf(first, "AirResistance")}, Velocity {RawOf(first, "Velocity")}, ConeAngle {RawOf(first, "ConeAngle")}, Angles {RawOf(first, "Angles")}");
        var direction = await Use(PathUse.Direction);
        Expect(direction != null && doc.UndoCount == undo0 + 1 && doc.UndoLabel == Loc.T("Undo.Path.Direction"),
            $"use 1, direction (same stroke, the flight taken back first): {direction?.Readout}");
        var source = await Use(PathUse.Source);
        Expect(source != null && doc.Motion is { IsPath: true } && doc.UndoCount == undo0 + 1 && _w.Pipeline.PreviewOf(doc)?.Settings.Script is SourceMotion.Keyframed,
            $"use 3, source path: {source?.Readout}; the preview moves the source along it");
        var chain = await Use(PathUse.Chain);
        Expect(chain != null && doc.Ptc.Emitters.Count(e => e.Name.StartsWith(first.Name, StringComparison.Ordinal)) >= 5 && doc.Motion == null,
            $"use 5, chain: {chain?.Readout}; emitters {string.Join(", ", doc.Ptc.Emitters.Select(e => e.Name))}; BRateMast of the 2nd {RawOf(doc.Ptc.Emitters[1], "BRateMast")}");
        doc.UndoStep();
        await Settle();
        _w.Select(doc, new[] { first.Block }, false);
        _w.ViewFrom(ViewPreset.Top);
        _w.SetPathPlane(PathPlane.Ground);
        await Settle();
        f = _w.Gizmo.ActiveFrame()!;
        var c = f.SourcePosition;
        var circle = Enumerable.Range(0, 49).Select(i => c + new Vector3(2 * MathF.Sin(i * 7.5f * MathF.PI / 180), f.Origin.Y - c.Y, 2 * MathF.Cos(i * 7.5f * MathF.PI / 180)));
        var (cpx, ct) = Stroke(circle);
        _w.SetPathUse(PathUse.Orbit);
        var orbit = await Draw(cpx, ct);
        Expect(orbit != null && RawOf(first, "LocalTransform") == "1" && doc.Motion is { SpinRate: > 0 },
            $"use 4, swirl from a circle drawn clockwise seen from above: {orbit?.Readout}; Offset {RawOf(first, "Offset")}, spin {doc.Motion?.SpinRate} deg/s");
        while (doc.UndoCount > undo0)
            doc.UndoStep();
        await Settle();
        Expect(doc.Ptc.ToBytes().AsSpan().SequenceEqual(original) && doc.Motion == null, "undoing every path use gives the file back byte for byte and no script motion");
        _w.ViewFrom(ViewPreset.Free);
    }
}
