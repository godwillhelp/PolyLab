using System.Diagnostics;
using System.Numerics;
using Pfx.App.Core;
using Pfx.App.Model;
using Pfx.Core.Editing;
using Pfx.Core.Model;
using Pfx.Render;
using Pfx.Sim;

namespace Pfx.App.Viewport;

public enum PathPlane { Ground, Wall, Free }

public enum PathUse { Direction, Flight, Source, Orbit, Chain }

// What a stroke became: the line the tool shows over it, the distance of every drawn point from that line (metres),
// the numbers behind it and whether it was written into the effect.
public sealed record PathResult(PathUse Use, Vector3[] Line, float[] Errors, Func<string> Text, Vector3[] Marks)
{
    public string Readout => Text();
    public float Max => Errors.Length == 0 ? 0 : Errors.Max();
}

// The path tool (spec 3.7): the owner draws a stroke on a plane through the selected emitter (horizontal, upright
// facing the camera, or the camera plane) and picks what it becomes; the result is written as plain keys (or the
// part's script motion) in one undo step, with the error of each drawn point in metres. Fits run on a worker; the
// edit is made on the UI thread when the fit lands, only if the part and the emitter are still the ones drawn for.
// Picking another use for the same stroke takes the previous result back first if it is still the newest undo step.
// State: the stroke and the last result belong to this tool (view state, dropped with a new stroke); the documents
// own every written value.
public sealed class PathTool : IViewportTool
{
    // Mouse jitter below this many pixels between samples is not a new stroke point (a UI choice).
    public const float MinSamplePx = 3;
    public const int MinChain = 2, MaxChain = 16;
    // Points nearer the emitter than this share of the stroke length are left out of the aperture of use 1: their
    // angle is mostly the hand's wobble at the start.
    private const float ApertureSkip = 0.05f;

    private readonly IToolHost _host;
    private readonly List<Vector3> _points = new();
    private readonly List<float> _times = new();
    private readonly Stopwatch _clock = new();
    private Vector2 _lastPx;
    private bool _drawing;
    private Stroke? _stroke;
    private int _generation;
    private (EffectDoc Doc, int Count, string Label)? _applied;

    public PathTool(IToolHost host) => _host = host;

    public PathPlane Plane { get; set; } = PathPlane.Wall;
    public PathUse Use { get; set; } = PathUse.Flight;
    public int ChainCount { get; set; } = 5;
    public bool YawAlongPath { get; set; }
    public PathResult? Result { get; private set; }
    public bool Busy { get; private set; }
    public IReadOnlyList<Vector3> Points => _stroke?.Points ?? (IReadOnlyList<Vector3>)_points;

    // A result was written (or failed: null): the shell refreshes its readout, automation waits for it.
    public event Action<PathResult?>? Finished;

    private sealed record Stroke(EffectDoc Doc, EmitterView Emitter, IReadOnlyList<EmitterView> Selected, EmitterFrame Frame,
        Vector3[] Points, float[] Times, float MetresPerPixel, SimOptions Options);

    private Camera Cam => _host.Viewport.Camera;
    private float W => Math.Max(1, _host.Viewport.PixelWidth);
    private float H => Math.Max(1, _host.Viewport.PixelHeight);

    // The drawing plane through the emitter: horizontal, upright facing the camera (normal = the camera's horizontal
    // forward), or the camera plane.
    public (Vector3 Point, Vector3 Normal) PlaneOf(EmitterFrame f)
    {
        var fwd = Cam.Forward;
        var flat = new Vector3(fwd.X, 0, fwd.Z);
        var normal = Plane switch
        {
            PathPlane.Ground => Vector3.UnitY,
            PathPlane.Wall => flat.LengthSquared() > 1e-4f ? Vector3.Normalize(flat) : Vector3.UnitZ,
            _ => fwd,
        };
        return (f.Origin, normal);
    }

    private Vector3? Hit(EmitterFrame f, float x, float y)
    {
        var (eye, dir) = Cam.Ray(x, y, W, H);
        var (p, n) = PlaneOf(f);
        return GizmoTool.PlaneHit(p, n, eye, dir);
    }

    public bool Press(int x, int y, bool ctrl, bool shift)
    {
        var doc = _host.ActiveDoc;
        var sel = _host.SelectedEmitters;
        if (doc == null || sel.Count == 0)
        {
            _host.Toast(Loc.T("Toast.PathNoEmitter"), "info");
            return true;
        }
        var f = EmitterFrame.Of(sel[0], _host, doc);
        if (Hit(f, x, y) is not { } p)
            return true;
        _points.Clear();
        _times.Clear();
        _stroke = null;
        Result = null;
        _applied = null;
        _clock.Restart();
        _points.Add(p);
        _times.Add(0);
        _lastPx = new Vector2(x, y);
        _drawing = true;
        return true;
    }

    public void Drag(int x, int y, bool ctrl, bool shift)
    {
        if (!_drawing || _host.ActiveDoc is not { } doc || _host.SelectedEmitters.Count == 0)
            return;
        if (Vector2.Distance(new Vector2(x, y), _lastPx) < MinSamplePx)
            return;
        var f = EmitterFrame.Of(_host.SelectedEmitters[0], _host, doc);
        if (Hit(f, x, y) is not { } p)
            return;
        _points.Add(p);
        _times.Add((float)_clock.Elapsed.TotalSeconds);
        _lastPx = new Vector2(x, y);
    }

    public void Release(int x, int y)
    {
        if (!_drawing)
            return;
        _drawing = false;
        var doc = _host.ActiveDoc;
        var sel = _host.SelectedEmitters;
        if (doc == null || sel.Count == 0)
            return;
        if (_points.Count < 3)
        {
            _host.Toast(Loc.T("Toast.PathShort"), "info");
            return;
        }
        var f = EmitterFrame.Of(sel[0], _host, doc);
        var options = new SimOptions { Step = _host.PreviewSettings.SimStep };
        _stroke = new Stroke(doc, sel[0], sel.ToList(), f, _points.ToArray(), _times.ToArray(),
            Cam.MetresPerPixel(Math.Max(Camera.NearPlane, Vector3.Dot(f.Origin - Cam.Eye, Cam.Forward)), H), options);
        Run();
    }

    public void Cancel()
    {
        _drawing = false;
        _points.Clear();
        _times.Clear();
    }

    public void Hover(int x, int y)
    {
    }

    // Draws the same stroke again with another use (no new drawing needed).
    public void Rerun()
    {
        if (_stroke != null && !Busy)
            Run();
    }

    // Automation and checks: a stroke given in viewport pixels with drawing times, through the same steps as the mouse.
    public void Draw(IReadOnlyList<Vector2> pixels, IReadOnlyList<float> seconds)
    {
        if (!Press((int)pixels[0].X, (int)pixels[0].Y, false, false))
            return;
        _times[0] = seconds[0];
        for (int i = 1; i < pixels.Count; i++)
        {
            int n = _points.Count;
            Drag((int)MathF.Round(pixels[i].X), (int)MathF.Round(pixels[i].Y), false, false);
            if (_points.Count > n)
                _times[^1] = seconds[i];
        }
        Release((int)pixels[^1].X, (int)pixels[^1].Y);
    }

    private void Run()
    {
        var s = _stroke!;
        var use = Use;
        int chain = Math.Clamp(ChainCount, MinChain, MaxChain);
        int generation = ++_generation;
        Busy = true;
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => Compute(s, use, chain)).ContinueWith(t =>
        {
            if (generation != _generation)
                return;
            Busy = false;
            if (t.Exception is { } ex)
            {
                var inner = ex.InnerException ?? ex;
                _host.Toast(Loc.F("Toast.PathFailed", inner is PathError pe ? Loc.T(pe.Key) : inner.Message), "err");
                Finished?.Invoke(null);
                return;
            }
            Apply(s, t.Result);
        }, ui);
    }

    // ---- the five uses (worker) ----

    // A failure the worker reports by its text key (the worker cannot read the language dictionaries).
    private sealed class PathError : Exception
    {
        public PathError(string key) : base(key) => Key = key;
        public string Key { get; }
    }

    private sealed record Fit(PathUse Use, Vector3[] Line, float[] Errors, Vector3[] Marks, Vector3 Direction, float Aperture, float Speed,
        BallisticFit? Flight, OrbitFit? Orbit, float[]? KeyTimes, Vector3[]? Keys, int Rounds);

    private static float MeanLife(EmitterParams p) => p.LifetimeByAnim && p.AnimFps > 0 ? p.BaseLifeTime : p.LifeTime + Math.Max(0, p.LifeTimeRnd) * 0.5f;

    private static Fit Compute(Stroke s, PathUse use, int chain)
    {
        var p = s.Frame.Params;
        var o = s.Frame.Origin;
        var pts = s.Points;
        switch (use)
        {
            case PathUse.Direction:
            {
                var end = pts[^1];
                var d = end - o;
                if (d.LengthSquared() < 1e-8f)
                    throw new PathError("Path.Err.NoLength");
                d = Vector3.Normalize(d);
                float total = PathFit.Length(pts.Prepend(o).ToList());
                float widest = 0;
                foreach (var q in pts)
                {
                    var r = q - o;
                    if (r.Length() < total * ApertureSkip)
                        continue;
                    widest = Math.Max(widest, MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(r), d), -1, 1)) * (180f / MathF.PI));
                }
                float life = MeanLife(p);
                float g = p.GravityMultiply + p.GravityMultiplyRnd * 0.5f, k = p.AirResistance + p.AirResistanceRnd * 0.5f;
                float reach = Vector3.Dot(end - o, d);
                float speed = SpeedFor(o, d, reach, g, k, life, s.Options);
                var line = Ballistics.Path(o, d * speed, g, k, life, s.Options);
                return new Fit(use, line, PathFit.Distances(pts, line), new[] { end }, d, 2 * widest, speed, null, null, null, null, 0);
            }
            case PathUse.Flight:
            {
                // Rounds stop when the rms distance improves by less than a tenth of a pixel at the emitter: the
                // stroke itself is not drawn finer than a pixel.
                var fit = PathFit.Ballistic(pts, o, MeanLife(p), s.Options, s.MetresPerPixel * 0.1f);
                return new Fit(use, fit.Path, fit.Errors, new[] { o }, fit.Velocity, 0, fit.Velocity.Length(), fit, null, null, null, fit.Iterations);
            }
            case PathUse.Source:
            {
                // Keys where the stroke bends by more than a pixel; straight between them, as the motion plays.
                var keep = PathFit.Simplify(pts, s.MetresPerPixel);
                var keys = keep.Select(i => pts[i]).ToArray();
                var shift = s.Frame.Origin - s.Frame.SourcePosition;
                return new Fit(use, keys, PathFit.Distances(pts, keys), keys, default, 0, 0, null, null,
                    keep.Select(i => s.Times[i] - s.Times[0]).ToArray(), keys.Select(k => k - shift).ToArray(), 0);
            }
            case PathUse.Orbit:
            {
                var c = s.Frame.SourcePosition;
                var fit = PathFit.Orbit(pts, s.Times, c);
                var line = new List<Vector3>();
                int n = Math.Max(2, pts.Length * 2);
                float h0 = fit.StartHeading, sweep = fit.DegreesPerSecond * (s.Times[^1] - s.Times[0]);
                for (int i = 0; i < n; i++)
                {
                    float h = h0 + sweep * i / (n - 1);
                    float r = h * (MathF.PI / 180f);
                    line.Add(c + new Vector3(fit.Radius * MathF.Sin(r), fit.Height + fit.RisePerDegree * h, fit.Radius * MathF.Cos(r)));
                }
                return new Fit(use, line.ToArray(), fit.Errors, new[] { c }, default, 0, 0, null, fit, null, null, 0);
            }
            default:
            {
                var keys = PathFit.Resample(pts, chain);
                return new Fit(use, keys, PathFit.Distances(pts, keys), keys, default, 0, 0, null, null, null, keys, 0);
            }
        }
    }

    // The launch speed along d whose centre particle covers `reach` metres along d in its life (gravity and drag of
    // the emitter included): the reach grows with the speed, so bisection finds it; 60 halvings take any bracket
    // below float precision.
    private static float SpeedFor(Vector3 o, Vector3 d, float reach, float g, float k, float life, SimOptions opt)
    {
        float Reach(float v) => Vector3.Dot(Ballistics.Path(o, d * v, g, k, life, opt)[^1] - o, d);
        float lo = 0, hi = 1;
        for (int i = 0; i < 40 && Reach(hi) < reach; i++)
            hi *= 2;
        for (int i = 0; i < 60; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Reach(mid) < reach)
                lo = mid;
            else
                hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    // ---- writing the result (UI thread) ----

    private void Apply(Stroke s, Fit fit)
    {
        var doc = s.Doc;
        bool live = _host.ActiveDoc == doc && doc.Ptc.Emitters.Any(e => e.Block == s.Emitter.Block);
        if (!live)
        {
            _host.Toast(Loc.T("Toast.PathStale"), "warn");
            Finished?.Invoke(null);
            return;
        }
        if (doc.Locked.Contains(s.Emitter.Block))
        {
            _host.Toast(Loc.T("Toast.GizmoLocked"), "warn");
            Finished?.Invoke(null);
            return;
        }
        if (_applied is { } a && a.Doc == doc && doc.UndoCount == a.Count && doc.UndoLabel == a.Label)
            doc.UndoStep();
        var e = doc.Ptc.Emitters.First(v => v.Block == s.Emitter.Block);
        var p = s.Frame.Params;
        string label = Loc.T("Undo.Path." + fit.Use);
        string merge = "path:" + Guid.NewGuid().ToString("N");
        // Composed when shown, so it follows the interface language.
        Func<string> readout;
        var makers = new List<Func<IEdit?>>();
        float life = MeanLife(p);
        double mpp = s.MetresPerPixel;
        switch (fit.Use)
        {
            case PathUse.Direction:
            {
                var (yaw, cone) = Orientation.Aim(s.Frame.ToSource(fit.Direction), fit.Aperture);
                double velocity = Math.Max(0, ToolEdits.Round(fit.Speed - p.VelocityRnd * 0.5f, ToolEdits.Precision(mpp / life)));
                makers.Add(ToolEdits.SetVec(e, "Angles", new double[] { Math.Round(yaw, 1), 0, 0 }));
                makers.Add(ToolEdits.SetVec(e, "ConeAngle", new double[] { Math.Round(cone.X, 1), Math.Round(cone.Y, 1), Math.Round(cone.Z, 1) }));
                makers.Add(ToolEdits.SetNum(e, "Velocity", velocity));
                readout = () => Loc.F("Path.Read.Direction", Loc.Num(Math.Round(yaw, 1)), Loc.Num(Math.Round(cone.X, 1)), Loc.Num(Math.Round(cone.Y, 1)),
                    Loc.Num(Math.Round(cone.Z, 1)), Loc.Num(velocity));
                break;
            }
            case PathUse.Flight:
            {
                var f = fit.Flight!;
                float speed = f.Velocity.Length();
                var (yaw, cone) = Orientation.Aim(s.Frame.ToSource(f.Velocity), 0);
                // Each value is rounded where the change moves the end of the flight less than a pixel: speed by
                // dv · life, gravity by g dm life² / 2, drag by about k v life² / 2.
                double v = Math.Max(0, ToolEdits.Round(speed - p.VelocityRnd * 0.5f, ToolEdits.Precision(mpp / life)));
                double g = ToolEdits.Round(f.GravityMultiply - p.GravityMultiplyRnd * 0.5f, ToolEdits.Precision(2 * mpp / (s.Options.Gravity * life * life)));
                double k = Math.Max(0, ToolEdits.Round(f.AirResistance - p.AirResistanceRnd * 0.5f, ToolEdits.Precision(2 * mpp / (Math.Max(speed, 1) * life * life))));
                makers.Add(ToolEdits.SetVec(e, "Angles", new double[] { Math.Round(yaw, 1), 0, 0 }));
                makers.Add(ToolEdits.SetVec(e, "ConeAngle", new double[] { 0, Math.Round(cone.Y, 1), Math.Round(cone.Z, 1) }));
                makers.Add(ToolEdits.SetNum(e, "Velocity", v));
                makers.Add(ToolEdits.SetNum(e, "GravityMultiply", g));
                makers.Add(ToolEdits.SetNum(e, "AirResistance", k));
                // The game takes X + U[0,1) RND (mean X + RND / 2, P3); a spread wider than twice the fitted value
                // cannot keep that mean with X at the engine minimum 0.
                bool wide = speed < p.VelocityRnd * 0.5f || f.AirResistance < p.AirResistanceRnd * 0.5f;
                bool ignored = p.Spring > 0 || p.Wind || !p.VelocityMast.IsConstant || !p.AirResistanceMast.IsConstant;
                readout = () => Loc.F("Path.Read.Flight", Loc.Num(v), Loc.Num(g), Loc.Num(k), Loc.Num(Math.Round(yaw, 1)), Loc.Num(Math.Round(cone.Y, 1)), f.Iterations)
                    + (wide ? " · " + Loc.T("Path.Read.RndWide") : "") + (ignored ? " · " + Loc.T("Path.Read.FlightIgnored") : "");
                break;
            }
            case PathUse.Source:
            {
                var motion = new ScriptMotion(fit.KeyTimes, fit.Keys, YawAlongPath, 0);
                makers.Add(() => doc.SetMotion(motion));
                readout = () => Loc.F("Path.Read.Source", fit.Keys!.Length, Loc.Num(Math.Round(fit.KeyTimes![^1], 2)), Loc.Num(Math.Round(PathFit.Length(fit.Keys), 2)));
                break;
            }
            case PathUse.Orbit:
            {
                var f = fit.Orbit!;
                double precision = ToolEdits.Precision(mpp);
                float heading = (f.StartHeading - s.Frame.SourceYaw) * (MathF.PI / 180f);
                double height = ToolEdits.Round(f.Height + f.RisePerDegree * f.StartHeading, precision);
                var offset = new double[] { ToolEdits.Round(f.Radius * MathF.Sin(heading), precision), height, ToolEdits.Round(f.Radius * MathF.Cos(heading), precision) };
                double rate = Math.Round(f.DegreesPerSecond, 1);
                makers.Add(ToolEdits.SetNum(e, "LocalTransform", 1));
                makers.Add(ToolEdits.SetVec(e, "Offset", offset));
                float sweep = MathF.Abs(f.DegreesPerSecond * (s.Times[^1] - s.Times[0]));
                // A rise smaller than two pixels over the drawn turn is the hand, not a helix: the emitter keeps its own
                // speed and cone.
                bool helix = MathF.Abs(f.RisePerDegree) * sweep > 2 * mpp;
                double rise = ToolEdits.Round(f.RisePerDegree * f.DegreesPerSecond, ToolEdits.Precision(mpp));
                if (helix)
                {
                    makers.Add(ToolEdits.SetNum(e, "Velocity", rise));
                    makers.Add(ToolEdits.SetVec(e, "ConeAngle", new double[] { 0, 0, 0 }));
                    makers.Add(ToolEdits.SetNum(e, "GravityMultiply", 0));
                    makers.Add(ToolEdits.SetNum(e, "AirResistance", 0));
                }
                makers.Add(() => doc.SetMotion(new ScriptMotion(null, null, false, (float)rate)));
                readout = () => Loc.F("Path.Read.Orbit", Loc.Num(Math.Round(f.Radius, 3)), Loc.Num(rate), helix ? Loc.F("Path.Read.Rise", Loc.Num(rise)) : Loc.T("Path.Read.NoRise"));
                break;
            }
            default:
            {
                readout = ChainEdits(doc, e, s, fit.Keys!, label, merge);
                break;
            }
        }
        if (fit.Use != PathUse.Chain)
            ToolEdits.Steps(doc, label, merge, makers);
        doc.Seal();
        _applied = (doc, doc.UndoCount, label);
        var result = new PathResult(fit.Use, fit.Line, fit.Errors, () => Loc.F("Path.Read.Error", Loc.Num(Math.Round(Rms(fit.Errors), 3)),
            Loc.Num(Math.Round(fit.Errors.Max(), 3))) + " · " + readout(), fit.Marks);
        Result = result;
        Finished?.Invoke(result);
    }

    private static float Rms(float[] e) => e.Length == 0 ? 0 : MathF.Sqrt(e.Sum(x => x * x) / e.Length);

    // Use 5: the emitter and N - 1 copies of it along the stroke, each born in its own window of the effect time
    // (BRateMast 0 -> 1 -> 0 around (i + 0.5) / N, neighbours overlapping), so the births travel along the path once
    // per EffectTime.
    private Func<string> ChainEdits(EffectDoc doc, EmitterView template, Stroke s, Vector3[] points, string label, string merge)
    {
        int n = points.Length;
        var chain = ToolEdits.CloneChain(doc, template, n, label, merge);
        double precision = ToolEdits.Precision(s.MetresPerPixel);
        var blocks = new List<Pfx.Core.Text.EnfBlock>();
        for (int i = 0; i < n; i++)
        {
            var v = chain[i];
            blocks.Add(v.Block);
            var local = Vector3.Transform(points[i] - s.Frame.SourcePosition, Orientation.Yaw(-s.Frame.SourceYaw * (MathF.PI / 180f)));
            var makers = new List<Func<IEdit?>>
            {
                ToolEdits.SetVec(v, "Offset", new double[] { ToolEdits.Round(local.X, precision), ToolEdits.Round(local.Y, precision), ToolEdits.Round(local.Z, precision) }),
                () => v.SetGraph("BRateMast", Window(i, n)),
            };
            ToolEdits.Steps(doc, label, merge, makers);
        }
        _host.SelectEmitters(doc, blocks);
        return () => Loc.F("Path.Read.Chain", n, Loc.Num(s.Frame.Params.Cycle));
    }

    // BRateMast keys (time, value) of window i of n: 0 until the previous peak, 1 at (i + 0.5) / n, 0 from the next
    // peak on. Between flat keys the engine's spline dips a little below 0 next to a rise (Catmull-Rom); the simulator
    // reads it as no births (journal P8: whether the game does is checked in game).
    public static double[] Window(int i, int n)
    {
        double peak = (i + 0.5) / n, from = Math.Max(0, (i - 0.5) / n), to = Math.Min(1, (i + 1.5) / n);
        var keys = new List<(double T, double V)>();
        keys.Add((0, 0));
        if (from > 0)
            keys.Add((from, 0));
        keys.Add((peak, 1));
        if (to < 1)
            keys.Add((to, 0));
        keys.Add((1, 0));
        return keys.SelectMany(k => new[] { Math.Round(k.T, 6), k.V }).ToArray();
    }

    // ---- drawing ----

    public void Compose(OverlayBuilder o, List<LineVertex> lines)
    {
        if (_host.ActiveDoc is { } doc && _host.SelectedEmitters.Count > 0 && _stroke == null && !_drawing)
        {
            var f = EmitterFrame.Of(_host.SelectedEmitters[0], _host, doc);
            var (c, n) = PlaneOf(f);
            var u = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(n, u);
            float r = GizmoTool.GizmoPx * Cam.MetresPerPixel(Math.Max(Camera.NearPlane, Vector3.Dot(c - Cam.Eye, Cam.Forward)), H);
            var col = Palette.Path.WithAlpha(0.45f);
            Vector3[] q = { c + (u + v) * r, c + (u - v) * r, c + (-u - v) * r, c + (-u + v) * r };
            for (int i = 0; i < 4; i++)
                SceneGeometry.Segment(lines, q[i], q[(i + 1) % 4], col);
        }
        var pts = Points;
        Polyline(o, pts, Palette.Path, 2);
        if (Result is not { } res)
            return;
        Polyline(o, res.Line, Palette.Accent, 1.5f);
        foreach (var m in res.Marks)
            if (Project(m) is { } s)
                o.Dot(s.X, s.Y, 6, Palette.Accent);
        if (pts.Count != res.Errors.Length || res.Line.Length == 0)
            return;
        int worst = Array.IndexOf(res.Errors, res.Max);
        for (int i = 0; i < pts.Count; i++)
        {
            var (_, seg, along) = PathFit.Nearest(pts[i], res.Line);
            var near = res.Line.Length == 1 ? res.Line[0] : Vector3.Lerp(res.Line[seg], res.Line[Math.Min(seg + 1, res.Line.Length - 1)], along);
            if (Project(pts[i]) is { } a && Project(near) is { } b)
                o.Line(a.X, a.Y, b.X, b.Y, 1, Palette.Warn.WithAlpha(0.6f));
        }
        if (worst >= 0 && Project(pts[worst]) is { } w)
            o.Panel(w.X + 10, w.Y + 6, new[] { new OverlayLine(Loc.F("Path.WorstPoint", Loc.Num(Math.Round(res.Max, 3))), Palette.Warn) }, Palette.BgDeep.WithAlpha(0.8f));
    }

    private Vector2? Project(Vector3 p) => Cam.Project(p, W, H) is { } s ? new Vector2(s.X, s.Y) : null;

    private void Polyline(OverlayBuilder o, IReadOnlyList<Vector3> pts, Vector4 colour, float width)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
            if (Project(pts[i]) is { } a && Project(pts[i + 1]) is { } b)
                o.Line(a.X, a.Y, b.X, b.Y, width, colour);
    }
}
